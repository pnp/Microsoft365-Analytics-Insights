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
        public Task SaveAllEventTypesToSql(AnalyticsLogger logger, AppConfig config)
        {
            return SaveAllEventTypesToSql(logger, config, DefaultAnalyticsDbContextFactory.Instance);
        }

        /// <summary>
        /// As above, with the context factory supplied (issue #368/#369). Production behaviour is
        /// unchanged: DefaultAnalyticsDbContextFactory.Create() is `new AnalyticsEntitiesContext()`, and the
        /// single context still spans the whole event save exactly as it did.
        /// </summary>
        public async Task SaveAllEventTypesToSql(AnalyticsLogger logger, AppConfig config, IAnalyticsDbContextFactory contextFactory)
        {
            if (contextFactory == null) throw new ArgumentNullException(nameof(contextFactory));

            using (var database = contextFactory.Create())
            {
                // Hack to change/ensure correct DB schema. Needs moving to a migration
                await ImportDbHacks.EnsureSessionTableHasRightCollation(database.Database);

                // The section orchestration itself is database-free - see CustomEventSectionSaver (#369).
                var saver = new CustomEventSectionSaver(logger,
                    new SqlHitUpdatePersistenceManager(database, logger),
                    new SqlSearchesPersistenceManager(database, logger),
                    new SqlPageUpdatePersistenceManager(logger, config, contextFactory),
                    new SqlClicksPersistenceManager(database, logger));

                await saver.SaveAllSectionsAsync(this);
            }
        }
    }
}
