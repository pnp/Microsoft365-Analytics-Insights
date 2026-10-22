using Azure.Identity;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.UserScope;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// Starting class for AppInsights importing.
    /// </summary>
    public class AppInsightsImporter
    {
        private readonly AppConfig _importConfig;
        private readonly AnalyticsLogger _logger;
        private readonly IClock _clock;
        private readonly IAnalyticsDbContextFactory _contextFactory;

        // Optional ports (issue #374). When all of them are supplied the import runs with no database
        // context and no HTTP client created at all, which is what makes the orchestration testable.
        private readonly IAppInsightsSourceLoader _source;
        private readonly ISiteFilterLoader _siteFilterLoader;
        private readonly IHitWatermarkStore _hitWatermarkStore;
        private readonly IAppInsightsDayPersistenceManager _persistence;

        // UserGroupsFilter: who this cycle may import data about, and (process-lifetime) how far it has read.
        private readonly UserImportScope _userScope;
        private readonly AppInsightsScanWatermark _scanWatermark;

        // Process-lifetime: which days failed to save, and how many times in a row.
        private readonly AppInsightsDayFailureTracker _dayFailures;

        // Process-lifetime: the query windows each day needed, so a day over the API's limits is not re-split every cycle.
        private readonly AppInsightsQueryWindowCache _queryWindows;

        /// <param name="userScope">The <c>UserGroupsFilter</c> scope for this cycle; null means unfiltered.</param>
        /// <param name="scanWatermark">
        /// Process-lifetime record of how far the import has read, consulted only when the scope is filtered. See
        /// <see cref="AppInsightsScanWatermark"/>.
        /// </param>
        /// <param name="dayFailureTracker">
        /// Process-lifetime record of the days that failed to save, so a failed day is retried next cycle before any
        /// later day, and given up on after a few attempts. See <see cref="AppInsightsDayFailureTracker"/>. Null uses
        /// one shared by the whole process.
        /// </param>
        /// <param name="queryWindowCache">
        /// Process-lifetime record of the query windows each day needed. See <see cref="AppInsightsQueryWindowCache"/>.
        /// Null uses one shared by the whole process.
        /// </param>
        public AppInsightsImporter(AppConfig importConfig, AnalyticsLogger logger, IClock clock = null,
            IAppInsightsSourceLoader source = null,
            ISiteFilterLoader siteFilterLoader = null,
            IHitWatermarkStore hitWatermarkStore = null,
            IAppInsightsDayPersistenceManager persistence = null,
            IAnalyticsDbContextFactory contextFactory = null,
            UserImportScope userScope = null,
            AppInsightsScanWatermark scanWatermark = null,
            AppInsightsDayFailureTracker dayFailureTracker = null,
            AppInsightsQueryWindowCache queryWindowCache = null)
        {
            _importConfig = importConfig;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? SystemClock.Instance;
            _contextFactory = contextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
            _source = source;
            _siteFilterLoader = siteFilterLoader;
            _hitWatermarkStore = hitWatermarkStore;
            _persistence = persistence;
            _userScope = userScope ?? UserImportScope.Unfiltered;
            _scanWatermark = scanWatermark;
            _dayFailures = dayFailureTracker ?? AppInsightsDayFailureTracker.ProcessDefault;
            _queryWindows = queryWindowCache ?? AppInsightsQueryWindowCache.ProcessDefault;
        }

        /// <summary>
        /// True when every database-backed port was supplied, so no context needs creating at all.
        /// </summary>
        private bool AllDatabasePortsSupplied =>
            _siteFilterLoader != null && _hitWatermarkStore != null && _persistence != null;

        public async Task ImportAndSave(bool saveRestResponses, int? daysBeforeOverride)
        {
            if (AllDatabasePortsSupplied)
            {
                await ImportAndSaveWith(saveRestResponses, daysBeforeOverride,
                    _siteFilterLoader, _hitWatermarkStore, _persistence);
                return;
            }

            // Production: one context for the whole run, exactly as before, with SQL adapters over it for
            // whichever ports were not supplied.
            using (var db = _contextFactory.Create())
            {
                await ImportAndSaveWith(saveRestResponses, daysBeforeOverride,
                    _siteFilterLoader ?? new SqlSiteFilterLoader(db),
                    _hitWatermarkStore ?? new SqlHitWatermarkStore(db),
                    _persistence ?? new SqlAppInsightsDayPersistenceManager(db, _logger, _importConfig, _contextFactory));
            }
        }

        private async Task ImportAndSaveWith(bool saveRestResponses, int? daysBeforeOverride,
            ISiteFilterLoader siteFilterLoader,
            IHitWatermarkStore hitWatermarkStore, IAppInsightsDayPersistenceManager persistence)
        {
            // App Insights timestamps are UTC, so the scan window must be UTC too. Using local
            // DateTime.Now on a non-UTC host shifts the day boundaries and the per-day KQL filter,
            // missing or duplicating edge hits near midnight. The rule itself lives in
            // AppInsightsImportWindow so it can be tested without a database or the wall clock (#374).
            var nowUtc = _clock.UtcNow;
            var scanFromDateOverride = AppInsightsImportWindow.ResolveOverrideStartUtc(daysBeforeOverride, nowUtc);

            var sw = Stopwatch.StartNew();

            var filterUrls = await siteFilterLoader.LoadAsync();
            _logger.LogInformation($"Startup: loaded {filterUrls.Count} URL filters in {sw.Elapsed.TotalSeconds:N1}s");

            var newestHitTimestamp = await hitWatermarkStore.GetNewestHitTimestampUtcAsync();
            var effectiveNewestHitTimestamp = AppInsightsImportWindow.NormalizeStoredHitTimestampUtc(newestHitTimestamp);

            // Under UserGroupsFilter the newest STORED hit stops moving whenever the people in scope stop browsing, so
            // resume from how far this process has already READ when that is later. See AppInsightsScanWatermark.
            var newestScanned = _userScope.IsFiltered ? _scanWatermark?.NewestScannedUtc : null;
            if (!scanFromDateOverride.HasValue && newestScanned.HasValue
                && (!effectiveNewestHitTimestamp.HasValue || newestScanned.Value > effectiveNewestHitTimestamp.Value))
            {
                _logger.LogInformation($"Resuming from {newestScanned.Value:O}, the newest page view already read this process; " +
                    "later page views by people outside UserGroupsFilter are not stored, so the newest stored hit is older.");
                effectiveNewestHitTimestamp = newestScanned;
            }
            if (!scanFromDateOverride.HasValue && effectiveNewestHitTimestamp.HasValue && effectiveNewestHitTimestamp.Value > nowUtc)
            {
                _logger.LogWarning($"App Insights hit watermark is in the future: newest stored hit_timestamp {effectiveNewestHitTimestamp.Value:O} is later than importer clock {nowUtc:O}. Using the importer clock as the watermark for this run so the import window does not walk backwards.");
                effectiveNewestHitTimestamp = nowUtc;
            }

            // Figure out when to start. Either the debug override, or last hit (if there is one), or 31 days ago,
            // rewound a little to catch edge hits. hit_timestamp is stored in UTC; the clock keeps the
            // fallback on the same clock. See AppInsightsImportWindow (#374).
            var startDate = AppInsightsImportWindow.ResolveStartDateUtc(
                scanFromDateOverride, effectiveNewestHitTimestamp, nowUtc);

            // A day that failed to save is retried before any later day, even if the newest stored hit has moved
            // past it since (a browser with its clock ahead can do that). See AppInsightsDayFailureTracker.
            var retryDayUtc = _dayFailures.EarliestDayAwaitingRetryUtc;
            if (retryDayUtc.HasValue && retryDayUtc.Value < startDate.Date)
            {
                _logger.LogInformation($"Day {retryDayUtc.Value:yyyy-MM-dd} failed to save in an earlier cycle; starting from it so it is retried before any later day.");
                startDate = retryDayUtc.Value;
            }

            // The query windows remembered for days this run no longer reads are of no further use.
            _queryWindows.ForgetDaysBefore(startDate.Date);

            var jobTimer = new JobTimer(_logger, "Hits import");
            if (newestHitTimestamp.HasValue)
            {
                _logger.LogInformation($"Requesting data from AppInsights from {startDate} (newest hit is from {newestHitTimestamp.Value})...");
            }
            else
            {
                _logger.LogInformation($"Requesting data from AppInsights from {startDate} (no hits yet to start from previous)...");
            }

            if (_source != null)
            {
                if (!await ImportDaysAndSave(_source, saveRestResponses, startDate, filterUrls, persistence, sw))
                {
                    // The run stopped early: a fatal download failure, or a day that failed to save and is retried
                    // next cycle. The original code returned straight out of ImportAndSave on a download failure, so
                    // the section was never reported as finished; keep that for both, or liveness monitoring would
                    // see a successful section import for a cycle that did not import everything. (A download
                    // failure only gets here in Release - DEBUG rethrows.)
                    return;
                }
            }
            else
            {
                // Import page-views first
                var credential = new ClientSecretCredential(
                    this._importConfig.TenantGUID.ToString(),
                    this._importConfig.ClientID,
                    this._importConfig.ClientSecret);
                using (var ai = new AppInsightsAPIClient(this._importConfig.AppInsightsConnectionString, credential, _logger, null, _queryWindows))
                {
                    if (!await ImportDaysAndSave(ai, saveRestResponses, startDate, filterUrls, persistence, sw))
                    {
                        return;
                    }
                }
            }

            // Track finished event 
            jobTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
        }

        /// <summary>
        /// The day loop: fetch a day, save it, and stop at a day that fails to save so it is retried next cycle
        /// before any later day - unless it has already failed too often in a row, when it is given up on so the
        /// import cannot stall on it (see <see cref="AppInsightsDayFailureTracker"/>).
        /// </summary>
        /// <returns>
        /// False when the run stopped early - a download failure (Release only: the DEBUG build rethrows) or a day
        /// that failed to save - so the caller must NOT report the section as finished.
        /// </returns>
        private async Task<bool> ImportDaysAndSave(IAppInsightsSourceLoader source, bool saveRestResponses, DateTime startDate,
            List<FilterUrlConfig> filterUrls, IAppInsightsDayPersistenceManager persistence, Stopwatch sw)
        {
            // UTC to match App Insights' UTC timestamps (see startDate above).
            var endDate = _clock.UtcNow;
            var daysToRead = AppInsightsImportWindow.EnumerateDays(startDate, endDate);
            _logger.LogInformation($"Importing hits for {daysToRead.Count} days...");
            var totalDays = 0;
            var totalPageViews = 0;
            var totalEvents = 0;
            foreach (var d in daysToRead)
            {
                totalDays++;
                var dayTimer = Stopwatch.StartNew();
                _logger.LogInformation($"Importing day {totalDays}/{daysToRead.Count}: {d.ToString("yyyy-MM-dd")}...");

                // Fetch page-views and custom events for the same day in parallel.
                // Both are independent read-only API calls with no shared state.
                PageViewCollection pageViewsResult;
                CustomEventsResultCollection events;
                try
                {
                    sw.Restart();
                    var pageViewsTask = source.GetPageViewsAsync(d, saveRestResponses);
                    var eventsTask = source.GetCustomEventsAsync(d, saveRestResponses);
                    await Task.WhenAll(pageViewsTask, eventsTask);

                    pageViewsResult = await pageViewsTask;
                    events = await eventsTask;
                    _logger.LogInformation($"API fetch completed in {sw.Elapsed.TotalSeconds:N1}s - {pageViewsResult.Rows.Count:n0} page-views, {events.Rows.Count:n0} events");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Got fatal exception downloading from Application Insights REST: {ex.Message}");
#if DEBUG
                    throw;
#else
                    return false;
#endif
                }

                if (pageViewsResult.Rows.Count > 0)
                {
                    var earliest = pageViewsResult.Rows.OrderBy(v => v.Timestamp).Take(1).First();
                    var latest = pageViewsResult.Rows.OrderByDescending(v => v.Timestamp).Take(1).First();
                    _logger.LogInformation($"Hits range: {earliest.Timestamp:yyyy-MM-dd HH:mm:ss.ff} to {latest.Timestamp:yyyy-MM-dd HH:mm:ss.ff}");
                }

                // Newest page view READ for this day, before anything is filtered out: how far this day got.
                var newestRead = pageViewsResult.Rows.Count > 0 ? pageViewsResult.Rows.Max(v => v.Timestamp) : (DateTime?)null;

                // UserGroupsFilter: drop what people outside the scope did before anything is saved.
                if (_userScope.IsFiltered)
                {
                    var removed = AppInsightsUserScopeRules.Apply(pageViewsResult, events, _userScope);
                    if (removed.Total > 0)
                    {
                        _logger.LogInformation($"Left out data by people outside UserGroupsFilter: {removed.PageViewsRemoved:n0} page-view(s), " +
                            $"{removed.EventsRemoved:n0} event(s), {removed.CommentsRemoved:n0} page comment(s) and {removed.LikesRemoved:n0} like(s).");
                    }
                }

                if (pageViewsResult.Rows.Count > 0 || events.Rows.Count > 0)
                {
                    // Save to DB
                    try
                    {
                        sw.Restart();
                        await persistence.SavePageViewsAsync(pageViewsResult, filterUrls);
                        _logger.LogInformation($"Page-views SQL save completed in {sw.Elapsed.TotalSeconds:N1}s");

                        sw.Restart();
                        await persistence.SaveCustomEventsAsync(events);
                        _logger.LogInformation($"Events SQL save completed in {sw.Elapsed.TotalSeconds:N1}s");
                    }
                    catch (Exception ex)
                    {
                        // A day that failed to save must not be carried past: the next day's hits would move the
                        // watermark (the newest stored hit) beyond it and it would never be read again. Stop here and
                        // let the next cycle start from it. The save is idempotent, so retrying the parts that did
                        // save costs time, not duplicates. Every exception counts, not just SqlException: one that
                        // escaped used to stall the importer on the same day for months - which is also why a day
                        // that keeps failing is only retried a bounded number of times.
                        _logger.TrackException(ex);
                        var failures = _dayFailures.RecordFailure(d);
                        if (!_dayFailures.ShouldGiveUp(failures))
                        {
                            _logger.LogError($"Failed saving data for day {d:yyyy-MM-dd} (attempt {failures} of {_dayFailures.MaxAttempts}): {CommonExceptionHandler.GetErrorText(ex)}. " +
                                "Stopping this run here; the day will be retried next cycle, before any later day.");
                            _logger.LogError($"Exception detail: {ex}");
                            if (Debugger.IsAttached)
                            {
                                throw;
                            }
                            return false;
                        }

                        _logger.LogError($"Failed saving data for day {d:yyyy-MM-dd} again ({failures} failed attempts in a row): {CommonExceptionHandler.GetErrorText(ex)}. " +
                            $"Giving up on it and continuing with later days. Whatever that day did not save is a permanent gap in the web-traffic data for {d:yyyy-MM-dd}.");
                        _logger.LogError($"Exception detail: {ex}");
                        if (Debugger.IsAttached)
                        {
                            throw;
                        }
                        continue;
                    }

                    totalPageViews += pageViewsResult.Rows.Count;
                    totalEvents += events.Rows.Count;
                    _logger.LogInformation($"Day {d:yyyy-MM-dd} completed in {dayTimer.Elapsed.TotalSeconds:N1}s - saved {pageViewsResult.Rows.Count:n0} page-views, {events.Rows.Count:n0} events");
                }
                else
                {
                    _logger.LogInformation($"Day {d:yyyy-MM-dd} completed in {dayTimer.Elapsed.TotalSeconds:N1}s - no new data.");
                }

                // The day is fully processed - saved, or nothing left to save - so the next cycle need not read it again.
                _dayFailures.RecordSuccess(d);
                if (_userScope.IsFiltered)
                {
                    _scanWatermark?.Advance(newestRead);
                }
            }

            _logger.LogInformation($"Import loop finished: {totalDays} days processed, {totalPageViews:n0} total page-views, {totalEvents:n0} total events");
            return true;
        }
    }
}
