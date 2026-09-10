using System;

namespace WebJob.AppInsightsImporter.Engine.PageUpdates.Rules
{
    /// <summary>
    /// The metadata refresh-suppression rule: a URL whose metadata was refreshed recently is skipped, so a
    /// page that emits page-update events all day is not re-written on every import cycle.
    ///
    /// Extracted from <c>PageUpdateManager.SaveChunk</c>, which read <c>DateTime.UtcNow</c> inline inside the
    /// EF query and so could only be observed by running the import against a database. See issue #369.
    /// Takes the instant as a parameter, following the <c>ImportCadenceGate.ShouldRun(..., DateTime nowUtc)</c>
    /// convention.
    ///
    /// <para><b>One deliberate behavioural change.</b> The original predicate was
    /// <c>u.MetadataLastRefreshed &lt; DbFunctions.AddMinutes(DateTime.UtcNow, -N)</c>. EF6 does <b>not</b>
    /// evaluate a bare <c>DateTime.UtcNow</c> inside a LINQ-to-Entities query on the client - it maps it to
    /// the canonical function <c>CurrentUtcDateTime()</c>, which the SQL Server provider renders as
    /// <c>SysUtcDateTime()</c>. So "now" used to be the <b>database server's</b> clock; it is now the
    /// web-job <b>host's</b> clock. The difference is bounded by app-to-database clock skew (sub-second on
    /// NTP-synced Azure) against a 24-hour staleness window, and it is arguably more self-consistent:
    /// <c>MetadataLastRefreshed</c> is stamped from the host clock in <c>SaveAll</c>, so the comparison now
    /// uses one clock rather than two. Note there is no form of this predicate that both keeps the database
    /// clock and is injectable - passing <c>_clock.UtcNow</c> to <c>DbFunctions.AddMinutes</c> would
    /// parameterise the host clock just the same.</para>
    /// </summary>
    public static class PageUpdateRefreshPolicy
    {
        /// <summary>
        /// URLs whose <c>MetadataLastRefreshed</c> is <c>null</c> or strictly older than this instant are
        /// re-read; everything else is left alone until the window elapses.
        ///
        /// <paramref name="metadataRefreshMinutes"/> comes from <c>AppConfig.MetadataRefreshMinutes</c>, which
        /// already falls back to its 24-hour default for a missing, unparseable or negative app setting
        /// (see <c>AppConfigDefaultsTests.MetadataRefreshMinutes_*</c>), so this does not re-validate it.
        /// </summary>
        public static DateTime StaleBeforeUtc(int metadataRefreshMinutes, DateTime nowUtc)
        {
            return nowUtc.AddMinutes(-metadataRefreshMinutes);
        }
    }
}
