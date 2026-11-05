using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAuditBackfill;
using DataUtils;
using DataUtils.Health;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.AuditBackfill
{
    public static class CopilotAuditBackfillSafeRunner
    {
        public static async Task AdvanceSafely(Func<Task> advance, AnalyticsLogger logger)
        {
            try
            {
                await advance().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.TrackException(ex);
                logger?.LogWarning($"Copilot audit backfill failed independently of the live activity import: {ex.Message}. It will retry next cycle.");
            }
        }
    }

    /// <summary>
    /// Says which of a commit's interactions are already saved completely, so the backfill drops only those.
    /// </summary>
    public interface ICopilotAuditBackfillExistingEventFilter
    {
        /// <summary>
        /// The ids of the <paramref name="interactions"/> that have their <c>copilot_chats</c> row AND at least one row of
        /// every kind their payload makes the Copilot and DLP merges write beneath it (accessed resources, messages, AI
        /// models, contexts, plugins, DLP matches). An interaction with its chat row but a missing kind is not complete -
        /// a merge that failed partway leaves exactly that - and is committed again, which the idempotent merges turn into
        /// writing just the missing rows.
        /// </summary>
        Task<HashSet<Guid>> GetCompletelySavedIdsAsync(IEnumerable<AbstractAuditLogContent> interactions);
    }

    /// <summary>
    /// The kinds of row the Copilot and DLP merges write beneath an interaction, as far as its payload decides them.
    /// The values are the bits <see cref="SqlCopilotAuditBackfillExistingEventFilter"/>'s query returns.
    /// </summary>
    [Flags]
    public enum CopilotChildRows
    {
        None = 0,
        AccessedResources = 1,
        Messages = 2,
        AiModels = 4,
        Contexts = 8,
        AiSystemPlugins = 16,
        DlpMatches = 32,
    }

    public static class CopilotBackfillCompleteness
    {
        /// <summary>
        /// The kinds of child row the merges write for <paramref name="interaction"/>: at least one row of each. Mirrors what
        /// is staged (<c>CopilotAuditEventManager.PopulateCommonStagingFields</c>,
        /// <c>DlpAuditEventManager.SaveCopilotDlpMatchesToSqlStaging</c>) and the merges' filters. File and meeting rows are
        /// left out: whether one is written depends on what Graph resolves, not on the payload.
        /// </summary>
        public static CopilotChildRows Expected(AbstractAuditLogContent interaction)
        {
            var copilot = interaction as CopilotAuditLogContent;
            if (copilot == null) return CopilotChildRows.None;

            var expected = CopilotChildRows.None;
            if (copilot.CopilotEventData?.AccessedResources?.Count > 0) expected |= CopilotChildRows.AccessedResources;
            if (copilot.ParsedAuditEvent?.Messages?.Count > 0) expected |= CopilotChildRows.Messages;
            if (copilot.ParsedAuditEvent?.ModelTransparencyDetails?.Any(m => m?.ModelName != null) == true) expected |= CopilotChildRows.AiModels;
            if (copilot.CopilotEventData?.Contexts?.Count > 0) expected |= CopilotChildRows.Contexts;

            var plugins = copilot.CopilotEventData?.AISystemPlugin;
            if (plugins == null || plugins.Count == 0) plugins = copilot.ParsedAuditEvent?.AISystemPlugin;
            if (plugins?.Any(p => p != null && (p.Id != null || p.Name != null)) == true) expected |= CopilotChildRows.AiSystemPlugins;

            if (CopilotDlpRules.ExtractMatches(copilot).Any(m => !string.IsNullOrEmpty(m.PolicyId) || !string.IsNullOrEmpty(m.PolicyName)
                || !string.IsNullOrEmpty(m.RuleId) || !string.IsNullOrEmpty(m.RuleName)))
            {
                expected |= CopilotChildRows.DlpMatches;
            }
            return expected;
        }
    }

    public sealed class SqlCopilotAuditBackfillExistingEventFilter : ICopilotAuditBackfillExistingEventFilter
    {
        private const int ChunkSize = 1000;
        private readonly Func<AnalyticsEntitiesContext> _createContext;

        // Each child table, the bit it answers for and the EXISTS that answers it. Every one seeks the table's
        // copilot_chat_id index. A DLP match is also satisfied by the interaction's turn: the DLP merge stores a match
        // once per turn, so one record of a paired turn can rightly have none (copilot_chat_duplicates, #699).
        private static readonly (string Table, CopilotChildRows Kind, string Exists)[] ChildTables =
        {
            ("copilot_event_accessed_resources", CopilotChildRows.AccessedResources, "EXISTS (SELECT 1 FROM dbo.copilot_event_accessed_resources x WHERE x.copilot_chat_id = c.event_id)"),
            ("copilot_event_messages", CopilotChildRows.Messages, "EXISTS (SELECT 1 FROM dbo.copilot_event_messages x WHERE x.copilot_chat_id = c.event_id)"),
            ("copilot_event_ai_models", CopilotChildRows.AiModels, "EXISTS (SELECT 1 FROM dbo.copilot_event_ai_models x WHERE x.copilot_chat_id = c.event_id)"),
            ("copilot_event_contexts", CopilotChildRows.Contexts, "EXISTS (SELECT 1 FROM dbo.copilot_event_contexts x WHERE x.copilot_chat_id = c.event_id)"),
            ("copilot_event_ai_system_plugins", CopilotChildRows.AiSystemPlugins, "EXISTS (SELECT 1 FROM dbo.copilot_event_ai_system_plugins x WHERE x.copilot_chat_id = c.event_id)"),
            ("copilot_dlp_events", CopilotChildRows.DlpMatches, "EXISTS (SELECT 1 FROM dbo.copilot_dlp_events x WHERE x.copilot_chat_id = c.event_id)"),
        };

        // A record of a paired turn with no DLP row of its own is complete only when one of its turn mates holds one: the
        // record it is counted on, that record's other extra records, or its own extra records - the mates
        // insert_copilot_dlp_events_from_staging_table.sql checks before it stores a match. Being paired proves nothing on
        // its own. The Copilot merge records the pairing before the DLP merge runs, so a backfill commit that failed in
        // between left both records paired with no DLP row on either; counting that as complete dropped the retry's matches.
        private const string TurnMateDlpExists =
            "EXISTS (SELECT 1 FROM dbo.copilot_chat_duplicates d " +
                "INNER JOIN dbo.copilot_dlp_events x ON x.copilot_chat_id = d.counted_event_id WHERE d.event_id = c.event_id) " +
            "OR EXISTS (SELECT 1 FROM dbo.copilot_chat_duplicates d " +
                "INNER JOIN dbo.copilot_chat_duplicates other ON other.counted_event_id = d.counted_event_id AND other.event_id <> d.event_id " +
                "INNER JOIN dbo.copilot_dlp_events x ON x.copilot_chat_id = other.event_id WHERE d.event_id = c.event_id) " +
            "OR EXISTS (SELECT 1 FROM dbo.copilot_chat_duplicates d " +
                "INNER JOIN dbo.copilot_dlp_events x ON x.copilot_chat_id = d.event_id WHERE d.counted_event_id = c.event_id)";

        public SqlCopilotAuditBackfillExistingEventFilter(Func<AnalyticsEntitiesContext> createContext = null)
        {
            _createContext = createContext ?? (() => new AnalyticsEntitiesContext());
        }

        public async Task<HashSet<Guid>> GetCompletelySavedIdsAsync(IEnumerable<AbstractAuditLogContent> interactions)
        {
            var complete = new HashSet<Guid>();
            var expectedById = new Dictionary<Guid, CopilotChildRows>();
            foreach (var interaction in interactions ?? Enumerable.Empty<AbstractAuditLogContent>())
            {
                if (interaction == null) continue;
                expectedById.TryGetValue(interaction.Id, out var already);
                expectedById[interaction.Id] = already | CopilotBackfillCompleteness.Expected(interaction);
            }
            if (expectedById.Count == 0) return complete;

            var ids = expectedById.Keys.ToList();
            using (var db = _createContext())
            {
                var existingTables = new HashSet<string>(await db.Database.SqlQuery<string>(
                    "SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID('dbo') AND name IN ('copilot_event_accessed_resources', " +
                    "'copilot_event_messages', 'copilot_event_ai_models', 'copilot_event_contexts', 'copilot_event_ai_system_plugins', " +
                    "'copilot_dlp_events', 'copilot_chat_duplicates')").ToListAsync().ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i < ids.Count; i += ChunkSize)
                {
                    var chunk = ids.GetRange(i, Math.Min(ChunkSize, ids.Count - i));
                    var rows = await db.Database.SqlQuery<StoredChildRows>(BuildQuery(chunk.Count, existingTables), chunk.Cast<object>().ToArray())
                        .ToListAsync().ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        if ((expectedById[row.EventId] & ~(CopilotChildRows)row.Kinds) == CopilotChildRows.None)
                        {
                            complete.Add(row.EventId);
                        }
                    }
                }
            }

            return complete;
        }

        /// <summary>
        /// One row per id that has a <c>copilot_chats</c> row, with the bits of the child kinds stored beneath it. A kind
        /// whose table this database doesn't have counts as stored: the merges can't write it either.
        /// </summary>
        internal static string BuildQuery(int idCount, ISet<string> existingTables)
        {
            var kinds = new List<string>();
            foreach (var child in ChildTables)
            {
                var bit = ((int)child.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!existingTables.Contains(child.Table))
                {
                    kinds.Add(bit);
                    continue;
                }

                var exists = child.Exists;
                if (child.Kind == CopilotChildRows.DlpMatches && existingTables.Contains("copilot_chat_duplicates"))
                {
                    exists += " OR " + TurnMateDlpExists;
                }
                kinds.Add($"CASE WHEN {exists} THEN {bit} ELSE 0 END");
            }

            var parameters = string.Join(", ", Enumerable.Range(0, idCount).Select(n => "@p" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return "SELECT c.event_id AS EventId, " + string.Join(" + ", kinds) + " AS Kinds " +
                "FROM dbo.copilot_chats AS c WHERE c.event_id IN (" + parameters + ")";
        }

        private sealed class StoredChildRows
        {
            public Guid EventId { get; set; }
            public int Kinds { get; set; }
        }
    }

    /// <summary>
    /// Advances an admin-requested Microsoft Graph Audit Search backfill by a bounded amount during a normal import cycle.
    /// </summary>
    public sealed class CopilotAuditBackfillImporter
    {
        internal const int MaxInFlightQueries = 4;
        internal const int MaxSliceAttempts = 3;
        internal const int MaxRecordsPerCommit = 5000;
        internal const int DailySubmissionBudget = 100;
        internal static readonly TimeSpan MaxCycleBudget = TimeSpan.FromMinutes(8);
        internal static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan MinimumThrottlePause = TimeSpan.FromMinutes(15);
        internal static readonly TimeSpan SubmissionBudgetWindow = TimeSpan.FromHours(24);
        internal static readonly TimeSpan MaxUnknownStatusAge = TimeSpan.FromHours(24);
        private const int InitialSubmitFailureBackoffMinutes = 5;
        private const int MaxSubmitFailureBackoffMinutes = 60;
        internal static readonly TimeSpan TerminalDegradedHealthWindow = TimeSpan.FromDays(7);

        private readonly CopilotAuditBackfillStateStore _state;
        private readonly ICopilotAuditSearchSource _source;
        private readonly IActivityReportPersistenceManager _persistence;
        private readonly ICopilotAuditBackfillExistingEventFilter _existingEvents;
        private readonly AppConfig _settings;
        private readonly ILogger _logger;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<TimeSpan, Task> _delay;
        private readonly Func<TimeSpan> _elapsedOverride;

        public CopilotAuditBackfillImporter(CopilotAuditBackfillStateStore state, ICopilotAuditSearchSource source,
            IActivityReportPersistenceManager persistence, AppConfig settings, ILogger logger, Func<DateTime> utcNow = null, Func<TimeSpan, Task> delay = null,
            ICopilotAuditBackfillExistingEventFilter existingEvents = null, Func<TimeSpan> elapsedOverride = null)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _delay = delay ?? (d => Task.Delay(d));
            _existingEvents = existingEvents ?? new SqlCopilotAuditBackfillExistingEventFilter();
            _elapsedOverride = elapsedOverride;
        }

        public async Task<CopilotAuditBackfillJob> AdvanceLatestAsync()
        {
            var job = await _state.GetLatestAsync().ConfigureAwait(false);
            if (job == null || !job.IsActive)
            {
                if (job != null) TrackHealth(job);
                return job;
            }

            return await AdvanceAsync(job).ConfigureAwait(false);
        }

        internal async Task<CopilotAuditBackfillJob> AdvanceAsync(CopilotAuditBackfillJob job)
        {
            var sw = _elapsedOverride == null ? Stopwatch.StartNew() : null;
            Func<TimeSpan> elapsed = () => _elapsedOverride != null ? _elapsedOverride() : sw.Elapsed;
            job.CopilotImportEnabled = _settings.ImportJobSettings.Copilot;
            NormaliseJob(job);

            if (!job.CopilotImportEnabled)
            {
                return await FinishAsync(job, CopilotAuditBackfillStates.Failed, CopilotAuditBackfillErrorCodes.CopilotImportOff,
                    "The Copilot audit import toggle is off.").ConfigureAwait(false);
            }

            if (job.CancelRequested || await _state.IsCancelRequestedAsync(job.Id).ConfigureAwait(false))
            {
                return await FinishAsync(job, CopilotAuditBackfillStates.Cancelled, null, null).ConfigureAwait(false);
            }

            var access = await _source.GetPermissionAccessAsync().ConfigureAwait(false);
            job.PermissionStatus = ToPermissionStatus(access);
            if (access == AppTokenPermissionAccess.NotGranted || access == AppTokenPermissionAccess.NoIdentityToInspect)
            {
                _logger?.LogWarning("Skipping Copilot audit backfill: the runtime identity does not hold AuditLogsQuery.Read.All. This permission is opt-in and is not granted by the installer.");
                return await FinishAsync(job, CopilotAuditBackfillStates.Failed, CopilotAuditBackfillErrorCodes.MissingPermission,
                    "AuditLogsQuery.Read.All is missing from the runtime app token.").ConfigureAwait(false);
            }

            if (job.CancelRequested || await _state.IsCancelRequestedAsync(job.Id).ConfigureAwait(false))
            {
                return await FinishAsync(job, CopilotAuditBackfillStates.Cancelled, null, null).ConfigureAwait(false);
            }

            if (job.State == CopilotAuditBackfillStates.Queued)
            {
                job.State = CopilotAuditBackfillStates.Running;
                job.StartedUtc = _utcNow();
                Track(job, "started");
                TrackHealth(job);
            }

            try
            {
                while (elapsed() < MaxCycleBudget)
                {
                    if (job.CancelRequested || await _state.IsCancelRequestedAsync(job.Id).ConfigureAwait(false))
                    {
                        return await FinishAsync(job, CopilotAuditBackfillStates.Cancelled, null, null).ConfigureAwait(false);
                    }

                    await TopUpQueriesAsync(job, () => elapsed() >= MaxCycleBudget).ConfigureAwait(false);
                    if (job.CancelRequested || await _state.IsCancelRequestedAsync(job.Id).ConfigureAwait(false))
                    {
                        return await FinishAsync(job, CopilotAuditBackfillStates.Cancelled, null, null).ConfigureAwait(false);
                    }
                    if (job.InFlightSlices.Count == 0)
                    {
                        if (job.PendingSlices.Count > 0)
                        {
                            await _state.SaveAsync(job).ConfigureAwait(false);
                            TrackHealth(job);
                            return job;
                        }

                        var final = job.Gaps.Count > 0 ? CopilotAuditBackfillStates.CompletedWithGaps : CopilotAuditBackfillStates.Completed;
                        return await FinishAsync(job, final, null, null).ConfigureAwait(false);
                    }

                    var progressed = await PollInFlightAsync(job, () => elapsed() >= MaxCycleBudget).ConfigureAwait(false);
                    if (job.CancelRequested)
                    {
                        return await FinishAsync(job, CopilotAuditBackfillStates.Cancelled, null, null).ConfigureAwait(false);
                    }
                    if (job.InFlightSlices.Count == 0 && job.PendingSlices.Count > 0)
                    {
                        await _state.SaveAsync(job).ConfigureAwait(false);
                        TrackHealth(job);
                        return job;
                    }

                    await _state.SaveAsync(job).ConfigureAwait(false);
                    TrackHealth(job);

                    if (elapsed() >= MaxCycleBudget)
                    {
                        break;
                    }

                    if (!progressed && job.InFlightSlices.Count > 0)
                    {
                        // Remote queries can take hours. Do not hold up the next live-import cycle just to poll.
                        break;
                    }
                }

                return job;
            }
            catch (CopilotAuditBackfillFatalException ex)
            {
                return await FinishAsync(job, CopilotAuditBackfillStates.Failed, ex.Code, ex.Message).ConfigureAwait(false);
            }
            catch (GraphHttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
            {
                return await FinishAsync(job, CopilotAuditBackfillStates.Failed, CopilotAuditBackfillErrorCodes.GraphAccessDenied, ex.GraphErrorCode ?? ex.StatusCode.ToString()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Copilot audit backfill {job.Id}: cycle-level failure while advancing; active slices will retry next cycle.");
                job.LastErrorCode = CopilotAuditBackfillErrorCodes.Unexpected;
                job.LastErrorDetail = Truncate(ex.Message);
                await _state.SaveAsync(job).ConfigureAwait(false);
                Track(job, "cycleError");
                TrackHealth(job);
                return job;
            }
        }

        private static void NormaliseJob(CopilotAuditBackfillJob job)
        {
            job.PendingSlices = job.PendingSlices ?? new List<CopilotAuditBackfillSlice>();
            job.InFlightSlices = job.InFlightSlices ?? new List<CopilotAuditBackfillSlice>();
            job.CompletedDays = job.CompletedDays ?? new List<string>();
            job.Gaps = job.Gaps ?? new List<CopilotAuditBackfillGap>();
            job.SubmissionTimestampsUtc = job.SubmissionTimestampsUtc ?? new List<DateTime>();
            job.MappingFailureCounts = job.MappingFailureCounts ?? new Dictionary<string, long>(StringComparer.Ordinal);
            if (job.CurrentSlice != null)
            {
                job.InFlightSlices.Add(job.CurrentSlice);
                job.CurrentSlice = null;
            }
        }

        private async Task TopUpQueriesAsync(CopilotAuditBackfillJob job, Func<bool> budgetExpired)
        {
            var ledger = await _state.GetSubmissionLedgerAsync().ConfigureAwait(false);
            var recoveredLedger = MirrorLedger(job, ledger);
            var now = _utcNow();
            PruneSubmissionHistory(job, now);
            if (recoveredLedger)
            {
                await SaveLedgerAsync(job).ConfigureAwait(false);
            }
            if (job.SubmissionsPausedUntilUtc.HasValue && job.SubmissionsPausedUntilUtc.Value > now)
            {
                await SaveLedgerAsync(job).ConfigureAwait(false);
                return;
            }

            while (job.InFlightSlices.Count < MaxInFlightQueries && job.PendingSlices.Count > 0)
            {
                if (budgetExpired()) return;
                if (job.CancelRequested || (job.CancelRequested = await _state.IsCancelRequestedAsync(job.Id).ConfigureAwait(false)))
                {
                    return;
                }
                now = _utcNow();
                PruneSubmissionHistory(job, now);
                if (job.SubmissionTimestampsUtc.Count >= DailySubmissionBudget)
                {
                    job.SubmissionsPausedUntilUtc = job.SubmissionTimestampsUtc.Min().Add(SubmissionBudgetWindow);
                    await SaveLedgerAsync(job).ConfigureAwait(false);
                    Track(job, "submissionBudgetPaused");
                    return;
                }

                var slice = job.PendingSlices[0];
                job.PendingSlices.RemoveAt(0);
                slice.AttemptCount++;
                try
                {
                    await SubmitSliceAsync(job, slice, now).ConfigureAwait(false);
                }
                catch (CopilotAuditSearchThrottledException ex)
                {
                    slice.AttemptCount--;
                    job.PendingSlices.Insert(0, slice);
                    var pause = TimeSpan.FromSeconds(Math.Max(ex.RetryAfterSeconds ?? 0, (int)MinimumThrottlePause.TotalSeconds));
                    job.SubmissionsPausedUntilUtc = now.Add(pause);
                    job.LastErrorCode = CopilotAuditBackfillErrorCodes.QueryThrottled;
                    job.LastErrorDetail = "Microsoft Graph throttled Audit Search query submissions.";
                    await SaveLedgerAsync(job).ConfigureAwait(false);
                    Track(job, "throttled");
                    return;
                }
                catch (GraphHttpException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
                {
                    throw new CopilotAuditBackfillFatalException(CopilotAuditBackfillErrorCodes.QueryRejected, ex.GraphErrorCode ?? ex.StatusCode.ToString());
                }
                catch (GraphHttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RetryOrGap(job, slice, CopilotAuditBackfillErrorCodes.QueryFailed, DescribeForStorage(ex), resubmitLater: true);
                    var minutes = job.SubmitFailureBackoffMinutes <= 0
                        ? InitialSubmitFailureBackoffMinutes
                        : Math.Min(MaxSubmitFailureBackoffMinutes, job.SubmitFailureBackoffMinutes * 2);
                    job.SubmitFailureBackoffMinutes = minutes;
                    job.SubmissionsPausedUntilUtc = now.AddMinutes(minutes);
                    await SaveLedgerAsync(job).ConfigureAwait(false);
                    return;
                }

                job.SubmitFailureBackoffMinutes = 0;
                job.InFlightSlices.Add(slice);
                await SaveLedgerAsync(job).ConfigureAwait(false);
            }
        }

        private static bool MirrorLedger(CopilotAuditBackfillJob job, CopilotAuditBackfillSubmissionLedger ledger)
        {
            var shared = ledger.SubmissionTimestampsUtc ?? new List<DateTime>();
            var local = job.SubmissionTimestampsUtc ?? new List<DateTime>();
            // A successful POST can precede a failed ledger write. The saved job is a recovery copy.
            // Preserve multiplicity: several submissions can legitimately have the same timestamp.
            var merged = shared.GroupBy(t => t).Select(g => new { Time = g.Key, Count = g.Count() })
                .Concat(local.GroupBy(t => t).Select(g => new { Time = g.Key, Count = g.Count() }))
                .GroupBy(g => g.Time)
                .SelectMany(g => Enumerable.Repeat(g.Key, g.Max(x => x.Count)))
                .OrderBy(t => t).ToList();
            var pause = ledger.SubmissionsPausedUntilUtc;
            if (job.SubmissionsPausedUntilUtc.HasValue
                && (!pause.HasValue || job.SubmissionsPausedUntilUtc.Value > pause.Value))
            {
                pause = job.SubmissionsPausedUntilUtc;
            }
            var backoff = Math.Max(job.SubmitFailureBackoffMinutes, ledger.SubmitFailureBackoffMinutes);
            var recovered = merged.Count > shared.Count || pause != ledger.SubmissionsPausedUntilUtc
                || backoff > ledger.SubmitFailureBackoffMinutes;
            job.SubmissionTimestampsUtc = merged;
            job.SubmissionsPausedUntilUtc = pause;
            job.SubmitFailureBackoffMinutes = backoff;
            return recovered;
        }

        private Task SaveLedgerAsync(CopilotAuditBackfillJob job)
        {
            return _state.SaveSubmissionLedgerAsync(new CopilotAuditBackfillSubmissionLedger
            {
                SubmissionTimestampsUtc = job.SubmissionTimestampsUtc,
                SubmissionsPausedUntilUtc = job.SubmissionsPausedUntilUtc,
                SubmitFailureBackoffMinutes = job.SubmitFailureBackoffMinutes,
            });
        }

        private async Task SubmitSliceAsync(CopilotAuditBackfillJob job, CopilotAuditBackfillSlice slice, DateTime submittedUtc)
        {
            var submitted = await _source.SubmitQueryAsync(slice).ConfigureAwait(false);
            slice.QueryId = submitted.Id;
            slice.SubmittedUtc = submittedUtc;
            slice.RecordsNextLink = null;
            slice.ImportingRecords = false;
            slice.HasSeenRecords = false;
            slice.HasRecognisedRecords = false;
            slice.QueryTruncated = false;
            job.SlicesSubmitted++;
            job.SubmissionTimestampsUtc.Add(submittedUtc);
            _logger?.LogInformation($"Copilot audit backfill {job.Id}: submitted Audit Search query for {slice.StartUtc:o} to {slice.EndUtc:o} (attempt {slice.AttemptCount}).");
        }

        private async Task<bool> PollInFlightAsync(CopilotAuditBackfillJob job, Func<bool> budgetExpired)
        {
            var progressed = false;
            var remaining = new List<CopilotAuditBackfillSlice>();
            foreach (var slice in job.InFlightSlices)
            {
                if (budgetExpired() || job.CancelRequested
                    || (job.CancelRequested = await _state.IsCancelRequestedAsync(job.Id).ConfigureAwait(false)))
                {
                    remaining.Add(slice);
                    continue;
                }
                try
                {
                    if (!slice.ImportingRecords)
                    {
                        var query = await _source.GetQueryAsync(slice.QueryId).ConfigureAwait(false);
                        if (!query.IsTerminal)
                        {
                            if (slice.SubmittedUtc.HasValue
                                && _utcNow().Subtract(slice.SubmittedUtc.Value) >= MaxUnknownStatusAge)
                            {
                                RetryOrGap(job, slice, CopilotAuditBackfillErrorCodes.QueryFailed, query.Status ?? "unknownFutureValue", resubmitLater: true);
                                progressed = true;
                                continue;
                            }

                            remaining.Add(slice);
                            continue;
                        }

                        progressed = true;
                        if (!query.Succeeded)
                        {
                            RetryOrGap(job, slice, CopilotAuditBackfillErrorCodes.QueryFailed, query.Error ?? query.Status, resubmitLater: true);
                            continue;
                        }

                        if (query.IsTruncated)
                        {
                            if (CopilotAuditBackfillSlicer.CanSplitForTruncation(slice))
                            {
                                // The partial result is discarded: the smaller slices re-read this whole window.
                                var split = CopilotAuditBackfillSlicer.SplitForTruncation(slice);
                                job.PendingSlices.InsertRange(0, split);
                                job.SlicesSplit++;
                                _logger?.LogWarning($"Copilot audit backfill {job.Id}: Audit Search query was truncated; split its slice into {split.Count} smaller slice(s).");
                                Track(job, "truncated");
                                continue;
                            }

                            // Nothing narrower to re-query, so keep what Graph did return rather than discarding it.
                            // The slice is still recorded as incomplete once those records have been read.
                            slice.QueryTruncated = true;
                            _logger?.LogWarning($"Copilot audit backfill {job.Id}: the Audit Search query for {slice.StartUtc:o} to {slice.EndUtc:o} was still truncated at the narrowest slice; importing the records it returned, then recording the slice as incomplete.");
                        }

                        slice.ImportingRecords = true;
                    }

                    if (budgetExpired())
                    {
                        remaining.Add(slice);
                        continue;
                    }

                    var imported = await ImportRecordsAsync(job, slice, budgetExpired).ConfigureAwait(false);
                    job.RecordsSeen += imported.Seen;
                    job.RecordsImported += imported.Imported;
                    job.RecordsAlreadyPresent += imported.AlreadyPresent;
                    slice.HasSeenRecords = slice.HasSeenRecords == true || imported.Seen > 0;
                    slice.HasRecognisedRecords |= imported.Mapped > 0;
                    if (imported.Paused || job.CancelRequested)
                    {
                        remaining.Add(slice);
                        continue;
                    }
                    if (slice.HasSeenRecords == true && !slice.HasRecognisedRecords)
                    {
                        throw new CopilotAuditBackfillFatalException(CopilotAuditBackfillErrorCodes.UnrecognisedAuditData, "Graph returned Audit Search records, but their auditData shape was not recognised as CopilotInteraction.");
                    }

                    slice.ImportingRecords = false;
                    slice.RecordsNextLink = null;
                    if (slice.QueryTruncated)
                    {
                        _logger?.LogInformation($"Copilot audit backfill {job.Id}: imported {imported.Imported:N0} of {imported.Seen:N0} record(s) from a truncated Audit Search query; {imported.AlreadyPresent:N0} already existed.");
                        RecordGap(job, slice, CopilotAuditBackfillErrorCodes.QueryTruncated,
                            "The narrowest Audit Search slice was still truncated at the service record limit; the records it returned were imported, the rest of its window was not.",
                            incomplete: true);
                        continue;
                    }

                    job.SlicesCompleted++;
                    // Retain whole-day evidence if a later slice aborts this poll before queue publication.
                    if ((slice.EndUtc - slice.StartUtc).TotalHours >= 23)
                    {
                        var dayKey = CopilotAuditBackfillSlicer.DayKey(slice.StartUtc);
                        if (!job.CompletedDays.Contains(dayKey)) job.CompletedDays.Add(dayKey);
                    }
                    _logger?.LogInformation($"Copilot audit backfill {job.Id}: imported {imported.Imported:N0} of {imported.Seen:N0} record(s) from an Audit Search query; {imported.AlreadyPresent:N0} already existed.");
                }
                catch (GraphHttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw;
                }
                catch (CopilotAuditBackfillFatalException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, $"Copilot audit backfill {job.Id}: slice processing failed; it will retry or record a gap.");
                    progressed = true;
                    RetryOrGap(job, slice, CopilotAuditBackfillErrorCodes.QueryFailed, DescribeForStorage(ex), resubmitLater: true);
                }
            }

            job.InFlightSlices = remaining;
            job.CompletedDays = job.GetCompletedDays().ToList();
            return progressed;
        }

        private void RetryOrGap(CopilotAuditBackfillJob job, CopilotAuditBackfillSlice slice, string errorCode, string detail, bool resubmitLater)
        {
            if (slice.AttemptCount < MaxSliceAttempts && resubmitLater)
            {
                slice.QueryId = null;
                slice.SubmittedUtc = null;
                slice.RecordsNextLink = null;
                slice.ImportingRecords = false;
                slice.HasSeenRecords = false;
                slice.HasRecognisedRecords = false;
                slice.QueryTruncated = false;
                job.PendingSlices.Add(slice);
                job.LastErrorCode = errorCode;
                job.LastErrorDetail = Truncate(detail);
                _logger?.LogWarning($"Copilot audit backfill {job.Id}: slice {slice.StartUtc:o}-{slice.EndUtc:o} failed on attempt {slice.AttemptCount}; it will retry.");
                return;
            }

            RecordGap(job, slice, errorCode, detail, incomplete: false);
        }

        private void RecordGap(CopilotAuditBackfillJob job, CopilotAuditBackfillSlice slice, string errorCode, string detail, bool incomplete)
        {
            var gap = new CopilotAuditBackfillGap
            {
                StartUtc = slice.StartUtc,
                EndUtc = slice.EndUtc,
                Day = CopilotAuditBackfillSlicer.DayKey(slice.StartUtc),
                ErrorCode = errorCode,
                Detail = Truncate(detail),
                Attempts = slice.AttemptCount,
                Incomplete = incomplete,
            };
            job.Gaps.Add(gap);
            job.LastErrorCode = errorCode;
            job.LastErrorDetail = gap.Detail;
            _logger?.LogWarning($"Copilot audit backfill {job.Id}: recorded {(incomplete ? "incomplete" : "failed")} slice {slice.StartUtc:o}-{slice.EndUtc:o} after {slice.AttemptCount} attempt(s).");
            Track(job, incomplete ? "sliceIncomplete" : "sliceFailed");
        }

        private async Task<ImportOutcome> ImportRecordsAsync(CopilotAuditBackfillJob job, CopilotAuditBackfillSlice slice, Func<bool> budgetExpired)
        {
            if (!slice.HasSeenRecords.HasValue)
            {
                if (!string.IsNullOrEmpty(slice.RecordsNextLink))
                    _logger?.LogInformation("Replaying a legacy Copilot audit backfill query to recover mapping evidence; existing SQL records remain de-duplicated.");
                slice.RecordsNextLink = null;
                slice.HasSeenRecords = false;
                slice.HasRecognisedRecords = false;
            }
            var outcome = new ImportOutcome();
            var mapped = new WebActivityReportSet();
            var next = slice.RecordsNextLink;
            do
            {
                CopilotAuditSearchRecordPage page;
                try
                {
                    page = await _source.GetRecordsAsync(slice.QueryId, next).ConfigureAwait(false);
                }
                catch (GraphHttpException ex) when (!string.IsNullOrEmpty(next)
                    && (ex.StatusCode == HttpStatusCode.BadRequest || ex.StatusCode == HttpStatusCode.Gone))
                {
                    next = null;
                    slice.RecordsNextLink = null;
                    page = await _source.GetRecordsAsync(slice.QueryId, null).ConfigureAwait(false);
                }

                foreach (var record in page.Records)
                {
                    outcome.Seen++;
                    var mapping = CopilotAuditSearchRecordMapper.Map(record, _logger);
                    if (mapping.Content == null)
                    {
                        AddMappingFailure(job, mapping.ErrorCode ?? "unknown");
                        if (!job.Gaps.Any(g => g.StartUtc == slice.StartUtc && g.EndUtc == slice.EndUtc
                            && g.ErrorCode == CopilotAuditBackfillErrorCodes.UnrecognisedAuditData))
                        {
                            RecordGap(job, slice, CopilotAuditBackfillErrorCodes.UnrecognisedAuditData,
                                "One or more Audit Search records could not be mapped; inspect mapping failure counts.", incomplete: true);
                        }
                        continue;
                    }

                    outcome.Mapped++;
                    mapped.Add(mapping.Content);
                    if (mapped.Count >= MaxRecordsPerCommit)
                    {
                        var committed = await CommitBatchAsync(mapped).ConfigureAwait(false);
                        outcome.Imported += committed.Imported;
                        outcome.AlreadyPresent += committed.AlreadyPresent;
                        mapped = new WebActivityReportSet();
                    }
                }
                next = page.NextLink;
                slice.RecordsNextLink = next;

                job.CancelRequested = await _state.IsCancelRequestedAsync(job.Id).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(next) && (budgetExpired() || job.CancelRequested))
                {
                    if (mapped.Count > 0)
                    {
                        var committed = await CommitBatchAsync(mapped).ConfigureAwait(false);
                        outcome.Imported += committed.Imported;
                        outcome.AlreadyPresent += committed.AlreadyPresent;
                    }
                    outcome.Paused = true;
                    return outcome;
                }
            } while (!string.IsNullOrEmpty(next));

            if (mapped.Count > 0)
            {
                var committed = await CommitBatchAsync(mapped).ConfigureAwait(false);
                outcome.Imported += committed.Imported;
                outcome.AlreadyPresent += committed.AlreadyPresent;
            }
            return outcome;
        }

        private async Task<CommitOutcome> CommitBatchAsync(WebActivityReportSet mapped)
        {
            // Only interactions saved completely are dropped. One whose chat row is there but whose child rows are not - left by
            // a merge that failed partway, or a DLP commit that failed after it - is committed again, so a retried or re-read
            // slice converges instead of skipping it for good.
            var existing = await _existingEvents.GetCompletelySavedIdsAsync(mapped).ConfigureAwait(false);
            var toCommit = existing.Count == 0
                ? mapped
                : new WebActivityReportSet(mapped.Where(a => !existing.Contains(a.Id)));
            if (toCommit.Count == 0)
            {
                return new CommitOutcome { AlreadyPresent = mapped.Count };
            }

            toCommit.RecoverMissingCopilotMetadata = true;
            var stats = await _persistence.CommitAll(toCommit).ConfigureAwait(false);
            return new CommitOutcome { Imported = stats.Imported, AlreadyPresent = mapped.Count - toCommit.Count };
        }

        private Task<CopilotAuditBackfillJob> FinishAsync(CopilotAuditBackfillJob job, string state, string errorCode, string detail)
        {
            if (state == CopilotAuditBackfillStates.CompletedWithGaps && errorCode == null && job.Gaps.Count > 0)
            {
                var gap = job.Gaps[job.Gaps.Count - 1];
                errorCode = gap.ErrorCode;
                detail = gap.Detail;
            }
            job.State = state;
            job.CompletedDays = job.GetCompletedDays().ToList();
            job.CompletedUtc = _utcNow();
            job.LastErrorCode = errorCode;
            job.LastErrorDetail = Truncate(detail);
            Track(job, state);
            TrackHealth(job);
            return SaveAndReturnAsync(job);
        }

        private async Task<CopilotAuditBackfillJob> SaveAndReturnAsync(CopilotAuditBackfillJob job)
        {
            await _state.SaveAsync(job).ConfigureAwait(false);
            return job;
        }

        private static string ToPermissionStatus(AppTokenPermissionAccess access)
        {
            switch (access)
            {
                case AppTokenPermissionAccess.Granted: return CopilotAuditBackfillPermissionStates.Granted;
                case AppTokenPermissionAccess.NotGranted: return CopilotAuditBackfillPermissionStates.Missing;
                case AppTokenPermissionAccess.NoIdentityToInspect: return CopilotAuditBackfillPermissionStates.NoIdentity;
                default: return CopilotAuditBackfillPermissionStates.Unknown;
            }
        }

        private void AddMappingFailure(CopilotAuditBackfillJob job, string errorCode)
        {
            if (job.MappingFailureCounts == null)
            {
                job.MappingFailureCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            }
            job.MappingFailureCounts[errorCode] = job.MappingFailureCounts.TryGetValue(errorCode, out var count) ? count + 1 : 1;
        }

        private static void PruneSubmissionHistory(CopilotAuditBackfillJob job, DateTime nowUtc)
        {
            job.SubmissionTimestampsUtc = (job.SubmissionTimestampsUtc ?? new List<DateTime>())
                .Where(t => t > nowUtc.Subtract(SubmissionBudgetWindow))
                .OrderBy(t => t)
                .ToList();
        }

        private static string Truncate(string value)
            => string.IsNullOrEmpty(value) || value.Length <= 1000 ? value : value.Substring(0, 1000);

        private static string DescribeForStorage(Exception ex)
        {
            if (ex is GraphHttpException graph)
            {
                return $"Graph HTTP {(int)graph.StatusCode} {graph.StatusCode}; errorCode={graph.GraphErrorCode ?? "unknown"}";
            }

            return ex == null ? null : ex.GetType().Name;
        }

        private sealed class ImportOutcome { public int Seen; public int Mapped; public int Imported; public int AlreadyPresent; public bool Paused; }
        private sealed class CommitOutcome { public int Imported; public int AlreadyPresent; }

        private sealed class CopilotAuditBackfillFatalException : Exception
        {
            public CopilotAuditBackfillFatalException(string code, string detail) : base(detail) { Code = code; }
            public string Code { get; }
        }

        private void Track(CopilotAuditBackfillJob job, string outcome)
        {
            var analytics = _logger as AnalyticsLogger;
            if (analytics == null || job == null) return;

            var metrics = new Dictionary<string, double>
            {
                { "PendingSlices", job.PendingSlices?.Count ?? 0 },
                { "InFlightSlices", job.InFlightSlices?.Count ?? 0 },
                { "SlicesCompleted", job.SlicesCompleted },
                { "Gaps", job.Gaps?.Count ?? 0 },
                { "RecordsSeen", job.RecordsSeen },
                { "RecordsImported", job.RecordsImported },
                { "RecordsAlreadyPresent", job.RecordsAlreadyPresent },
            };
            if (job.MappingFailureCounts != null)
            {
                foreach (var failure in job.MappingFailureCounts)
                {
                    metrics["MappingFailure_" + failure.Key] = failure.Value;
                }
            }

            analytics.TrackEvent(
                AnalyticsLogger.AnalyticsEvent.CopilotAuditBackfill,
                new Dictionary<string, string>
                {
                    { "Outcome", outcome ?? string.Empty },
                    { "State", job.State ?? string.Empty },
                    { "ErrorCode", job.LastErrorCode ?? string.Empty },
                    { "PermissionStatus", job.PermissionStatus ?? string.Empty },
                },
                metrics);
        }

        private void TrackHealth(CopilotAuditBackfillJob job)
        {
            var analytics = _logger as AnalyticsLogger;
            if (analytics == null || job == null) return;

            var health = ResolveHealth(job, _utcNow());
            analytics.TrackHealthCheck(HealthComponent.CopilotAuditBackfill, health.Status, health.Detail, reasonKey: health.ReasonKey);
        }

        internal static CopilotAuditBackfillHealth ResolveHealth(CopilotAuditBackfillJob job, DateTime? nowUtc = null)
        {
            HealthStatus status;
            string reasonKey;
            string detail;
            var terminalAge = job.CompletedUtc.HasValue
                ? (nowUtc ?? DateTime.UtcNow).Subtract(job.CompletedUtc.Value)
                : TimeSpan.Zero;
            switch (job.State)
            {
                case CopilotAuditBackfillStates.Running:
                case CopilotAuditBackfillStates.Queued:
                    status = HealthStatus.Healthy;
                    reasonKey = "copilotAuditBackfill.running";
                    detail = "Copilot audit backfill is running.";
                    break;
                case CopilotAuditBackfillStates.CompletedWithGaps:
                    if (terminalAge > TerminalDegradedHealthWindow)
                    {
                        status = HealthStatus.Healthy;
                        reasonKey = "copilotAuditBackfill.lastJobOld";
                        detail = "The last Copilot audit backfill completed with issues more than seven days ago.";
                    }
                    else
                    {
                        status = HealthStatus.Degraded;
                        reasonKey = "copilotAuditBackfill.completedWithGaps";
                        detail = "Copilot audit backfill completed with failed or incomplete slices.";
                    }
                    break;
                case CopilotAuditBackfillStates.Failed:
                    if (terminalAge > TerminalDegradedHealthWindow)
                    {
                        status = HealthStatus.Healthy;
                        reasonKey = "copilotAuditBackfill.lastJobOld";
                        detail = "The last Copilot audit backfill failure is more than seven days old.";
                    }
                    else
                    {
                        status = HealthStatus.Degraded;
                        reasonKey = job.LastErrorCode == CopilotAuditBackfillErrorCodes.MissingPermission
                            ? "copilotAuditBackfill.missingPermission"
                            : "copilotAuditBackfill.failed";
                        detail = job.LastErrorCode == CopilotAuditBackfillErrorCodes.MissingPermission
                            ? "Copilot audit backfill cannot run because AuditLogsQuery.Read.All is missing."
                            : "Copilot audit backfill failed.";
                    }
                    break;
                case CopilotAuditBackfillStates.Completed:
                    status = HealthStatus.Healthy;
                    reasonKey = "copilotAuditBackfill.completed";
                    detail = "Copilot audit backfill completed.";
                    break;
                case CopilotAuditBackfillStates.Cancelled:
                    status = HealthStatus.Healthy;
                    reasonKey = "copilotAuditBackfill.cancelled";
                    detail = "Copilot audit backfill was cancelled.";
                    break;
                default:
                    status = HealthStatus.Healthy;
                    reasonKey = "copilotAuditBackfill.healthy";
                    detail = "No Copilot audit backfill is currently blocked.";
                    break;
            }

            return new CopilotAuditBackfillHealth(status, reasonKey, detail);
        }
    }

    internal struct CopilotAuditBackfillHealth
    {
        public CopilotAuditBackfillHealth(HealthStatus status, string reasonKey, string detail)
        {
            Status = status;
            ReasonKey = reasonKey;
            Detail = detail;
        }

        public HealthStatus Status { get; }
        public string ReasonKey { get; }
        public string Detail { get; }
    }
}