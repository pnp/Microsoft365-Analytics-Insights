using Common.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.Properties;
using WebJob.AppInsightsImporter.Engine.Sql.Models;
using WebJob.AppInsightsImporter.Engine.Sql.Rules;

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    public static class PageViewsSaveExtension
    {
        /// <summary>
        /// Save hits to staging table & then import all to real hits + lookups
        /// </summary>
        public static async Task<PageViewSaveResult> SaveToSQL(this PageViewCollection pageViews, AnalyticsEntitiesContext database, ILogger logger, List<FilterUrlConfig> filterUrls)
        {
            var sw = Stopwatch.StartNew();

            // Hack to change/ensure correct DB schema. Needs moving to a migration
            await ImportDbHacks.EnsureSessionTableHasRightCollation(database.Database);

            // Which rows to stage, and why the rest were dropped. Pure decision logic - see issue #369.
            var plan = PageViewStagingRules.Plan(pageViews, filterUrls);

            var logsToInsert = new EFInsertBatch<HitTempEntity>(database, logger);
            logsToInsert.Rows.AddRange(plan.RowsToStage);

            if (plan.OutOfScopeUrls > 0)
            {
                logger.LogInformation($"Filtered {plan.OutOfScopeUrls} out-of-scope URLs.");
            }
            if (plan.DuplicatePageRequestIds > 0)
            {
                logger.LogInformation($"Skipped {plan.DuplicatePageRequestIds} duplicate page-request IDs.");
            }

            logger.LogInformation($"Staging {plan.RowsToStage.Count:n0} hits for SQL import (filtered from {plan.RawPageViews:n0} raw page-views in {sw.Elapsed.TotalSeconds:N1}s)...");

            sw.Restart();
            const int MAX_HITS_PER_THREAD = 1000;
            var mergeRowsAffected = await logsToInsert.SaveToStagingTable(MAX_HITS_PER_THREAD, FixScript(Resources.Migrate_Hits_Import_into_Hits));

            logger.LogInformation($"Hits batch imported and merged in {sw.Elapsed.TotalSeconds:N1}s.");

            // Reporting only - the counts above used to end at the log lines. See issue #369.
            return PageViewSaveResult.FromPlan(plan, mergeRowsAffected);
        }


        public static async Task<PageViewSaveResult> SaveToSQL(this PageViewCollection pageViews, AnalyticsEntitiesContext database, ILogger logger)
        {
            return await SaveToSQL(pageViews, database, logger, new List<FilterUrlConfig>());
        }


        /// <summary>
        /// Replaces table name var in SQL script with correct table name
        /// </summary>
        static string FixScript(string sql)
        {
            return sql.Replace(
                        "${STAGING_TABLE_HIT_IMPORTS}",
                        HitTempEntity.STAGING_TABLENAME);
        }

    }
}
