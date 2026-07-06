using Common.Entities;
using Common.Entities.Config;
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


        public CustomEventsResultCollection(AppInsightsTable fromTable, DateTime fromWhen, ILogger logger) : base(fromTable, fromWhen, logger)
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
        public async Task SaveAllEventTypesToSql(AnalyticsLogger logger, AppConfig config)
        {
            using (var database = new AnalyticsEntitiesContext())
            {
                // Hack to change/ensure correct DB schema. Needs moving to a migration
                await ImportDbHacks.EnsureSessionTableHasRightCollation(database.Database);

                // Each section runs inside its own isolation boundary (see SaveSectionSafe). A failure
                // in one section (e.g. a page-update that trips a DbUpdateException) is logged in full
                // but never aborts the sibling sections nor escapes to stall the whole importer.
                var hitUpdatesCount = await SaveSectionSafe(logger, "Hit updates",
                    () => this.SaveHitsUpdatesToSQL(logger, database));

                var searchesInserted = await SaveSectionSafe(logger, "Searches",
                    () => this.SaveSearchesToSQL(logger, database));

                var pagesUpdated = await SaveSectionSafe(logger, "Page updates",
                    () => this.SavePageUpdatesToSQL(logger, config));

                var clicks = await SaveSectionSafe(logger, "Clicks",
                    () => this.SaveClicksToSQL(logger, database));

                logger.LogInformation($"Event save summary: {hitUpdatesCount:n0} hit-updates, {searchesInserted:n0} searches, {pagesUpdated:n0} page-updates, {clicks:n0} clicks");
            }
        }

        /// <summary>
        /// Runs one event-save section (hit-updates / searches / page-updates / clicks) inside its own
        /// timer and isolation boundary. A failure in one section is logged in full and reported via
        /// TrackException, but never propagates - so a single bad section can neither abort its sibling
        /// sections nor escape up the call stack and stall the whole importer.
        /// </summary>
        private static async Task<int> SaveSectionSafe(AnalyticsLogger logger, string sectionName, Func<Task<int>> saveAction)
        {
            var timer = new JobTimer(logger, sectionName);
            timer.Start();
            try
            {
                var count = await saveAction();
                timer.PrintElapsed();
                if (count > 0)
                {
                    timer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                }
                return count;
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                logger.LogError($"Failed importing '{sectionName}' section: {CommonExceptionHandler.GetErrorText(ex)}. Skipping this section and continuing.");
                logger.LogError($"Exception detail: {ex}");
                return 0;
            }
        }
    }
}
