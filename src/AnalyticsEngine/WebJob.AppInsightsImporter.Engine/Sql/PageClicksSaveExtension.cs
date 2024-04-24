using Common.Entities;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Properties;
using WebJob.AppInsightsImporter.Engine.Sql.Models;

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    public static class PageClicksSaveExtension
    {
        /// <summary>
        /// Save hits to staging table & then import all to real hits + lookups
        /// </summary>
        public static async Task<int> SaveClicksToSQL(this CustomEventsResultCollection events, ILogger telemetry, AnalyticsEntitiesContext database)
        {
            var logsToInsert = new EFInsertBatch<ClickTempEntity>(database, telemetry);

            // Only process click events
            foreach (var p in events.Rows)
            {
                if (p is ClickEventAppInsightsQueryResult)
                {
                    var click = (ClickEventAppInsightsQueryResult)p;
                    if (click.IsValid)
                    {
                        logsToInsert.Rows.Add(new ClickTempEntity(click));
                    }
                    else
                    {
                        telemetry.LogWarning("Found invalid click data");
                    }
                }
            }

            const int MAX_HITS_PER_THREAD = 1000;
            return await logsToInsert.SaveToStagingTable(MAX_HITS_PER_THREAD, FixScript(Resources.Migrate_clicks_from_staging));
        }


        /// <summary>
        /// Replaces table name var in SQL script with correct table name
        /// </summary>
        static string FixScript(string sql)
        {
            return sql.Replace(
                        "${STAGING_TABLE_CLICKS}",
                        ClickTempEntity.STAGING_TABLENAME);
        }
    }
}
