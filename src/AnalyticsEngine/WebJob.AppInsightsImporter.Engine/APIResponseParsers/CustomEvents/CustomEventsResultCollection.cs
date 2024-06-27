using Common.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.Sql;

namespace WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents
{
    public class CustomEventsResultCollection : AppInsightsQueryResultCollection<BaseCustomEventAppInsightsQueryResult>
    {
        public CustomEventsResultCollection() : base()
        {
        }


        public CustomEventsResultCollection(AppInsightsTable fromTable, DateTime fromWhen, ILogger debugTracer) : base(fromTable, fromWhen, debugTracer)
        {
        }

        protected override BaseCustomEventAppInsightsQueryResult Build(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic)
        {
            var baseEvent = new NameOnlyCustomEventAppInsightsQueryResult(rowColumnVals, propDic);
            if (baseEvent.IsValid)
            {
                BaseCustomEventAppInsightsQueryResult e = null;
                if (baseEvent.Name == AppInsightsImporterConstants.EVENT_NAME_PAGE_EXIT)
                {
                    e = new PageExitEventAppInsightsQueryResult(rowColumnVals, propDic);
                }
                else if (baseEvent.Name == AppInsightsImporterConstants.EVENT_NAME_USER_SEARCH)
                {
                    e = new SearchEventAppInsightsQueryResult(rowColumnVals, propDic);
                }
                else if (baseEvent.Name == AppInsightsImporterConstants.EVENT_NAME_CLICK)
                {
                    e = new ClickEventAppInsightsQueryResult(rowColumnVals, propDic);
                }
                else if (baseEvent.Name == AppInsightsImporterConstants.EVENT_NAME_PAGE_UPDATE)
                {
                    e = new PageUpdateEventAppInsightsQueryResult(rowColumnVals, propDic);
                }

                if (e != null && e.IsValid)
                {
                    return e;
                }
            }


            // Unknown event type. Ignore
            return null;
        }

        /// <summary>
        /// Apply hit patches, save searches & clicks
        /// </summary>
        public async Task SaveAllEventTypesToSql(AnalyticsLogger telemetry)
        {

            using (var database = new AnalyticsEntitiesContext())
            {
                // Hack to change/ensure correct DB schema. Needs moving to a migration
                ImportDbHacks.EnsureSessionTableHasRightCollation(database.Database);


                var hitUpdatesTimer = new JobTimer(telemetry, "Hit updates");
                hitUpdatesTimer.Start();
                await this.SaveHitsUpdatesToSQL(telemetry, database);
                hitUpdatesTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);

                var searchesInsertTimer = new JobTimer(telemetry, "Searches");
                searchesInsertTimer.Start();
                var searchesInserted = await this.SaveSearchesToSQL(telemetry, database);
                searchesInsertTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                telemetry.LogInformation($"Search batch imported - {searchesInserted.ToString("n0")} new searches inserted");


                var pageUpdatesTimer = new JobTimer(telemetry, "Page updates");
                pageUpdatesTimer.Start();
                var pagesUpdated = await this.SavePageUpdatesToSQL(telemetry);
                pageUpdatesTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                telemetry.LogInformation($"Page updates imported - {pagesUpdated.ToString("n0")}");


                var clicksInsertTimer = new JobTimer(telemetry, "Clicks");
                clicksInsertTimer.Start();
                var clicks = await this.SaveClicksToSQL(telemetry, database);
                telemetry.LogInformation($"Clicks batch imported - {clicks} clicks inserted");
                clicksInsertTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
            }
        }
    }
}
