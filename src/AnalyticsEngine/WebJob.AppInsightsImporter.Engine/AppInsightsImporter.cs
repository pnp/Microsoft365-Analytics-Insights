using Azure.Identity;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using System.Data.SqlClient;
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
        private readonly AnalyticsLogger _telemetry;

        public AppInsightsImporter(AppConfig importConfig, AnalyticsLogger telemetry)
        {
            _importConfig = importConfig;
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        public async Task ImportAndSave(bool saveRestResponses, int? daysBeforeOverride)
        {

            DateTime? scanFromDateOverride = null;
            if (daysBeforeOverride.HasValue)
            {
                scanFromDateOverride = DateTime.Now.AddDays(daysBeforeOverride.Value * -1);
            }

            var sw = Stopwatch.StartNew();
            using (var db = new AnalyticsEntitiesContext())
            {
                // Delete duplicate hits 1st. It also creates the page-request-ID index
                await ImportDbHacks.CleanDuplicateHitsAndCreateIX_PageRequestID(db);
                _telemetry.LogInformation($"Startup: duplicate-hit cleanup completed in {sw.Elapsed.TotalSeconds:N1}s");

                sw.Restart();
                var filterUrls = await SiteFilterLoader.Load(db);
                _telemetry.LogInformation($"Startup: loaded {filterUrls.Count} URL filters in {sw.Elapsed.TotalSeconds:N1}s");

                var newestHit = await db.hits.OrderByDescending(h => h.hit_timestamp).Take(1).FirstOrDefaultAsync();

                // Figure out when to start. Either the debug override, or last hit (if there is one), or 31 days ago
                var startDate = scanFromDateOverride.HasValue ? scanFromDateOverride.Value : newestHit?.hit_timestamp ?? DateTime.Now.AddDays(-31);

                // Rewind start-date a wee bit just to make sure we get edge hits...
                startDate = startDate.AddMinutes(-1);

                var jobTimer = new JobTimer(_telemetry, "Hits import");
                if (newestHit != null)
                {
                    _telemetry.LogInformation($"Requesting data from AppInsights from {startDate} (newest hit is from {newestHit.hit_timestamp})...");
                }
                else
                {
                    _telemetry.LogInformation($"Requesting data from AppInsights from {startDate} (no hits yet to start from previous)...");
                }

                // Import page-views first
                var credential = new ClientSecretCredential(
                    this._importConfig.TenantGUID.ToString(),
                    this._importConfig.ClientID,
                    this._importConfig.ClientSecret);
                using (var ai = new AppInsightsAPIClient(this._importConfig.AppInsightsConnectionString, credential, _telemetry))
                {

                    var endDate = DateTime.Now;
                    var daysToRead = startDate.EachDay(endDate);
                    _telemetry.LogInformation($"Importing hits for {daysToRead.Count()} days...");
                    var totalDays = 0;
                    var totalPageViews = 0;
                    var totalEvents = 0;
                    foreach (var d in daysToRead)
                    {
                        totalDays++;
                        var dayTimer = Stopwatch.StartNew();
                        _telemetry.LogInformation($"Importing day {totalDays}/{daysToRead.Count()}: {d.ToString("yyyy-MM-dd")}...");

                        // Fetch page-views and custom events for the same day in parallel.
                        // Both are independent read-only API calls with no shared state.
                        PageViewCollection pageViewsResult;
                        CustomEventsResultCollection events;
                        try
                        {
                            sw.Restart();
                            var pageViewsTask = ai.GetPageViewsFromAppInsights(d, saveRestResponses);
                            var eventsTask = ai.GetCustomEventsFromAppInsights(d, saveRestResponses);
                            await Task.WhenAll(pageViewsTask, eventsTask);

                            pageViewsResult = pageViewsTask.Result;
                            events = eventsTask.Result;
                            _telemetry.LogInformation($"API fetch completed in {sw.Elapsed.TotalSeconds:N1}s - {pageViewsResult.Rows.Count:n0} page-views, {events.Rows.Count:n0} events");
                        }
                        catch (Exception ex)
                        {
                            _telemetry.LogError(ex, $"Got fatal exception downloading from Application Insights REST: {ex.Message}");
#if DEBUG
                            throw;
#else
                            return;
#endif
                        }

                        if (pageViewsResult.Rows.Count > 0)
                        {
                            var earliest = pageViewsResult.Rows.OrderBy(v => v.Timestamp).Take(1).First();
                            var latest = pageViewsResult.Rows.OrderByDescending(v => v.Timestamp).Take(1).First();
                            _telemetry.LogInformation($"Hits range: {earliest.Timestamp:yyyy-MM-dd HH:mm:ss.ff} to {latest.Timestamp:yyyy-MM-dd HH:mm:ss.ff}");
                        }

                        if (pageViewsResult.Rows.Count > 0 || events.Rows.Count > 0)
                        {
                            // Save to DB
                            try
                            {
                                sw.Restart();
                                await pageViewsResult.SaveToSQL(db, _telemetry, filterUrls);
                                _telemetry.LogInformation($"Page-views SQL save completed in {sw.Elapsed.TotalSeconds:N1}s");

                                sw.Restart();
                                await events.SaveAllEventTypesToSql(_telemetry, _importConfig);
                                _telemetry.LogInformation($"Events SQL save completed in {sw.Elapsed.TotalSeconds:N1}s");
                            }
                            catch (SqlException ex)
                            {
                                _telemetry.LogError(ex, "Got SQL error: " + ex.Message);
                                if (Debugger.IsAttached)
                                {
                                    throw;
                                }
                            }

                            totalPageViews += pageViewsResult.Rows.Count;
                            totalEvents += events.Rows.Count;
                            _telemetry.LogInformation($"Day {d:yyyy-MM-dd} completed in {dayTimer.Elapsed.TotalSeconds:N1}s - saved {pageViewsResult.Rows.Count:n0} page-views, {events.Rows.Count:n0} events");
                        }
                        else
                        {
                            _telemetry.LogInformation($"Day {d:yyyy-MM-dd} completed in {dayTimer.Elapsed.TotalSeconds:N1}s - no new data.");
                        }
                    }

                    _telemetry.LogInformation($"Import loop finished: {totalDays} days processed, {totalPageViews:n0} total page-views, {totalEvents:n0} total events");
                }

                // Track finished event 
                jobTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
            }
        }
    }
}
