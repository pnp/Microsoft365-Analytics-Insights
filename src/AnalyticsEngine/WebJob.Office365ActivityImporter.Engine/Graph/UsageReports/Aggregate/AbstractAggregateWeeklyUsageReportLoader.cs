using Common.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate
{
    /// <summary>
    /// A usage report loader that only loads/saves on a specific day of the week. Has SQL and Graph dependencies. 
    /// </summary>
    public abstract class GraphAndSqlAggregateWeeklyUsageReportLoader<T> : AbstractAggregateWeeklyUsageReportLoader<T> where T : BaseAggregateItemStats
    {
        protected readonly ManualGraphCallClient _client;
        protected readonly AnalyticsEntitiesContext _context;

        protected GraphAndSqlAggregateWeeklyUsageReportLoader(AnalyticsEntitiesContext db, ManualGraphCallClient client, ILogger logger) : base(logger)
        {
            _client = client;
            _context = db;
        }

        /// <summary>
        /// HTTP implementation of loading a report page
        /// </summary>
        public override async Task<AggregateResourceUsageDetail<T>> LoadReportDataForUrl(string requestUrl)
        {
            var reportPage = await _client.GetAsyncWithThrottleRetries<AggregateResourceUsageDetail<T>>(requestUrl);
            return reportPage;
        }
    }

    /// <summary>
    /// A usage report loader that only loads/saves on a specific day of the week
    /// </summary>
    public abstract class AbstractAggregateWeeklyUsageReportLoader<T> : ActivityReportLoader where T : BaseAggregateItemStats
    {
        public AbstractAggregateWeeklyUsageReportLoader(ILogger logger) : base(logger)
        {
        }

        protected abstract Task<DateTime?> GetLastStoredResultFor(T item);
        protected abstract Task AddItemToSaveList(T item);
        protected abstract Task CommitAllChanges();

        public virtual async Task<IEnumerable<T>> LoadReportData()
        {
            var requestUrl = ReportGraphURL;

            var allStats = new List<T>();

            // Keep loading until we run out of pages
            int pageCount = 0;
            while (requestUrl != null)
            {
                pageCount++;
                var reportPage = await LoadReportDataForUrl(requestUrl);
                if (reportPage != null)
                {
                    if (reportPage.HasNextLink)
                    {
                        Telemetry.LogInformation($"Loading next page from '{reportPage.NextLink}'");
                    }
                    requestUrl = reportPage.NextLink;
                    allStats.AddRange(reportPage.Stats);
                }
                else
                {
                    Telemetry.LogWarning($"No items loaded from {requestUrl}");
                }
            }
            Telemetry.LogInformation($"Loaded {allStats.Count} items for '{ReportName}' across {pageCount} pages");
            return allStats;
        }

        public abstract string ReportName { get; }

        public async Task<int> LoadAndSaveLastWeeksReportsIfRefreshOnDay(DayOfWeek uptoDay)
        {
            Telemetry.LogInformation($"Loading {GetType().Name} and saving reports refreshed on a {uptoDay}");

            var report = await LoadReportData();
            Telemetry.LogInformation($"Loaded {report.Count()} items for {ReportName} reports");

            return await SaveLoadedReportsIfRefreshOnDay(uptoDay, report);
        }

        public async Task<int> SaveLoadedReportsIfRefreshOnDay(DayOfWeek uptoDay, IEnumerable<T> data)
        {
            // Materialise once so we can both hand the full set to BeginSaveAsync (for bulk pre-loading)
            // and iterate it below.
            var items = data as IReadOnlyList<T> ?? data.ToList();

            // Give subclasses a chance to bulk-load existing state in a single query instead of doing a
            // per-item DB round-trip in GetLastStoredResultFor / AddItemToSaveList. For large tenants this
            // is the difference between one query and tens of thousands (one per site).
            await BeginSaveAsync(items);

            try
            {
                var itemsSaved = 0;
                var alreadyUpToDate = 0;
                var notRefreshedOnDay = 0;

                foreach (var item in items)
                {
                    // Only save new data if it's on our day of the week
                    if (item.ReportRefreshDate.DayOfWeek == uptoDay)
                    {
                        // What's the last date we have stored for this item?
                        var itemLastDate = await GetLastStoredResultFor(item);
                        if (!itemLastDate.HasValue || itemLastDate.Value < item.ReportRefreshDate)
                        {
                            await AddItemToSaveList(item);
                            itemsSaved++;
                        }
                        else
                        {
                            alreadyUpToDate++;
                        }
                    }
                    else
                    {
                        notRefreshedOnDay++;
                    }
                }

                // Per-item logging here produced one INFO trace per report row (tens of thousands for a
                // large SharePoint tenant), which floods App Insights and slows the run. Log a summary instead.
                Telemetry.LogInformation($"{ReportName}: considered {items.Count} item(s) - saving {itemsSaved} new weekly report(s); " +
                    $"{alreadyUpToDate} already up to date; {notRefreshedOnDay} not refreshed on a {uptoDay}.");

                if (itemsSaved > 0)
                {
                    Telemetry.LogInformation($"Saving {itemsSaved} items to SQL for {ReportName} reports");
                    await CommitAllChanges();
                }
                return itemsSaved;
            }
            finally
            {
                await EndSaveAsync();
            }
        }

        /// <summary>
        /// Called once before the save loop with every item under consideration. Override to bulk pre-load
        /// existing DB state (and e.g. tune change-tracking) so per-item lookups become in-memory. Default: no-op.
        /// </summary>
        protected virtual Task BeginSaveAsync(IReadOnlyList<T> allItems) => Task.CompletedTask;

        /// <summary>
        /// Called once after the save loop (in a finally) so overrides can restore any state changed in
        /// <see cref="BeginSaveAsync"/> even when nothing was saved. Default: no-op.
        /// </summary>
        protected virtual Task EndSaveAsync() => Task.CompletedTask;

        public abstract Task<AggregateResourceUsageDetail<T>> LoadReportDataForUrl(string requestUrl);
    }
}
