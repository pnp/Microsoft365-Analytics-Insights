using Common.Entities.CopilotAuditBackfill;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Linq;
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

            var payload = JsonConvert.SerializeObject(body);
            using (var response = await _client.PostAsync(BaseUrl, new StringContent(payload, Encoding.UTF8, "application/json")).ConfigureAwait(false))
            {
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.StatusCode == (HttpStatusCode)429)
                {
                    throw new CopilotAuditSearchThrottledException(response.GetRetryAfterHeaderSeconds(), GetGraphRequestId(response));
                }
                try
                {
                    response.EnsureSuccessStatusCode();
                }
                catch (HttpRequestException ex)
                {
                    throw new GraphHttpException(response.StatusCode, BaseUrl, json, ex, "POST", GetGraphRequestId(response));
                }
                return CopilotAuditSearchQuery.FromJson(json);
            }
        }

        public async Task<CopilotAuditSearchQuery> GetQueryAsync(string queryId)
            => CopilotAuditSearchQuery.FromJson(await _client.GetStringAsyncWithThrottleRetries(BaseUrl + "/" + Uri.EscapeDataString(queryId)).ConfigureAwait(false));

        public async Task<CopilotAuditSearchRecordPage> GetRecordsAsync(string queryId, string nextLink = null)
            => CopilotAuditSearchRecordPage.FromJson(await _client.GetStringAsyncWithThrottleRetries(nextLink ?? (BaseUrl + "/" + Uri.EscapeDataString(queryId) + "/records")).ConfigureAwait(false));

        private static string GetGraphRequestId(HttpResponseMessage response)
        {
            return response != null && response.Headers.TryGetValues("request-id", out var requestIds)
                ? requestIds.FirstOrDefault()
                : null;
        }
    }


    public sealed class CopilotAuditSearchThrottledException : Exception
    {
        public CopilotAuditSearchThrottledException(int? retryAfterSeconds, string requestId)
            : base("Microsoft Graph Audit Search throttled query submissions.")
        {
            RetryAfterSeconds = retryAfterSeconds;
            RequestId = requestId;
        }

        public int? RetryAfterSeconds { get; }
        public string RequestId { get; }
    }

    public sealed class CopilotAuditSearchQuery
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public long? ApproximateReturnedRecordCount { get; set; }
        public long? RecordCountLimit { get; set; }
        public bool IsTruncated { get; set; }

        public bool IsTerminal => string.Equals(Status, "succeeded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "cancelled", StringComparison.OrdinalIgnoreCase);

        public bool Succeeded => string.Equals(Status, "succeeded", StringComparison.OrdinalIgnoreCase);

        public bool IsKnownNonTerminal => string.Equals(Status, "notStarted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase);

        public static CopilotAuditSearchQuery FromJson(string json)
        {
            var o = JObject.Parse(json);
            var q = new CopilotAuditSearchQuery
            {
                Id = (string)o["id"],
                Status = ((string)o["status"]) ?? "unknown",
                Error = (string)o.SelectToken("error.message") ?? (string)o.SelectToken("error.code"),
                ApproximateReturnedRecordCount = (long?)o["approximateReturnedRecordCount"],
                RecordCountLimit = (long?)o["recordCountLimit"],
            };

            var limitExceeded = o["isRecordCountLimitExceeded"];
            q.IsTruncated = limitExceeded != null && limitExceeded.Type == JTokenType.Boolean
                ? (bool)limitExceeded
                : q.ApproximateReturnedRecordCount.HasValue
                  && q.RecordCountLimit.HasValue
                  && q.ApproximateReturnedRecordCount.Value >= q.RecordCountLimit.Value;
            return q;
        }
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
        [JsonProperty("createdDateTime")]
        public DateTime? CreatedDateTime { get; set; }
        [JsonProperty("auditLogRecordType")]
        public string AuditLogRecordType { get; set; }
        [JsonProperty("operation")]
        public string Operation { get; set; }
        [JsonProperty("service")]
        public string Service { get; set; }
        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }
        [JsonProperty("auditData")]
        public JToken AuditData { get; set; }
    }
}
