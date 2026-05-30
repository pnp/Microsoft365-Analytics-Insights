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
        internal AbstractDailyActivityLoader(ManualGraphCallClient client, ILogger telemetry) : base(telemetry)
        {
            _client = client;
        }

        public abstract DbSet<TReportDbType> GetTable(AnalyticsEntitiesContext context);

        public Dictionary<DateTime, List<TUserActivityUserDetail>> LoadedReportPages { get; set; } = new Dictionary<DateTime, List<TUserActivityUserDetail>>();

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
            var allInserts = new List<TReportDbType>();

            Telemetry.LogInformation($"Saving {this.GetType().Name} for {LoadedReportPages.Keys.Count} dates");

            // Compute total once. The previous "LoadedReportPages.SelectMany(r => r.Value).Count()"
            // call ran on every 1000-row progress print, making progress O(n^2).
            var totalReports = LoadedReportPages.Sum(kv => kv.Value.Count);

            // Pre-fetch all existing reports across the whole date range in one query - the previous
            // code issued one EF query per date (up to 28 sequential round-trips per loader run).
            Dictionary<DateTime, List<TReportDbType>> existingByDate;
            if (LoadedReportPages.Count > 0)
            {
                var minDate = LoadedReportPages.Keys.Min().Date;
                var maxDate = LoadedReportPages.Keys.Max().Date;
                var rangeRows = await GetTable(lookupCache.DB)
                    .Where(t => t.Date >= minDate && t.Date <= maxDate)
                    .ToListAsync();
                existingByDate = rangeRows
                    .GroupBy(r => r.Date.Date)
                    .ToDictionary(g => g.Key, g => g.ToList());
            }
            else
            {
                existingByDate = new Dictionary<DateTime, List<TReportDbType>>();
            }

            // For each day in dataset (Key)
            foreach (var dateTime in LoadedReportPages.Keys)
            {
                // Pre-cache all reports on that date
                if (!existingByDate.TryGetValue(dateTime.Date, out var allReportsOnDate))
                {
                    allReportsOnDate = new List<TReportDbType>();
                }

                // Look through Graph results & compare with already saved reports for this date
                foreach (var reportPage in LoadedReportPages[dateTime])
                {
                    // Usually we're checking if the user is in scope (Entra ID group memembership for group filter)
                    var isInScope = await IdInScope(reportPage.LookupFieldValue);
                    if (!isInScope)
                    {
                        Telemetry.LogInformation($"Skipping {reportPage.LookupFieldValue} as not in scope");
                        continue;   // Skip this record
                    }

                    // Do we have a cached ID for the lookup?
                    int? lookupId = null;
                    bool needsResolve = false;
                    lock (userEmailToDbIdCache)
                    {
                        lookupId = userEmailToDbIdCache.GetCachedIdForName<TReportDbType>(reportPage.LookupFieldValue);
                        if (lookupId == null) needsResolve = true;
                    }

                    if (needsResolve)
                    {
                        // Resolve the lookup OUTSIDE the lock. The previous .Result-inside-lock pattern
                        // held a shared mutex through a full DB round-trip, starving all parallel import
                        // threads on the same cache.
                        var lookup = await reportPage.GetOrCreateLookup(lookupCache);

                        // Sanity
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
                    }

                    var dateRequestedLog = allReportsOnDate.FirstOrDefault(t => t.AssociatedLookupId == lookupId.Value);

                    // Output progress every 1000 imports
                    if (i > 0 && i % 1000 == 0)
                    {
                        Console.WriteLine($"{this.GetType().Name}: Saved {i} / {totalReports}");
                    }

                    // Create new log if necesary
                    if (dateRequestedLog == null)
                    {
                        dateRequestedLog = new TReportDbType()
                        {
                            AssociatedLookupId = lookupId.Value   // date set below
                        };

                        // Add new logs to list to insert
                        allInserts.Add(dateRequestedLog);
                        // Track in the per-date cache so a duplicate within the same import doesn't insert again
                        allReportsOnDate.Add(dateRequestedLog);
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

                    i++;
                }
            }

            // All inserts at once
            GetTable(lookupCache.DB).AddRange(allInserts);

            await lookupCache.DB.SaveChangesAsync();
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
