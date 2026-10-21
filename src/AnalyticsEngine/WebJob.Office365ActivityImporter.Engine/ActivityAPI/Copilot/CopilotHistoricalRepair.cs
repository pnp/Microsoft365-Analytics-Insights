using Common.Entities;
using Common.Entities.Copilot;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace ActivityImporter.Engine.ActivityAPI.Copilot
{
    /// <summary>
    /// Opt-in bounded historical repair from audit_events.event_data, which retains CopilotEventData
    /// (NOT the original top-level AgentId, AppIdentity or AgentPlatform). No Graph calls.
    /// Call with an explicit half-open UTC window. Persist the returned timestamp/user/event checkpoint
    /// only after success, and repeat until Scanned is zero. Replaying a checkpoint is idempotent.
    /// Rows inserted behind a checkpoint require another pass of that window; no watermark can
    /// promise a complete history while a writer is inserting historical timestamps.
    /// </summary>
    public static class CopilotHistoricalRepair
    {
        /// <summary>
        /// Operational checkpoints contain record/user identifiers and must never be written inside
        /// a Git worktree, even when ignored. Metadata-only preflight; no artifacts or SQL access.
        /// Reparse points fail closed so a junction/symlink cannot alias a repository path.
        /// </summary>
        public static string ValidateCheckpointPath(string path) =>
            PrivateArtifactPath.Validate(path, ".lock", ".pending");

        public static async Task<CopilotHistoricalRepairBatch> RunBatchAsync(string connectionString,
            DateTime fromUtc, DateTime toUtc, DateTime? afterTime, Guid? afterEventId, int batchSize, ILogger logger,
            int? afterUserId = null)
        {
            if (fromUtc >= toUtc) throw new ArgumentException("A nonempty half-open UTC window is required.");
            if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Window bounds must be UTC.");
            if (batchSize < 1 || batchSize > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
            if (afterTime.HasValue != afterEventId.HasValue) throw new ArgumentException("Both checkpoint fields are required.");
            if (afterTime.HasValue && (afterTime < fromUtc || afterTime >= toUtc)) throw new ArgumentOutOfRangeException(nameof(afterTime));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            var rows = new List<StoredRow>();
            using (var connection = AzureSqlTokenAuth.CreateConnection(connectionString))
            {
                await connection.OpenAsync();
                // Match the existing time_stamp/user_id index, whose clustering-key suffix is event_id.
                // Persist the user component too: retention may remove the cursor row between batches.
                using (var command = new SqlCommand(@"
SELECT TOP (@take) c.event_id, c.time_stamp, a.agent_id,
       CASE WHEN DATALENGTH(e.event_data) <= 1048576 THEN e.event_data END AS event_data, c.user_id
FROM dbo.copilot_chats c
JOIN dbo.audit_events e ON e.id = c.event_id
LEFT JOIN dbo.copilot_agents a ON a.id = c.agent_id
WHERE c.time_stamp >= @from AND c.time_stamp < @to
  AND (@after IS NULL OR c.time_stamp > @after OR (c.time_stamp = @after AND (
       c.user_id > @after_user OR (@after_user IS NULL AND c.user_id IS NOT NULL)
       OR ((c.user_id = @after_user OR (c.user_id IS NULL AND @after_user IS NULL)) AND c.event_id > @event))))
ORDER BY c.time_stamp, c.user_id, c.event_id OPTION (RECOMPILE)", connection) { CommandTimeout = 300 })
                {
                    command.Parameters.AddWithValue("@take", batchSize);
                    command.Parameters.AddWithValue("@from", fromUtc);
                    command.Parameters.AddWithValue("@to", toUtc);
                    command.Parameters.Add("@after", System.Data.SqlDbType.DateTime).Value = (object)afterTime ?? DBNull.Value;
                    command.Parameters.Add("@event", System.Data.SqlDbType.UniqueIdentifier).Value = (object)afterEventId ?? DBNull.Value;
                    command.Parameters.Add("@after_user", System.Data.SqlDbType.Int).Value = (object)afterUserId ?? DBNull.Value;
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        var payloadBytes = 0;
                        while (await reader.ReadAsync())
                        {
                            var data = reader.IsDBNull(3) ? null : reader.GetString(3);
                            var bytes = (data?.Length ?? 0) * 2;
                            if (rows.Count > 0 && payloadBytes + bytes > 8 * 1024 * 1024) break;
                            payloadBytes += bytes;
                            rows.Add(new StoredRow { Id = reader.GetGuid(0), Time = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                                AgentId = reader.IsDBNull(2) ? null : reader.GetString(2),
                                UserId = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                                Data = data });
                        }
                    }
                }
            }
            var result = new CopilotHistoricalRepairBatch();
            using (var manager = new CopilotAuditEventManager(
                new SqlCopilotStagingWriter(connectionString, logger, throwOnPairingFailure: true),
                new NoMetadata(), logger, resolveResourceMetadata: false))
            {
                foreach (var row in rows)
                {
                    result.Scanned++;
                    result.AfterTime = row.Time;
                    result.AfterEventId = row.Id;
                    result.AfterUserId = row.UserId;
                    CopilotAuditLogContent record;
                    try { record = Reconstruct(row); }
                    catch (JsonException) { record = null; }
                    if (record == null)
                    {
                        result.Unrecoverable++;
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(row.AgentId) && record.AgentKind == CopilotAgentKind.Unknown)
                        result.UnknownKind++;
                    await manager.SaveSingleCopilotEventToSqlStaging(record, new CommonAuditEvent { Id = row.Id, TimeStamp = row.Time });
                    result.Replayed++;
                }
                // A failure does not return a checkpoint. Committed earlier statements may be replayed.
                await manager.CommitAllChanges();
            }
            return result;
        }

        private static CopilotAuditLogContent Reconstruct(StoredRow row)
        {
            if (string.IsNullOrWhiteSpace(row.Data)) return null;
            var data = JToken.Parse(row.Data) as JObject;
            if (data == null || data["AppHost"]?.Type != JTokenType.String) return null;
            // Do not truncate conversation ids into false matches, or coerce unknown JSON shapes.
            if (data["ConversationId"] != null && data["ConversationId"].Type != JTokenType.Null
                && (data["ConversationId"].Type != JTokenType.String || ((string)data["ConversationId"]).Length > 450))
                return null;
            var platform = data["PlatformAgentType"]?.Type == JTokenType.String ? (string)data["PlatformAgentType"] : null;
            var target = data["TargetAgentName"]?.Type == JTokenType.String ? (string)data["TargetAgentName"] : null;
            var classification = CopilotAgentClassifier.Classify(row.AgentId, row.AgentId, target, platform, null);
            var parsed = data.ToObject<CopilotAuditEvent>();
            return new CopilotAuditLogContent {
                AgentId = row.AgentId, IsCustomAgent = classification.IsCustomAgentFlag,
                AgentKind = classification.Kind, AgentOrigin = classification.Origin,
                EventRaw = row.Data, ParsedAuditEvent = parsed, CopilotEventData = data.ToObject<CopilotEventData>(),
                Cost = string.IsNullOrWhiteSpace(row.AgentId) || classification.Kind == CopilotAgentKind.Unknown
                    ? null : CopilotCreditEstimation.Analyze(parsed, classification)
            };
        }

        private class StoredRow
        {
            public Guid Id;
            public DateTime Time;
            public string AgentId;
            public int? UserId;
            public string Data;
        }

        private sealed class NoMetadata : ICopilotMetadataLoader
        {
            public Task<SpoDocumentFileInfo> GetSpoFileInfo(string id, string upn) => throw new InvalidOperationException("Historical repair must not call Graph.");
            public Task<MeetingMetadata> GetMeetingInfo(string id, string user) => throw new InvalidOperationException("Historical repair must not call Graph.");
            public Task<string> GetUserIdFromUpn(string upn) => throw new InvalidOperationException("Historical repair must not call Graph.");
        }
    }

    public sealed class CopilotHistoricalRepairBatch
    {
        public int Scanned { get; set; }
        public int Replayed { get; set; }
        public int Unrecoverable { get; set; }
        public int UnknownKind { get; set; }
        public DateTime? AfterTime { get; set; }
        public Guid? AfterEventId { get; set; }
        public int? AfterUserId { get; set; }
    }
}
