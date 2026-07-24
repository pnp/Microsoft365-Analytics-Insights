using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Engine.Entities;

namespace Tests.UnitTests.StressHarness
{
    /// <summary>
    /// Deterministic content-metadata loader for the DB-backed ActivityAPI load test. Emits the whole
    /// blob set (<see cref="StressAuditDataConfig.BlobCount"/>) exactly once, so the total set of blobs is
    /// deterministic and identical across COLD and WARM runs, independent of the base class's time-chunk math.
    /// </summary>
    public class DeterministicContentMetaDataLoaderForStress : ContentMetaDataLoader<ActivityReportInfo>
    {
        private readonly StressAuditDataConfig _cfg;
        private int _emitted; // 0 = not yet emitted, 1 = emitted

        public DeterministicContentMetaDataLoaderForStress(StressAuditDataConfig cfg, ILogger logger, AppConfig settings)
            : base(logger, settings)
        {
            _cfg = cfg;
        }

        protected override Task<List<ActivityReportInfo>> LoadAllActivityReports(string auditContentType, TimePeriod chunk, int batchId)
        {
            // First call (across all content-type x time-chunk calls) wins and emits every blob; the rest
            // return empty. Guarantees a deterministic total blob set for both COLD and WARM.
            if (Interlocked.CompareExchange(ref _emitted, 1, 0) != 0)
            {
                return Task.FromResult(new List<ActivityReportInfo>());
            }

            var list = new List<ActivityReportInfo>(_cfg.BlobCount);
            for (int i = 0; i < _cfg.BlobCount; i++)
            {
                list.Add(new ActivityReportInfo
                {
                    ContentId = StressBlobId.Format(i),
                    ContentUri = new Uri($"https://stress.local/blob/{i}"),
                    ContentType = auditContentType,
                    BatchID = batchId,
                    Created = _cfg.BaseTimeUtc.AddMinutes(-i)
                });
            }
            return Task.FromResult(list);
        }
    }

    /// <summary>Encodes / decodes a blob's deterministic index in its ContentId.</summary>
    internal static class StressBlobId
    {
        private const string Prefix = "stress-blob-";
        public static string Format(int blobIndex) => Prefix + blobIndex;
        public static int Parse(string contentId) => int.Parse(contentId.Substring(Prefix.Length));
    }
}
