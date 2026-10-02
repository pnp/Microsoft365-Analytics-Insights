using Common.Entities.CopilotAuditBackfill;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.AuditBackfill
{
    public interface ICopilotAuditSearchSource
    {
        Task<AppTokenPermissionAccess> GetPermissionAccessAsync();
        Task<CopilotAuditSearchQuery> SubmitQueryAsync(CopilotAuditBackfillSlice slice);
        Task<CopilotAuditSearchQuery> GetQueryAsync(string queryId);
        Task<CopilotAuditSearchRecordPage> GetRecordsAsync(string queryId, string nextLink = null);
    }

    public sealed class GraphCopilotAuditSearchSource : ICopilotAuditSearchSource
    {
        public const string RequiredPermission = "AuditLogsQuery.Read.All";
        public const string CopilotOperationFilter = "CopilotInteraction";
        public const string BaseUrl = "https://graph.microsoft.com/v1.0/security/auditLog/queries";

        private readonly ManualGraphCallClient _client;
        private readonly GraphAppIndentityOAuthContext _auth;
        private readonly ILogger _logger;

        public GraphCopilotAuditSearchSource(ManualGraphCallClient client, GraphAppIndentityOAuthContext auth, ILogger logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _auth = auth;
            _logger = logger;
        }

        public Task<AppTokenPermissionAccess> GetPermissionAccessAsync()
            => AppTokenPermissionVerifier.GetAccessAsync(_auth, new[] { RequiredPermission }, _logger, RequiredPermission);

        public async Task<CopilotAuditSearchQuery> SubmitQueryAsync(CopilotAuditBackfillSlice slice)
        {
            var body = new
            {
                displayName = $"M365 Analytics Insights Copilot backfill {slice.StartUtc:yyyy-MM-ddTHH:mmZ}-{slice.EndUtc:yyyy-MM-ddTHH:mmZ}",
                filterStartDateTime = slice.StartUtc,
                filterEndDateTime = slice.EndUtc,
                operationFilters = new[] { CopilotOperationFilter },
            };

            using (var response = await _client.PostAsyncWithThrottleRetries(BaseUrl, body, _logger).ConfigureAwait(false))
            {
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return CopilotAuditSearchQuery.FromJson(json);
            }
        }

        public async Task<CopilotAuditSearchQuery> GetQueryAsync(string queryId)
            => CopilotAuditSearchQuery.FromJson(await _client.GetStringAsyncWithThrottleRetries(BaseUrl + "/" + Uri.EscapeDataString(queryId)).ConfigureAwait(false));

        public async Task<CopilotAuditSearchRecordPage> GetRecordsAsync(string queryId, string nextLink = null)
            => CopilotAuditSearchRecordPage.FromJson(await _client.GetStringAsyncWithThrottleRetries(nextLink ?? (BaseUrl + "/" + Uri.EscapeDataString(queryId) + "/records")).ConfigureAwait(false));
    }

    public sealed class CopilotAuditSearchQuery
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public int? RecordCount { get; set; }
        public bool IsTruncated { get; set; }

        public bool IsTerminal => string.Equals(Status, "succeeded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "cancelled", StringComparison.OrdinalIgnoreCase);

        public bool Succeeded => string.Equals(Status, "succeeded", StringComparison.OrdinalIgnoreCase);

        public static CopilotAuditSearchQuery FromJson(string json)
        {
            var o = JObject.Parse(json);
            var q = new CopilotAuditSearchQuery
            {
                Id = (string)o["id"],
                Status = ((string)o["status"]) ?? "unknown",
                Error = (string)o.SelectToken("error.message") ?? (string)o.SelectToken("error.code"),
                RecordCount = (int?)o["recordCount"] ?? (int?)o["recordsCount"] ?? (int?)o.SelectToken("resultInfo.recordCount"),
            };
            q.IsTruncated = Truthy(o["isTruncated"]) || Truthy(o["resultTruncated"]) || Truthy(o.SelectToken("resultInfo.isTruncated")) || q.RecordCount >= 1000000;
            return q;
        }

        private static bool Truthy(JToken token) => token != null && token.Type == JTokenType.Boolean && (bool)token;
    }

    public sealed class CopilotAuditSearchRecordPage
    {
        public List<CopilotAuditSearchRecord> Records { get; set; } = new List<CopilotAuditSearchRecord>();
        public string NextLink { get; set; }

        public static CopilotAuditSearchRecordPage FromJson(string json)
        {
            var o = JObject.Parse(json);
            return new CopilotAuditSearchRecordPage
            {
                NextLink = (string)o["@odata.nextLink"],
                Records = (o["value"] as JArray ?? new JArray()).Select(v => v.ToObject<CopilotAuditSearchRecord>()).Where(r => r != null).ToList(),
            };
        }
    }

    public sealed class CopilotAuditSearchRecord
    {
        [JsonProperty("id")]
        public string Id { get; set; }
        [JsonProperty("auditData")]
        public string AuditData { get; set; }
    }
}
