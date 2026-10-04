using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAuditBackfill;
using DataUtils;
using DataUtils.Health;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.AuditBackfill
{
    /// <summary>
    /// Advances an admin-requested Microsoft Graph Audit Search backfill by a bounded amount during a normal import cycle.
    /// </summary>
    public sealed class CopilotAuditBackfillImporter
    {
        internal const int MaxInFlightQueries = 4;
        internal const int MaxSliceAttempts = 3;
        internal const int MaxRecordsPerCommit = 5000;
        internal static readonly TimeSpan MaxCycleBudget = TimeSpan.FromMinutes(8);
        internal static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(20);

        private readonly CopilotAuditBackfillStateStore _state;
        private readonly ICopilotAuditSearchSource _source;
        private readonly IActivityReportPersistenceManager _persistence;
        private readonly AppConfig _settings;
        private readonly ILogger _logger;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<TimeSpan, Task> _delay;

        public CopilotAuditBackfillImporter(CopilotAuditBackfillStateStore state, ICopilotAuditSearchSource source,
            IActivityReportPersistenceManager persistence, AppConfig settings, ILogger logger, Func<DateTime> utcNow = null, Func<TimeSpan, Task> delay = null)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _delay = delay ?? (d => Task.Delay(d));
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
            var sw = Stopwatch.StartNew();
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
                while (sw.Elapsed < MaxCycleBudget)
                {
                    if (job.CancelRequested || await _state.IsCancelRequestedAsync(job.Id).ConfigureAwait(false))
                    {
                        return await FinishAsync(job, CopilotAuditBackfillStates.Cancelled, null, null).ConfigureAwait(false);
                    }

                    await TopUpQueriesAsync(job).ConfigureAwait(false);
                    if (job.InFlightSlices.Count == 0)
                    {
                        var final = job.Gaps.Count > 0 ? CopilotAuditBackfillStates.CompletedWithGaps : CopilotAuditBackfillStates.Completed;
                        return await FinishAsync(job, final, null, null).ConfigureAwait(false);
                    }

                    var progressed = await PollInFlightAsync(job).ConfigureAwait(false);
                    await _state.SaveAsync(job).ConfigureAwait(false);
                    TrackHealth(job);

                    if (sw.Elapsed >= MaxCycleBudget)
                    {
                        break;
                    }

                    if (!progressed && job.InFlightSlices.Count > 0)
                    {
                        var remaining = MaxCycleBudget - sw.Elapsed;
                        if (remaining <= PollDelay)
                        {
                            break;
                        }
                        await _delay(PollDelay).ConfigureAwait(false);
                    }
                }

                return job;
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
            if (job.CurrentSlice != null)
            {
                job.InFlightSlices.Add(job.CurrentSlice);
                job.CurrentSlice = null;
            }
        }

        private async Task TopUpQueriesAsync(CopilotAuditBackfillJob job)
        {
            while (job.InFlightSlices.Count < MaxInFlightQueries && job.PendingSlices.Count > 0)
            {
                var slice = job.PendingSlices[0];
                job.PendingSlices.RemoveAt(0);
                try
                {
                    await SubmitSliceAsync(job, slice).ConfigureAwait(false);
                    job.InFlightSlices.Add(slice);
                }
                catch (GraphHttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RetryOrGap(job, slice, CopilotAuditBackfillErrorCodes.QueryFailed, ex.Message, resubmitLater: true);
                }
            }
        }

        private async Task SubmitSliceAsync(CopilotAuditBackfillJob job, CopilotAuditBackfillSlice slice)
        {
            slice.AttemptCount++;
            var submitted = await _source.SubmitQueryAsync(slice).ConfigureAwait(false);
            slice.QueryId = submitted.Id;
            slice.SubmittedUtc = _utcNow();
            job.SlicesSubmitted++;
            _logger?.LogInformation($"Copilot audit backfill {job.Id}: submitted Audit Search query for {slice.StartUtc:o} to {slice.EndUtc:o} (attempt {slice.AttemptCount}).");
        }

        private async Task<bool> PollInFlightAsync(CopilotAuditBackfillJob job)
        {
            var progressed = false;
            var remaining = new List<CopilotAuditBackfillSlice>();
            foreach (var slice in job.InFlightSlices)
            {
                try
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
                        if (slice.SplitLevel >= 1)
                        {
                            RecordGap(job, slice, CopilotAuditBackfillErrorCodes.QueryTruncated, "An hourly Audit Search slice was still truncated at the service limit.", incomplete: true);
                            continue;
                        }

                        var split = CopilotAuditBackfillSlicer.SplitIntoHours(slice);
                        job.PendingSlices.InsertRange(0, split);
                        job.SlicesSplit++;
                        _logger?.LogWarning($"Copilot audit backfill {job.Id}: Audit Search query was truncated; split its day into {split.Count} hour slice(s).");
                        Track(job, "truncated");
                        continue;
                    }

                    var imported = await ImportRecordsAsync(slice.QueryId).ConfigureAwait(false);
                    job.RecordsSeen += imported.Seen;
                    job.RecordsImported += imported.Imported;
                    job.SlicesCompleted++;
                    if ((slice.EndUtc - slice.StartUtc).TotalHours >= 23)
                    {
                        var dayKey = CopilotAuditBackfillSlicer.DayKey(slice.StartUtc);
                        if (!job.CompletedDays.Contains(dayKey)) job.CompletedDays.Add(dayKey);
                    }
                    _logger?.LogInformation($"Copilot audit backfill {job.Id}: imported {imported.Imported:N0} of {imported.Seen:N0} record(s) from an Audit Search query.");
                }
                catch (GraphHttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
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

        private async Task<ImportOutcome> ImportRecordsAsync(string queryId)
        {
            var seen = 0;
            var imported = 0;
            var mapped = new WebActivityReportSet();
            string next = null;
            do
            {
                var page = await _source.GetRecordsAsync(queryId, next).ConfigureAwait(false);
                foreach (var record in page.Records)
                {
                    seen++;
                    var mapping = CopilotAuditSearchRecordMapper.Map(record, _logger);
                    if (mapping.Content == null)
                    {
                        continue;
                    }

                    mapped.Add(mapping.Content);
                    if (mapped.Count >= MaxRecordsPerCommit)
                    {
                        imported += await CommitBatchAsync(mapped).ConfigureAwait(false);
                        mapped = new WebActivityReportSet();
                    }
                }
                next = page.NextLink;
            } while (!string.IsNullOrEmpty(next));

            if (mapped.Count > 0)
            {
                imported += await CommitBatchAsync(mapped).ConfigureAwait(false);
            }
            return new ImportOutcome { Seen = seen, Imported = imported };
        }

        private async Task<int> CommitBatchAsync(WebActivityReportSet mapped)
        {
            var stats = await _persistence.CommitAll(mapped).ConfigureAwait(false);
            return stats.Imported;
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

        private static string Truncate(string value)
            => string.IsNullOrEmpty(value) || value.Length <= 1000 ? value : value.Substring(0, 1000);

        private sealed class ImportOutcome { public int Seen; public int Imported; }

        private void Track(CopilotAuditBackfillJob job, string outcome)
        {
            var analytics = _logger as AnalyticsLogger;
            if (analytics == null || job == null) return;

            analytics.TrackEvent(
                AnalyticsLogger.AnalyticsEvent.CopilotAuditBackfill,
                new Dictionary<string, string>
                {
                    { "Outcome", outcome ?? string.Empty },
                    { "State", job.State ?? string.Empty },
                    { "ErrorCode", job.LastErrorCode ?? string.Empty },
                    { "PermissionStatus", job.PermissionStatus ?? string.Empty },
                },
                new Dictionary<string, double>
                {
                    { "PendingSlices", job.PendingSlices?.Count ?? 0 },
                    { "InFlightSlices", job.InFlightSlices?.Count ?? 0 },
                    { "SlicesCompleted", job.SlicesCompleted },
                    { "Gaps", job.Gaps?.Count ?? 0 },
                    { "RecordsSeen", job.RecordsSeen },
                    { "RecordsImported", job.RecordsImported },
                });
        }

        private void TrackHealth(CopilotAuditBackfillJob job)
        {
            var analytics = _logger as AnalyticsLogger;
            if (analytics == null || job == null) return;

            HealthStatus status;
            string reasonKey;
            string detail;
            switch (job.State)
            {
                case CopilotAuditBackfillStates.Running:
                case CopilotAuditBackfillStates.Queued:
                    status = HealthStatus.Degraded;
                    reasonKey = "copilotAuditBackfill.running";
                    detail = "Copilot audit backfill is running.";
                    break;
                case CopilotAuditBackfillStates.CompletedWithGaps:
                    status = HealthStatus.Degraded;
                    reasonKey = "copilotAuditBackfill.completedWithGaps";
                    detail = "Copilot audit backfill completed with failed or incomplete slices.";
                    break;
                case CopilotAuditBackfillStates.Failed:
                    status = HealthStatus.Unhealthy;
                    reasonKey = job.LastErrorCode == CopilotAuditBackfillErrorCodes.MissingPermission
                        ? "copilotAuditBackfill.missingPermission"
                        : "copilotAuditBackfill.failed";
                    detail = job.LastErrorCode == CopilotAuditBackfillErrorCodes.MissingPermission
                        ? "Copilot audit backfill cannot run because AuditLogsQuery.Read.All is missing."
                        : "Copilot audit backfill failed.";
                    break;
                default:
                    status = HealthStatus.Healthy;
                    reasonKey = "copilotAuditBackfill.healthy";
                    detail = "No Copilot audit backfill is currently blocked.";
                    break;
            }

            analytics.TrackHealthCheck(HealthComponent.CopilotAuditBackfill, status, detail, reasonKey: reasonKey);
        }
    }
}
