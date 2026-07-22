using System;
using System.Collections.Generic;
using WebJob.Office365ActivityImporter.Engine.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Entities
{
    /// <summary>
    /// Stats for work done on a batch
    /// </summary>
    public class ImportStat
    {
        public int Imported { get; set; }
        public int ProcessedAlready { get; set; }
        public int URLsOutOfScope { get; set; }
        public int UsersOutOfScope { get; set; }
        public int DownloadErrors { get; set; }
        public int Total { get; set; }

        /// <summary>
        /// Number of content blobs skipped this cycle because the blob-level checkpoint had already
        /// recorded them as fully committed in a previous cycle (so they weren't re-downloaded).
        /// </summary>
        public int BlobsSkipped { get; set; }

        /// <summary>
        /// Number of metadata/summary download failures from the Activity API
        /// </summary>
        public int MetadataDownloadErrors { get; set; }

        /// <summary>
        /// Number of full report download failures from the Activity API
        /// </summary>
        public int ReportDownloadErrors { get; set; }

        /// <summary>
        /// Number of save batches that permanently failed this cycle (after transient-SQL retries) and were
        /// skipped so the rest of the cycle could continue. Their content blobs are NOT checkpointed, so they
        /// are retried on the next import cycle.
        /// </summary>
        public int FailedBatches { get; set; }

        /// <summary>
        /// Approximate number of audit events in the batches counted by <see cref="FailedBatches"/> (those
        /// events were not saved this cycle; they will be re-attempted next cycle).
        /// </summary>
        public int FailedBatchEvents { get; set; }

        /// <summary>
        /// Per-workload save sub-costs (milliseconds, summed across batches this cycle), so the per-cycle
        /// summary can show what the optional workloads cost:
        ///   <see cref="SaveCopilotResolveMs"/>  - the Copilot per-event Graph resolution (file + meeting).
        ///   <see cref="SavePowerPlatformMs"/>    - the Power Platform staging-table merges.
        /// Both are zero when the corresponding workload / resolution is disabled.
        /// </summary>
        public double SaveCopilotResolveMs { get; set; }
        public double SavePowerPlatformMs { get; set; }

        public List<TimePeriod> ForTimeSlots { get; set; }

        public void AddStats(ImportStat statsToAdd)
        {
            if (statsToAdd == null)
            {
                throw new ArgumentNullException("statsToAdd");
            }
            this.ProcessedAlready += statsToAdd.ProcessedAlready;
            this.Imported += statsToAdd.Imported;
            this.URLsOutOfScope += statsToAdd.URLsOutOfScope;
            this.DownloadErrors += statsToAdd.DownloadErrors;
            this.MetadataDownloadErrors += statsToAdd.MetadataDownloadErrors;
            this.ReportDownloadErrors += statsToAdd.ReportDownloadErrors;
            this.BlobsSkipped += statsToAdd.BlobsSkipped;
            this.FailedBatches += statsToAdd.FailedBatches;
            this.FailedBatchEvents += statsToAdd.FailedBatchEvents;
            this.SaveCopilotResolveMs += statsToAdd.SaveCopilotResolveMs;
            this.SavePowerPlatformMs += statsToAdd.SavePowerPlatformMs;
            this.Total += statsToAdd.Total;
        }

        public override string ToString()
        {
            return
                $"Imported successfully: {this.Imported.ToString("n0")}, " +
                $"already processed: {this.ProcessedAlready.ToString("n0")}, " +
                $"URLs out of scope (orgs table): {this.URLsOutOfScope.ToString("n0")}, " +
                $"users out of scope: {this.UsersOutOfScope.ToString("n0")}, " +
                $"blobs skipped (checkpoint): {this.BlobsSkipped.ToString("n0")}, " +
                $"failed batches: {this.FailedBatches.ToString("n0")} (~{this.FailedBatchEvents.ToString("n0")} events, will retry next cycle), " +
                $"errors: {this.DownloadErrors.ToString("n0")}, " +
                $"metadata download errors: {this.MetadataDownloadErrors.ToString("n0")}, " +
                $"report download errors: {this.ReportDownloadErrors.ToString("n0")}, " +
                $"total: {this.Total.ToString("n0")}";
        }
    }
}
