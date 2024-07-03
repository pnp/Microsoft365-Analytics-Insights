using Common.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
        public static async Task SaveToSQL(this PageViewCollection pageViews, AnalyticsEntitiesContext database, ILogger telemetry, List<FilterUrlConfig> filterUrls)
        {
            // Hack to change/ensure correct DB schema. Needs moving to a migration
            ImportDbHacks.EnsureSessionTableHasRightCollation(database.Database);

            var pageViewsProcessed = new System.Collections.Concurrent.ConcurrentBag<Guid>();

            var logsToInsert = new EFInsertBatch<HitTempEntity>(database, telemetry);
            foreach (var pv in pageViews.Rows.Where(p => p.CustomProperties?.PageRequestId != null))
            {
                var userName = pv.Username;
                var hitIsNew = pv.CustomProperties.PageRequestId != Guid.Empty && !pageViewsProcessed.Contains(pv.CustomProperties.PageRequestId.Value);

                if (hitIsNew)
                {
                    // Remember page view ID in case we get duplicates. 
                    pageViewsProcessed.Add(pv.CustomProperties.PageRequestId.Value);

                    // Filter URLs based on org_urls table 
                    if (!filterUrls.UrlInScope(pv.CustomProperties.SiteUrl, pv.Url))
                    {
                        telemetry.LogInformation($"Ignoring out-of-scope URL: {pv.Url}");
                    }
                    else
                    {
                        // URL is in scope. Add to staging table. 
                        logsToInsert.Rows.Add(new HitTempEntity(pv));
                    }
                }
                else
                {
#if DEBUG
                    if (pv.CustomProperties.PageRequestId != Guid.Empty)
                    {
                        Console.WriteLine("DEBUG: Ignoring duplicate page-request " + pv.CustomProperties.PageRequestId);
                    }
                    else
                    {
                        Console.WriteLine("DEBUG: Ignoring page-request with empty page-request.");
                    }
#endif
                }
            }

            const int MAX_HITS_PER_THREAD = 1000;
            await logsToInsert.SaveToStagingTable(MAX_HITS_PER_THREAD, FixScript(Resources.Migrate_Hits_Import_into_Hits));

            telemetry.LogInformation($"Hits batch imported.");
        }


        public static async Task SaveToSQL(this PageViewCollection pageViews, AnalyticsEntitiesContext database, ILogger telemetry)
        {
            await SaveToSQL(pageViews, database, telemetry, new List<FilterUrlConfig>());
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
