using System;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules
{
    /// <summary>
    /// Which de-duplication cache an audit-log save batch uses.
    /// </summary>
    public enum ActivityDedupCacheScope
    {
        /// <summary>
        /// Default. One cache covering the whole download window, built once for the import cycle and kept
        /// current in memory as batches commit.
        /// </summary>
        PerRun,

        /// <summary>
        /// The <c>AUDIT_PERBATCH_DEDUP_CACHE</c> safety-valve: rebuild the cache for every batch, over just
        /// that batch's own [oldest, newest] span. Restores the pre-optimisation behaviour without a
        /// redeploy.
        /// </summary>
        PerBatch
    }

    /// <summary>
    /// The window of <c>audit_events</c> / <c>ignored_audit_events</c> a de-duplication cache is loaded for.
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
        /// How many days back a per-run window reaches. Reported in the operator-facing "built run dedup
        /// cache" log line. Zero for a per-batch window, where the span comes from the batch itself.
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
        /// Extra lower margin, in days, on the per-run window. The download window is computed slightly
        /// earlier (at cycle start) than the cache is built, so a day of slack guarantees an event created
        /// just outside the exact boundary can never be missed.
        /// </summary>
        public const int RunWindowLowerMarginDays = 1;

        /// <summary>
        /// Upper margin on the per-run window, covering events created between the cache being built and the
        /// batch being processed.
        /// </summary>
        public static readonly TimeSpan RunWindowUpperMargin = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Padding applied to any cache-load range. EF6 maps <c>DateTime</c> to <c>datetime2</c>, whose
        /// precision differs from the <c>datetime</c> columns actually in the database, so an exact boundary
        /// comparison can miss edge values. A minute either side is cheaper than migrating every date column.
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

            // Every event processed this cycle has a CreationTime inside the download window (the API only
            // serves it there) and the cache is keyed by event id, so one full-window load is equivalent to
            // the old per-batch [Min,Max] loads - without the redundancy of reloading almost the whole
            // in-window event set on every batch.
            var daysBack = Math.Max(daysBeforeNowToDownload, 1) + RunWindowLowerMarginDays;
            return new ActivityDedupCacheWindow(nowUtc.AddDays(-daysBack), nowUtc.Add(RunWindowUpperMargin),
                ActivityDedupCacheScope.PerRun, daysBack);
        }

        /// <summary>
        /// Lower bound of a cache load, widened by <see cref="DateTime2EdgePadding"/>. Applied when the cache
        /// is actually loaded, to both scopes.
        /// </summary>
        public static DateTime PadFrom(DateTime fromUtc) => fromUtc.Subtract(DateTime2EdgePadding);

        /// <summary>
        /// Upper bound of a cache load, widened by <see cref="DateTime2EdgePadding"/>.
        /// </summary>
        public static DateTime PadTo(DateTime toUtc) => toUtc.Add(DateTime2EdgePadding);
    }
}
