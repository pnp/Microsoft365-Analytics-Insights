using System;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// Parsing for the audit-import runtime safety valves, which are environment variables rather than
    /// installer configuration so they can be flipped on a live App Service without a redeploy.
    ///
    /// Extracted from <c>ProgramTasks.DownloadActivityData</c> (issue #376) so the parsing is testable: these
    /// are the switches an operator reaches for when the import is misbehaving in production, and getting
    /// "what does AUDIT_PERBATCH_DEDUP_CACHE=yes do?" wrong is the sort of thing that is only discovered
    /// during an incident.
    /// </summary>
    public static class ImportRuntimeOptions
    {
        /// <summary>
        /// The original strictly-serial save. Concurrent-save mode is opt-in.
        /// </summary>
        public const int DefaultMaxConcurrentSaves = 1;

        public const string MaxConcurrentSavesEnvVariable = "AUDIT_MAX_CONCURRENT_SAVES";
        public const string PerBatchDedupCacheEnvVariable = "AUDIT_PERBATCH_DEDUP_CACHE";

        /// <summary>
        /// How many audit batches may commit in parallel (sharded staging; shared-table writes are still
        /// serialised). Anything that is not a whole number greater than 1 - unset, blank, non-numeric,
        /// zero or negative - leaves the strictly-serial default, deliberately: this valve can only ever
        /// turn concurrency <i>on</i>, so a typo cannot stop the importer saving.
        /// </summary>
        public static int ResolveMaxConcurrentSaves(string rawEnvValue)
        {
            if (!string.IsNullOrWhiteSpace(rawEnvValue)
                && int.TryParse(rawEnvValue.Trim(), out int parsed)
                && parsed > 1)
            {
                return parsed;
            }

            return DefaultMaxConcurrentSaves;
        }

        /// <summary>
        /// Whether to rebuild the de-dup cache for every batch (the pre-optimisation behaviour) instead of
        /// once per cycle. Accepts "1" or "true" in any casing, with surrounding whitespace ignored;
        /// everything else means off.
        /// </summary>
        public static bool ResolveUsePerBatchDedupCache(string rawEnvValue)
        {
            if (string.IsNullOrWhiteSpace(rawEnvValue)) return false;

            var trimmed = rawEnvValue.Trim();
            return trimmed == "1" || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
