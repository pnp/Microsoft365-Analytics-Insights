using Common.DataUtils;
using Common.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// List of activity reports that are cached either because they're processed or newly ignored. Processed includes previously ignored. 
    /// Cache is chunked into items per hour to speed up indexing?
    /// </summary>
    public class ActivityImportCache
    {
        private ActivityImportCache()
        {
            ProcessedIDs = new Dictionary<int, Dictionary<Guid, DateTime>>();
            NewlyIgnoredIDs = new Dictionary<int, Dictionary<Guid, DateTime>>();
            AnonymisedUserNameCache = new Dictionary<string, string>();
        }

        public static ActivityImportCache GetEmptyCache()
        {
            return new ActivityImportCache();
        }

        public static ActivityImportCache GetAndBuildNewCache(DateTime cacheFrom, DateTime cacheTo)
        {
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                return GetAndBuildNewCache(cacheFrom, cacheTo, db);
            }
        }

        /// <summary>
        /// Get new import-cache, with processed & ignored IDs already added for a specific range
        /// </summary>
        public static ActivityImportCache GetAndBuildNewCache(DateTime cacheFrom, DateTime cacheTo, AnalyticsEntitiesContext db)
        {
            if (cacheFrom > cacheTo)
            {
                throw new ArgumentOutOfRangeException("'From' date-time should be greater than 'to' clause.");
            }

            // Include an extra minute either side of cache-loading range, as EF6 assumes datetime2 which can miss some datetime edge values
            // This is easier than doing a new migration to convert every DT field.
            cacheTo = cacheTo.AddMinutes(1);
            cacheFrom = cacheFrom.AddMinutes(-1);

#if DEBUG
            Console.WriteLine($"DEBUG: Loading activity cache from {cacheFrom} to {cacheTo}.");
#endif

            var c = new ActivityImportCache();

            // Build list of events to ignore
            // Get events from X time before
            var ignoredEvents = db.ignored_audit_events.Where(e => e.processed_timestamp >= cacheFrom && e.processed_timestamp <= cacheTo).ToList();

            var importedEventsQuery = db.AuditEventsCommon.Where(e => e.TimeStamp >= cacheFrom && e.TimeStamp <= cacheTo)
                .Select(e => new BasicEventDetails() { ID = e.Id, Time = e.TimeStamp });
            var importedEvents = importedEventsQuery.ToList();


            // Processed means "ignored or saved"
            foreach (var ignoredEvent in ignoredEvents)
            {
                c.AddProcessedID(ignoredEvent.event_id, ignoredEvent.processed_timestamp);
            }
            foreach (var e in importedEvents)
            {
                c.AddProcessedID(e.ID, e.Time);
            }


            return c;
        }

        // Just used above
        private class BasicEventDetails
        {
            public DateTime Time { get; set; }
            public Guid ID { get; set; }
        }

        public enum CacheType
        {
            None,
            NewlyIgnored,
            Processed
        }

        /// <summary>
        /// Return the cache dictionary for a specific DT
        /// </summary>
        Dictionary<Guid, DateTime> GetCacheChunkForAuditLog(DateTime dt, CacheType cacheType)
        {
            if (cacheType == CacheType.None)
            {
                throw new ArgumentOutOfRangeException("cacheType");
            }

            TimeSpan span = dt.Subtract(DateTime.MinValue);
            int key = (int)Math.Round(span.TotalHours, 0);
            Dictionary<int, Dictionary<Guid, DateTime>> targetDictionary = null;

            // Pick type of dictionary
            if (cacheType == CacheType.NewlyIgnored)
            {
                targetDictionary = NewlyIgnoredIDs;
            }
            else if (cacheType == CacheType.Processed)
            {
                targetDictionary = ProcessedIDs;
            }

            // Pick cache-chunk by hour
            if (!targetDictionary.ContainsKey(key))
            {
                targetDictionary.Add(key, new Dictionary<Guid, DateTime>());
            }
            return targetDictionary[key];
        }

        /// <summary>
        /// Return the cache dictionary for a specific DT
        /// </summary>
        Dictionary<Guid, DateTime> GetCacheChunkForAuditLog(AbstractAuditLogContent log, CacheType type)
        {
            return GetCacheChunkForAuditLog(log.CreationTime, type);
        }

        void AddProcessedID(Guid id, DateTime processedDate)
        {
            lock (this)
            {
                this.GetCacheChunkForAuditLog(processedDate, CacheType.Processed).Add(id, processedDate);
            }
        }


        /// <summary>
        /// Cached list of all processed event IDs
        /// </summary>
        private Dictionary<int, Dictionary<Guid, DateTime>> ProcessedIDs { get; set; }

        /// <summary>
        /// List of all event IDs ignored (not imported). Events also added to ProcessedIDs.
        /// </summary>
        private Dictionary<int, Dictionary<Guid, DateTime>> NewlyIgnoredIDs { get; set; }

        private List<OrgUrl> _orgUrlCache = null;
        public List<OrgUrl> OrgUrlCache(System.Data.Entity.Database database)
        {
            if (_orgUrlCache == null)
            {
                using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext(database.Connection.ConnectionString, true, false))
                {
                    _orgUrlCache = db.org_urls.ToList();
                }
            }
            return _orgUrlCache;
        }

        /// <summary>
        /// Real username + hash
        /// </summary>
        public Dictionary<string, string> AnonymisedUserNameCache { get; set; }

        public List<Dictionary<Guid, DateTime>> GetIds(CacheType cacheType)
        {
            if (cacheType == CacheType.NewlyIgnored)
            {
                return this.NewlyIgnoredIDs.Values.ToList();
            }
            else if (cacheType == CacheType.Processed)
            {
                return this.ProcessedIDs.Values.ToList();
            }
            throw new ArgumentOutOfRangeException("cacheType");
        }

        /// <summary>
        /// Clear "ignored" cache
        /// </summary>
        public void ClearNewIgnoredIDs()
        {
            lock (this)
            {
                this.NewlyIgnoredIDs.Clear();

            }
        }

        /// <summary>
        /// Checks the "processed" and ignored cache.
        /// </summary>
        public bool HaveSeenInProcessedOrIgnoredEvents(AbstractAuditLogContent auditLogContent)
        {
            return HaveSeenInProcessedOrIgnoredEvents(auditLogContent.Id);
        }

        public bool HaveSeenInProcessedOrIgnoredEvents(Guid id)
        {
            lock (this)
            {

                foreach (Dictionary<Guid, DateTime> cache in this.ProcessedIDs.Values)
                {
                    if (cache.ContainsKey(id))
                    {
                        return true;
                    }
                }

                foreach (Dictionary<Guid, DateTime> cache in this.NewlyIgnoredIDs.Values)
                {
                    if (cache.ContainsKey(id))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Add item to "processed" queue
        /// </summary>
        public void RememberProcessedEvent(AbstractAuditLogContent auditLogContent)
        {
            lock (this)
            {
                Dictionary<Guid, DateTime> theCache = this.GetCacheChunkForAuditLog(auditLogContent, CacheType.Processed);
                theCache.Add(auditLogContent.Id, auditLogContent.CreationTime);
            }
        }

        /// <summary>
        /// Remember a new event to ignore. Will be saved in DownloadChunk.DownloadAllAndSaveToSQL
        /// </summary>
        internal void RememberNewlyIgnoredEvent(AbstractAuditLogContent auditLogContent)
        {
            lock (this)
            {
                this.GetCacheChunkForAuditLog(auditLogContent, CacheType.NewlyIgnored).Add(auditLogContent.Id, auditLogContent.CreationTime);
                this.GetCacheChunkForAuditLog(auditLogContent, CacheType.Processed).Add(auditLogContent.Id, auditLogContent.CreationTime);
            }
        }

        // Sync for ProcessedIDs
        private static object _idCheckLock = new object();


        internal async Task SaveNewlyIgnoredEvents(AnalyticsEntitiesContext db)
        {
            // Update ignored
            List<IgnoredEvent> ignoreList = new List<IgnoredEvent>();

            // Prevent any loader threads from adding to "ignored events" cache
            lock (_idCheckLock)
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
                const string PRIMARY_KEY_VIOLATION = "Violation of PRIMARY KEY constraint 'PK_processed_audit_events'. Cannot insert duplicate key in object 'dbo.ignored_audit_events'";
                if (CommonExceptionHandler.GetErrorText(ex).Contains(PRIMARY_KEY_VIOLATION))
                {
                    await DeleteIgnoredEvents(ignoreList, db);
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
