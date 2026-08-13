using Common.Entities;
using Common.Entities.ActivityReports;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
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

        /// <summary>
        /// Recent-day window (in days) during which Graph usage data can still change and therefore must be
        /// re-imported every run. Graph usage reports have a ~2-3 day latency and are stable once finalized, so
        /// dates older than this are treated as final: once stored they are skipped entirely (no re-download,
        /// no re-write). 3 is a safe default; every date is still fully re-imported on its first ~3 daily runs
        /// (as day-1, day-2, day-3) before it is ever skipped, so transient partial saves self-heal. Settable so
        /// tests can exercise the boundary.
        /// </summary>
        public int RefreshableRecentDays { get; set; } = 3;

        /// <summary>
        /// Number of rows actually inserted/updated in SQL by the last <see cref="SaveLoadedReportsToSql"/> call.
        /// Unchanged rows are dirty-checked and skipped, so this is 0 when nothing changed. Exposed for tests and
        /// diagnostics.
        /// </summary>
        public int LastSaveDbWriteCount { get; private set; }

        private const int MIN_DAYS_BACK = 3;    // Activity reports don't tend to refresh until a couple of days late; always collect something useful.
        private const int MAX_DAYS_BACK = 28;   // Graph only retains ~28 days of daily detail.

        private static int ClampDaysBack(int daysBackMax)
        {
            if (daysBackMax < MIN_DAYS_BACK) return MIN_DAYS_BACK;
            if (daysBackMax > MAX_DAYS_BACK) return MAX_DAYS_BACK;
            return daysBackMax;
        }

        /// <summary>
        /// The set of dates within the [now-daysBackMax, now) import window that are already stored in SQL AND old
        /// enough that Graph will no longer change them (older than <see cref="RefreshableRecentDays"/>). These can
        /// be skipped entirely on the next import - no re-download, no re-write. Dates within the recent window are
        /// never returned because their data can still change.
        /// </summary>
        public async Task<HashSet<DateTime>> GetFinalizedStoredDatesToSkipAsync(AnalyticsEntitiesContext db, int daysBackMax)
        {
            daysBackMax = ClampDaysBack(daysBackMax);
            var today = DateTime.UtcNow.Date;
            var windowStart = today.AddDays(-daysBackMax);
            var mutableCutoff = today.AddDays(-RefreshableRecentDays);   // dates >= this can still change; never skip them

            var storedFinalizedDates = await GetTable(db)
                .Where(t => t.Date >= windowStart && t.Date < mutableCutoff)
                .Select(t => t.Date)
                .Distinct()
                .ToListAsync();

            return new HashSet<DateTime>(storedFinalizedDates.Select(d => d.Date));
        }

        public async Task PopulateLoadedReportPagesFromGraph(int daysBackMax, ISet<DateTime> datesToSkip = null)
        {
            daysBackMax = ClampDaysBack(daysBackMax);

            LoadedReportPages.Clear();

            for (int daysBackIdx = 0; daysBackIdx < daysBackMax; daysBackIdx++)
            {
                // Go back one extra day always. Otherwise we risk asking for data too soon...
                // Example: Message: {"error":{"code":"InvalidArgument","message":"Invalid date value specified: $DateTime.Now. Only support data for the past 28 days."}}
                var daysBack = (daysBackIdx + 1) * -1;
                // Graph Usage Reports API operates in UTC; DateTime.Now on a non-UTC server
                // produces the wrong date bucket near midnight.
                var dt = DateTime.UtcNow.AddDays(daysBack);

                // Finalized days we already hold don't change in Graph - skip the (often slow) paged download entirely.
                if (datesToSkip != null && datesToSkip.Contains(dt.Date))
                {
                    Telemetry.LogInformation($"Skipping {this.GetType().Name} for date {dt.ToString("dd-MM-yyyy")} - already stored and finalized (no longer changes in Graph).");
                    continue;
                }

                Telemetry.LogInformation($"Loading {this.GetType().Name} for date {dt.ToString("dd-MM-yyyy")}");

                var dayReports = await LoadReportPageForDateFromGraph(dt);

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
        /// Fetch one day's report from Graph (all pages, with throttle retries). Virtual so tests can supply canned
        /// data with no HTTP, and so the date-skipping in <see cref="PopulateLoadedReportPagesFromGraph"/> can be
        /// verified without hitting Graph.
        /// </summary>
        protected virtual Task<List<TUserActivityUserDetail>> LoadReportPageForDateFromGraph(DateTime date)
        {
            var requestUrl = $"{ReportGraphURL}(date={date.ToString("yyyy-MM-dd")})?$format=application/json";
            return _client.LoadAllPagesWithThrottleRetries<TUserActivityUserDetail>(requestUrl, Telemetry);
        }

        /// <summary>
        /// Save to SQL. Needs a shared ConcurrentLookupDbIdsCache if running in parallel with other imports.
        /// </summary>
        public async Task SaveLoadedReportsToSql(ConcurrentLookupDbIdsCache userEmailToDbIdCache, CACHETYPE lookupCache)
        {
            int i = 0; int dbWrites = 0; var enUS = new System.Globalization.CultureInfo("en-US");
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

                        // Auto-detect is off, so state the change explicitly. Only write when something actually
                        // changed: existing rows for finalized days re-fetched by the recent-window rule are almost
                        // always identical to what's stored, so dirty-checking skips the vast majority of UPDATEs -
                        // the dominant cost of this import at large-tenant scale.
                        bool willWrite;
                        if (isNewLog)
                        {
                            GetTable(db).Add(dateRequestedLog);
                            willWrite = true;
                        }
                        else
                        {
                            willWrite = HasMappedValueChanged(db.Entry(dateRequestedLog));
                            if (willWrite)
                            {
                                db.Entry(dateRequestedLog).State = EntityState.Modified;
                            }
                        }

                        i++;
                        if (willWrite)
                        {
                            dbWrites++;
                            pendingChanges++;
                            if (pendingChanges >= SaveBatchSize)
                            {
                                await db.SaveChangesAsync();
                                pendingChanges = 0;
                            }
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

            LastSaveDbWriteCount = dbWrites;
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

        // True if any mapped scalar value on the tracked entity differs from the value originally loaded from the
        // DB. Compares the EF6 original/current value snapshots directly so it works with
        // AutoDetectChangesEnabled = false (auto-detect is deliberately off to keep bulk saves O(n)). Navigation
        // properties and [NotMapped] members (e.g. AssociatedLookupId) are not in these snapshots, so only real
        // column changes trigger an UPDATE.
        private static bool HasMappedValueChanged(DbEntityEntry entry)
        {
            var current = entry.CurrentValues;
            var original = entry.OriginalValues;
            foreach (var propertyName in current.PropertyNames)
            {
                if (!object.Equals(original[propertyName], current[propertyName]))
                {
                    return true;
                }
            }
            return false;
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
