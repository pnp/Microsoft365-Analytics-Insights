using Common.Entities;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Properties;
using WebJob.AppInsightsImporter.Engine.Sql.Models;

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    public static class HitUpdatesSqlExtension
    {
        public static Task<int> SaveHitsUpdatesToSQL(this CustomEventsResultCollection eventList, ILogger logger, AnalyticsEntitiesContext database)
            => eventList.SaveHitsUpdatesToSQL(logger, database, CancellationToken.None);

        internal static async Task<int> SaveHitsUpdatesToSQL(this CustomEventsResultCollection eventList, ILogger logger,
            AnalyticsEntitiesContext database, CancellationToken cancellationToken)
        {
            if (eventList.Rows.Count == 0)
            {
                return 0;
            }

            var updatesToInsert = new EFInsertBatch<HitUpdate>(database, logger, cancellationToken);
            foreach (var item in eventList.Rows)
            {
                if (item is PageExitEventAppInsightsQueryResult)
                {
                    updatesToInsert.Rows.Add(new HitUpdate((PageExitEventAppInsightsQueryResult)item));
                }
            }
            return await updatesToInsert.SaveToStagingTable(FixHitUpdatesScript(Resources.Update_Hits_From_Staging));

        }

        static string FixHitUpdatesScript(string sql)
        {
            return sql.Replace("${STAGING_TABLE_UPDATES}", HitUpdate.STAGING_TABLENAME);
        }
    }
}
