using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAuditBackfill;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Linq;
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
        internal const int MaxSlicesPerCycle = 3;
        internal static readonly TimeSpan MaxCycleBudget = TimeSpan.FromMinutes(8);

        private readonly CopilotAuditBackfillStateStore _state;
        private readonly ICopilotAuditSearchSource _source;
        private readonly IActivityReportPersistenceManager _persistence;
        private readonly AppConfig _settings;
        private readonly ILogger _logger;
        private readonly Func<DateTime> _utcNow;

        public CopilotAuditBackfillImporter(CopilotAuditBackfillStateStore state, ICopilotAuditSearchSource source,
            IActivityReportPersistenceManager persistence, AppConfig settings, ILogger logger, Func<DateTime> utcNow = null)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public async Task<CopilotAuditBackfillJob> AdvanceLatestAsync()
        {
            var job = await _state.GetLatestAsync().ConfigureAwait(false);
            if (job == null || !job.IsActive) return job;

            return await AdvanceAsync(job).ConfigureAwait(false);
        }

        internal async Task<CopilotAuditBackfillJob> AdvanceAsync(CopilotAuditBackfillJob job)
        {
            var sw = Stopwatch.StartNew();
            var completedThisCycle = 0;

            job.CopilotImportEnabled = _settings.ImportJobSettings.Copilot;
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
            }

            try
            {
                while (completedThisCycle < MaxSlicesPerCycle && sw.Elapsed < MaxCycleBudget)
                {
                    if (job.CancelRequested || await _state.IsCancelRequestedAsync(job.Id).ConfigureAwait(false))
                    {
                        return await FinishAsync(job, CopilotAuditBackfillStates.Cancelled, null, null).ConfigureAwait(false);
                    }

                    if (job.CurrentSlice == null)
                    {
                        if (job.PendingSlices.Count == 0)
                        {
                            return await FinishAsync(job, CopilotAuditBackfillStates.Completed, null, null).ConfigureAwait(false);
                        }
                        job.CurrentSlice = job.PendingSlices[0];
                        job.PendingSlices.RemoveAt(0);
                    }

                    if (string.IsNullOrEmpty(job.CurrentSlice.QueryId))
                    {
                        var submitted = await _source.SubmitQueryAsync(job.CurrentSlice).ConfigureAwait(false);
                        job.CurrentSlice.QueryId = submitted.Id;
                        job.CurrentSlice.SubmittedUtc = _utcNow();
                        job.SlicesSubmitted++;
                        _logger?.LogInformation($"Copilot audit backfill {job.Id}: submitted Audit Search query {submitted.Id} for {job.CurrentSlice.StartUtc:o} to {job.CurrentSlice.EndUtc:o}.");
                        await _state.SaveAsync(job).ConfigureAwait(false);
                        return job;
                    }

                    var query = await _source.GetQueryAsync(job.CurrentSlice.QueryId).ConfigureAwait(false);
                    if (!query.IsTerminal)
                    {
                        await _state.SaveAsync(job).ConfigureAwait(false);
                        return job;
                    }

                    if (!query.Succeeded)
                    {
                        return await FinishAsync(job, CopilotAuditBackfillStates.Failed, CopilotAuditBackfillErrorCodes.QueryFailed,
                            query.Error ?? query.Status).ConfigureAwait(false);
                    }

                    if (query.IsTruncated)
                    {
                        if (job.CurrentSlice.SplitLevel >= 1)
                        {
                            return await FinishAsync(job, CopilotAuditBackfillStates.Failed, CopilotAuditBackfillErrorCodes.QueryFailed,
                                "An hourly Audit Search slice was still truncated at the service limit.").ConfigureAwait(false);
                        }
                        var split = CopilotAuditBackfillSlicer.SplitIntoHours(job.CurrentSlice);
                        job.PendingSlices.InsertRange(0, split);
                        job.SlicesSplit++;
                        _logger?.LogWarning($"Copilot audit backfill {job.Id}: Audit Search query {job.CurrentSlice.QueryId} was truncated; split its day into {split.Count} hour slice(s).");
                        Track(job, "truncated");
                        job.CurrentSlice = null;
                        await _state.SaveAsync(job).ConfigureAwait(false);
                        continue;
                    }

                    var imported = await ImportRecordsAsync(job.CurrentSlice.QueryId).ConfigureAwait(false);
                    job.RecordsSeen += imported.Seen;
                    job.RecordsImported += imported.Imported;
                    job.SlicesCompleted++;
                    if ((job.CurrentSlice.EndUtc - job.CurrentSlice.StartUtc).TotalHours >= 23)
                    {
                        var dayKey = CopilotAuditBackfillSlicer.DayKey(job.CurrentSlice.StartUtc);
                        if (!job.CompletedDays.Contains(dayKey)) job.CompletedDays.Add(dayKey);
                    }
                    _logger?.LogInformation($"Copilot audit backfill {job.Id}: imported {imported.Imported:N0} of {imported.Seen:N0} record(s) from query {job.CurrentSlice.QueryId}.");
                    job.CurrentSlice = null;
                    completedThisCycle++;
                    await _state.SaveAsync(job).ConfigureAwait(false);
                }

                return job;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Copilot audit backfill {job.Id} failed: {ex.Message}");
                return await FinishAsync(job, CopilotAuditBackfillStates.Failed, CopilotAuditBackfillErrorCodes.Unexpected, ex.Message).ConfigureAwait(false);
            }
        }

        private async Task<ImportOutcome> ImportRecordsAsync(string queryId)
        {
            var seen = 0;
            var mapped = new WebActivityReportSet();
            string next = null;
            do
            {
                var page = await _source.GetRecordsAsync(queryId, next).ConfigureAwait(false);
                foreach (var record in page.Records)
                {
                    seen++;
                    var mapping = CopilotAuditSearchRecordMapper.Map(record, _logger);
                    if (mapping.Content != null) mapped.Add(mapping.Content);
                }
                next = page.NextLink;
            } while (!string.IsNullOrEmpty(next));

            if (mapped.Count == 0) return new ImportOutcome { Seen = seen, Imported = 0 };
            var stats = await _persistence.CommitAll(mapped).ConfigureAwait(false);
            return new ImportOutcome { Seen = seen, Imported = stats.Imported };
        }

        private Task<CopilotAuditBackfillJob> FinishAsync(CopilotAuditBackfillJob job, string state, string errorCode, string detail)
        {
            job.State = state;
            job.CompletedUtc = _utcNow();
            job.LastErrorCode = errorCode;
            job.LastErrorDetail = detail == null || detail.Length <= 1000 ? detail : detail.Substring(0, 1000);
            Track(job, state);
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

        private sealed class ImportOutcome { public int Seen; public int Imported; }

        private void Track(CopilotAuditBackfillJob job, string outcome)
        {
            var analytics = _logger as AnalyticsLogger;
            if (analytics == null || job == null) return;

            analytics.TrackEvent(
                AnalyticsLogger.AnalyticsEvent.CopilotAuditBackfill,
                new System.Collections.Generic.Dictionary<string, string>
                {
                    { "Outcome", outcome ?? string.Empty },
                    { "State", job.State ?? string.Empty },
                    { "ErrorCode", job.LastErrorCode ?? string.Empty },
                    { "PermissionStatus", job.PermissionStatus ?? string.Empty },
                },
                new System.Collections.Generic.Dictionary<string, double>
                {
                    { "PendingSlices", job.PendingSlices?.Count ?? 0 },
                    { "SlicesCompleted", job.SlicesCompleted },
                    { "RecordsSeen", job.RecordsSeen },
                    { "RecordsImported", job.RecordsImported },
                });
        }
    }
}
