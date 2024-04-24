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

        protected GraphAndSqlAggregateWeeklyUsageReportLoader(AnalyticsEntitiesContext db, ManualGraphCallClient client, ILogger telemetry) : base(telemetry)
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
        public AbstractAggregateWeeklyUsageReportLoader(ILogger telemetry) : base(telemetry)
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
            var itemsSaved = 0;
            foreach (var item in data)
            {
                // Only save new data if it's on our day of the week
                if (item.ReportRefreshDate.DayOfWeek == uptoDay)
                {
                    // What's the last date we have stored for this item?  
                    var itemLastDate = await GetLastStoredResultFor(item);
                    if (!itemLastDate.HasValue || itemLastDate.Value < item.ReportRefreshDate)
                    {
                        Telemetry.LogInformation($"Saving {ReportName} for ID '{item.OfficeUniqueIdField}' as report was refreshed on {item.ReportRefreshDateString} (a {uptoDay})");
                        await AddItemToSaveList(item);
                        itemsSaved++;
                    }
                    else
                    {
                        Telemetry.LogInformation($"Not saving {ReportName} for ID '{item.OfficeUniqueIdField}' as last saved report was refreshed on {itemLastDate.Value.ToString("dd-MM-yyyy")} " +
                            $"and this report was refreshed on {item.ReportRefreshDate.ToString("dd-MM-yyyy")}");
                    }
                }
                else
                {
                    Telemetry.LogInformation($"Not saving {ReportName} for ID '{item.OfficeUniqueIdField}' as report was refreshed for this item on {item.ReportRefreshDateString} (a {item.ReportRefreshDate.DayOfWeek})");
                }
            }

            if (itemsSaved > 0)
            {
                Telemetry.LogInformation($"Saving {itemsSaved} items to SQL for {ReportName} reports");
                await CommitAllChanges();
            }
            else
            {
                Telemetry.LogInformation($"Finished processing {ReportName} reports. No weekly reports to save");
            }
            return itemsSaved;
        }

        public abstract Task<AggregateResourceUsageDetail<T>> LoadReportDataForUrl(string requestUrl);
    }
}
