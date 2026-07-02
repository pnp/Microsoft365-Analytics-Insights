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

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    public static class PageViewsSaveExtension
    {
        /// <summary>
        /// Save hits to staging table & then import all to real hits + lookups
        /// </summary>
        public static async Task SaveToSQL(this PageViewCollection pageViews, AnalyticsEntitiesContext database, ILogger logger, List<FilterUrlConfig> filterUrls)
        {
            var sw = Stopwatch.StartNew();

            // Hack to change/ensure correct DB schema. Needs moving to a migration
            await ImportDbHacks.EnsureSessionTableHasRightCollation(database.Database);

            // HashSet for O(1) duplicate lookups instead of O(n) List.Contains
            var pageRequestIdProcessed = new HashSet<Guid>();
            var duplicateCount = 0;
            var outOfScopeCount = 0;

            var logsToInsert = new EFInsertBatch<HitTempEntity>(database, logger);
            foreach (var pv in pageViews.Rows.Where(p => p.CustomProperties?.PageRequestId != null))
            {
                var hitIsNew = pv.CustomProperties.PageRequestId != Guid.Empty && pageRequestIdProcessed.Add(pv.CustomProperties.PageRequestId.Value);

                if (hitIsNew)
                {
                    // Filter URLs based on org_urls table 
                    if (!filterUrls.UrlInScope(pv.CustomProperties.SiteUrl, pv.Url))
                    {
                        outOfScopeCount++;
                    }
                    else
                    {
                        // URL is in scope. Add to staging table. 
                        logsToInsert.Rows.Add(new HitTempEntity(pv));
                    }
                }
                else
                {
                    duplicateCount++;
                }
            }

            if (outOfScopeCount > 0)
            {
                logger.LogInformation($"Filtered {outOfScopeCount} out-of-scope URLs.");
            }
            if (duplicateCount > 0)
            {
                logger.LogInformation($"Skipped {duplicateCount} duplicate page-request IDs.");
            }

            logger.LogInformation($"Staging {logsToInsert.Rows.Count:n0} hits for SQL import (filtered from {pageViews.Rows.Count:n0} raw page-views in {sw.Elapsed.TotalSeconds:N1}s)...");

            sw.Restart();
            const int MAX_HITS_PER_THREAD = 1000;
            await logsToInsert.SaveToStagingTable(MAX_HITS_PER_THREAD, FixScript(Resources.Migrate_Hits_Import_into_Hits));

            logger.LogInformation($"Hits batch imported and merged in {sw.Elapsed.TotalSeconds:N1}s.");
        }


        public static async Task SaveToSQL(this PageViewCollection pageViews, AnalyticsEntitiesContext database, ILogger logger)
        {
            await SaveToSQL(pageViews, database, logger, new List<FilterUrlConfig>());
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
