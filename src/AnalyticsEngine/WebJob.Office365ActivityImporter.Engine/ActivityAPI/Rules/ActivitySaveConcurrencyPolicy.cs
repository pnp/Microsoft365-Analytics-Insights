using System;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules
{
    /// <summary>
    /// The concurrency safety-valve for the audit-log save path (app setting
    /// <c>AUDIT_MAX_CONCURRENT_SAVES</c>). Decides, from that one number, whether a save runs on the
    /// original strictly-serial path against the shared staging table, or on the opt-in sharded path where
    /// several saves build their own staging table in parallel and only the shared-table writes (the merge
    /// and the metadata pass) are serialised.
    ///
    /// Extracted from <c>ActivityReportSqlPersistenceManager</c> so the valve is assertable without SQL
    /// Server. See issue #373.
    /// </summary>
    public static class ActivitySaveConcurrencyPolicy
    {
        /// <summary>
        /// Prefix of a per-save (sharded) staging table. A global temp table (<c>##</c>) so it is visible to
        /// the merge running on the same connection and is dropped when that session ends.
        ///
        /// Note this is deliberately NOT derived from <c>ActivityImportConstants.STAGING_TABLE_ACTIVITY</c>,
        /// which is a <c>debug_</c>-prefixed permanent table in DEBUG builds. The sharded name has always
        /// been the literal below in every configuration; keeping it literal preserves that exactly.
        /// </summary>
        public const string ShardedStagingTablePrefix = "##import_staging_event_lookups_";

        /// <summary>
        /// The configured value clamped to something usable. Anything below 1 (unset, zero, negative) means
        /// the default single-threaded behaviour rather than an error, so a bad app setting can never stop
        /// the import.
        /// </summary>
        public static int NormaliseMaxConcurrentSaves(int configuredMaxConcurrentSaves)
        {
            return Math.Max(1, configuredMaxConcurrentSaves);
        }

        /// <summary>
        /// True when saves get their own sharded staging table and the shared-table writes need the
        /// shared-write lock. False - the default - is the original strictly-serial path.
        /// </summary>
        public static bool UseShardedStaging(int maxConcurrentSaves)
        {
            return NormaliseMaxConcurrentSaves(maxConcurrentSaves) > 1;
        }

        /// <summary>
        /// A fresh staging-table name for one save. Unique per call, because the whole point of the sharded
        /// path is that concurrent saves must not load into the same table.
        /// </summary>
        public static string NewShardedStagingTableName()
        {
            return ShardedStagingTablePrefix + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// The staging table the merge SQL should be pointed at: the save's own shard when there is one,
        /// otherwise the single shared staging table.
        /// </summary>
        public static string EffectiveStagingTableName(string shardedStagingTableName)
        {
            return shardedStagingTableName ?? ActivityImportConstants.STAGING_TABLE_ACTIVITY;
        }
    }
}
