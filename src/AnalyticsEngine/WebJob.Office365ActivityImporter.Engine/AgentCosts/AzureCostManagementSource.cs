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

            // Every page URL already visited. A nextLink that points back at a page we have read would
            // otherwise be followed until the page cap, and MapAndAggregate would SUM the repeats - turning a
            // server-side paging bug into inflated spend rather than an error.
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pages = 0;

            while (!string.IsNullOrEmpty(url))
            {
                if (!visited.Add(url))
                {
                    throw new AgentCostIncompleteReadException(
                        $"Azure Cost Management returned a nextLink for scope '{scope}' that points back to a page already "
                        + "read, so paging could not complete. Nothing was stored for this scope; it will be retried on the "
                        + "next cycle.");
                }

                if (++pages > MaxPages)
                {
                    throw new AgentCostIncompleteReadException(
                        $"Azure Cost Management paging for scope '{scope}' exceeded the {MaxPages}-page safety limit, so the "
                        + "window could not be read completely. Nothing was stored for this scope rather than a partial "
                        + "figure that would look like a drop in spend.");
                }

                var json = await PostJsonAsync(url, body, scope);
                if (json == null)
                {
                    // An empty body on the FIRST request means the scope genuinely had nothing in the window.
                    // An empty body after following a nextLink means the page chain broke part way through -
                    // and because the write REPLACES the scope and window, silently accepting that truncated
                    // result would delete the spend the missing pages were going to supply.
                    if (pages > 1)
                    {
                        throw new AgentCostIncompleteReadException(
                            $"Azure Cost Management returned an empty page part way through paging for scope '{scope}', "
                            + "so the window could not be read completely. Nothing was stored for this scope rather than "
                            + "a partial figure that would look like a drop in spend.");
                    }
                    break;
                }

                results.AddRange(AzureCostQueryParser.ParsePage(json));

                // Paging is by nextLink, which already carries the skip token. The body is re-posted unchanged.
                url = AzureCostQueryParser.GetNextLink(json);
            }

            return results;
        }

        /// <summary>
        /// Builds the query body: daily granularity, cost and quantity aggregated, grouped by the configured
        /// dimensions, and optionally filtered to the configured meters.
        /// </summary>
        /// <remarks>
        /// <b>At most two groupings.</b> Cost Management's <c>QueryDataset</c> schema declares
        /// <c>grouping</c> with <c>maxItems: 2</c> ("Query can have up to 2 group by clauses") in every API
        /// version, and a request carrying more is rejected outright with HTTP 400 - so a richer grain cannot be
        /// asked for here however much we would like it. <c>aggregation</c> carries the same limit, which is why
        /// only cost and quantity are requested.
        /// </remarks>
        internal string BuildRequestBody(DateTime fromDate, DateTime toDate)
        {
            var grouping = new JArray();
            foreach (var dimension in _settings.ResolvedGroupBy)
            {
                grouping.Add(Dimension(dimension));
            }

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
                    ["grouping"] = grouping,
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
