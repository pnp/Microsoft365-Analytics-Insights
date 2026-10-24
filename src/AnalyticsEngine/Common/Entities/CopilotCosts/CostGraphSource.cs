using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotCosts
{
    public sealed class CostQuantity
    {
        public string Type { get; set; }
        public double? Value { get; set; }
        public string Unit { get; set; }
    }

    public sealed class CostServiceObservation
    {
        public string ServiceId { get; set; }
        public string SpendingPolicyId { get; set; }
        public double? ConsumedQuantity { get; set; }
        public string Unit { get; set; }
        public CostQuantity ConfiguredPeriodLimit { get; set; }
        public CostQuantity ConfiguredPerUserLimit { get; set; }
        public CostQuantity EffectiveCapacityQuantity { get; set; }
        public CostQuantity RemainingQuantity { get; set; }
        public bool? IsConsumptionAllowed { get; set; }
        public string AccessDecisionReason { get; set; }
        public JObject DataStatus { get; set; }
        public CostDataFreshness Freshness { get; set; }
    }

    public sealed class CostUserObservation
    {
        public Guid PublicationId { get; set; } = Guid.NewGuid();
        public string Id { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
        public JObject SelectionDataStatus { get; set; }
        public CostDataFreshness SelectionFreshness { get; set; }
        // lastModifiedDateTime is freshness, NOT accounting-period evidence. The beta contract
        // supplies no trustworthy period identifier, so no month or daily-spend delta is inferred.
        public DateTime? SourceMonth { get; set; }
        public string PeriodStatus { get; set; } = "unverified";
        public List<CostServiceObservation> Services { get; set; } = new List<CostServiceObservation>();
        public List<JObject> UnevaluatedServices { get; set; } = new List<JObject>();
    }

    public sealed class CostGraphSource
    {
        private readonly HttpClient _http;
        private readonly string _token;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly Func<CancellationToken, Task> _assertAccess;
        public CostGraphSource(HttpClient http, string token, Func<TimeSpan, CancellationToken, Task> delay = null,
            Func<CancellationToken, Task> assertAccess = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _token = token;
            _delay = delay ?? Task.Delay;
            _assertAccess = assertAccess ?? (_ => Task.CompletedTask);
        }

        public static Uri ValidateUrl(string url, string pathPrefix)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
                || !uri.IsDefaultPort || uri.UserInfo != "" || uri.Fragment != ""
                || !(pathPrefix.EndsWith("/", StringComparison.Ordinal)
                    ? uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal)
                    : string.Equals(uri.AbsolutePath, pathPrefix, StringComparison.Ordinal))
                || uri.AbsolutePath.Contains("%") || uri.AbsolutePath.Contains(".."))
                throw new CostConnectionException("invalidPage");
            return uri;
        }

        public async Task<JObject> ReadAsync(string url, string pathPrefix, CancellationToken ct)
        {
            try { return await ReadCoreAsync(url, pathPrefix, ct); }
            catch (HttpRequestException) { throw new CostConnectionException("sourceUnavailable"); }
            catch (System.IO.IOException) { throw new CostConnectionException("sourceUnavailable"); }
        }

        private async Task<JObject> ReadCoreAsync(string url, string pathPrefix, CancellationToken ct)
        {
            var uri = ValidateUrl(url, pathPrefix);
            for (var attempt = 0; ; attempt++)
            {
                await _assertAccess(ct);
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                    using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                        {
                            var retry = response.Headers.RetryAfter?.Delta
                                ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                                ?? TimeSpan.FromSeconds(1 << attempt);
                            if (attempt >= 2 || retry > TimeSpan.FromSeconds(5))
                                throw new CostConnectionException(response.StatusCode == (HttpStatusCode)429 ? "throttled" : "sourceUnavailable", retry);
                            await _delay(retry > TimeSpan.Zero ? retry : TimeSpan.Zero, ct);
                            continue;
                        }
                        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new CostConnectionException("reconnectRequired");
                        if (response.StatusCode == HttpStatusCode.Forbidden) throw new CostConnectionException("permissionDenied");
                        if (response.StatusCode == HttpStatusCode.NotFound) throw new CostConnectionException("notFound");
                        if (!response.IsSuccessStatusCode) throw new CostConnectionException("sourceFailure");
                        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new CostConnectionException("invalidResponse");
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var buffer = new System.IO.MemoryStream())
                        {
                            var bytes = new byte[8192];
                            int count;
                            while ((count = await stream.ReadAsync(bytes, 0, bytes.Length, ct)) > 0)
                            {
                                if (buffer.Length + count > 2 * 1024 * 1024) throw new CostConnectionException("invalidResponse");
                                buffer.Write(bytes, 0, count);
                            }
                            try
                            {
                                using (var reader = new JsonTextReader(new System.IO.StringReader(new System.Text.UTF8Encoding(false, true).GetString(buffer.ToArray())))
                                { DateParseHandling = DateParseHandling.None, MaxDepth = 32 })
                                {
                                    var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                                    if (reader.Read()) throw new CostConnectionException("invalidResponse");
                                    return result;
                                }
                            }
                            catch (JsonException) { throw new CostConnectionException("invalidResponse"); }
                            catch (System.Text.DecoderFallbackException) { throw new CostConnectionException("invalidResponse"); }
                        }
                    }
                }
            }
        }

        public async Task<CostUserObservation> ReadUserAsync(string userId, CancellationToken ct)
        {
            var id = Guid.Parse(userId).ToString();
            var path = "/beta/copilot/costManagement/userBalances/" + id;
            var json = await ReadAsync("https://graph.microsoft.com" + path + "?$expand=serviceBalances", path, ct);
            var result = ParseUser(json, id, DateTimeOffset.UtcNow);
            var next = Text(json["serviceBalances@odata.nextLink"]);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (next != null)
            {
                if (seen.Count >= 100 || !seen.Add(next)) throw new CostConnectionException("invalidPage");
                var page = await ReadAsync(next, path + "/serviceBalances", ct);
                if (!(page["value"] is JArray rows)) throw new CostConnectionException("invalidResponse");
                foreach (var row in rows) result.Services.Add(ParseService(row));
                next = Text(page["@odata.nextLink"]);
            }
            if (result.Services.Select(s => s.ServiceId).Distinct(StringComparer.Ordinal).Count() != result.Services.Count)
                throw new CostConnectionException("invalidResponse");
            result.ObservedAt = DateTimeOffset.UtcNow;
            return result;
        }

        public static CostUserObservation ParseUser(JObject json, string expectedId, DateTimeOffset observedAt)
        {
            if (!Guid.TryParse(Text(json["id"]), out var id) || id != Guid.Parse(expectedId)
                || !(json["serviceBalances"] is JArray services) || !(json["unevaluatedServices"] is JArray unevaluated)
                || !(json["selectionDataStatus"] is JObject selection))
                throw new CostConnectionException("invalidResponse");
            var result = new CostUserObservation { Id = id.ToString(), ObservedAt = observedAt,
                SelectionDataStatus = selection, SelectionFreshness = ParseFreshness(selection) };
            foreach (var row in services) result.Services.Add(ParseService(row));
            foreach (var row in unevaluated)
            {
                if (!(row is JObject item) || string.IsNullOrEmpty(Text(item["serviceId"])) || string.IsNullOrEmpty(Text(item["reason"])))
                    throw new CostConnectionException("invalidResponse");
                result.UnevaluatedServices.Add(item);
            }
            if (result.Services.Select(s => s.ServiceId).Distinct(StringComparer.Ordinal).Count() != result.Services.Count)
                throw new CostConnectionException("invalidResponse");
            return result;
        }

        private static CostServiceObservation ParseService(JToken row)
        {
            if (!(row is JObject) || string.IsNullOrEmpty(Text(row["serviceId"]))
                || string.IsNullOrEmpty(Text(row["spendingPolicyId"])))
                throw new CostConnectionException("invalidResponse");
            if (row["isConsumptionAllowed"] != null && row["isConsumptionAllowed"].Type != JTokenType.Null
                && row["isConsumptionAllowed"].Type != JTokenType.Boolean) throw new CostConnectionException("invalidResponse");
            if (row["dataStatus"] != null && row["dataStatus"].Type != JTokenType.Null && !(row["dataStatus"] is JObject))
                throw new CostConnectionException("invalidResponse");
            return new CostServiceObservation
            {
                ServiceId = Text(row["serviceId"]), SpendingPolicyId = Text(row["spendingPolicyId"]),
                ConsumedQuantity = Number(row["consumedQuantity"]), Unit = Text(row["unit"]),
                ConfiguredPeriodLimit = Quantity(row["configuredPeriodLimit"]),
                ConfiguredPerUserLimit = Quantity(row["configuredPerUserLimit"]),
                EffectiveCapacityQuantity = Quantity(row["effectiveCapacityQuantity"]),
                RemainingQuantity = Quantity(row["remainingQuantity"]),
                IsConsumptionAllowed = row["isConsumptionAllowed"]?.Type == JTokenType.Boolean ? (bool?)row["isConsumptionAllowed"] : null,
                AccessDecisionReason = Text(row["accessDecisionReason"]), DataStatus = row["dataStatus"] as JObject,
                Freshness = ParseFreshness(row["dataStatus"])
            };
        }

        public static double? Number(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) throw new CostConnectionException("invalidResponse");
            var value = (double)token;
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new CostConnectionException("invalidResponse");
            return value;
        }

        public static CostQuantity Quantity(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JObject) || string.IsNullOrEmpty(Text(token["@odata.type"]))) throw new CostConnectionException("invalidResponse");
            var result = new CostQuantity { Type = Text(token["@odata.type"]), Value = Number(token["value"]), Unit = Text(token["unit"]) };
            if (result.Type == "#microsoft.graph.limitedCostManagementQuantity" && (!result.Value.HasValue || result.Unit == null))
                throw new CostConnectionException("invalidResponse");
            return result;
        }

        public static CostDataFreshness ParseFreshness(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JObject)) throw new CostConnectionException("invalidResponse");
            var text = Text(token["lastModifiedDateTime"]);
            if (text == null) return new CostDataFreshness();
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                || !(text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || text.LastIndexOf('+') > 10
                    || text.LastIndexOf('-') > 10))
                throw new CostConnectionException("invalidResponse");
            return new CostDataFreshness { LastModifiedDateTime = at };
        }

        public async Task<Tuple<IReadOnlyCollection<Guid>, string>> ReadDirectoryPageAsync(string next, CancellationToken ct)
        {
            ValidateDirectoryPage(next);
            var json = await ReadAsync(next, "/v1.0/users", ct);
            if (!(json["value"] is JArray rows) || rows.Count > 1000) throw new CostConnectionException("invalidResponse");
            var ids = new HashSet<Guid>();
            foreach (var row in rows)
            {
                if (!(row is JObject) || !Guid.TryParse(Text(row["id"]), out var id) || !ids.Add(id))
                    throw new CostConnectionException("invalidResponse");
            }
            var continuation = Text(json["@odata.nextLink"]);
            if (continuation != null) ValidateDirectoryPage(continuation);
            return Tuple.Create((IReadOnlyCollection<Guid>)ids, continuation);
        }

        public static void ValidateDirectoryPage(string url)
        {
            var uri = ValidateUrl(url, "/v1.0/users");
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            foreach (var key in query.AllKeys)
                if (key != "$select" && key != "$top" && key != "$skiptoken") throw new CostConnectionException("invalidPage");
            if (query["$select"] != "id"
                || (query["$top"] != null && (!int.TryParse(query["$top"], out var top) || top < 1 || top > 1000)))
                throw new CostConnectionException("invalidPage");
        }

        public async Task<CostContextPage> ReadContextPageAsync(CostContextTask task, CancellationToken ct)
        {
            const string root = "/beta/copilot/costManagement/";
            var path = ValidateUrl(task.Url, root).AbsolutePath;
            var result = new CostContextPage();
            var json = await ReadAsync(task.Url, path, ct);
            if (task.Kind == "tenant")
            {
                if (path != root + "getTenantCreditBalance" || !(json["billingMethodBalances"] is JArray balances))
                    throw new CostConnectionException("invalidResponse");
                var rows = new List<CostTenantBalance>();
                foreach (var item in balances)
                {
                    if (!(item is JObject) || string.IsNullOrEmpty(Text(item["billingMethod"])))
                        throw new CostConnectionException("invalidResponse");
                    rows.Add(new CostTenantBalance
                    {
                        BillingMethod = Text(item["billingMethod"]), PurchasedQuantity = Number(item["purchasedQuantity"]),
                        ConsumedQuantity = Number(item["consumedQuantity"]), RemainingQuantity = Number(item["remainingQuantity"]),
                        Unit = Text(item["unit"]), Freshness = ParseFreshness(item["dataStatus"])
                    });
                }
                result.Document = JsonConvert.SerializeObject(rows);
                return result;
            }
            if (!(json["value"] is JArray values)) throw new CostConnectionException("invalidResponse");
            var next = Text(json["@odata.nextLink"]);
            if (next != null)
            {
                ValidateUrl(next, path);
                result.Following.Add(new CostContextTask { Kind = task.Kind, Url = next });
            }
            if (task.Kind == "policies")
            {
                if (path != root + "spendingPolicies") throw new CostConnectionException("invalidPage");
                var policies = new List<CostPolicyContext>();
                foreach (var row in values)
                {
                    if (!(row is JObject)) throw new CostConnectionException("invalidResponse");
                    var id = Text(row["id"]);
                    if (string.IsNullOrEmpty(id) || id.Length > 500
                        || id.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_')))
                        throw new CostConnectionException("invalidResponse");
                    var policy = new CostPolicyContext
                    {
                        Id = id, Type = Text(row["@odata.type"]), DisplayName = Text(row["displayName"]),
                        Status = Text(row["status"]), BillingMethods = Text(row["billingMethods"]),
                        PeriodLimit = Quantity(row["periodLimit"]), PerUserLimit = Quantity(row["perUserLimit"]),
                        CreatedAt = ParseFreshness(new JObject { ["lastModifiedDateTime"] = row["createdDateTime"] })?.LastModifiedDateTime,
                        ModifiedAt = ParseFreshness(new JObject { ["lastModifiedDateTime"] = row["lastModifiedDateTime"] })?.LastModifiedDateTime
                    };
                    policies.Add(policy);
                    var policyUrl = "https://graph.microsoft.com" + root + "spendingPolicies/" + Uri.EscapeDataString(id);
                    result.Following.Add(new CostContextTask { Kind = "services", Url = policyUrl + "/serviceSettings" });
                    if (policy.Type == "#microsoft.graph.selectedGroupsSpendingPolicy")
                        result.Following.Add(new CostContextTask { Kind = "groups", Url = policyUrl + "/microsoft.graph.selectedGroupsSpendingPolicy/assignedGroups" });
                }
                if (policies.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != policies.Count)
                    throw new CostConnectionException("invalidResponse");
                result.Document = JsonConvert.SerializeObject(policies);
            }
            else if (task.Kind == "services")
            {
                if (!path.EndsWith("/serviceSettings", StringComparison.Ordinal)) throw new CostConnectionException("invalidPage");
                var services = new List<CostPolicyServiceContext>();
                foreach (var row in values)
                {
                    if (!(row is JObject) || string.IsNullOrEmpty(Text(row["serviceId"])) || string.IsNullOrEmpty(Text(row["state"])))
                        throw new CostConnectionException("invalidResponse");
                    services.Add(new CostPolicyServiceContext { ServiceId = Text(row["serviceId"]), State = Text(row["state"]),
                        ParentStatus = Text(row["parentStatus"]), ParentEtag = Text(row["parentEtag"]) });
                }
                if (services.Select(s => s.ServiceId).Distinct(StringComparer.Ordinal).Count() != services.Count)
                    throw new CostConnectionException("invalidResponse");
                result.Document = JsonConvert.SerializeObject(services);
            }
            else if (task.Kind == "groups")
            {
                if (!path.EndsWith("/microsoft.graph.selectedGroupsSpendingPolicy/assignedGroups", StringComparison.Ordinal))
                    throw new CostConnectionException("invalidPage");
                var groups = new HashSet<Guid>();
                foreach (var row in values)
                    if (!(row is JObject) || !Guid.TryParse(Text(row["id"]), out var id) || !groups.Add(id))
                        throw new CostConnectionException("invalidResponse");
                result.Document = JsonConvert.SerializeObject(groups);
            }
            else throw new CostConnectionException("invalidPage");
            return result;
        }

        public static string Text(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String) throw new CostConnectionException("invalidResponse");
            return (string)token;
        }
    }
}
