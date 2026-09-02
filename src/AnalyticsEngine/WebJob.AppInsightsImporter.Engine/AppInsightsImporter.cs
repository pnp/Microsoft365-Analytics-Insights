using Azure.Identity;
using Common.Entities;
using Common.Entities.Config;
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
        private readonly IImportDbMaintenance _dbMaintenance;
        private readonly ISiteFilterLoader _siteFilterLoader;
        private readonly IHitWatermarkStore _hitWatermarkStore;
        private readonly IAppInsightsDayPersistenceManager _persistence;

        public AppInsightsImporter(AppConfig importConfig, AnalyticsLogger logger, IClock clock = null,
            IAppInsightsSourceLoader source = null,
            IImportDbMaintenance dbMaintenance = null,
            ISiteFilterLoader siteFilterLoader = null,
            IHitWatermarkStore hitWatermarkStore = null,
            IAppInsightsDayPersistenceManager persistence = null,
            IAnalyticsDbContextFactory contextFactory = null)
        {
            _importConfig = importConfig;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? SystemClock.Instance;
            _contextFactory = contextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
            _source = source;
            _dbMaintenance = dbMaintenance;
            _siteFilterLoader = siteFilterLoader;
            _hitWatermarkStore = hitWatermarkStore;
            _persistence = persistence;
        }

        /// <summary>
        /// True when every database-backed port was supplied, so no context needs creating at all.
        /// </summary>
        private bool AllDatabasePortsSupplied =>
            _dbMaintenance != null && _siteFilterLoader != null && _hitWatermarkStore != null && _persistence != null;

        public async Task ImportAndSave(bool saveRestResponses, int? daysBeforeOverride)
        {
            if (AllDatabasePortsSupplied)
            {
                await ImportAndSaveWith(saveRestResponses, daysBeforeOverride,
                    _dbMaintenance, _siteFilterLoader, _hitWatermarkStore, _persistence);
                return;
            }

            // Production: one context for the whole run, exactly as before, with SQL adapters over it for
            // whichever ports were not supplied.
            using (var db = _contextFactory.Create())
            {
                await ImportAndSaveWith(saveRestResponses, daysBeforeOverride,
                    _dbMaintenance ?? new SqlImportDbMaintenance(db),
                    _siteFilterLoader ?? new SqlSiteFilterLoader(db),
                    _hitWatermarkStore ?? new SqlHitWatermarkStore(db),
                    _persistence ?? new SqlAppInsightsDayPersistenceManager(db, _logger, _importConfig));
            }
        }

        private async Task ImportAndSaveWith(bool saveRestResponses, int? daysBeforeOverride,
            IImportDbMaintenance dbMaintenance, ISiteFilterLoader siteFilterLoader,
            IHitWatermarkStore hitWatermarkStore, IAppInsightsDayPersistenceManager persistence)
        {
            // App Insights timestamps are UTC, so the scan window must be UTC too. Using local
            // DateTime.Now on a non-UTC host shifts the day boundaries and the per-day KQL filter,
            // missing or duplicating edge hits near midnight. The rule itself lives in
            // AppInsightsImportWindow so it can be tested without a database or the wall clock (#374).
            var scanFromDateOverride = AppInsightsImportWindow.ResolveOverrideStartUtc(daysBeforeOverride, _clock.UtcNow);

            var sw = Stopwatch.StartNew();

            // Delete duplicate hits 1st. It also creates the page-request-ID index
            await dbMaintenance.RunStartupMaintenanceAsync();
            _logger.LogInformation($"Startup: duplicate-hit cleanup completed in {sw.Elapsed.TotalSeconds:N1}s");

            sw.Restart();
            var filterUrls = await siteFilterLoader.LoadAsync();
            _logger.LogInformation($"Startup: loaded {filterUrls.Count} URL filters in {sw.Elapsed.TotalSeconds:N1}s");

            var newestHitTimestamp = await hitWatermarkStore.GetNewestHitTimestampUtcAsync();

            // Figure out when to start. Either the debug override, or last hit (if there is one), or 31 days ago,
            // rewound a little to catch edge hits. hit_timestamp is stored in UTC; the clock keeps the
            // fallback on the same clock. See AppInsightsImportWindow (#374).
            var startDate = AppInsightsImportWindow.ResolveStartDateUtc(
                scanFromDateOverride, newestHitTimestamp, _clock.UtcNow);

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
                    // A fatal download failure aborted the run. The original code returned straight out of
                    // ImportAndSave here, so the section was never reported as finished; keep that, or
                    // liveness monitoring would see a successful section import for a cycle that imported
                    // nothing. (Release only - DEBUG rethrows.)
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
                using (var ai = new AppInsightsAPIClient(this._importConfig.AppInsightsConnectionString, credential, _logger))
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
        /// The day loop: fetch a day, save it, and never let one bad day abort the rest of the run.
        /// </summary>
        /// <returns>
        /// False when a download failure aborted the whole run, so the caller must NOT report the section as
        /// finished. Only reachable in Release: the DEBUG build rethrows instead.
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
                        // Isolate per-day save failures. A single bad day - e.g. a page-update
                        // event that trips a DbUpdateException - must never abort the whole
                        // multi-day run and stall the importer's watermark indefinitely (the
                        // SqlException-only catch used to let any other exception escape, which
                        // permanently stuck the importer re-processing the same day). Log the
                        // full error, record it, and carry on with the next day.
                        _logger.TrackException(ex);
                        _logger.LogError($"Failed saving data for day {d:yyyy-MM-dd}: {CommonExceptionHandler.GetErrorText(ex)}. Skipping this day and continuing.");
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
            }

            _logger.LogInformation($"Import loop finished: {totalDays} days processed, {totalPageViews:n0} total page-views, {totalEvents:n0} total events");
            return true;
        }
    }
}
