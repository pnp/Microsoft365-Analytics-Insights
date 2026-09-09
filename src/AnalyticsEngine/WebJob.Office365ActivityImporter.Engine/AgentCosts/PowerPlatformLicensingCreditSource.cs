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

        public async Task<CopilotStudioCapacitySnapshot> GetCapacityAsync()        {
            var url = $"{BaseUrl}/licensing/entitlements/{EntitlementId}?api-version={ApiVersion}";
            var json = await GetJsonAsync(url, "Copilot Studio credit entitlement");
            return CopilotStudioCreditParser.ParseCapacity(json);
        }

        /// <summary>
        /// One page of per-user consumption for the given day.
        /// </summary>
        /// <remarks>
        /// Microsoft added this route in July 2026. It is the only documented source of per-user Copilot
        /// Studio credit consumption - the per-agent route reports a distinct-user count and nothing more.
        /// A 404 is treated as "this tenant's API does not offer the route" and returns null rather than
        /// throwing, because a deployment against an older or restricted API surface must still get its
        /// per-agent figures.
        /// </remarks>
        public async Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
        {
            var url = $"{BaseUrl}/licensing/entitlements/{EntitlementId}/users"
                + $"?fromDate={fromDate:yyyy-MM-dd}"
                + $"&toDate={toDate:yyyy-MM-dd}"
                + $"&pageSize={PageSize.ToString(CultureInfo.InvariantCulture)}"
                + $"&api-version={ApiVersion}";

            if (!string.IsNullOrEmpty(continuationToken))
            {
                url += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";
            }

            var response = await ReadAsync(url, "Copilot Studio per-user credit consumption", treatNotFoundAsUnavailable: true);

            // Only a MISSING ROUTE returns null. A day with no consumption comes back as 204 or an empty
            // body, which is an empty page - not an absent route. Collapsing the two would let one quiet day
            // abort the whole window and discard the days already read.
            if (response.RouteUnavailable) return null;

            return CopilotStudioCreditParser.ParseUserConsumptionPage(response.Json);
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
            return (await ReadAsync(url, what, treatNotFoundAsUnavailable: false)).Json;
        }

        /// <summary>
        /// One licensing API read, keeping "the route does not exist" separate from "the route returned
        /// nothing".
        /// </summary>
        /// <remarks>
        /// The distinction is load-bearing. A 404 means this tenant's API surface has no such route, so the
        /// caller should stop asking. A 204 or an empty body means the route exists and simply had nothing
        /// for the requested range, so the caller should carry on to the next day. Returning a bare null for
        /// both let one quiet day be mistaken for a missing route.
        /// </remarks>
        private async Task<LicensingApiResponse> ReadAsync(string url, string what, bool treatNotFoundAsUnavailable)
        {
            using (var response = await _httpClient.ExecuteHttpCallWithThrottleRetries(() => _httpClient.GetAsync(url), url))
            {
                if (treatNotFoundAsUnavailable && response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogInformation($"The Power Platform licensing API has no route for {what} on this tenant "
                        + "(HTTP 404). This is expected where the per-user entitlement routes are not available; the "
                        + "per-agent figures are unaffected.");
                    return LicensingApiResponse.Unavailable;
                }

                // 204 No Content is a documented response and simply means there is nothing for the
                // requested range. It is NOT the same as the route being absent.
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return LicensingApiResponse.Empty;
                }

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
                    // An empty body on a 200. Same meaning as 204: the route answered, with nothing.
                    return LicensingApiResponse.Empty;
                }

                return new LicensingApiResponse(JObject.Parse(content));
            }
        }

        /// <summary>
        /// One licensing API read: the payload, plus whether the route exists at all.
        /// </summary>
        private class LicensingApiResponse
        {
            /// <summary>The route does not exist on this tenant's API surface. Stop asking.</summary>
            public static readonly LicensingApiResponse Unavailable = new LicensingApiResponse(null, routeUnavailable: true);

            /// <summary>The route answered but had nothing for the range. Carry on.</summary>
            public static readonly LicensingApiResponse Empty = new LicensingApiResponse(null);

            public LicensingApiResponse(JObject json, bool routeUnavailable = false)
            {
                Json = json;
                RouteUnavailable = routeUnavailable;
            }

            public JObject Json { get; }

            public bool RouteUnavailable { get; }
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

    /// <summary>
    /// Thrown when paging could not complete, so only part of the requested window was read.
    /// </summary>
    /// <remarks>
    /// A partial read must never be stored as if it were the whole window. These are upserts keyed on the
    /// usage date, so a truncated read would leave the missing slices at whatever they were last set to -
    /// and, on a first import, simply absent. Either way the report would show a fall in spend that never
    /// happened. Failing the run keeps the last good figures and retries on the next cycle.
    /// </remarks>
    public class AgentCostIncompleteReadException : Exception
    {
        public AgentCostIncompleteReadException(string message) : base(message) { }
    }
}
