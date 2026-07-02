using Common.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Properties;

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    public static class SearchesSaveExtension
    {

        public static async Task<int> SaveSearchesToSQL(this CustomEventsResultCollection eventList, ILogger logger, AnalyticsEntitiesContext database)
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

            if (searches.Count == 0)
            {
                return 0;
            }

            logger.LogInformation($"Processing {searches.Count.ToString("n0")} searches...");
            var sw = Stopwatch.StartNew();

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

                    // Bulk-insert using a single parameterised command, reusing parameters each iteration
                    var cmd = con.CreateCommand();
                    cmd.CommandText = $"INSERT INTO [{AppInsightsImporterConstants.STAGING_TABLE_SEARCHES}] " +
                        "([ai_session_id], [user_name], [search_term], [date_time]) VALUES (@p0, @p1, @p2, @p3)";

                    var pSessionId = cmd.Parameters.Add("@p0", SqlDbType.NVarChar, 100);
                    var pUserName = cmd.Parameters.Add("@p1", SqlDbType.NVarChar, 250);
                    var pSearchTerm = cmd.Parameters.Add("@p2", SqlDbType.NVarChar, 250);
                    var pDateTime = cmd.Parameters.Add("@p3", SqlDbType.DateTime);
                    cmd.Prepare();

                    foreach (var customEvent in searches)
                    {
                        string searchTerm = customEvent.CustomProperties.SearchText;
                        if (searchTerm.Length > 250)
                        {
                            searchTerm = searchTerm.Substring(0, 247) + "...";
                        }

                        pSessionId.Value = (object)customEvent.CustomProperties.SessionId ?? DBNull.Value;
                        pUserName.Value = (object)customEvent.Username ?? DBNull.Value;
                        pSearchTerm.Value = searchTerm;
                        pDateTime.Value = customEvent.Timestamp;

                        await cmd.ExecuteNonQueryAsync();
                    }

                    logger.LogInformation($"Inserted {searches.Count:n0} searches into staging in {sw.Elapsed.TotalSeconds:N1}s. Running merge script...");
                    sw.Restart();

                    // Run script to copy to proper tables
                    var searchesInserted = await db.Database.ExecuteSqlCommandAsync(FixSearchScript(Resources.Migrate_Searches_Import));

                    logger.LogInformation($"Search merge completed in {sw.Elapsed.TotalSeconds:N1}s - {searchesInserted:n0} new rows.");
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
