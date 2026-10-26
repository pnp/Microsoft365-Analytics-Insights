using System;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules
{
    /// <summary>
    /// Which de-duplication cache an audit-log save batch uses.
    /// </summary>
    public enum ActivityDedupCacheScope
    {
        /// <summary>
        /// Default. One cache for the whole import cycle, shared by every save batch. It loads nothing up front:
        /// each batch checks its own event ids against the database by primary key, and what the cycle stages
        /// or ignores is kept in memory as batches commit.
        /// </summary>
        PerRun,

        /// <summary>
        /// The <c>AUDIT_PERBATCH_DEDUP_CACHE</c> safety-valve: rebuild the cache for every batch by loading every
        /// processed id in that batch's own [oldest, newest] span. Restores the pre-optimisation behaviour without
        /// a redeploy.
        /// </summary>
        PerBatch
    }

    /// <summary>
    /// The window a de-duplication cache covers. The per-batch safety-valve loads every processed id in it; the
    /// per-run cache only reports it, because it looks each batch's own ids up instead.
    /// </summary>
    public sealed class ActivityDedupCacheWindow
    {
        internal ActivityDedupCacheWindow(DateTime fromUtc, DateTime toUtc, ActivityDedupCacheScope scope, int daysBack)
        {
            FromUtc = fromUtc;
            ToUtc = toUtc;
            Scope = scope;
            DaysBack = daysBack;
        }

        public DateTime FromUtc { get; }
        public DateTime ToUtc { get; }
        public ActivityDedupCacheScope Scope { get; }

        /// <summary>
        /// How many days back a per-run window reaches. Reported in the operator-facing "run dedup cache ready"
        /// log line. Zero for a per-batch window, where the span comes from the batch itself.
        /// </summary>
        public int DaysBack { get; }
    }

    /// <summary>
    /// The de-duplication cache window rules for the audit-log import, extracted from
    /// <c>ActivityReportSqlPersistenceManager</c> so both the <c>AUDIT_PERBATCH_DEDUP_CACHE</c> safety-valve
    /// and the window padding can be asserted with no SQL Server and no wall clock. See issue #373.
    ///
    /// Follows the <c>ImportCadenceGate.ShouldRun(..., DateTime nowUtc)</c> convention: the instant is a
    /// parameter, not an <c>IClock</c> dependency.
    /// </summary>
    public static class ActivityImportCacheWindow
    {
        /// <summary>
        /// Extra lower margin, in days, on the per-run window, from when that window was loaded at the start of
        /// each cycle. It is now only reported: the per-run cache looks each batch's ids up instead.
        /// </summary>
        public const int RunWindowLowerMarginDays = 1;

        /// <summary>
        /// Upper margin on the per-run window, covering events created between the cycle starting and the
        /// batch being processed.
        /// </summary>
        public static readonly TimeSpan RunWindowUpperMargin = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Padding applied to a cache-load range. The load's parameters are <c>datetime2</c> (as EF6 maps
        /// <c>DateTime</c>), whose precision differs from the <c>datetime</c> columns actually in the database,
        /// so an exact boundary comparison can miss edge values. A minute either side is cheaper than migrating
        /// every date column.
        /// </summary>
        public static readonly TimeSpan DateTime2EdgePadding = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Which cache to load, and over what range, for a save batch.
        /// </summary>
        /// <param name="usePerBatchDedupCache">The <c>AUDIT_PERBATCH_DEDUP_CACHE</c> safety-valve.</param>
        /// <param name="oldestContentUtc">Oldest <c>CreationTime</c> in the batch (per-batch mode only).</param>
        /// <param name="newestContentUtc">Newest <c>CreationTime</c> in the batch (per-batch mode only).</param>
        /// <param name="daysBeforeNowToDownload">The configured download window, in days.</param>
        /// <param name="nowUtc">The current instant, passed in rather than read.</param>
        public static ActivityDedupCacheWindow Resolve(bool usePerBatchDedupCache, DateTime oldestContentUtc, DateTime newestContentUtc,
            int daysBeforeNowToDownload, DateTime nowUtc)
        {
            if (usePerBatchDedupCache)
            {
                return new ActivityDedupCacheWindow(oldestContentUtc, newestContentUtc, ActivityDedupCacheScope.PerBatch, 0);
            }

            // The per-run window is the download window plus a margin. The run cache no longer loads it - each
            // batch looks its own ids up by primary key, whatever their age - so it is reported, not queried.
            var daysBack = Math.Max(daysBeforeNowToDownload, 1) + RunWindowLowerMarginDays;
            return new ActivityDedupCacheWindow(nowUtc.AddDays(-daysBack), nowUtc.Add(RunWindowUpperMargin),
                ActivityDedupCacheScope.PerRun, daysBack);
        }

        /// <summary>
        /// Lower bound of a cache load, widened by <see cref="DateTime2EdgePadding"/>. Applied when a window is
        /// actually loaded - the per-batch safety-valve.
        /// </summary>
        public static DateTime PadFrom(DateTime fromUtc) => fromUtc.Subtract(DateTime2EdgePadding);

        /// <summary>
        /// Upper bound of a cache load, widened by <see cref="DateTime2EdgePadding"/>.
        /// </summary>
        public static DateTime PadTo(DateTime toUtc) => toUtc.Add(DateTime2EdgePadding);
    }
}
