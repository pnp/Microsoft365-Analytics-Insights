using Common.Entities.Config;
using DataUtils;
using DataUtils.Sql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{

    /// <summary>
    /// Audit log processor. Uses abstract loader implementations
    /// </summary>
    public abstract class ActivityImporter<SUMMARYTYPE> : AbstractApiLoader where SUMMARYTYPE : BaseActivityReportInfo
    {
        private readonly int _maxSavesPerBatch;
        private readonly IProcessedBlobStore _processedBlobStore;
        private readonly int _maxConcurrentSaves;
        private int _reportSummariesTotal = 0;
        private int _reportSummariesProcessed = 0;
        private int _lastReportedPercentDone = 0;

        public ActivityImporter(AppConfig settings, AnalyticsLogger logger, int maxSavesPerBatch, IProcessedBlobStore processedBlobStore = null, int maxConcurrentSaves = 1) : base(logger, settings)
        {
            _maxSavesPerBatch = maxSavesPerBatch;
            _processedBlobStore = processedBlobStore;
            _maxConcurrentSaves = Math.Max(1, maxConcurrentSaves);
        }

        public abstract IActivityReportLoader<SUMMARYTYPE> ReportLoader { get; }
        public abstract ContentMetaDataLoader<SUMMARYTYPE> ContentMetaDataLoader { get; }
        public abstract IActivitySubscriptionManager ActivitySubscriptionManager { get; }

        // Retry policy for a batch save that hits a transient SQL fault (a dropped/unrecoverable connection,
        // timeout, deadlock, or Azure SQL throttling/failover). Overridable so tests can shrink the backoff
        // to milliseconds; the defaults are chosen so a brief DB blip is ridden out without materially
        // slowing a healthy cycle.
        protected virtual int BatchSaveMaxAttempts => 4;
        protected virtual TimeSpan BatchSaveRetryBaseDelay => TimeSpan.FromSeconds(3);


        public async Task<ImportStat> LoadReportsAndSave(IActivityReportPersistenceManager activityReportPersistenceManager)
        {
            var timer = new JobTimer(_logger, "Audit events import");
            timer.Start();

            var active = await ActivitySubscriptionManager.EnsureActiveSubscriptionContentTypesActive();

            var timeChunks = ContentMetaDataLoader.GetScanningTimeChunksFromNow();
            var allStats = new ImportStat() { ForTimeSlots = timeChunks };

            var allSummaries = await ContentMetaDataLoader.GetChangesSummary(active, timeChunks);

            // De-duplicate summaries by content-blob id. TimeChunkOverlapMinutes (default 5) means a blob
            // created in the overlap window is listed by two adjacent time-chunks, and GetChangesSummary
            // concatenates per-(contentType x chunk) results without deduping - so the same blob would be
            // downloaded twice and its events streamed in twice. In serial mode the per-batch import cache
            // catches the second copy; under concurrent saves (opt C) two in-flight batches can both carry
            // the same event id and collide on the audit_events primary key, failing the cycle. Deduping the
            // summaries here removes the duplicate at the source (and avoids the redundant download).
            if (allSummaries.Count > 1)
            {
                int beforeDedup = allSummaries.Count;
                var seenBlobIds = new HashSet<string>();
                allSummaries = allSummaries.Where(s => string.IsNullOrEmpty(s.BlobId) || seenBlobIds.Add(s.BlobId)).ToList();
                int duplicateSummaries = beforeDedup - allSummaries.Count;
                if (duplicateSummaries > 0)
                {
                    _logger.LogInformation($"Audit events import: de-duplicated {duplicateSummaries.ToString("n0")} overlapping content-blob summary link(s).");
                }
            }

            // Blob-level checkpoint: skip re-downloading content blobs already fully committed in a previous
            // cycle (consecutive cycles overlap almost entirely). A blob is only recorded as done once all
            // its events are committed, so skipping it here can never drop un-saved data. The checkpoint is a
            // best-effort OPTIMISATION: a store failure must never fail the import - we just process every
            // blob this cycle (and marking is likewise best-effort in BlobCommitTracker).
            BlobCommitTracker blobTracker = null;
            if (_processedBlobStore != null)
            {
                int beforeFilter = allSummaries.Count;
                ISet<string> alreadyProcessed = null;
                try
                {
                    alreadyProcessed = await _processedBlobStore.GetProcessedBlobIdsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Audit events import: blob checkpoint read failed ({ex.Message}); processing all content blobs this cycle.");
                }
                if (alreadyProcessed != null && alreadyProcessed.Count > 0)
                {
                    allSummaries = allSummaries
                        .Where(s => { var id = s.BlobId; return string.IsNullOrEmpty(id) || !alreadyProcessed.Contains(id); })
                        .ToList();
                }
                allStats.BlobsSkipped = beforeFilter - allSummaries.Count;
                _logger.LogInformation($"Audit events import: blob checkpoint skipped {allStats.BlobsSkipped.ToString("n0")} of {beforeFilter.ToString("n0")} content blobs already committed in a previous cycle.");
                blobTracker = new BlobCommitTracker(_processedBlobStore, _logger);
            }

            // Remember total so we can report on progress when threads finish loading a chunk
            lock (this)
            {
                _reportSummariesTotal = allSummaries.Count;
                _lastReportedPercentDone = 0;
            }

            await LoadFullReportsFromActivityApi(allSummaries, ReportLoader, async (reportChunk) =>
            {
                try
                {
                    // Retry transient SQL faults (a dropped/unrecoverable connection, timeout, deadlock, Azure
                    // SQL throttling/failover) so a momentary blip doesn't discard the batch and abort the
                    // cycle. CommitAll is safe to re-run: the merge SQL is idempotent (NOT EXISTS guards) and
                    // the metadata pass uses get-or-create lookups, and each attempt opens fresh connections.
                    var stats = await TransientSqlRetry.ExecuteWithRetryAsync(
                        () => activityReportPersistenceManager.CommitAll(new WebActivityReportSet(reportChunk)),
                        BatchSaveMaxAttempts, _logger, "Audit events import: batch save", BatchSaveRetryBaseDelay);

                    lock (allStats)
                    {
                        allStats.AddStats(stats);
                    }

                    // Record blobs whose events are now all committed (safe to skip next cycle).
                    if (blobTracker != null)
                    {
                        await blobTracker.OnBatchCommittedAsync(reportChunk);
                    }
                }
                catch (Exception ex)
                {
                    // Isolate the failure: one batch that can't be saved (even after retries, or a
                    // non-transient error such as a constraint violation) must NOT abort the whole cycle.
                    // We deliberately do not call OnBatchCommittedAsync, so this batch's content blobs stay
                    // un-checkpointed and are re-attempted next cycle (any events that did save dedup against
                    // audit_events). Log loudly and carry on with the remaining batches.
                    lock (allStats)
                    {
                        allStats.FailedBatches++;
                        allStats.FailedBatchEvents += reportChunk.Count;
                    }
                    _logger.LogError($"Audit events import: batch save FAILED after retries for {reportChunk.Count.ToString("n0")} event(s) - {ex.Message}. " +
                        $"Skipping this batch and continuing; its content blobs are NOT checkpointed and will be retried next cycle.");
                }

            }, blobTracker);

            // Capture error counts from loaders
            if (ContentMetaDataLoader is WebContentMetaDataLoader webMetaLoader)
            {
                allStats.MetadataDownloadErrors = webMetaLoader.MetadataDownloadErrorCount;
            }
            if (ReportLoader is ActivityReportWebLoader webReportLoader)
            {
                allStats.ReportDownloadErrors = webReportLoader.ReportDownloadErrorCount;
            }

#if DEBUG
            Console.WriteLine($"DEBUG: Got {allStats.Total.ToString("N0")} reports from {allSummaries.Count.ToString("N0")} summary reports");
#endif
            _logger.LogInformation($"Audit events import: Got {allStats.Total.ToString("N0")} audit events from {allSummaries.Count.ToString("N0")} summary reports. " +
                $"{allStats.Imported} imported, {allStats.ProcessedAlready} processed already, {allStats.URLsOutOfScope} URLs out of scope of SharePoint site import whitelist (org_urls)");

            // Log warning if there were download errors
            if (allStats.MetadataDownloadErrors > 0 || allStats.ReportDownloadErrors > 0)
            {
                _logger.LogWarning($"Audit events import: DOWNLOAD ERRORS DETECTED - {allStats.MetadataDownloadErrors} metadata download failures, " +
                    $"{allStats.ReportDownloadErrors} report download failures. Some data may be missing from this import cycle. " +
                    $"These items will be retried on the next import cycle.");
            }

            // Make a partial import unmistakable in the traces. Before batch isolation, a save failure aborted
            // the whole cycle and the only signal was the raw exception - operators saw "processed 3%..." then
            // a silent restart. Now every cycle ends with an explicit outcome line.
            if (allStats.FailedBatches > 0)
            {
                _logger.LogError($"Audit events import: COMPLETED WITH FAILURES - {allStats.FailedBatches.ToString("n0")} save batch(es) " +
                    $"(~{allStats.FailedBatchEvents.ToString("n0")} event(s)) could not be committed this cycle after retries. Their content blobs were NOT " +
                    $"checkpointed and will be retried next cycle, so this cycle did not import all available data.");
            }
            else
            {
                _logger.LogInformation("Audit events import: all save batches committed successfully this cycle.");
            }

            // Optional-workload save costs this cycle (aggregate across batches). Both are zero when the
            // workload / resolution is disabled - this is how we tell whether Copilot resource resolution or
            // Power Platform is meaningfully extending the import, rather than guessing.
            _logger.LogInformation($"Audit events import: optional-workload save cost - Copilot resource resolution " +
                $"{(allStats.SaveCopilotResolveMs / 1000.0).ToString("n1")}s, Power Platform {(allStats.SavePowerPlatformMs / 1000.0).ToString("n1")}s.");

            timer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);

            return allStats;
        }

        /// <summary>
        /// Process a new chunk of report summaries
        /// </summary>
        public async Task LoadFullReportsFromActivityApi(List<SUMMARYTYPE> reportSummaries, IActivityReportLoader<SUMMARYTYPE> activityReportLoader, Func<List<AbstractAuditLogContent>, Task> newReportsLoadedCallback, BlobCommitTracker blobTracker = null)
        {
            // Sanity
            if (reportSummaries.Count == 0)
            {
                return;
            }

            // Only generate saves in batches of MAX_REPORTS_PER_THREAD. In concurrent-save mode
            // (_maxConcurrentSaves > 1) up to that many batches commit in parallel.
            var listBatchProcessor = new ListBatchProcessor<AbstractAuditLogContent>(_maxSavesPerBatch, async (newChunk) => await newReportsLoadedCallback(newChunk), _maxConcurrentSaves);

            // For each summary chunk, load full reports in parallel. Reduced chunk size to 1000 to prevent OOM with large datasets
            var loader = new ParallelListProcessor<SUMMARYTYPE>(1000);

            // Load in parallel & call parent func on listBatchProcessor to save
            await loader.ProcessListInParallel(reportSummaries.OrderByDescending(j => j.Created),
                async (threadListChunk, threadIndex) => await ProcessSummaryChunkAsync(threadListChunk, listBatchProcessor, activityReportLoader, blobTracker),
                    threads => _logger.LogInformation($"Audit events import: full-loading activity reports from {reportSummaries.Count.ToString("n0")} links, across {threads.ToString("n0")} thread(s)..."));

            await listBatchProcessor.Flush();
        }

        private async Task ProcessSummaryChunkAsync(List<SUMMARYTYPE> summariesToLoad, ListBatchProcessor<AbstractAuditLogContent> listBatchProcessor, IActivityReportLoader<SUMMARYTYPE> activityReportLoader, BlobCommitTracker blobTracker)
        {
            foreach (var job in summariesToLoad)
            {
                var metaReports = await activityReportLoader.Load(job);

                // Tag each event with its source blob and register the blob's event count BEFORE queueing
                // for save, so the checkpoint tracker can record the blob once all its events are committed.
                // ONLY track a blob whose download COMPLETED cleanly: a failed or partial download returns
                // zero / a prefix of events, and registering it (a zero count marks a blob done immediately)
                // would checkpoint it and skip it next cycle - permanently losing the un-downloaded events.
                // Leaving an incomplete blob's events untagged means it is never checkpointed, so it is
                // re-downloaded next cycle (any events it did commit dedup against audit_events).
                if (blobTracker != null && metaReports.DownloadComplete)
                {
                    var blobId = job.BlobId;
                    if (!string.IsNullOrEmpty(blobId))
                    {
                        foreach (var ev in metaReports) ev.SourceContentId = blobId;
                        await blobTracker.RegisterAsync(blobId, metaReports.Count);
                    }
                }

                await listBatchProcessor.AddRange(metaReports);

                // Update reports done stats
                lock (this)
                {
                    _reportSummariesProcessed++;

                    if (_reportSummariesProcessed > 0)
                    {
                        var percentDone = (_reportSummariesProcessed / (float)_reportSummariesTotal) * 100;
                        if (percentDone < 100 && percentDone > 0)
                        {
                            int pcDone = Convert.ToInt32(Math.Round(percentDone, 0));
                            if (_lastReportedPercentDone < pcDone)
                            {
                                _logger.LogInformation($"Audit events import: processed {pcDone.ToString("n0")}% activity report data...");
                                _lastReportedPercentDone = pcDone;
                            }
                        }
                    }
                }
            }
        }
    }
}
