using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace Tests.UnitTests.StressHarness
{
    /// <summary>
    /// Deterministic activity-report loader for the DB-backed load test. For a given blob (its index is
    /// encoded in <see cref="ActivityReportInfo.ContentId"/>) it always returns the same set of events.
    /// Tracks how many blobs were actually loaded so the blob-checkpoint optimisation (B) can be measured
    /// by the drop in loads on the WARM re-run.
    /// </summary>
    public class DeterministicActivityReportLoaderForStress : IActivityReportLoader<ActivityReportInfo>
    {
        private readonly StressAuditDataConfig _cfg;
        private long _blobsLoaded;
        private long _eventsGenerated;

        public DeterministicActivityReportLoaderForStress(StressAuditDataConfig cfg)
        {
            _cfg = cfg;
        }

        public long BlobsLoaded => Interlocked.Read(ref _blobsLoaded);
        public long EventsGenerated => Interlocked.Read(ref _eventsGenerated);

        public async Task<ActivityReportSet> Load(ActivityReportInfo metadata)
        {
            // Model per-blob download cost (network) so the blob-checkpoint win is visible in wall-time.
            if (_cfg.SimulatedBlobLatencyMs > 0)
            {
                await Task.Delay(_cfg.SimulatedBlobLatencyMs);
            }

            int blobIndex = StressBlobId.Parse(metadata.ContentId);
            Interlocked.Increment(ref _blobsLoaded);

            // Simulate a failed/partial download for a deterministic subset: an empty set flagged
            // DownloadComplete=false. The importer must NOT checkpoint these (they'd be permanently lost),
            // so they must be re-downloaded next cycle.
            if (_cfg.FailedBlobPercent > 0 && (blobIndex % 100) < _cfg.FailedBlobPercent)
            {
                return new WebActivityReportSet { DownloadComplete = false };
            }

            var set = StressAuditDataGenerator.GenerateBlobEvents(_cfg, blobIndex);
            Interlocked.Add(ref _eventsGenerated, set.Count);
            return set;
        }
    }
}
