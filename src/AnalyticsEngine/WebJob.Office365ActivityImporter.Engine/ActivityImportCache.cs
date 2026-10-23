using Common.Entities;
using DataUtils;
using DataUtils.Sql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// Which audit events have already been imported or ignored, so a save batch does not stage them again.
    ///
    /// Two kinds of knowledge, kept apart on purpose:
    /// <list type="bullet">
    /// <item>What this run decided - the events it staged or newly ignored. Held in memory and shared by every
    ///       save batch of the cycle, so a concurrent batch cannot stage the same id.</item>
    /// <item>What the database already holds. The run cache does not load that up front: each save batch asks
    ///       <see cref="IProcessedAuditEventLookup"/> about its own ids only (<see cref="WithStoreLookupAsync"/>)
    ///       and decides against a per-batch view. The previous design loaded every id in the download window
    ///       plus a day (seven days by default) into hourly dictionaries for the whole cycle, which is several GB
    ///       on a tenant with millions of events a day, even when the cycle had only a few thousand new events.</item>
    /// </list>
    /// The <c>AUDIT_PERBATCH_DEDUP_CACHE</c> safety-valve still bulk-loads a window, through
    /// <see cref="GetAndBuildNewCache(DateTime, DateTime)"/>.
    /// </summary>
    public class ActivityImportCache
    {
        /// <summary>
        /// Everything the run cache and its per-batch views share.
        /// </summary>
        private sealed class RunState
        {
            public RunState(IProcessedAuditEventLookup store)
            {
                Store = store;
            }

            // Single private lock guarding the three collections below and the snapshots taken from them.
            public readonly object Lock = new object();

            /// <summary>Processed ids bulk-loaded from SQL, rather than decided by this run.</summary>
            public readonly HashSet<Guid> LoadedIds = new HashSet<Guid>();

            /// <summary>
            /// Ids this run staged or newly ignored. For a staged id this is a tentative reservation: the save path
            /// releases it again if its merge or metadata phase fails, so the retry can stage the event again.
            /// </summary>
            public readonly HashSet<Guid> RememberedThisCycle = new HashSet<Guid>();

            /// <summary>Ids this run ignored (URL out of scope), with their CreationTime.</summary>
            public readonly Dictionary<Guid, DateTime> NewlyIgnored = new Dictionary<Guid, DateTime>();

            /// <summary>Where a per-batch view asks about ids memory cannot answer. Null for a memory-only cache.</summary>
            public readonly IProcessedAuditEventLookup Store;

            public Dictionary<string, string> AnonymisedUserNameCache = new Dictionary<string, string>();
            public List<OrgUrl> OrgUrlCache;
        }

        private readonly RunState _run;

        /// <summary>
        /// Per-batch views only: the batch's ids the database already holds as imported or ignored. Never changed
        /// once the view exists, so it is read without the lock, and it goes when the view goes - which is what
        /// keeps the database's answers to one batch's worth of memory.
        /// </summary>
        private readonly HashSet<Guid> _batchIdsAlreadyInStore;

        private ActivityImportCache(RunState run, HashSet<Guid> batchIdsAlreadyInStore)
        {
            _run = run;
            _batchIdsAlreadyInStore = batchIdsAlreadyInStore;
        }

        public static ActivityImportCache GetEmptyCache()
        {
            return new ActivityImportCache(new RunState(null), null);
        }

        /// <summary>
        /// The run-scoped cache: starts empty, and each save batch's ids are checked against
        /// <paramref name="store"/> through <see cref="WithStoreLookupAsync"/>.
        /// </summary>
        internal static ActivityImportCache ForRun(IProcessedAuditEventLookup store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            return new ActivityImportCache(new RunState(store), null);
        }

        public static ActivityImportCache GetAndBuildNewCache(DateTime cacheFrom, DateTime cacheTo)
        {
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                return GetAndBuildNewCache(cacheFrom, cacheTo, db);
            }
        }

        /// <summary>
        /// Get new import-cache, with processed & ignored IDs already added for a specific range. This is the
        /// <c>AUDIT_PERBATCH_DEDUP_CACHE</c> safety-valve's load; the default run-scoped cache loads nothing up front.
        /// </summary>
        public static ActivityImportCache GetAndBuildNewCache(DateTime cacheFrom, DateTime cacheTo, AnalyticsEntitiesContext db)
        {
            if (cacheFrom > cacheTo)
            {
                throw new ArgumentOutOfRangeException("'From' date-time should be greater than 'to' clause.");
            }
            if (db == null) throw new ArgumentNullException(nameof(db));

            // Include an extra minute either side of cache-loading range: the parameters are datetime2 while the
            // columns are datetime, which can miss some edge values. This is easier than doing a new migration to
            // convert every DT field.
            cacheTo = ActivityImportCacheWindow.PadTo(cacheTo);
            cacheFrom = ActivityImportCacheWindow.PadFrom(cacheFrom);

#if DEBUG
            Console.WriteLine($"DEBUG: Loading activity cache from {cacheFrom} to {cacheTo}.");
#endif

            var c = GetEmptyCache();

            // Stream the ids straight into one set - "processed" means "ignored or saved". No per-row objects, no
            // timestamps and no hourly copies: the previous EF query was buffered in full (EF buffers every row
            // while the Azure retrying execution strategy is registered), then copied into a list of id + time
            // objects, then copied again into hourly dictionaries, all alive at once.
            var connection = db.Database.Connection;
            var openedHere = connection.State != ConnectionState.Open;
            if (openedHere)
            {
                AzureSqlTokenAuth.OpenAsync(connection).GetAwaiter().GetResult();
            }
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = ProcessedIdsInWindowSql;
                    command.CommandTimeout = 0;     // As the EF context: a wide window on a large table can take a while.
                    AddParameter(command, "@from", DbType.DateTime2, cacheFrom);
                    AddParameter(command, "@to", DbType.DateTime2, cacheTo);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            c._run.LoadedIds.Add(reader.GetGuid(0));
                        }
                    }
                }
            }
            finally
            {
                if (openedHere) connection.Close();
            }

            return c;
        }

        /// <summary>
        /// Ids of the events imported (<c>audit_events.time_stamp</c>) or ignored
        /// (<c>ignored_audit_events.processed_timestamp</c>) in [@from, @to]. Both columns are indexed by
        /// migration <c>IndexAuditEventsTimeStamp</c>; the predicates are the ones EF generated before.
        /// </summary>
        internal const string ProcessedIdsInWindowSql =
            "SELECT id FROM dbo.audit_events WHERE time_stamp >= @from AND time_stamp <= @to " +
            "UNION ALL " +
            "SELECT event_id FROM dbo.ignored_audit_events WHERE processed_timestamp >= @from AND processed_timestamp <= @to;";

        internal static void AddParameter(DbCommand command, string name, DbType type, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.DbType = type;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        public enum CacheType
        {
            None,
            NewlyIgnored,
            Processed
        }

        internal void RememberLoadedProcessedEvent(AbstractAuditLogContent auditLogContent)
        {
            if (auditLogContent == null) throw new ArgumentNullException(nameof(auditLogContent));
            lock (_run.Lock)
            {
                _run.LoadedIds.Add(auditLogContent.Id);
            }
        }

        internal bool WasRememberedThisCycle(AbstractAuditLogContent auditLogContent)
        {
            if (auditLogContent == null) throw new ArgumentNullException(nameof(auditLogContent));

            lock (_run.Lock)
            {
                // By id alone. The old hourly chunks keyed this on CreationTime, so the same event presented with a
                // differently-rounded time could land in another chunk and be missed.
                return _run.RememberedThisCycle.Contains(auditLogContent.Id);
            }
        }

        /// <summary>
        /// How many processed (imported-or-ignored) event ids are held in memory: ids bulk-loaded by the
        /// safety-valve plus the ids this run staged or ignored. A per-batch view's database answers are not
        /// held, so they are not counted.
        /// </summary>
        public int ProcessedIdCount
        {
            get
            {
                lock (_run.Lock)
                {
                    return _run.LoadedIds.Count + _run.RememberedThisCycle.Count(id => !_run.LoadedIds.Contains(id));
                }
            }
        }

        /// <summary>
        /// A view of this cache for one save batch that also knows which of <paramref name="eventIds"/> the
        /// database already holds as imported or ignored. Only ids memory cannot already answer are looked up, in
        /// one call, so the cost follows the batch rather than the download window, and the answers live only as
        /// long as the view. Whatever the batch remembers or forgets still goes to the shared run state, so other
        /// batches see it.
        ///
        /// Returns this cache unchanged when it has no store (a memory-only or safety-valve cache) or nothing needs
        /// asking. A store failure propagates: the batch fails before staging anything, and is retried.
        /// </summary>
        internal async Task<ActivityImportCache> WithStoreLookupAsync(IEnumerable<Guid> eventIds)
        {
            if (eventIds == null) throw new ArgumentNullException(nameof(eventIds));

            var store = _run.Store;
            if (store == null) return this;

            var candidates = new List<Guid>();
            var distinct = new HashSet<Guid>();
            foreach (var id in eventIds)
            {
                if (distinct.Add(id)) candidates.Add(id);
            }

            var toLookUp = new List<Guid>(candidates.Count);
            lock (_run.Lock)
            {
                foreach (var id in candidates)
                {
                    if (!IsKnownInMemory(id)) toLookUp.Add(id);
                }
            }
            if (toLookUp.Count == 0) return this;

            var alreadyInStore = await store.FindAlreadyProcessedAsync(toLookUp);
            if (alreadyInStore == null || alreadyInStore.Count == 0) return this;

            var batchIdsAlreadyInStore = new HashSet<Guid>(alreadyInStore);
            if (_batchIdsAlreadyInStore != null)
            {
                batchIdsAlreadyInStore.UnionWith(_batchIdsAlreadyInStore);
            }
            return new ActivityImportCache(_run, batchIdsAlreadyInStore);
        }

        // Caller holds _run.Lock.
        private bool IsKnownInMemory(Guid id)
        {
            return _run.RememberedThisCycle.Contains(id)
                || _run.LoadedIds.Contains(id)
                || _run.NewlyIgnored.ContainsKey(id)
                || (_batchIdsAlreadyInStore != null && _batchIdsAlreadyInStore.Contains(id));
        }

        public List<OrgUrl> OrgUrlCache(System.Data.Entity.Database database)
        {
            if (_run.OrgUrlCache == null)
            {
                using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext(database.Connection.ConnectionString, true, false))
                {
                    _run.OrgUrlCache = db.org_urls.ToList();
                }
            }
            return _run.OrgUrlCache;
        }

        /// <summary>
        /// Real username + hash
        /// </summary>
        public Dictionary<string, string> AnonymisedUserNameCache
        {
            get { return _run.AnonymisedUserNameCache; }
            set { _run.AnonymisedUserNameCache = value; }
        }

        /// <summary>
        /// A snapshot of the newly-ignored ids and their CreationTime, as a single chunk. Processed ids are not
        /// available here: they are held as a compact id set without timestamps, so use
        /// <see cref="HaveSeenInProcessedOrIgnoredEvents(Guid)"/> or <see cref="ProcessedIdCount"/>.
        /// </summary>
        public List<Dictionary<Guid, DateTime>> GetIds(CacheType cacheType)
        {
            if (cacheType == CacheType.NewlyIgnored)
            {
                lock (_run.Lock)
                {
                    return new List<Dictionary<Guid, DateTime>> { new Dictionary<Guid, DateTime>(_run.NewlyIgnored) };
                }
            }
            else if (cacheType == CacheType.Processed)
            {
                throw new NotSupportedException("Processed ids are held as a compact set without timestamps; " +
                    "use HaveSeenInProcessedOrIgnoredEvents or ProcessedIdCount.");
            }
            throw new ArgumentOutOfRangeException("cacheType");
        }

        /// <summary>
        /// Clear "ignored" cache
        /// </summary>
        public void ClearNewIgnoredIDs()
        {
            lock (_run.Lock)
            {
                _run.NewlyIgnored.Clear();
            }
        }

        /// <summary>
        /// Checks the "processed" and ignored cache.
        /// </summary>
        public bool HaveSeenInProcessedOrIgnoredEvents(AbstractAuditLogContent auditLogContent)
        {
            return HaveSeenInProcessedOrIgnoredEvents(auditLogContent.Id);
        }

        /// <summary>
        /// Has this id been imported or ignored: by this run, by the safety-valve's bulk load, or - on a per-batch
        /// view - according to the database? One hash lookup per set, not one per hour of the window.
        /// </summary>
        public bool HaveSeenInProcessedOrIgnoredEvents(Guid id)
        {
            if (_batchIdsAlreadyInStore != null && _batchIdsAlreadyInStore.Contains(id))
            {
                return true;
            }

            lock (_run.Lock)
            {
                return _run.RememberedThisCycle.Contains(id)
                    || _run.LoadedIds.Contains(id)
                    || _run.NewlyIgnored.ContainsKey(id);
            }
        }

        /// <summary>
        /// Add item to "processed" queue. A save attempt uses this as a tentative reservation so another
        /// concurrent batch does not stage the same id; the save path removes the reservation if its merge
        /// or metadata phase fails, allowing the retry to stage the event again.
        /// </summary>
        public void RememberProcessedEvent(AbstractAuditLogContent auditLogContent)
        {
            lock (_run.Lock)
            {
                // A set, so remembering the same id twice is idempotent - the run-scoped cache is shared by
                // concurrent save batches, which can both remember one event id.
                _run.RememberedThisCycle.Add(auditLogContent.Id);
            }
        }

        /// <summary>
        /// Release processed-id reservations made by a save attempt that did not complete. Events that were
        /// newly staged are supplied here; on a retry-aware pass that may include an id the safety-valve loaded
        /// from SQL after an earlier attempt committed the audit row but failed during metadata. A per-batch view's
        /// database answers are not touched: they are dropped with the failed batch's view.
        /// </summary>
        internal int ForgetProcessedEvents(IEnumerable<AbstractAuditLogContent> auditLogContents)
        {
            if (auditLogContents == null) throw new ArgumentNullException(nameof(auditLogContents));

            lock (_run.Lock)
            {
                var removedIds = new HashSet<Guid>();
                foreach (var auditLogContent in auditLogContents)
                {
                    if (auditLogContent == null) continue;

                    var wasRemembered = _run.RememberedThisCycle.Remove(auditLogContent.Id);
                    var wasLoaded = _run.LoadedIds.Remove(auditLogContent.Id);
                    if (wasRemembered || wasLoaded)
                    {
                        removedIds.Add(auditLogContent.Id);
                    }
                }

                return removedIds.Count;
            }
        }

        /// <summary>
        /// Remember a new event to ignore. Will be saved in DownloadChunk.DownloadAllAndSaveToSQL
        /// </summary>
        internal void RememberNewlyIgnoredEvent(AbstractAuditLogContent auditLogContent)
        {
            lock (_run.Lock)
            {
                // Indexer (not .Add): idempotent for the shared run-scoped cache (see RememberProcessedEvent).
                _run.NewlyIgnored[auditLogContent.Id] = auditLogContent.CreationTime;
                _run.RememberedThisCycle.Add(auditLogContent.Id);
            }
        }


        internal async Task SaveNewlyIgnoredEvents(AnalyticsEntitiesContext db)
        {
            // Update ignored
            List<IgnoredEvent> ignoreList = new List<IgnoredEvent>();

            // Snapshot + clear of in-memory cache must be atomic so concurrent loader threads don't
            // add events between the snapshot and the clear (causing those events to be permanently
            // lost from both the in-memory cache and the DB save).
            lock (_run.Lock)
            {
                // Save newly ignored events to table in SQL
                foreach (var cache in this.GetIds(ActivityImportCache.CacheType.NewlyIgnored))
                {
                    foreach (var newIgnoredEvent in cache)
                    {
                        ignoreList.Add(new IgnoredEvent() { event_id = newIgnoredEvent.Key, processed_timestamp = newIgnoredEvent.Value });
                    }
                }
                // Events processes also added to ProcessedEvents. 
                this.ClearNewIgnoredIDs();
            }
            db.ignored_audit_events.AddRange(ignoreList);

            try
            {
                await db.SaveChangesAsync();
            }
            catch (System.Data.Entity.Infrastructure.DbUpdateException ex)
            {
                // Detect duplicate-key violations by SQL error number (2601/2627) - the same robust
                // approach used by DBLookupCache. The previous hard-coded message match never fired
                // (it named the wrong constraint, PK_processed_audit_events, for the
                // ignored_audit_events table), so the dedup retry was dead code AND every genuine
                // DbUpdateException was silently swallowed.
                var sqlException = ex.InnerException?.InnerException as SqlException;
                if (sqlException != null && (sqlException.Number == 2601 || sqlException.Number == 2627))
                {
                    await DeleteIgnoredEvents(ignoreList, db);

                    // Re-insert the current batch after deleting the duplicate older rows. Without this
                    // retry the events were silently lost (already cleared from in-memory cache, never
                    // committed to the ignored table), causing them to be re-processed on the next run.
                    db.ignored_audit_events.AddRange(ignoreList);
                    await db.SaveChangesAsync();
                }
                else
                {
                    // Not a duplicate-key error - don't swallow a genuine DB failure (timeout,
                    // deadlock, connection drop, etc.); surface it so the import fails loudly.
                    throw;
                }
            }
            Console.WriteLine($"Saved {ignoreList.Count.ToString("n0")} events to ignore-list.");
        }



        private async Task DeleteIgnoredEvents(List<IgnoredEvent> ignoreList, AnalyticsEntitiesContext db)
        {
            var ignoreGuids = ignoreList.Select(e => e.event_id).ToList();
            var ignoreEventRecords = db.ignored_audit_events.Where(e => ignoreGuids.Contains(e.event_id)).ToList();

            db.ignored_audit_events.RemoveRange(ignoreEventRecords);

            await db.SaveChangesAsync();

            Console.WriteLine($"UNEXPECTED: Hit duplicate \"new\" events to ignore. Deleting {ignoreEventRecords.Count} old duplicates from ignore-list.");
        }
    }
}
