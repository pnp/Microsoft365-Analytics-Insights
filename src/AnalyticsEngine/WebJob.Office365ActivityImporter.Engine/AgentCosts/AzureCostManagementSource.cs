using Common.Entities.Config;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Reads daily Azure spend from the Microsoft Cost Management <c>query</c> API.
    /// </summary>
    /// <remarks>
    /// <para>The <c>query</c> API is used rather than <c>generateCostDetailsReport</c>. The report API returns
    /// every individual line item and is the right tool for a full financial export, but it is an
    /// asynchronous job that polls for a blob of CSVs. This import wants one aggregated row per day per meter,
    /// which <c>query</c> returns directly - and at a once-a-day cadence its per-scope throttle (a few calls a
    /// minute) is not a constraint.</para>
    ///
    /// <para>The filter is <b>configured, not hard-coded</b>. See
    /// <see cref="AzureCostImportSettings.MeterFilterValues"/> for why.</para>
    /// </remarks>
    public class AzureCostManagementSource : IAzureCostSource
    {
        public const string ApiVersion = "2026-06-01";
        private const string BaseUrl = "https://management.azure.com";

        /// <summary>
        /// Guard against following a malformed or looping <c>nextLink</c> for ever. Cost Management pages a
        /// filtered daily query in a handful of pages, so this is far above any legitimate response.
        /// </summary>
        private const int MaxPages = 200;

        private readonly AutoThrottleHttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly AzureCostImportSettings _settings;

        public AzureCostManagementSource(AutoThrottleHttpClient httpClient, AzureCostImportSettings settings, ILogger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IReadOnlyList<AzureCostRow>> GetDailyCostsAsync(string scope, DateTime fromDate, DateTime toDate)
        {
            if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("A Cost Management scope is required.", nameof(scope));

            var results = new List<AzureCostRow>();
            var url = $"{BaseUrl}/{scope.Trim('/')}/providers/Microsoft.CostManagement/query?api-version={ApiVersion}";
            var body = BuildRequestBody(fromDate, toDate);

            var pages = 0;
            while (!string.IsNullOrEmpty(url))
            {
                if (++pages > MaxPages)
                {
                    _logger.LogWarning($"Stopped reading Azure cost data after {MaxPages} pages for scope '{scope}'. "
                        + "This is a safety limit; the imported window may be incomplete.");
                    break;
                }

                var json = await PostJsonAsync(url, body, scope);
                if (json == null) break;

                results.AddRange(AzureCostQueryParser.ParsePage(json));

                // Paging is by nextLink, which already carries the skip token. The body is re-posted unchanged.
                url = AzureCostQueryParser.GetNextLink(json);
            }

            return results;
        }

        /// <summary>
        /// Builds the query body: daily granularity, cost and quantity aggregated, grouped by the dimensions
        /// stored on <see cref="AzureCostDaily"/>, and optionally filtered to the configured meters.
        /// </summary>
        internal string BuildRequestBody(DateTime fromDate, DateTime toDate)
        {
            var request = new JObject
            {
                ["type"] = "ActualCost",
                ["timeframe"] = "Custom",
                ["timePeriod"] = new JObject
                {
                    ["from"] = fromDate.ToString("yyyy-MM-ddT00:00:00Z"),
                    ["to"] = toDate.ToString("yyyy-MM-ddT23:59:59Z"),
                },
                ["dataset"] = new JObject
                {
                    ["granularity"] = "Daily",
                    ["aggregation"] = new JObject
                    {
                        ["totalCost"] = new JObject { ["name"] = "PreTaxCost", ["function"] = "Sum" },
                        ["totalQuantity"] = new JObject { ["name"] = "UsageQuantity", ["function"] = "Sum" },
                    },
                    ["grouping"] = new JArray
                    {
                        Dimension("SubscriptionId"),
                        Dimension("ResourceId"),
                        Dimension("ResourceGroup"),
                        Dimension("ServiceName"),
                        Dimension("MeterCategory"),
                        Dimension("MeterSubCategory"),
                        Dimension("Meter"),
                    },
                },
            };

            var filter = BuildFilter();
            if (filter != null)
            {
                ((JObject)request["dataset"])["filter"] = filter;
            }

            return request.ToString(Formatting.None);
        }

        /// <summary>
        /// The dimension filter, or null to import every meter at the scope.
        /// </summary>
        /// <remarks>
        /// Returning null on an empty filter is deliberate. Microsoft Cowork - the workload this import was
        /// built for - is billed through Copilot Credits managed in the Microsoft 365 admin centre, and
        /// Microsoft does not document any Azure meter name for it, so there is no correct value to hard-code.
        /// An unfiltered import brings back every meter at the scope, which lets an operator find the right
        /// filter from their own data rather than guess it.
        /// </remarks>
        private JObject BuildFilter()
        {
            var values = _settings.MeterFilterValues;
            if (values == null || values.Count == 0) return null;

            var array = new JArray();
            foreach (var value in values) array.Add(value);

            return new JObject
            {
                ["dimensions"] = new JObject
                {
                    ["name"] = _settings.MeterFilterDimension,
                    ["operator"] = "In",
                    ["values"] = array,
                },
            };
        }

        private static JObject Dimension(string name)
        {
            return new JObject { ["type"] = "Dimension", ["name"] = name };
        }

        private async Task<JObject> PostJsonAsync(string url, string body, string scope)
        {
            using (var response = await _httpClient.ExecuteHttpCallWithThrottleRetries(
                () => _httpClient.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json")), url))
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new AgentCostAuthorisationException(
                        $"Azure Cost Management refused the cost query for scope '{scope}' with HTTP {(int)response.StatusCode} ({response.StatusCode}). "
                        + "Grant the app registration's service principal the 'Cost Management Reader' role on that scope. For a subscription or "
                        + "management group that is a normal Azure RBAC assignment; for an EA or MCA billing account the equivalent role is granted in "
                        + "the billing portal rather than through Azure RBAC. If the scope is an EA enrollment, the 'AO view charges' policy must also "
                        + "be enabled. No other import is affected.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new HttpRequestException(
                        $"Azure Cost Management returned HTTP 404 for scope '{scope}'. Check the configured scope is a full resource path such as "
                        + "'/subscriptions/00000000-0000-0000-0000-000000000000' and that the service principal can see it.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await SafeReadAsync(response);
                    throw new HttpRequestException(
                        $"Azure Cost Management returned HTTP {(int)response.StatusCode} ({response.StatusCode}) for scope '{scope}'. {errorBody}");
                }

                var content = await response.Content.ReadAsStringAsync();
                return string.IsNullOrWhiteSpace(content) ? null : JObject.Parse(content);
            }
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage response)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(body)) return string.Empty;
                return body.Length > 500 ? body.Substring(0, 500) + "..." : body;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
