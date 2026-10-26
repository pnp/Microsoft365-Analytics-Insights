using System;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Rules
{
    /// <summary>
    /// When to try the weekly SharePoint sites usage report again after a download that stored nothing: no usable row
    /// carried the refresh day's date (Sunday). Graph only offers the current refresh, so a Sunday that is not caught
    /// while it is the newest refresh - roughly a day - is lost for good.
    /// </summary>
    /// <remarks>
    /// A short retry on its own would download the whole report every hour for the six days a week on which Sunday
    /// data cannot exist yet. The newest refresh date Graph returned says when the next refresh day falls, and its
    /// report covers that whole day, so it cannot be published before the day after it: until then there is
    /// nothing to wait for, and from then on the report is retried every <see cref="RetryInterval"/> until it is
    /// stored.
    /// <para>
    /// A report none of whose rows could be used (concealed site names, say) is never retried within the hour. The one
    /// exception to waiting for the next refresh day is the refresh day's own report: it can be downloaded only until
    /// Graph replaces it with the next day's, so it is retried every <see cref="UnusableRefreshDayReportRetryInterval"/>
    /// while that can still be ahead (<see cref="ReportReplacedByUtc"/>). An admin who turns concealment off, or a Graph
    /// incident that clears, while it is still the newest report then still catches that week instead of losing it.
    /// </para>
    /// </remarks>
    public static class WeeklyUsageReportRetryPolicy
    {
        /// <summary>The retry interval once a refresh day's report could have been published.</summary>
        public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

        /// <summary>
        /// The shortest wait after a download in which Graph returned rows but none could be used (concealed site names,
        /// say). Those rows are the tenant's, not a download that went wrong, so retrying within the hour cannot help -
        /// and with the weekly check made on every cycle, it would re-download the whole report every hour, for good.
        /// </summary>
        public static readonly TimeSpan UnusableReportRetryInterval = TimeSpan.FromDays(1);

        /// <summary>
        /// How often the refresh day's own report is retried, after none of its rows could be used, while it can still be
        /// the newest report Graph offers. Sooner than <see cref="UnusableReportRetryInterval"/>, because the week is lost
        /// once Graph replaces it; never as often as <see cref="RetryInterval"/>, because each attempt downloads it all.
        /// </summary>
        public static readonly TimeSpan UnusableRefreshDayReportRetryInterval = TimeSpan.FromHours(4);

        /// <summary>
        /// How much later than the day after it Graph can publish a day's usage report: they run about two to three days
        /// behind (see <see cref="UsageReportRefreshPolicy"/>).
        /// </summary>
        public static readonly TimeSpan MaxPublicationDelay = TimeSpan.FromDays(3);

        /// <summary>
        /// The latest time at which the report refreshed on <paramref name="refreshDate"/> can still be the newest Graph
        /// offers: when the next day's report, which replaces it, can have been published. That one covers the whole of the
        /// next day, so it cannot be published before the day after that, and can be up to <see cref="MaxPublicationDelay"/>
        /// later still. For a Sunday: the Friday after, 00:00 UTC.
        /// </summary>
        public static DateTime ReportReplacedByUtc(DateTime refreshDate)
            => DateTime.SpecifyKind(refreshDate.Date.AddDays(2).Add(MaxPublicationDelay), DateTimeKind.Utc);

        /// <summary>
        /// Whether the newest report Graph returned is the refresh day's own, and can still be downloaded after
        /// <paramref name="nowUtc"/>. Each attempt re-reads the newest refresh date, so once Graph has moved on to the next
        /// day's report this is false whatever the time; the time limit only stops the retries if Graph stalls.
        /// </summary>
        public static bool IsRefreshDayReportStillCurrent(DateTime nowUtc, DateTime? latestRefreshDate, DayOfWeek refreshDay)
            => latestRefreshDate.HasValue
               && latestRefreshDate.Value.Date.DayOfWeek == refreshDay
               && nowUtc < ReportReplacedByUtc(latestRefreshDate.Value);

        /// <param name="nowUtc">Current time (UTC).</param>
        /// <param name="latestRefreshDate">
        /// The newest report refresh date among the rows Graph returned, usable or not; null if no row had one.
        /// </param>
        /// <param name="refreshDay">The day of the week whose report is stored.</param>
        /// <param name="noUsableRows">Graph returned rows, but none could be used.</param>
        public static DateTime NextAttemptAfterMissedRefreshDayUtc(DateTime nowUtc, DateTime? latestRefreshDate, DayOfWeek refreshDay, bool noUsableRows = false)
        {
            if (noUsableRows && IsRefreshDayReportStillCurrent(nowUtc, latestRefreshDate, refreshDay))
            {
                // Waiting for the next refresh day would lose this week for good, even if the cause is fixed within the hour.
                return DateTime.SpecifyKind(nowUtc.Add(UnusableRefreshDayReportRetryInterval), DateTimeKind.Utc);
            }

            var retryUtc = DateTime.SpecifyKind(nowUtc.Add(noUsableRows ? UnusableReportRetryInterval : RetryInterval), DateTimeKind.Utc);
            if (!latestRefreshDate.HasValue)
            {
                return retryUtc;
            }

            var latest = latestRefreshDate.Value.Date;
            var daysUntilRefreshDay = ((int)refreshDay - (int)latest.DayOfWeek + 7) % 7;
            if (daysUntilRefreshDay == 0)
            {
                daysUntilRefreshDay = 7;
            }

            var earliestPublicationUtc = DateTime.SpecifyKind(latest.AddDays(daysUntilRefreshDay + 1), DateTimeKind.Utc);
            return earliestPublicationUtc > retryUtc ? earliestPublicationUtc : retryUtc;
        }
    }
}
