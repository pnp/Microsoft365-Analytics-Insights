using Common.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Properties;

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    public static class SearchesSaveExtension
    {

        public static async Task<int> SaveSearchesToSQL(this CustomEventsResultCollection eventList, ILogger debugTracer, AnalyticsEntitiesContext database)
        {
            if (eventList.Rows.Count == 0)
            {
                return 0;
            }

            var searches = new List<SearchEventAppInsightsQueryResult>();
            foreach (var r in eventList.Rows)
            {
                if (r is SearchEventAppInsightsQueryResult)
                {
                    searches.Add((SearchEventAppInsightsQueryResult)r);
                }
            }

            debugTracer.LogInformation($"Processing {searches.Count.ToString("n0")} searches...");

            // Read default connection-string
            var defaultConnectionString = database.Database.Connection.ConnectionString;

            // Create our own connection & context to use it
            using (var con = new SqlConnection(defaultConnectionString))
            {
                con.Open();

                using (var db = new AnalyticsEntitiesContext(con))
                {

                    // Create staging table if doesn't exist
                    await db.Database.ExecuteSqlCommandAsync(FixSearchScript(Resources.Create_Searches_Import_Temp_Table));

                    // Import data into staging table
                    int i = 0;
                    foreach (var customEvent in searches)
                    {
                        string sqlInsert = Environment.NewLine + $"insert into [{AppInsightsImporterConstants.STAGING_TABLE_SEARCHES}] (" +
                            @"[ai_session_id],
                            [user_name],
                            [search_term],
                            [date_time]
                            )
                            values (@p0,@p1,@p2,@p3)";

#if DEBUG
                        if (i % 100 == 0)
                        {
                            Console.Write($"{i}...");
                        }
#endif

                        // Max length
                        string searchTerm = customEvent.CustomProperties.SearchText;
                        if (searchTerm.Length > 250)
                        {
                            searchTerm = searchTerm.Substring(0, 247) + "...";
                        }

                        await db.Database.ExecuteSqlCommandAsync(sqlInsert,
                            customEvent.CustomProperties.SessionId,
                            customEvent.Username,
                            searchTerm,
                            customEvent.Timestamp
                        );


                        i++;
                    }

                    // Run script to copy to proper tables
                    var searchesInserted = await db.Database.ExecuteSqlCommandAsync(FixSearchScript(Resources.Migrate_Searches_Import));

                    return searchesInserted;
                }
            }
        }
        static string FixSearchScript(string sql)
        {
            return sql.Replace("${STAGING_TABLE_SEARCHES}", AppInsightsImporterConstants.STAGING_TABLE_SEARCHES);
        }

    }
}
