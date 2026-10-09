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
    /// Reads Copilot Studio consumption from the Power Platform licensing API
    /// (<c>api.powerplatform.com/licensing/entitlements/MCSMessages</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>What each identity can read.</b> Measured against a live tenant, the capacity route
    /// (<c>/entitlements/MCSMessages</c>) answers to the application identity, while every consumption route
    /// answers only to a delegated administrator token (the "Copilot Studio billing connection"). The
    /// tenant-wide per-agent route (<c>/resources</c>) is refused to both - Microsoft restricted it to its own
    /// admin center - so this source does not call it. Per-agent figures are rebuilt from
    /// <c>/users/{userId}/resources</c> by the importer.</para>
    /// <para>An application-only instance (<c>delegated: false</c>) therefore reads capacity only, and its
    /// consumption methods refuse to run rather than send a request that is known to fail.</para>
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

        private const int PageSize = 5000;

        private readonly AutoThrottleHttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly bool _delegated;

        public PowerPlatformLicensingCreditSource(AutoThrottleHttpClient httpClient, ILogger logger, bool delegated = false)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _delegated = delegated;
        }

        public bool CanReadConsumption => _delegated;

        /// <summary>
        /// One page of ONE user's consumption by resource (agent) for a single day.
        /// </summary>
        /// <remarks>
        /// A 404 is not treated as "route unavailable": the user was just listed by the per-user route, so a
        /// missing route or user here means the read is incomplete, and the generic failure keeps the
        /// importer from storing a figure that silently omits them.
        /// </remarks>
        public async Task<CopilotStudioCreditPage> GetUserResourceConsumptionPageAsync(string userId, DateTime day, string continuationToken)
        {
            RequireDelegated();
            if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("A user id is required.", nameof(userId));

            var url = $"{BaseUrl}/licensing/entitlements/{EntitlementId}/users/{Uri.EscapeDataString(userId)}/resources"
                + $"?fromDate={QueryDate(day)}"
                + $"&toDate={QueryDate(day)}"
                + $"&pageSize={PageSize.ToString(CultureInfo.InvariantCulture)}"
                + $"&api-version={ApiVersion}";

            if (!string.IsNullOrEmpty(continuationToken))
            {
                url += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";
            }

            var json = await GetJsonAsync(url, "Copilot Studio per-user credit consumption by agent");
            return CopilotStudioCreditParser.ParseConsumptionPage(json);
        }

        public async Task<CopilotStudioCapacitySnapshot> GetCapacityAsync()
        {
            var url = $"{BaseUrl}/licensing/entitlements/{EntitlementId}?api-version={ApiVersion}";
            var json = await GetJsonAsync(url, "Copilot Studio credit entitlement");
            return CopilotStudioCreditParser.ParseCapacity(json);
        }

        private void RequireDelegated()
        {
            if (!_delegated)
            {
                throw new InvalidOperationException(
                    "Copilot Studio consumption can only be read with a delegated administrator connection.");
            }
        }

        /// <summary>
        /// A date as the licensing API expects it, in the invariant culture. An interpolated
        /// <c>{date:yyyy-MM-dd}</c> uses the CURRENT culture's calendar, so a th-TH host would ask for 2569.
        /// </summary>
        internal static string QueryDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>
        /// One page of per-user consumption for the given day (<c>/users</c>).
        /// </summary>
        /// <remarks>
        /// Microsoft added this route in July 2026. It is the entry point for everything the importer reads:
        /// it lists who consumed, and the per-agent figures are rebuilt from each active user's
        /// <c>/users/{userId}/resources</c>.
        /// <para>A 404 is treated as "this tenant's API does not offer the route" and returns null rather
        /// than throwing, so the caller can say so. Note that 404 is <b>not</b> the only way an unusable route
        /// presents: against a real tenant whose service principal was not authorised this route returned a
        /// persistent HTTP 500 while its siblings returned 403, and only a genuinely unrouted path produced a
        /// clean 404 (<c>RouteNotFound</c>).</para>
        /// </remarks>
        public async Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
        {
            RequireDelegated();

            var url = $"{BaseUrl}/licensing/entitlements/{EntitlementId}/users"
                + $"?fromDate={QueryDate(fromDate)}"
                + $"&toDate={QueryDate(toDate)}"
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
        /// read, so failing to resolve one must not cost the customer the billing data it decorates. The
        /// application identity is refused on this route, so without a delegated connection it is not asked.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, string>> GetEnvironmentNamesAsync()
        {
            if (!_delegated) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
            using (var response = await _httpClient.ExecuteHttpCallWithThrottleRetries(
                () => _httpClient.GetAsync(url), url, isReplayableIdempotentGet: true))
            {
                if (treatNotFoundAsUnavailable && response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogInformation($"The Power Platform licensing API has no route for {what} on this tenant "
                        + "(HTTP 404).");
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
                    if (_delegated)
                    {
                        throw new AgentCostAuthorisationException(
                            $"Microsoft refused to let the connected billing administrator read Copilot Studio usage (HTTP {(int)response.StatusCode}). "
                            + "Check that the account still has the Power Platform Administrator role and isn't blocked by Conditional Access, "
                            + "then reconnect in Administration > Copilot Studio billing connection. "
                            + "Azure costs and Copilot Credits capacity are unaffected.");
                    }
                    // Consumption reads only run delegated, so an app-only refusal here is the capacity route.
                    throw new AgentCostAuthorisationException(
                        $"The Power Platform licensing API refused the request for {what} with HTTP {(int)response.StatusCode} ({response.StatusCode}). "
                        + (response.StatusCode == HttpStatusCode.Unauthorized
                            ? "The API did not accept authentication. Check the runtime app's tenant and token audience (https://api.powerplatform.com). "
                            : "This does not prove that a role is missing. Verify 'Power Platform reader' at tenant scope for the enterprise application the importer actually uses, "
                              + "and that the runtime app is registered with New-PowerAppManagementApp. ")
                        + "If the assignment is already verified, do not recreate it or grant a broader role blindly: test the failing endpoint with the same runtime app identity "
                        + "and retain the API response and request/correlation ID privately for Microsoft support. "
                        + "Other imports are unaffected.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"The Power Platform licensing API returned HTTP {(int)response.StatusCode} ({response.StatusCode}) for {what}.");
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

    }

    /// <summary>
    /// Thrown when a cost API rejects authentication or authorisation. Typed separately so the importer
    /// preserves actionable guidance without treating a refused read as an empty successful import.
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
