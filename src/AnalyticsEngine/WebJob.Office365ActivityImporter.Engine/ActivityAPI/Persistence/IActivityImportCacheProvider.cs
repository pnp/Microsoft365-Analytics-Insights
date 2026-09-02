using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence
{
    /// <summary>
    /// Read port for the audit-log de-duplication cache: the one place that queries <c>audit_events</c> and
    /// <c>ignored_audit_events</c> to find out which event ids have already been seen.
    ///
    /// Split out by issue #373 so the cache <i>lifecycle</i> (build once per cycle, or once per batch under
    /// the <c>AUDIT_PERBATCH_DEDUP_CACHE</c> safety-valve) can be asserted without SQL Server. The load is
    /// deliberately synchronous because <see cref="ActivityImportCache.GetAndBuildNewCache(DateTime, DateTime)"/>
    /// is - wrapping it in a Task would change when the caller's thread blocks.
    /// </summary>
    public interface IActivityImportCacheLoader
    {
        /// <summary>
        /// Load every already-processed (imported or ignored) event id whose timestamp falls in
        /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>].
        /// </summary>
        ActivityImportCache Load(DateTime fromUtc, DateTime toUtc);
    }

    /// <summary>
    /// Production <see cref="IActivityImportCacheLoader"/>. A one-line adapter over the existing static
    /// factory, which is what makes that factory's <c>new AnalyticsEntitiesContext()</c> substitutable.
    /// </summary>
    public sealed class SqlActivityImportCacheLoader : IActivityImportCacheLoader
    {
        public static readonly SqlActivityImportCacheLoader Instance = new SqlActivityImportCacheLoader();

        public ActivityImportCache Load(DateTime fromUtc, DateTime toUtc)
        {
            return ActivityImportCache.GetAndBuildNewCache(fromUtc, toUtc);
        }
    }

    /// <summary>
    /// Hands a save batch the de-duplication cache it should use, per
    /// <see cref="ActivityImportCacheWindow.Resolve"/>'s decision.
    /// </summary>
    public interface IActivityImportCacheProvider
    {
        /// <summary>
        /// The cache for one save batch. For <see cref="ActivityDedupCacheScope.PerBatch"/> this is a fresh
        /// cache over the batch's own span; for <see cref="ActivityDedupCacheScope.PerRun"/> it is the
        /// single run-scoped cache, built on first use and returned unchanged thereafter.
        /// </summary>
        Task<ActivityImportCache> GetForWindowAsync(ActivityDedupCacheWindow window);
    }

    /// <summary>
    /// The de-duplication cache lifecycle for one import cycle, lifted out of
    /// <c>ActivityReportSqlPersistenceManager</c> by issue #373.
    ///
    /// One instance per import cycle (the persistence manager is itself created per cycle), so the
    /// run-scoped cache is built ONCE for the whole download window instead of being re-queried for every
    /// save batch. That matters at scale: a 2000-event batch's [Min,Max] CreationTime spans almost the whole
    /// window because events download out-of-order across ~130 threads, so the old per-batch load
    /// materialised ~the entire in-window <c>audit_events</c> set on every single batch.
    ///
    /// Correctness is unchanged - the same ids end up cached (full window, keyed by id), the cache is kept
    /// current in memory by <c>RememberProcessedEvent</c> / <c>RememberNewlyIgnoredEvent</c> as batches
    /// commit, and the merge SQL's <c>NOT EXISTS</c> guards remain the authoritative cross-instance /
    /// cross-cycle backstop.
    ///
    /// <see cref="ActivityDedupCacheScope.PerBatch"/> is the <c>AUDIT_PERBATCH_DEDUP_CACHE</c> operator
    /// safety-valve: it restores the old per-batch build without a redeploy.
    /// </summary>
    public sealed class ActivityImportCacheProvider : IActivityImportCacheProvider
    {
        private readonly IActivityImportCacheLoader _loader;
        private readonly ILogger _logger;

        private ActivityImportCache _runImportCache;
        private bool _runImportCacheBuilt;
        private readonly SemaphoreSlim _runImportCacheInitLock = new SemaphoreSlim(1, 1);

        public ActivityImportCacheProvider(IActivityImportCacheLoader loader, ILogger logger)
        {
            if (loader == null) throw new ArgumentNullException(nameof(loader));
            _loader = loader;
            _logger = logger;
        }

        public async Task<ActivityImportCache> GetForWindowAsync(ActivityDedupCacheWindow window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));

            if (window.Scope == ActivityDedupCacheScope.PerBatch)
            {
                return _loader.Load(window.FromUtc, window.ToUtc);
            }

            return await GetOrBuildRunImportCacheAsync(window);
        }

        /// <summary>
        /// Lazily build the run-scoped cache ONCE, over the window resolved on entry to the save. Every event
        /// processed this cycle has a CreationTime inside that window (the API only serves it there) and the
        /// cache is keyed by event id, so a single full-window load is equivalent to the old per-batch
        /// [Min,Max] loads - without the massive redundancy.
        ///
        /// Concurrent callers are serialised by a single-permit lock, so the load happens once, and a load
        /// that throws leaves the flag clear so the next batch retries.
        ///
        /// The pre-check outside the lock is an ordinary (non-volatile) read of <c>_runImportCacheBuilt</c>,
        /// with the reference published before the flag is set. That is the pre-#373 code moved verbatim and
        /// it has always worked on the deployed runtime (.NET Framework on x86/x64), but it relies on
        /// implementation behaviour rather than a guarantee the ECMA memory model makes - an ordinary read
        /// is not an acquire. Making it a formal guarantee means <c>Volatile.Read</c>/<c>Volatile.Write</c>,
        /// which is a change to inherited code and therefore out of scope for this extraction. A caller that
        /// lost the race simply takes the lock and re-checks.
        ///
        /// Note <see cref="ActivityImportCache"/> locks the de-duplication members this path uses
        /// (<c>HaveSeenInProcessedOrIgnoredEvents</c>, <c>RememberProcessedEvent</c>,
        /// <c>RememberNewlyIgnoredEvent</c>, <c>ProcessedIdCount</c>) - not every member on it - which is what
        /// makes sharing one cache across concurrent batches safe here.
        ///
        /// Once built, later calls return the same cache and their window argument is ignored.
        /// </summary>
        private async Task<ActivityImportCache> GetOrBuildRunImportCacheAsync(ActivityDedupCacheWindow window)
        {
            if (_runImportCacheBuilt) return _runImportCache;
            await _runImportCacheInitLock.WaitAsync();
            try
            {
                if (!_runImportCacheBuilt)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var built = _loader.Load(window.FromUtc, window.ToUtc);
                    sw.Stop();

                    _logger.LogInformation($"Audit events import: built run dedup cache from audit_events in " +
                        $"{sw.Elapsed.TotalSeconds.ToString("n1")}s ({built.ProcessedIdCount.ToString("n0")} already-processed id(s), " +
                        $"{window.DaysBack}-day window) - reused across all save batches this cycle instead of reloading per batch.");

                    _runImportCache = built;
                    _runImportCacheBuilt = true;
                }
            }
            finally
            {
                _runImportCacheInitLock.Release();
            }
            return _runImportCache;
        }
    }
}
