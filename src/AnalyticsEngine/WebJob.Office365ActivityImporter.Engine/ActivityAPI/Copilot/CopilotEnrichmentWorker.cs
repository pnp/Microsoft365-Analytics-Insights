using Common.Entities;
using Common.Entities.Copilot;
using Common.Entities.UserScope;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace ActivityImporter.Engine.ActivityAPI.Copilot
{
    public sealed class CopilotEnrichmentWorker
    {
        private readonly string _connectionString;
        private readonly CopilotEnrichmentQueue _queue;
        private readonly ICopilotMetadataLoader _loader;
        private readonly UserImportScope _scope;
        private readonly ILogger _logger;

        public CopilotEnrichmentWorker(string connectionString, CopilotEnrichmentQueue queue, ICopilotMetadataLoader loader,
            UserImportScope scope, ILogger logger)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _scope = scope ?? throw new ArgumentNullException(nameof(scope));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task RunAsync()
        {
            var elapsed = Stopwatch.StartNew();
            var completed = 0;
            var deferred = 0;
            // Due-time range seek, never a scan of every queued event. One Graph resolution at a time; stop
            // starting work after two minutes (the in-progress bounded Graph/SQL operation can finish later).
            foreach (var item in await _queue.ReadDueAsync(DateTimeOffset.UtcNow, 25))
            {
                if (elapsed.Elapsed >= TimeSpan.FromMinutes(2)) break;
                var claim = await _queue.TryClaimAsync(item, DateTimeOffset.UtcNow);
                if (claim == null) continue;
                bool complete;
                try
                {
                    complete = await EnrichEventAsync(item.EventId);
                }
                catch (Exception ex)
                {
                    // Never turn an unexpected SQL/Graph failure into completed work. If state itself is down,
                    // deferral throws too; the durable item remains and its lease makes it available again.
                    _logger.LogWarning("Copilot enrichment retry did not complete ({ExceptionType}); work remains durable for retry.", ex.GetType().Name);
                    complete = false;
                }
                if (complete)
                {
                    await _queue.CompleteAsync(claim);
                    completed++;
                }
                else
                {
                    await _queue.DeferAsync(claim, DateTimeOffset.UtcNow);
                    deferred++;
                }
            }
            _logger.LogInformation("Copilot enrichment retries: completed {Completed}, deferred {Deferred}; elapsed {ElapsedMs} ms.",
                completed, deferred, elapsed.ElapsedMilliseconds);
        }

        internal async Task<bool> EnrichEventAsync(Guid eventId)
        {
            string upn, objectId, data;
            bool hasChat, hasEnrichment;
            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand(@"
SELECT u.user_name, u.azure_ad_id, CASE WHEN DATALENGTH(e.event_data) <= 1048576 THEN e.event_data END,
       CAST(CASE WHEN c.event_id IS NULL THEN 0 ELSE 1 END AS bit),
       CAST(CASE WHEN EXISTS(SELECT 1 FROM dbo.copilot_event_files f WHERE f.copilot_chat_id = e.id)
                   OR EXISTS(SELECT 1 FROM dbo.copilot_event_meetings m WHERE m.copilot_chat_id = e.id) THEN 1 ELSE 0 END AS bit)
FROM dbo.audit_events e
LEFT JOIN dbo.users u ON u.id = e.user_id
LEFT JOIN dbo.copilot_chats c ON c.event_id = e.id
WHERE e.id = @event;", connection) { CommandTimeout = 90 })
                {
                    command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = eventId;
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        // Enqueue happens after the base audit merge committed. An absent base event was
                        // subsequently removed (retention/user-scope purge); never recreate it from retry state.
                        if (!await reader.ReadAsync()) return true;
                        upn = reader.IsDBNull(0) ? null : reader.GetString(0);
                        objectId = reader.IsDBNull(1) ? null : reader.GetString(1);
                        data = reader.IsDBNull(2) ? null : reader.GetString(2);
                        hasChat = reader.GetBoolean(3);
                        hasEnrichment = reader.GetBoolean(4);
                    }
                }
            }
            if (!_scope.IsAnyInScope(upn, objectId) || hasEnrichment) return true;
            if (!hasChat) return false; // Retry may have raced the source batch's metadata commit.
            CopilotEventData payload;
            try { payload = string.IsNullOrWhiteSpace(data) ? null : JsonConvert.DeserializeObject<CopilotEventData>(data); }
            catch (JsonException)
            {
                _logger.LogWarning("Copilot enrichment cannot be recovered: retained event data is malformed. Core audit and Copilot evidence are unchanged.");
                return true;
            }
            if (payload == null || data.Length * 2 > 1048576)
            {
                _logger.LogWarning("Copilot enrichment cannot be recovered: retained event data is absent or exceeds the 1 MiB replay bound. Core evidence is unchanged.");
                return true;
            }
            var context = payload.Contexts?.FirstOrDefault(c => c.Type != ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT);
            if (context == null) return true;
            if (string.IsNullOrWhiteSpace(upn))
            {
                _logger.LogWarning("Copilot enrichment cannot be recovered: its retained user has no UPN. Core evidence is unchanged.");
                return true;
            }
            var writer = new SqlCopilotStagingWriter(_connectionString, _logger, isolateStagingTables: true, enrichmentOnly: true);
            var graph = _loader as GraphFileMetadataLoader;
            if (context.Type == ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING)
            {
                var userId = await _loader.GetUserIdFromUpn(upn);
                var meetingId = StringUtils.GetOnlineMeetingId(context.Id, userId);
                var meeting = graph == null
                    ? await _loader.GetMeetingInfo(meetingId, userId)
                    : await graph.GetMeetingInfo(meetingId, userId, requireDefiniteResult: true);
                if (meeting == null)
                {
                    _logger.LogInformation("Copilot enrichment retry: Graph could not resolve the meeting; core evidence is retained without optional meeting metadata.");
                    return true;
                }
                writer.StageTeams(new TeamsCopilotLogTempEntity
                {
                    EventId = eventId, AppHost = payload.AppHost ?? "Unknown", MeetingId = meetingId, MeetingCreatedUTC = meeting.CreatedUTC,
                    MeetingName = AuditStagingText.TrimToLength(meeting.Subject, AuditStagingText.MeetingNameMaxLength)
                });
            }
            else
            {
                if (CopilotAuditEventManager.ShouldSkipGraphFileLookup(context.Id)) return true;
                var file = graph == null
                    ? await _loader.GetSpoFileInfo(context.Id, upn)
                    : await graph.GetSpoFileInfoSingleAttempt(context.Id, upn, requireDefiniteResult: true);
                if (file == null)
                {
                    _logger.LogInformation("Copilot enrichment retry: Graph could not resolve the file; core evidence is retained without optional file metadata.");
                    return true;
                }
                writer.StageSharePoint(new SPCopilotLogTempEntity
                {
                    EventId = eventId, AppHost = payload.AppHost ?? "Unknown", FileName = AuditStagingText.TrimToLength(file.Filename, AuditStagingText.FileNameMaxLength),
                    FileExtension = file.Extension, Url = StringUtils.EnsureUrlWithinLength(file.Url, Url.FullUrlMaxLength), UrlBase = file.SiteUrl
                });
            }
            // This writer runs only the file/meeting merge: never reconstruct agents, costs, chat fields or
            // denormalised identity from event_data, which omits several original top-level audit properties.
            await writer.CommitAllChanges();
            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            using (var command = new SqlCommand(@"
SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.copilot_event_files WHERE copilot_chat_id = @event)
                  OR EXISTS (SELECT 1 FROM dbo.copilot_event_meetings WHERE copilot_chat_id = @event) THEN 1 ELSE 0 END;", connection))
            {
                await connection.OpenAsync();
                command.CommandTimeout = 90;
                command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = eventId;
                if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 1)
                    throw new InvalidOperationException("Copilot enrichment SQL returned without saving a resource link; retry work is retained.");
            }
            return true;
        }
    }
}
