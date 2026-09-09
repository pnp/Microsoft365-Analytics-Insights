using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Reads billed Copilot Studio consumption from the Power Platform licensing API
    /// (<c>api.powerplatform.com/licensing/entitlements/MCSMessages</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Application-only access to these routes is not confirmed by Microsoft.</b> The Power Platform
    /// API supports service principals through its tenant-scoped RBAC model, but Microsoft's own
    /// authentication guidance has said the API uses delegated permissions only, and the licensing entitlement
    /// routes specifically have not been documented as working with client credentials. That is why a 401 or
    /// 403 here is translated into a precise instruction: it is the single most likely way this import fails
    /// on a correctly-installed system, and "Forbidden" on its own would send an admin looking in the wrong
    /// place.</para>
    /// </remarks>
    public class PowerPlatformLicensingCreditSource : ICopilotStudioCreditSource
    {
        /// <summary>
        /// The entitlement that Copilot Studio Copilot Credits are billed under. Every harness - Standard,
        /// Copilot Chat and GitHub Copilot - bills through this one entitlement; the harness is only visible
        /// per-row via the feature name.
        /// </summary>
        public const string EntitlementId = "MCSMessages";

        public const string ApiVersion = "2024-10-01";
        private const string BaseUrl = "https://api.powerplatform.com";

        /// <summary>
        /// Requests the richer per-row metadata (agent name, feature, model, tool, distinct-user count).
        /// Undocumented in the public REST reference but required for anything beyond a bare credit total, so
        /// the parser is written to cope if it stops being honoured.
        /// </summary>
        private const string IncludeFields = "users,tags,asOfDate";

        private const int PageSize = 5000;

        private readonly AutoThrottleHttpClient _httpClient;
        private readonly ILogger _logger;

        public PowerPlatformLicensingCreditSource(AutoThrottleHttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<CopilotStudioCreditPage> GetConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
        {
            var url = $"{BaseUrl}/licensing/entitlements/{EntitlementId}/resources"
                + $"?fromDate={fromDate:yyyy-MM-dd}"
                + $"&toDate={toDate:yyyy-MM-dd}"
                + $"&includeFields={Uri.EscapeDataString(IncludeFields)}"
                + $"&pageSize={PageSize.ToString(CultureInfo.InvariantCulture)}"
                + $"&api-version={ApiVersion}";

            if (!string.IsNullOrEmpty(continuationToken))
            {
                url += $"&continuationtoken={Uri.EscapeDataString(continuationToken)}";
            }

            var json = await GetJsonAsync(url, "Copilot Studio credit consumption");
            return CopilotStudioCreditParser.ParseConsumptionPage(json);
        }

        public async Task<CopilotStudioCapacitySnapshot> GetCapacityAsync()
        {
            var url = $"{BaseUrl}/licensing/entitlements/{EntitlementId}?api-version={ApiVersion}";
            var json = await GetJsonAsync(url, "Copilot Studio credit entitlement");
            return CopilotStudioCreditParser.ParseCapacity(json);
        }

        /// <summary>
        /// Environment id =&gt; display name. Never throws: an environment name only makes a report easier to
        /// read, so failing to resolve one must not cost the customer the billing data it decorates.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, string>> GetEnvironmentNamesAsync()
        {
            try
            {
                var url = $"{BaseUrl}/environmentmanagement/environments?api-version={ApiVersion}";
                var json = await GetJsonAsync(url, "Power Platform environments");
                return CopilotStudioCreditParser.ParseEnvironmentNames(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Couldn't resolve Power Platform environment names ({ex.Message}). "
                    + "Copilot Studio credit rows will be imported with environment IDs but no environment names.");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private async Task<JObject> GetJsonAsync(string url, string what)
        {
            using (var response = await _httpClient.ExecuteHttpCallWithThrottleRetries(() => _httpClient.GetAsync(url), url))
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new AgentCostAuthorisationException(
                        $"The Power Platform licensing API refused the request for {what} with HTTP {(int)response.StatusCode} ({response.StatusCode}). "
                        + "A valid token is not sufficient for these routes: the app registration's service principal must also hold a Power Platform "
                        + "RBAC role at tenant scope. Assign the 'Power Platform reader' role to the service principal (POST "
                        + "https://api.powerplatform.com/authorization/roleAssignments?api-version=2024-10-01, or use the Power Platform admin centre), "
                        + "then re-run. Note that Microsoft has not confirmed application-only access to the licensing entitlement routes, so if the "
                        + "role assignment is in place and this persists, the API may currently require a signed-in administrator - in which case leave "
                        + "this import turned off. No other import is affected.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await SafeReadAsync(response);
                    throw new HttpRequestException(
                        $"The Power Platform licensing API returned HTTP {(int)response.StatusCode} ({response.StatusCode}) for {what}. {body}");
                }

                var content = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(content))
                {
                    return null;
                }

                return JObject.Parse(content);
            }
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage response)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync();

                // Truncated: this goes into a log and, via the import log, a 1000-character SQL column.
                if (string.IsNullOrWhiteSpace(body)) return string.Empty;
                return body.Length > 500 ? body.Substring(0, 500) + "..." : body;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Thrown when a cost API rejects the request for authorisation reasons rather than because something is
    /// broken. Typed separately so the importer can log the specific remedy - the fix is a role assignment,
    /// which no amount of retrying will achieve.
    /// </summary>
    public class AgentCostAuthorisationException : Exception
    {
        public AgentCostAuthorisationException(string message) : base(message) { }
    }
}
