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
using WebJob.Office365ActivityImporter.Engine.Entities;

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
    /// Advances an admin-requested Microsoft Graph Audit Search backfill by a bounded amount during a normal import cycle.
    /// </summary>
    public interface ICopilotAuditBackfillExistingEventFilter
    {
        Task<HashSet<Guid>> GetExistingIdsAsync(IEnumerable<Guid> ids);
    }

    public sealed class SqlCopilotAuditBackfillExistingEventFilter : ICopilotAuditBackfillExistingEventFilter
    {
        private const int ChunkSize = 1000;
        private readonly Func<AnalyticsEntitiesContext> _createContext;

        public SqlCopilotAuditBackfillExistingEventFilter(Func<AnalyticsEntitiesContext> createContext = null)
        {
            _createContext = createContext ?? (() => new AnalyticsEntitiesContext());
        }

        public async Task<HashSet<Guid>> GetExistingIdsAsync(IEnumerable<Guid> ids)
        {
            var existing = new HashSet<Guid>();
            var list = (ids ?? Enumerable.Empty<Guid>()).Distinct().ToList();
            if (list.Count == 0) return existing;

            using (var db = _createContext())
            {
                for (var i = 0; i < list.Count; i += ChunkSize)
                {
                    var chunk = list.GetRange(i, Math.Min(ChunkSize, list.Count - i));
                    var found = await db.AuditEventsCommon
                        .Where(e => chunk.Contains(e.Id))
                        .Select(e => e.Id)
                        .ToListAsync()
                        .ConfigureAwait(false);
                    foreach (var id in found) existing.Add(id);
                }
            }

            return existing;
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

                    await TopUpQueriesAsync(job).ConfigureAwait(false);
                    if (job.InFlightSlices.Count == 0)
                    {
                        if (job.PendingSlices.Count > 0
                            && job.SubmissionsPausedUntilUtc.HasValue
                            && job.SubmissionsPausedUntilUtc.Value > _utcNow())
                        {
                            await _state.SaveAsync(job).ConfigureAwait(false);
                            TrackHealth(job);
                            return job;
                        }

                        var final = job.Gaps.Count > 0 ? CopilotAuditBackfillStates.CompletedWithGaps : CopilotAuditBackfillStates.Completed;
                        return await FinishAsync(job, final, null, null).ConfigureAwait(false);
                    }

                    var progressed = await PollInFlightAsync(job, () => elapsed() >= MaxCycleBudget).ConfigureAwait(false);
                    await _state.SaveAsync(job).ConfigureAwait(false);
                    TrackHealth(job);

                    if (elapsed() >= MaxCycleBudget)
                    {
                        break;
                    }

                    if (!progressed && job.InFlightSlices.Count > 0)
                    {
                        var remaining = MaxCycleBudget - elapsed();
                        if (remaining <= PollDelay)
                        {
                            break;
                        }
                        await _delay(PollDelay).ConfigureAwait(false);
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

        private async Task TopUpQueriesAsync(CopilotAuditBackfillJob job)
        {
            var now = _utcNow();
            PruneSubmissionHistory(job, now);
            if (job.SubmissionsPausedUntilUtc.HasValue && job.SubmissionsPausedUntilUtc.Value > now)
            {
                return;
            }

            while (job.InFlightSlices.Count < MaxInFlightQueries && job.PendingSlices.Count > 0)
            {
                now = _utcNow();
                PruneSubmissionHistory(job, now);
                if (job.SubmissionTimestampsUtc.Count >= DailySubmissionBudget)
                {
                    job.SubmissionsPausedUntilUtc = job.SubmissionTimestampsUtc.Min().Add(SubmissionBudgetWindow);
                    Track(job, "submissionBudgetPaused");
                    return;
                }

                var slice = job.PendingSlices[0];
                job.PendingSlices.RemoveAt(0);
                slice.AttemptCount++;
                try
                {
                    await SubmitSliceAsync(job, slice, now).ConfigureAwait(false);
                    job.InFlightSlices.Add(slice);
                }
                catch (CopilotAuditSearchThrottledException ex)
                {
                    slice.AttemptCount--;
                    job.PendingSlices.Insert(0, slice);
                    var pause = TimeSpan.FromSeconds(Math.Max(ex.RetryAfterSeconds ?? 0, (int)MinimumThrottlePause.TotalSeconds));
                    job.SubmissionsPausedUntilUtc = now.Add(pause);
                    job.LastErrorCode = CopilotAuditBackfillErrorCodes.QueryThrottled;
                    job.LastErrorDetail = "Microsoft Graph throttled Audit Search query submissions.";
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
                    RetryOrGap(job, slice, CopilotAuditBackfillErrorCodes.QueryFailed, ex.Message, resubmitLater: true);
                    return;
                }
            }
        }

        private async Task SubmitSliceAsync(CopilotAuditBackfillJob job, CopilotAuditBackfillSlice slice, DateTime submittedUtc)
        {
            var submitted = await _source.SubmitQueryAsync(slice).ConfigureAwait(false);
            slice.QueryId = submitted.Id;
            slice.SubmittedUtc = submittedUtc;
            slice.RecordsNextLink = null;
            slice.ImportingRecords = false;
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
                try
                {
                    if (!slice.ImportingRecords)
                    {
                        var query = await _source.GetQueryAsync(slice.QueryId).ConfigureAwait(false);
                        if (!query.IsTerminal)
                        {
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
                            if (slice.SplitLevel >= 2)
                            {
                                RecordGap(job, slice, CopilotAuditBackfillErrorCodes.QueryTruncated, "An hourly Audit Search slice was still truncated at the service limit.", incomplete: true);
                                continue;
                            }

                            var split = CopilotAuditBackfillSlicer.SplitForTruncation(slice);
                            job.PendingSlices.InsertRange(0, split);
                            job.SlicesSplit++;
                            _logger?.LogWarning($"Copilot audit backfill {job.Id}: Audit Search query was truncated; split its slice into {split.Count} smaller slice(s).");
                            Track(job, "truncated");
                            continue;
                        }

                        slice.ImportingRecords = true;
                    }

                    var imported = await ImportRecordsAsync(job, slice, budgetExpired).ConfigureAwait(false);
                    job.RecordsSeen += imported.Seen;
                    job.RecordsImported += imported.Imported;
                    job.RecordsAlreadyPresent += imported.AlreadyPresent;
                    if (imported.Seen > 0 && imported.Mapped == 0)
                    {
                        throw new CopilotAuditBackfillFatalException(CopilotAuditBackfillErrorCodes.UnrecognisedAuditData, "Graph returned Audit Search records, but their auditData shape was not recognised as CopilotInteraction.");
                    }
                    if (imported.Paused)
                    {
                        remaining.Add(slice);
                        continue;
                    }

                    slice.ImportingRecords = false;
                    slice.RecordsNextLink = null;
                    job.SlicesCompleted++;
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
                    progressed = true;
                    RetryOrGap(job, slice, CopilotAuditBackfillErrorCodes.QueryFailed, ex.Message, resubmitLater: true);
                }
            }

            job.InFlightSlices = remaining;
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
                catch (GraphHttpException) when (!string.IsNullOrEmpty(next))
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

                if (!string.IsNullOrEmpty(next) && budgetExpired())
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
            var existing = await _existingEvents.GetExistingIdsAsync(mapped.Select(a => a.Id)).ConfigureAwait(false);
            var toCommit = existing.Count == 0
                ? mapped
                : new WebActivityReportSet(mapped.Where(a => !existing.Contains(a.Id)));
            if (toCommit.Count == 0)
            {
                return new CommitOutcome { AlreadyPresent = mapped.Count };
            }

            var stats = await _persistence.CommitAll(toCommit).ConfigureAwait(false);
            return new CommitOutcome { Imported = stats.Imported, AlreadyPresent = mapped.Count - toCommit.Count };
        }

        private Task<CopilotAuditBackfillJob> FinishAsync(CopilotAuditBackfillJob job, string state, string errorCode, string detail)
        {
            job.State = state;
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

            var health = ResolveHealth(job);
            analytics.TrackHealthCheck(HealthComponent.CopilotAuditBackfill, health.Status, health.Detail, reasonKey: health.ReasonKey);
        }

        internal static CopilotAuditBackfillHealth ResolveHealth(CopilotAuditBackfillJob job)
        {
            HealthStatus status;
            string reasonKey;
            string detail;
            switch (job.State)
            {
                case CopilotAuditBackfillStates.Running:
                case CopilotAuditBackfillStates.Queued:
                    status = HealthStatus.Healthy;
                    reasonKey = "copilotAuditBackfill.running";
                    detail = "Copilot audit backfill is running.";
                    break;
                case CopilotAuditBackfillStates.CompletedWithGaps:
                    status = HealthStatus.Degraded;
                    reasonKey = "copilotAuditBackfill.completedWithGaps";
                    detail = "Copilot audit backfill completed with failed or incomplete slices.";
                    break;
                case CopilotAuditBackfillStates.Failed:
                    status = HealthStatus.Degraded;
                    reasonKey = job.LastErrorCode == CopilotAuditBackfillErrorCodes.MissingPermission
                        ? "copilotAuditBackfill.missingPermission"
                        : "copilotAuditBackfill.failed";
                    detail = job.LastErrorCode == CopilotAuditBackfillErrorCodes.MissingPermission
                        ? "Copilot audit backfill cannot run because AuditLogsQuery.Read.All is missing."
                        : "Copilot audit backfill failed.";
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