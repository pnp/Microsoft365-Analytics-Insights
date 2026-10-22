using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate
{
    /// <summary>
    /// A usage report loader that only loads/saves on a specific day of the week. Has Graph (and, via a
    /// store port, storage) dependencies.
    /// </summary>
    public abstract class GraphAndSqlAggregateWeeklyUsageReportLoader<T> : AbstractAggregateWeeklyUsageReportLoader<T> where T : BaseAggregateItemStats
    {
        protected readonly ManualGraphCallClient _client;

        protected GraphAndSqlAggregateWeeklyUsageReportLoader(ManualGraphCallClient client, ILogger logger) : base(logger)
        {
            _client = client;
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

        /// <summary>How many rows Graph returned on the last <see cref="LoadReportData"/>, before any subclass dropped unusable ones.</summary>
        public int LastLoadRowCount { get; private set; }

        /// <summary>
        /// The newest refresh date among the rows Graph returned on the last <see cref="LoadReportData"/> - unusable rows
        /// included, because it says when the next refresh can be published whether or not any row could be saved.
        /// </summary>
        public DateTime? LastLoadLatestRefreshDate { get; private set; }

        public virtual async Task<IEnumerable<T>> LoadReportData()
        {
            LastLoadRowCount = 0;
            LastLoadLatestRefreshDate = null;

            var requestUrl = ReportGraphURL;

            var allStats = new List<T>();

            // Keep loading until we run out of pages. Strict: a page that cannot be read fails the whole report for this
            // cycle, before anything is saved, so the caller records it as failed and it is retried next cycle.
            int pageCount = 0;
            while (requestUrl != null)
            {
                pageCount++;
                var reportPage = await LoadReportDataForUrl(requestUrl);

                // A 200 whose body was empty or not JSON comes back null (ManualGraphCallClient logs the body). This used
                // to log a warning and request the same URL again, for ever - hanging the import cycle and hammering Graph.
                // A page without its "value" collection is no more usable: its rows are unknown, not absent. Neither
                // message names the URL, which carries a paging $skiptoken.
                if (reportPage == null)
                {
                    throw new InvalidOperationException($"{ReportName}: Graph returned page {pageCount} of the report in a form that could not be read. " +
                        "Nothing from this report was saved; it is retried on a later cycle.");
                }
                if (reportPage.Stats == null)
                {
                    throw new InvalidOperationException($"{ReportName}: page {pageCount} of the report came back from Graph without its \"value\" collection. " +
                        "Nothing from this report was saved; it is retried on a later cycle.");
                }

                if (reportPage.HasNextLink)
                {
                    Telemetry.LogInformation($"Loading next page from '{reportPage.NextLink}'");
                }
                requestUrl = reportPage.NextLink;
                allStats.AddRange(reportPage.Stats);
            }

            LastLoadRowCount = allStats.Count;
            foreach (var row in allStats)
            {
                if (row != null && row.TryGetReportRefreshDate(out var refreshDate)
                    && (!LastLoadLatestRefreshDate.HasValue || refreshDate > LastLoadLatestRefreshDate.Value))
                {
                    LastLoadLatestRefreshDate = refreshDate;
                }
            }

            Telemetry.LogInformation($"Loaded {allStats.Count} items for '{ReportName}' across {pageCount} pages");
            return allStats;
        }

        public abstract string ReportName { get; }

        public async Task<int> LoadAndSaveLastWeeksReportsIfRefreshOnDay(DayOfWeek uptoDay)
            => (await LoadAndSaveLastWeeksReportsIfRefreshOnDayWithResult(uptoDay)).ItemsSaved;

        public virtual async Task<WeeklyUsageReportSaveResult> LoadAndSaveLastWeeksReportsIfRefreshOnDayWithResult(DayOfWeek uptoDay)
        {
            Telemetry.LogInformation($"Loading {GetType().Name} and saving reports refreshed on a {uptoDay}");

            var report = await LoadReportData();
            var usable = report as IReadOnlyList<T> ?? report.ToList();
            Telemetry.LogInformation($"Loaded {usable.Count} usable item(s), of {Math.Max(LastLoadRowCount, usable.Count)} returned, for {ReportName} reports");

            var result = await SaveLoadedReportsIfRefreshOnDayWithResult(uptoDay, usable);
            return result.WithRowsReturned(LastLoadRowCount, LastLoadLatestRefreshDate);
        }

        public async Task<int> SaveLoadedReportsIfRefreshOnDay(DayOfWeek uptoDay, IEnumerable<T> data)
            => (await SaveLoadedReportsIfRefreshOnDayWithResult(uptoDay, data)).ItemsSaved;

        public virtual async Task<WeeklyUsageReportSaveResult> SaveLoadedReportsIfRefreshOnDayWithResult(DayOfWeek uptoDay, IEnumerable<T> data)
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
                DateTime? latestRefreshDate = null;

                foreach (var item in items)
                {
                    var refreshDate = item.ReportRefreshDate;
                    if (!latestRefreshDate.HasValue || refreshDate > latestRefreshDate.Value)
                    {
                        latestRefreshDate = refreshDate;
                    }

                    // Only save new data if it's on our day of the week
                    if (refreshDate.DayOfWeek == uptoDay)
                    {
                        // What's the last date we have stored for this item?
                        var itemLastDate = await GetLastStoredResultFor(item);
                        if (!itemLastDate.HasValue || itemLastDate.Value < refreshDate)
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
                return new WeeklyUsageReportSaveResult(itemsSaved, alreadyUpToDate, notRefreshedOnDay, latestRefreshDate);
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

    public sealed class WeeklyUsageReportSaveResult
    {
        public WeeklyUsageReportSaveResult(int itemsSaved, int alreadyUpToDate, int notRefreshedOnDay, DateTime? latestRefreshDate = null, int? rowsReturned = null)
        {
            ItemsSaved = itemsSaved;
            AlreadyUpToDate = alreadyUpToDate;
            NotRefreshedOnDay = notRefreshedOnDay;
            LatestRefreshDate = latestRefreshDate;
            RowsReturned = Math.Max(rowsReturned ?? 0, UsableRows);
        }

        public int ItemsSaved { get; }
        public int AlreadyUpToDate { get; }
        public int NotRefreshedOnDay { get; }

        /// <summary>
        /// The newest report refresh date among the rows Graph returned - usable or not - or null when it returned no
        /// row with a readable one.
        /// </summary>
        public DateTime? LatestRefreshDate { get; }

        /// <summary>How many rows Graph returned, before unusable ones (no site URL, say) were dropped.</summary>
        public int RowsReturned { get; }

        /// <summary>How many of those rows could be considered for saving at all.</summary>
        public int UsableRows => ItemsSaved + AlreadyUpToDate + NotRefreshedOnDay;

        /// <summary>
        /// Graph returned rows, but not one could be used. Retrying within the hour cannot change that: the rows are
        /// the tenant's (concealed site names, say), not a download that went wrong.
        /// </summary>
        public bool NoUsableRows => RowsReturned > 0 && UsableRows == 0;

        public bool ObservedRefreshDayReport => ItemsSaved + AlreadyUpToDate > 0;

        /// <summary>
        /// This result, with the rows Graph returned before any were dropped: a larger count, and a newer refresh date
        /// when an unusable row carried one.
        /// </summary>
        public WeeklyUsageReportSaveResult WithRowsReturned(int rowsReturned, DateTime? latestReturnedRefreshDate)
        {
            var latest = LatestRefreshDate;
            if (latestReturnedRefreshDate.HasValue && (!latest.HasValue || latestReturnedRefreshDate.Value > latest.Value))
            {
                latest = latestReturnedRefreshDate;
            }

            return new WeeklyUsageReportSaveResult(ItemsSaved, AlreadyUpToDate, NotRefreshedOnDay, latest, Math.Max(rowsReturned, RowsReturned));
        }
    }
}
