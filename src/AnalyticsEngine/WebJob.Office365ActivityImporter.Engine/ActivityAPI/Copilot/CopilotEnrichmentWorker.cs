using Common.Entities;
using Common.Entities.Copilot;
using Common.Entities.UserScope;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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
            : this(connectionString, queue, loader, scope, logger, DefaultRunBudget)
        {
        }

        public CopilotEnrichmentWorker(string connectionString, CopilotEnrichmentQueue queue, ICopilotMetadataLoader loader,
            UserImportScope scope, ILogger logger, TimeSpan runBudget)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _scope = scope ?? throw new ArgumentNullException(nameof(scope));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _runBudget = runBudget;
        }
        /// <summary>
        /// How long one run keeps starting new items. Time-based rather than a fixed count, so a backlog drains as fast as
        /// Graph and SQL allow; each item's own Graph lookups are bounded by the loader's worker deadline.
        /// </summary>
        public static readonly TimeSpan DefaultRunBudget = TimeSpan.FromMinutes(3);

        private const int PageSize = 100;
        private readonly TimeSpan _runBudget;

        public async Task<CopilotEnrichmentRunSummary> RunAsync()
        {
            var elapsed = Stopwatch.StartNew();
            var summary = new CopilotEnrichmentRunSummary();
            // Fixed for the run: an item deferred during it falls due later than this, so paging never meets it again.
            var dueBy = DateTimeOffset.UtcNow;
            CopilotEnrichmentWorkItem after = null;
            var more = true;
            while (more)
            {
                // Due-time range seek from the last item seen, never a scan of every queued event.
                var page = await _queue.ReadDueAsync(dueBy, PageSize, after);
                more = page.Count == PageSize;
                foreach (var item in page)
                {
                    if (elapsed.Elapsed >= _runBudget)
                    {
                        summary.BudgetReached = true;
                        more = false;
                        break;
                    }
                    after = item;
                    summary.Due++;
                    if (summary.OldestDueUtc == null) summary.OldestDueUtc = item.DueUtc;
                    var claim = await _queue.TryClaimAsync(item, DateTimeOffset.UtcNow);
                    if (claim == null)
                    {
                        summary.LeasedSkipped++; // Another worker holds it, or it completed after the due query.
                        continue;
                    }
                    summary.Claimed++;
                    summary.MaxAttempts = Math.Max(summary.MaxAttempts, claim.Attempts + 1);
                    await ProcessClaimAsync(claim, summary);
                }
            }
            summary.ElapsedMs = elapsed.ElapsedMilliseconds;
            LogSummary(summary);
            return summary;
        }

        private async Task ProcessClaimAsync(CopilotEnrichmentClaim claim, CopilotEnrichmentRunSummary summary)
        {
            string retryReason;
            try
            {
                if (await EnrichEventAsync(claim.EventId))
                {
                    await _queue.CompleteAsync(claim);
                    summary.Completed++;
                    return;
                }
                retryReason = "chat-not-committed";
            }
            catch (CopilotEnrichmentTerminalException ex)
            {
                // Can never succeed: retrying would only spend Graph calls every few hours for ever.
                await _queue.CompleteAsync(claim);
                CopilotEnrichmentRunSummary.Count(summary.TerminalByReason, ex.ReasonCode);
                return;
            }
            catch (Exception ex)
            {
                // Never turn an unexpected SQL/Graph failure into completed work. If state itself is down, the
                // deferral below throws too; the durable item remains and its lease makes it available again.
                retryReason = FailureReason(ex);
            }

            var now = DateTimeOffset.UtcNow;
            if (CopilotEnrichmentQueue.ShouldAbandon(claim, now))
            {
                await _queue.AbandonAsync(claim, now, retryReason);
                CopilotEnrichmentRunSummary.Count(summary.AbandonedByReason, retryReason);
            }
            else
            {
                await _queue.DeferAsync(claim, now);
                CopilotEnrichmentRunSummary.Count(summary.DeferredByReason, retryReason);
            }
        }

        /// <summary>Exception type plus the HTTP status or SQL error number: enough to classify, never a message or URL.</summary>
        internal static string FailureReason(Exception ex)
        {
            var reason = ex.GetType().Name;
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is ApiException api && api.ResponseStatusCode != 0) return reason + ":http-" + api.ResponseStatusCode;
                if (e is SqlException sql) return reason + ":sql-" + sql.Number;
            }
            return reason;
        }

        private void LogSummary(CopilotEnrichmentRunSummary s)
        {
            var oldestAgeMinutes = s.OldestDueUtc == null ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - s.OldestDueUtc.Value).TotalMinutes);
            var message = $"Copilot enrichment queue: due {s.Due:n0}, claimed {s.Claimed:n0}, completed {s.Completed:n0}, " +
                $"terminal {s.Terminal:n0}, deferred {s.Deferred:n0}, abandoned {s.Abandoned:n0}, leased-skipped {s.LeasedSkipped:n0}; " +
                $"oldest due {oldestAgeMinutes:n0} min ago; max attempts {s.MaxAttempts:n0}; " +
                (s.BudgetReached ? $"run budget of {_runBudget.TotalMinutes:n0} min reached with work still due" : "no more work due") +
                $"; elapsed {s.ElapsedMs:n0} ms." +
                (s.Deferred > 0 ? " Deferred by reason: " + CopilotEnrichmentRunSummary.Format(s.DeferredByReason) + "." : string.Empty);
            if (s.Deferred > 0 || s.Abandoned > 0 || s.BudgetReached)
                _logger.LogWarning(message);
            else
                _logger.LogInformation(message);

            if (s.Terminal > 0)
            {
                _logger.LogWarning($"Copilot enrichment queue: {s.Terminal:n0} item(s) completed without file or meeting details because " +
                    "retrying can never succeed; the interactions stay counted, chat-only. By reason: " +
                    CopilotEnrichmentRunSummary.Format(s.TerminalByReason) + ".");
            }
            if (s.Abandoned > 0)
            {
                _logger.LogError($"Copilot enrichment queue: {s.Abandoned:n0} item(s) abandoned after {CopilotEnrichmentQueue.MaxFailedAttempts} " +
                    $"failed attempts or {CopilotEnrichmentQueue.MaxAge.TotalDays:n0} days, and parked as 'abandoned-<event id>' rows in the " +
                    "CopilotEnrichment state partition; the interactions stay counted, chat-only. By last failure: " +
                    CopilotEnrichmentRunSummary.Format(s.AbandonedByReason) + ".");
            }
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
                var userId = graph == null ? await _loader.GetUserIdFromUpn(upn) : await graph.GetUserIdForDurableRetry(upn);
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
                // A URL ending in '/' or a file with no extension: the file merge skips empty dimensions and inner-joins
                // them, so no link would ever be written and the item would be retried for ever.
                if (string.IsNullOrEmpty(file.Filename) || string.IsNullOrEmpty(file.Extension))
                {
                    throw new CopilotEnrichmentTerminalException("file-name-or-extension-empty",
                        "The resolved file has no name or no extension, so no file link can be saved.");
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

    /// <summary>What one <see cref="CopilotEnrichmentWorker.RunAsync"/> did. Counts and reason codes only.</summary>
    public sealed class CopilotEnrichmentRunSummary
    {
        public int Due { get; set; }
        public int Claimed { get; set; }
        public int Completed { get; set; }
        public int LeasedSkipped { get; set; }
        public int MaxAttempts { get; set; }
        public DateTimeOffset? OldestDueUtc { get; set; }
        public bool BudgetReached { get; set; }
        public long ElapsedMs { get; set; }
        public Dictionary<string, int> TerminalByReason { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, int> DeferredByReason { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, int> AbandonedByReason { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public int Terminal => TerminalByReason.Values.Sum();
        public int Deferred => DeferredByReason.Values.Sum();
        public int Abandoned => AbandonedByReason.Values.Sum();

        internal static void Count(Dictionary<string, int> byReason, string reason)
        {
            reason = reason ?? "unknown";
            byReason[reason] = byReason.TryGetValue(reason, out var n) ? n + 1 : 1;
        }

        internal static string Format(Dictionary<string, int> byReason)
            => string.Join(", ", byReason.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
    }}
