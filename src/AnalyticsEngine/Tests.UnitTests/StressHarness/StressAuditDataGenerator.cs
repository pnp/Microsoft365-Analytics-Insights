using System;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests.StressHarness
{
    /// <summary>
    /// Immutable, fully deterministic description of a synthetic SharePoint audit-log data set for the
    /// DB-backed ActivityAPI load test (<see cref="ActivityApiDbStressRunner"/>).
    ///
    /// Determinism is the whole point: the COLD and WARM scenarios must generate byte-identical events
    /// (same GUIDs + timestamps) because WARM re-runs the *exact same* window so the import cache marks
    /// every event as "already processed / ignored". <see cref="BaseTimeUtc"/> is captured ONCE by the
    /// runner and shared by both scenarios so timestamps don't drift between runs.
    ///
    /// Models a large tenant with a NARROW org-URL whitelist: only <see cref="InScopePercent"/>% of
    /// events fall under <see cref="InScopePrefix"/> (imported); the rest fall under
    /// <see cref="OutOfScopePrefix"/> and are ignored (the ~99% out-of-scope case). All synthetic - no
    /// customer data (Contoso host).
    /// </summary>
    public sealed class StressAuditDataConfig
    {
        public int TotalEvents { get; set; }
        public int EventsPerBlob { get; set; }
        public int InScopePercent { get; set; }
        public int DistinctUsers { get; set; }
        public int DistinctInScopeSites { get; set; }
        public int DistinctOutOfScopeSites { get; set; }
        public int WindowDays { get; set; }

        /// <summary>Optional per-blob simulated download latency (ms) to model network cost.</summary>
        public int SimulatedBlobLatencyMs { get; set; }

        /// <summary>
        /// Optional number of historical rows to pre-seed into audit_events BEFORE the run, modelling a
        /// large pre-existing table on a mature tenant. The per-batch import-cache query scans audit_events
        /// by time_stamp, so this is what makes the <c>audit_events.time_stamp</c> index (optimisation A)
        /// measurable: without the index every batch full-scans all of these rows. Seeded with timestamps
        /// well outside the event window so they only bloat the table (they never match/dedup).
        /// </summary>
        public int PreSeedHistoricalAuditEvents { get; set; }

        /// <summary>
        /// Optional number of historical rows to pre-seed into audit_events WITHIN the event window (spread
        /// across <see cref="WindowDays"/>), modelling the production condition: a large standing set of
        /// already-imported in-window events that the per-batch dedup cache re-materialises into memory on
        /// EVERY save. This is what makes the per-cycle-cache optimisation measurable - unlike
        /// <see cref="PreSeedHistoricalAuditEvents"/>, which seeds OUT of window so it only bloats the table.
        /// Random ids, so they never dedup against the generated events.
        /// </summary>
        public int PreSeedInWindowAuditEvents { get; set; }

        /// <summary>When true, the persistence manager rebuilds the dedup cache per batch (the
        /// pre-optimisation behaviour) instead of once per cycle - lets the harness measure the before/after
        /// of the per-cycle-cache optimisation in a single build.</summary>
        public bool UsePerBatchDedupCache { get; set; } = false;

        /// <summary>Captured ONCE by the runner; shared by COLD + WARM so timestamps are identical.</summary>
        public DateTime BaseTimeUtc { get; set; }

        /// <summary>When true, the importer uses the blob-level checkpoint (opt B) - the in-memory store,
        /// which persists across the COLD and WARM scenarios so WARM skips already-committed blobs.</summary>
        public bool UseBlobCheckpoint { get; set; } = true;

        /// <summary>Max concurrent SQL saves (opt C). 1 = serial (original). &gt;1 shards staging and commits
        /// batches in parallel (shared-table writes still serialised).</summary>
        public int MaxConcurrentSaves { get; set; } = 1;

        /// <summary>Percent of blobs whose download "fails" (loader returns an empty, DownloadComplete=false
        /// set). Used to validate that failed downloads are NOT checkpointed and are re-processed next cycle.</summary>
        public int FailedBlobPercent { get; set; } = 0;

        public string InScopePrefix { get; set; } = "https://contoso.sharepoint.com/sites/inscope";
        public string OutOfScopePrefix { get; set; } = "https://contoso.sharepoint.com/sites/other";

        public int BlobCount => (TotalEvents + EventsPerBlob - 1) / EventsPerBlob;
    }

    /// <summary>
    /// Deterministic generator of <see cref="SharePointAuditLogContent"/> events for a given blob index.
    /// Given the same <see cref="StressAuditDataConfig"/> it always produces the same events.
    /// </summary>
    public static class StressAuditDataGenerator
    {
        // Bounded lookup pools keep the merge's distinct-value inserts (event_operations, event_types,
        // event_file_ext) bounded across runs, mirroring a real tenant where these are far fewer than events.
        private static readonly string[] Operations =
            { "FileAccessed", "FileModified", "FileDownloaded", "FileUploaded", "PageViewed", "FileRenamed", "SharingSet" };
        private static readonly string[] ItemTypes = { "File", "Folder", "Page", "Web", "List" };
        private static readonly string[] Extensions = { "docx", "xlsx", "pptx", "pdf", "txt", "one" };

        // Includes non-Latin (Greek/Spanish/Japanese) fragments so Unicode round-trips through the nvarchar
        // staging + urls + event_file_names path surface here, not in a customer tenant.
        private static readonly string[] FileNameFragments =
        {
            "QuarterlyReport", "Budget", "Roadmap", "Proposal", "MeetingNotes",
            "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5", // "Καλημέρα κόσμε"
            "\u03A3\u03C7\u03AD\u03B4\u03B9\u03BF",                                             // "Σχέδιο"
            "An\u00E1lisis", "Presupuesto", "\u6226\u7565\u8A08\u753B"                          // "戦略計画"
        };

        /// <summary>
        /// Stable, collision-free GUID from a (blob, event) pair - lets WARM reproduce COLD's ids exactly.
        /// </summary>
        public static Guid DeterministicGuid(int blobIndex, int eventIndex)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(blobIndex).CopyTo(bytes, 0);
            BitConverter.GetBytes(eventIndex).CopyTo(bytes, 4);
            unchecked
            {
                BitConverter.GetBytes((uint)blobIndex * 2654435761u ^ (uint)eventIndex).CopyTo(bytes, 8);
                BitConverter.GetBytes((uint)eventIndex * 40503u + (uint)blobIndex).CopyTo(bytes, 12);
            }
            return new Guid(bytes);
        }

        /// <summary>
        /// Generate the deterministic set of SharePoint events belonging to <paramref name="blobIndex"/>.
        /// </summary>
        public static ActivityReportSet GenerateBlobEvents(StressAuditDataConfig cfg, int blobIndex)
        {
            var set = new WebActivityReportSet();
            int startGlobal = blobIndex * cfg.EventsPerBlob;
            int windowMinutes = Math.Max(1, cfg.WindowDays * 24 * 60);
            // Each blob is a small time-slice, and consecutive blob indices are scattered across the whole
            // window by a coprime multiplier (a full-period step), so a SAVE BATCH of several blobs spans
            // ~the entire window - faithfully modelling the real importer, where ~130 threads download blobs
            // out of order so each 2000-event batch's [Min,Max] CreationTime covers almost the whole window
            // (which is what makes the per-batch dedup-cache reload materialise ~the entire in-window set).
            // Deterministic in blobIndex, so COLD and WARM still generate byte-identical timestamps.
            int blobBaseMinute = (int)(((long)blobIndex * 2654435761L) % windowMinutes);

            for (int j = 0; j < cfg.EventsPerBlob; j++)
            {
                int g = startGlobal + j;
                if (g >= cfg.TotalEvents) break;

                bool inScope = (g % 100) < cfg.InScopePercent;

                var operation = Operations[g % Operations.Length];
                var itemType = ItemTypes[g % ItemTypes.Length];
                var extension = Extensions[g % Extensions.Length];
                var fragment = FileNameFragments[g % FileNameFragments.Length];
                var fileName = $"{fragment}-{g}";

                string siteUrl, objectId;
                if (inScope)
                {
                    siteUrl = $"{cfg.InScopePrefix}/site{g % Math.Max(1, cfg.DistinctInScopeSites)}";
                    objectId = $"{siteUrl}/Shared Documents/{fileName}.{extension}";
                }
                else
                {
                    siteUrl = $"{cfg.OutOfScopePrefix}/site{g % Math.Max(1, cfg.DistinctOutOfScopeSites)}";
                    objectId = $"{siteUrl}/Shared Documents/{fileName}.{extension}";
                }

                var log = new SharePointAuditLogContent
                {
                    Id = DeterministicGuid(blobIndex, j),
                    Workload = ActivityImportConstants.WORKLOAD_SP,
                    UserId = $"stressuser{g % Math.Max(1, cfg.DistinctUsers)}@contoso.onmicrosoft.com",
                    Operation = operation,
                    ItemType = itemType,
                    SourceFileName = fileName,
                    SourceFileExtension = extension,
                    SiteUrl = siteUrl,
                    ObjectId = objectId,
                    EventData = "<Event><Id>" + g + "</Id></Event>",
                    // Stable, in-window timestamp: the blob's scattered base minute + a small within-blob
                    // offset (events in one blob are close in time, blobs are spread across the window). >= 1h
                    // before BaseTimeUtc so the per-batch cache window [oldest-1min, newest+1min] captures
                    // COLD's rows on the WARM re-run.
                    CreationTime = cfg.BaseTimeUtc.AddMinutes(-(((blobBaseMinute + j) % windowMinutes) + 60)),
                    OriginalImportFileContents = "stress"
                };

                set.Add(log);
            }

            return set;
        }
    }
}
