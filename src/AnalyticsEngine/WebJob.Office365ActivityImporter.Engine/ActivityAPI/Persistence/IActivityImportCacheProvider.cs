using Common.Entities;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence
{
    /// <summary>
    /// Primary-key existence check behind the audit-log de-duplication cache: which of one save batch's event
    /// ids are already in <c>audit_events</c> (imported) or <c>ignored_audit_events</c> (ignored).
    /// </summary>
    public interface IProcessedAuditEventLookup
    {
        /// <summary>
        /// The subset of <paramref name="eventIds"/> already imported or ignored. Looks up those ids and nothing
        /// else, so its cost and memory follow the batch, not the download window.
        /// </summary>
        Task<ICollection<Guid>> FindAlreadyProcessedAsync(IReadOnlyList<Guid> eventIds);
    }

    /// <summary>
    /// Read port for the audit-log de-duplication cache: the one place that queries <c>audit_events</c> and
    /// <c>ignored_audit_events</c> to find out which event ids have already been seen.
    ///
    /// Split out by issue #373 so the cache <i>lifecycle</i> (one run-scoped cache per cycle, or a window load
    /// per batch under the <c>AUDIT_PERBATCH_DEDUP_CACHE</c> safety-valve) can be asserted without SQL Server.
    /// <see cref="Load"/> is deliberately synchronous because
    /// <see cref="ActivityImportCache.GetAndBuildNewCache(DateTime, DateTime)"/> is - wrapping it in a Task would
    /// change when the caller's thread blocks.
    /// </summary>
    public interface IActivityImportCacheLoader : IProcessedAuditEventLookup
    {
        /// <summary>
        /// Load every already-processed (imported or ignored) event id whose timestamp falls in
        /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>]. Only the safety-valve does this; the default
        /// run-scoped cache asks <see cref="IProcessedAuditEventLookup.FindAlreadyProcessedAsync"/> per batch.
        /// </summary>
        ActivityImportCache Load(DateTime fromUtc, DateTime toUtc);
    }

    /// <summary>
    /// Production <see cref="IActivityImportCacheLoader"/>. <see cref="Load"/> is a one-line adapter over the
    /// existing static factory, which is what makes that factory's <c>new AnalyticsEntitiesContext()</c>
    /// substitutable.
    /// </summary>
    public sealed class SqlActivityImportCacheLoader : IActivityImportCacheLoader
    {
        public static readonly SqlActivityImportCacheLoader Instance = new SqlActivityImportCacheLoader();

        public ActivityImportCache Load(DateTime fromUtc, DateTime toUtc)
        {
            return ActivityImportCache.GetAndBuildNewCache(fromUtc, toUtc);
        }

        /// <summary>
        /// Clustered primary-key seeks on both tables, at most <see cref="ProcessedAuditEventLookupQuery.MaxIdsPerQuery"/>
        /// ids per query, over one connection. For an event that is new, the <c>audit_events</c> seek reads the
        /// page the merge's own <c>NOT EXISTS</c> check and insert are about to read anyway.
        /// </summary>
        public async Task<ICollection<Guid>> FindAlreadyProcessedAsync(IReadOnlyList<Guid> eventIds)
        {
            if (eventIds == null) throw new ArgumentNullException(nameof(eventIds));

            var found = new HashSet<Guid>();
            if (eventIds.Count == 0) return found;

            using (var db = new AnalyticsEntitiesContext())
            {
                var connection = db.Database.Connection;
                await AzureSqlTokenAuth.OpenAsync(connection);
                try
                {
                    foreach (var chunk in ProcessedAuditEventLookupQuery.Chunk(eventIds))
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = ProcessedAuditEventLookupQuery.SqlFor(chunk.Length);
                            command.CommandTimeout = 0;     // As the EF context and the window load it replaces.
                            for (var i = 0; i < chunk.Length; i++)
                            {
                                ActivityImportCache.AddParameter(command, "@id" + i, DbType.Guid, chunk[i]);
                            }
                            using (var reader = await command.ExecuteReaderAsync())
                            {
                                while (await reader.ReadAsync())
                                {
                                    found.Add(reader.GetGuid(0));
                                }
                            }
                        }
                    }
                }
                finally
                {
                    connection.Close();
                }
            }
            return found;
        }
    }

    /// <summary>
    /// How <see cref="SqlActivityImportCacheLoader.FindAlreadyProcessedAsync"/> splits a batch's ids into queries.
    /// </summary>
    internal static class ProcessedAuditEventLookupQuery
    {
        /// <summary>
        /// Ids per query. Each id is one parameter, and SQL Server allows at most 2,100 per command.
        /// </summary>
        internal const int MaxIdsPerQuery = 1000;

        /// <summary>
        /// Every query is padded to one of these parameter counts by repeating its last id (harmless in an
        /// <c>IN</c> list), so SQL Server compiles and caches at most five plans rather than one for every
        /// distinct number of not-yet-known ids a batch happens to have.
        /// </summary>
        internal static readonly int[] ParameterCounts = { 10, 100, 250, 500, MaxIdsPerQuery };

        private static readonly ConcurrentDictionary<int, string> SqlByParameterCount = new ConcurrentDictionary<int, string>();

        internal static IEnumerable<Guid[]> Chunk(IReadOnlyList<Guid> ids)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));

            for (var start = 0; start < ids.Count; start += MaxIdsPerQuery)
            {
                var count = Math.Min(MaxIdsPerQuery, ids.Count - start);
                var chunk = new Guid[ParameterCounts.First(size => size >= count)];
                for (var i = 0; i < chunk.Length; i++)
                {
                    chunk[i] = ids[start + Math.Min(i, count - 1)];
                }
                yield return chunk;
            }
        }

        internal static string SqlFor(int parameterCount)
        {
            return SqlByParameterCount.GetOrAdd(parameterCount, n =>
            {
                var parameters = string.Join(",", Enumerable.Range(0, n).Select(i => "@id" + i));
                return $"SELECT id FROM dbo.audit_events WHERE id IN ({parameters}) " +
                    "UNION ALL " +
                    $"SELECT event_id FROM dbo.ignored_audit_events WHERE event_id IN ({parameters});";
            });
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
        /// cache loaded over the batch's own span; for <see cref="ActivityDedupCacheScope.PerRun"/> it is the
        /// single run-scoped cache, created on first use and returned unchanged thereafter.
        /// </summary>
        Task<ActivityImportCache> GetForWindowAsync(ActivityDedupCacheWindow window);
    }

    /// <summary>
    /// The de-duplication cache lifecycle for one import cycle, lifted out of
    /// <c>ActivityReportSqlPersistenceManager</c> by issue #373.
    ///
    /// One instance per import cycle (the persistence manager is itself created per cycle), so every save batch
    /// of the cycle shares ONE run-scoped cache: what one batch stages or ignores is seen by the next, including
    /// a concurrent one.
    ///
    /// That cache no longer loads the download window. It used to load every <c>audit_events</c> /
    /// <c>ignored_audit_events</c> id from the last <c>DaysBeforeNowToDownload</c> + 1 days at the start of
    /// every cycle and hold them all - as EF rows, then a list, then hourly dictionaries - which is several GB on
    /// a tenant with millions of events a day, whether the cycle had two million new events or two thousand.
    /// Now each save batch checks only its own ids, by primary key (<see cref="IProcessedAuditEventLookup"/>,
    /// through <see cref="ActivityImportCache.WithStoreLookupAsync"/>), so memory follows the batch and the
    /// cycle's own decisions. Correctness is unchanged: every event a batch sees is checked against the same
    /// tables, and the merge SQL's <c>NOT EXISTS</c> guards remain the cross-instance backstop.
    ///
    /// <see cref="ActivityDedupCacheScope.PerBatch"/> is the <c>AUDIT_PERBATCH_DEDUP_CACHE</c> operator
    /// safety-valve: it restores the old per-batch window load without a redeploy.
    /// </summary>
    public sealed class ActivityImportCacheProvider : IActivityImportCacheProvider
    {
        private readonly IActivityImportCacheLoader _loader;
        private readonly ILogger _logger;

        private ActivityImportCache _runImportCache;
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

            return await GetOrCreateRunImportCacheAsync(window);
        }

        /// <summary>
        /// Create the run-scoped cache ONCE. Nothing is loaded, so there is no expensive build to fail and be
        /// retried by every later batch; a batch whose own lookup fails fails alone and is retried as usual.
        ///
        /// Concurrent callers are serialised by a single-permit lock so they all get the same instance, which
        /// is what lets a reservation made by one batch stop a concurrent batch staging the same id. Once
        /// created, later calls return the same cache and their window argument is ignored.
        /// </summary>
        private async Task<ActivityImportCache> GetOrCreateRunImportCacheAsync(ActivityDedupCacheWindow window)
        {
            var existing = Volatile.Read(ref _runImportCache);
            if (existing != null) return existing;

            await _runImportCacheInitLock.WaitAsync();
            try
            {
                if (_runImportCache == null)
                {
                    _logger?.LogInformation("Audit events import: run dedup cache ready - each save batch checks only its own event ids " +
                        $"against audit_events and ignored_audit_events by primary key (up to {ProcessedAuditEventLookupQuery.MaxIdsPerQuery} " +
                        $"per query) instead of loading the {window.DaysBack}-day window into memory. Events staged or ignored this cycle " +
                        "are shared by every save batch.");

                    Volatile.Write(ref _runImportCache, ActivityImportCache.ForRun(_loader));
                }
                return _runImportCache;
            }
            finally
            {
                _runImportCacheInitLock.Release();
            }
        }
    }
}
