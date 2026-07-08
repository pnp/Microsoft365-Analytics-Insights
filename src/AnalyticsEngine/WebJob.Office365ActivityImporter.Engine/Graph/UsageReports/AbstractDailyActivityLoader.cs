using Common.Entities;
using Common.Entities.ActivityReports;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    /// <summary>
    /// Generic Graph report loader. Recursively loads and saves any Graph activity report.
    /// </summary>
    /// <typeparam name="TReportDbType">Type of EF table</typeparam>
    /// <typeparam name="TUserActivityUserDetail">Type of report page</typeparam>
    public abstract class AbstractDailyActivityLoader<TReportDbType, TUserActivityUserDetail, TLookupType, CACHETYPE> : ActivityReportLoader
        where TReportDbType : AbstractUsageActivityLog, new()
        where TUserActivityUserDetail : AbstractActivityRecord<TLookupType>
        where TLookupType : AbstractEFEntity
        where CACHETYPE : DBLookupCache<TLookupType>
    {
        protected readonly ManualGraphCallClient _client;
        internal AbstractDailyActivityLoader(ManualGraphCallClient client, ILogger logger) : base(logger)
        {
            _client = client;
        }

        public abstract DbSet<TReportDbType> GetTable(AnalyticsEntitiesContext context);

        public Dictionary<DateTime, List<TUserActivityUserDetail>> LoadedReportPages { get; set; } = new Dictionary<DateTime, List<TUserActivityUserDetail>>();

        /// <summary>
        /// How many usage-log rows to persist per EF SaveChanges (and per existence query). Kept small enough
        /// that EF6 never builds millions of insert/update command trees in a single call (which
        /// OutOfMemoryExceptions at large-tenant scale) and that the per-batch IN clause stays well under SQL
        /// Server's parameter limit. Settable so tests can exercise the batch boundary cheaply.
        /// </summary>
        public int SaveBatchSize { get; set; } = 1000;

        public async Task PopulateLoadedReportPagesFromGraph(int daysBackMax)
        {
            // Activity reports don't tend to refresh until a couple of days late. Make sure we collect something useful. 
            if (daysBackMax < 3) daysBackMax = 3;
            else if (daysBackMax > 28) daysBackMax = 28;        // Also don't live for more than 28 days

            LoadedReportPages.Clear();

            for (int daysBackIdx = 0; daysBackIdx < daysBackMax; daysBackIdx++)
            {
                // Go back one extra day always. Otherwise we risk asking for data too soon...
                // Example: Message: {"error":{"code":"InvalidArgument","message":"Invalid date value specified: $DateTime.Now. Only support data for the past 28 days."}}
                var daysBack = (daysBackIdx + 1) * -1;
                // Graph Usage Reports API operates in UTC; DateTime.Now on a non-UTC server
                // produces the wrong date bucket near midnight.
                var dt = DateTime.UtcNow.AddDays(daysBack);

                Telemetry.LogInformation($"Loading {this.GetType().Name} for date {dt.ToString("dd-MM-yyyy")}");

                var requestUrl = $"{ReportGraphURL}(date={dt.ToString("yyyy-MM-dd")})?$format=application/json";
                var dayReports = await _client.LoadAllPagesWithThrottleRetries<TUserActivityUserDetail>(requestUrl, Telemetry);

                if (LoadedReportPages.ContainsKey(dt))
                {
                    Telemetry.LogWarning($"Duplicate date {dt.ToString("dd-MM-yyyy")}");
                }
                else
                {
                    Telemetry.LogInformation($"Finished loading {this.GetType().Name} for date {dt.ToString("dd-MM-yyyy")}");
                    LoadedReportPages.Add(dt, dayReports);
                }
            }
        }

        /// <summary>
        /// Save to SQL. Needs a shared ConcurrentLookupDbIdsCache if running in parallel with other imports.
        /// </summary>
        public async Task SaveLoadedReportsToSql(ConcurrentLookupDbIdsCache userEmailToDbIdCache, CACHETYPE lookupCache)
        {
            int i = 0; var enUS = new System.Globalization.CultureInfo("en-US");
            var db = lookupCache.DB;

            Telemetry.LogInformation($"Saving {this.GetType().Name} for {LoadedReportPages.Keys.Count} dates");

            // Compute total once. The previous "LoadedReportPages.SelectMany(r => r.Value).Count()"
            // call ran on every 1000-row progress print, making progress O(n^2).
            var totalReports = LoadedReportPages.Sum(kv => kv.Value.Count);

            // Persist one day at a time, committing in fixed-size batches. Previously every row across every date
            // was added to a single context and committed in ONE SaveChangesAsync, and every existing row across
            // the whole date range was pre-loaded and tracked. At ~200k users x up to 28 days EF6 builds an
            // insert/update command tree for every pending row at once and throws OutOfMemoryException on a small
            // App Service (observed on a 7GB P2v2). Reading only one day at a time and flushing in batches keeps
            // the command-tree build bounded; auto change-detection is turned off so adding a day's rows stays
            // O(n) instead of O(n^2). AssociatedLookupId is [NotMapped] (it maps to UserID / YammerGroupID per
            // subclass), so existing rows can only be filtered in SQL by the mapped Date column - we key them by
            // lookup id in memory.
            var autoDetectWasEnabled = db.Configuration.AutoDetectChangesEnabled;
            db.Configuration.AutoDetectChangesEnabled = false;
            try
            {
                foreach (var dateTime in LoadedReportPages.Keys)
                {
                    // This day's existing rows, tracked (so updates go through the identity map without attach
                    // conflicts), keyed in memory by the [NotMapped] AssociatedLookupId.
                    var existingByLookupId = new Dictionary<int, TReportDbType>();
                    foreach (var existingRow in await GetTable(db).Where(t => t.Date == dateTime.Date).ToListAsync())
                    {
                        // Graph returns one row per lookup per date; last wins if the DB somehow has duplicates.
                        existingByLookupId[existingRow.AssociatedLookupId] = existingRow;
                    }

                    var pendingChanges = 0;
                    foreach (var reportPage in LoadedReportPages[dateTime])
                    {
                        // A usage-report row with no user/group identifier (e.g. a Graph row with a null
                        // userPrincipalName when report anonymisation is enabled on the tenant) can't be matched
                        // to a DB lookup. Skip it rather than NRE / ArgumentNullException deeper in the loop.
                        if (string.IsNullOrWhiteSpace(reportPage.LookupFieldValue))
                        {
                            Telemetry.LogWarning($"Skipping a {typeof(TReportDbType).Name} report row with no lookup identifier (null/empty user or group name).");
                            continue;
                        }

                        // Usually an Entra ID group-membership check for a group filter.
                        if (!await IdInScope(reportPage.LookupFieldValue))
                        {
                            Telemetry.LogInformation($"Skipping {reportPage.LookupFieldValue} as not in scope");
                            continue;
                        }

                        var lookupId = await ResolveLookupIdAsync(reportPage, userEmailToDbIdCache, lookupCache);

                        // Output progress every 1000 imports
                        if (i > 0 && i % 1000 == 0)
                        {
                            Console.WriteLine($"{this.GetType().Name}: Saved {i} / {totalReports}");
                        }

                        // Upsert: reuse the existing row for this (date, lookup) if we have one, else insert.
                        var isNewLog = !existingByLookupId.TryGetValue(lookupId, out var dateRequestedLog);
                        if (isNewLog)
                        {
                            dateRequestedLog = new TReportDbType() { AssociatedLookupId = lookupId };
                            existingByLookupId[lookupId] = dateRequestedLog;
                        }

                        // Set log stats
                        dateRequestedLog.Date = dateTime.Date;

                        // Example: "2017-08-30"
                        var activityDate = DateTime.MinValue;
                        if (!string.IsNullOrEmpty(reportPage.LastActivityDateString))
                        {
                            if (DateTime.TryParseExact(reportPage.LastActivityDateString, "yyyy-MM-dd", enUS, System.Globalization.DateTimeStyles.None, out activityDate))
                            {
                                dateRequestedLog.LastActivityDate = activityDate;
                            }
                            else
                            {
                                Telemetry.LogInformation($"Invalid LastActivity value: '{reportPage.LastActivityDateString}'");
                                dateRequestedLog.LastActivityDate = null;
                            }
                        }
                        PopulateReportSpecificMetadata(dateRequestedLog, reportPage);

                        // Auto-detect is off, so state the change explicitly.
                        if (isNewLog)
                        {
                            GetTable(db).Add(dateRequestedLog);
                        }
                        else
                        {
                            db.Entry(dateRequestedLog).State = EntityState.Modified;
                        }

                        i++;
                        pendingChanges++;
                        if (pendingChanges >= SaveBatchSize)
                        {
                            await db.SaveChangesAsync();
                            pendingChanges = 0;
                        }
                    }

                    if (pendingChanges > 0)
                    {
                        await db.SaveChangesAsync();
                    }

                    // Release this day's tracked rows before moving to the next day.
                    DetachReportLogEntities(db);
                }
            }
            finally
            {
                db.Configuration.AutoDetectChangesEnabled = autoDetectWasEnabled;
            }
        }

        // Resolve the DB id for a report row's user/group lookup, using the shared cross-thread id cache and
        // only hitting the DB (via GetOrCreateLookup) on a cache miss. Resolution happens OUTSIDE the cache lock:
        // the previous .Result-inside-lock pattern held a shared mutex through a full DB round-trip, starving all
        // parallel import threads on the same cache.
        private async Task<int> ResolveLookupIdAsync(TUserActivityUserDetail reportPage, ConcurrentLookupDbIdsCache userEmailToDbIdCache, CACHETYPE lookupCache)
        {
            int? lookupId;
            lock (userEmailToDbIdCache)
            {
                lookupId = userEmailToDbIdCache.GetCachedIdForName<TReportDbType>(reportPage.LookupFieldValue);
            }
            if (lookupId != null)
            {
                return lookupId.Value;
            }

            var lookup = await reportPage.GetOrCreateLookup(lookupCache);
            if (!lookup.IsSavedToDB)
            {
                throw new InvalidOperationException("Cannot use unsaved lookups for activity records");
            }

            lock (userEmailToDbIdCache)
            {
                // Re-check in case another thread populated it while we were resolving.
                lookupId = userEmailToDbIdCache.GetCachedIdForName<TReportDbType>(reportPage.LookupFieldValue);
                if (lookupId == null)
                {
                    lookupId = lookup.ID;
                    userEmailToDbIdCache.AddOrUpdateForName<TReportDbType>(reportPage.LookupFieldValue, lookupId.Value);
                }
            }
            return lookupId.Value;
        }

        // Detach the day's saved usage-log entities so the EF6 change tracker (and the memory it holds) is
        // released before the next day. Lookup entities (users/groups) are intentionally left tracked so the
        // shared id cache keeps working.
        private static void DetachReportLogEntities(AnalyticsEntitiesContext db)
        {
            foreach (var entry in db.ChangeTracker.Entries<AbstractUsageActivityLog>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        protected virtual Task<bool> IdInScope(string lookupId)
        {
            // Default implementation assumes all IDs are in scope
            // Override this method to filter out IDs that should not be processed
            return Task.FromResult(true);
        }

        protected abstract long CountActivity(TUserActivityUserDetail activityPage);
        protected abstract void PopulateReportSpecificMetadata(TReportDbType newRecord, TUserActivityUserDetail activityPage);

        protected int GetOptionalInt(int? i)
        {
            if (i.HasValue)
            {
                return i.Value;
            }
            return 0;
        }
    }

}
