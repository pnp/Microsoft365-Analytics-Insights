using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using System.Data.SqlClient;
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
            // Delete duplicate hits 1st. It also creates the page-request-ID index
            ImportDbHacks.CleanDuplicateHits();

            DateTime? scanFromDateOverride = null;
            if (daysBeforeOverride.HasValue)
            {
                scanFromDateOverride = DateTime.Now.AddDays(daysBeforeOverride.Value * -1);
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                var filterUrls = await SiteFilterLoader.Load(db);

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
                using (var ai = new AppInsightsAPIClient(this._importConfig.AppInsightsAppId, this._importConfig.AppInsightsApiKey, _telemetry))
                {
                    // Read page-views
                    PageViewCollection pageViewsResult = null;
                    try
                    {
                        pageViewsResult = await ai.GetPageViewsFromAppInsights(startDate, saveRestResponses);
                    }
                    catch (Exception ex)
                    {
                        _telemetry.LogError(ex, $"Got fatal exception downloading page-views from Application Insights REST: {ex.Message}");
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

                        var pageViewsNewer = pageViewsResult.Rows.Where(v => v.Timestamp > startDate);
                        _telemetry.LogInformation($"Hits downloaded - '{pageViewsResult.Rows.Count.ToString("n0")}' in total. " +
                            $"Earliest: {earliest.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.ff")}, latest: {latest.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.ff")}");

                        // Read custom events
                        CustomEventsResultCollection events = null;
                        try
                        {
                            events = await ai.GetCustomEventsFromAppInsights(startDate, saveRestResponses);
                        }
                        catch (Exception ex)
                        {
                            _telemetry.LogError(ex, $"Got fatal exception downloading custom events from Application Insights REST: {ex.Message}");
                            return;
                        }

                        _telemetry.LogInformation($"Processed {events.Rows.Count} events, and {pageViewsResult.Rows.Count} page-views");


                        // Save to DB
                        try
                        {
                            await pageViewsResult.SaveToSQL(db, _telemetry, filterUrls);
                            await events.SaveAllEventTypesToSql(_telemetry, _importConfig);
                        }
                        catch (SqlException ex)
                        {
                            _telemetry.LogError(ex, "Got SQL error: " + ex.Message);
                            if (System.Diagnostics.Debugger.IsAttached)
                            {
                                throw;
                            }
                        }
                        _telemetry.LogInformation($"Saved {events.Rows.Count.ToString("n0")} events, and {pageViewsResult.Rows.Count.ToString("n0")} page-views");

                        // Track finished event 
                        jobTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                    }
                    else
                    {
                        _telemetry.LogInformation($"No new hits found.");
                    }
                }
            }
        }
    }
}
