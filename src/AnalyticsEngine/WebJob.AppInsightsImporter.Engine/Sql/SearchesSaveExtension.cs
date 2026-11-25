using Common.Entities;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Properties;
using WebJob.AppInsightsImporter.Engine.Sql.Rules;

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    public static class SearchesSaveExtension
    {

        public static async Task<int> SaveSearchesToSQL(this CustomEventsResultCollection eventList, ILogger logger, AnalyticsEntitiesContext database)
            => (await eventList.SaveSearchBatchAsync(logger, database)).RowsAffected;

        internal static async Task<SearchSaveResult> SaveSearchBatchAsync(this CustomEventsResultCollection eventList, ILogger logger,
            AnalyticsEntitiesContext database, int? mergeTimeoutSeconds = null)
        {
            if (eventList.Rows.Count == 0)
            {
                return SearchSaveResult.Empty;
            }

            var searches = new List<SearchEventAppInsightsQueryResult>();
            var skipped = 0;
            foreach (var r in eventList.Rows)
            {
                if (r is SearchEventAppInsightsQueryResult search)
                {
                    if (SearchTermRules.ShouldStage(search))
                    {
                        searches.Add(search);
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }

            if (skipped > 0)
            {
                logger.LogWarning($"Skipped {skipped:n0} search event(s) without search text or a usable user/session identity.");
            }

            if (searches.Count == 0)
            {
                return skipped == 0 ? SearchSaveResult.Empty : new SearchSaveResult(0, 0, skipped, new Dictionary<DateTime, int>());
            }

            logger.LogInformation($"Processing {searches.Count.ToString("n0")} searches...");
            var sw = Stopwatch.StartNew();

            using (var con = AzureSqlTokenAuth.CreateConnection(database.Database.Connection.ConnectionString))
            {
                await con.OpenAsync();
                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = FixSearchScript(Resources.Create_Searches_Import_Temp_Table);
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = $"INSERT INTO [{AppInsightsImporterConstants.STAGING_TABLE_SEARCHES}] " +
                        "([ai_session_id], [user_name], [search_term], [date_time], [ingestion_hour]) VALUES (@p0, @p1, @p2, @p3, @p4)";

                    var pSessionId = cmd.Parameters.Add("@p0", SqlDbType.VarChar, 50);
                    var pUserName = cmd.Parameters.Add("@p1", SqlDbType.NVarChar, 250);
                    var pSearchTerm = cmd.Parameters.Add("@p2", SqlDbType.NVarChar, SearchTermRules.MaxSearchTermLength);
                    var pDateTime = cmd.Parameters.Add("@p3", SqlDbType.DateTime);
                    var pIngestedHour = cmd.Parameters.Add("@p4", SqlDbType.DateTime);
                    cmd.Prepare();

                    foreach (var customEvent in searches)
                    {
                        var searchTerm = SearchTermRules.Truncate(customEvent.CustomProperties.SearchText);

                        pSessionId.Value = customEvent.CustomProperties.SessionId;
                        pUserName.Value = customEvent.Username;
                        pSearchTerm.Value = searchTerm;
                        pDateTime.Value = customEvent.Timestamp;
                        pIngestedHour.Value = (object)customEvent.SearchIngestionHourUtc ?? DBNull.Value;

                        await cmd.ExecuteNonQueryAsync();
                    }

                    logger.LogInformation($"Inserted {searches.Count:n0} searches into staging in {sw.Elapsed.TotalSeconds:N1}s. Running merge script...");
                    sw.Restart();
                }
                // The lookup and fact inserts used to run in EF's implicit transaction. Commit them together.
                using (var transaction = con.BeginTransaction())
                using (var merge = con.CreateCommand())
                {
                    merge.Transaction = transaction;
                    merge.CommandText = FixSearchScript(Resources.Migrate_Searches_Import);
                    merge.CommandTimeout = mergeTimeoutSeconds ?? database.Database.CommandTimeout ?? merge.CommandTimeout;
                    var unmatched = new Dictionary<DateTime, int>();
                    var missingIngestionTime = 0;
                    int facts, rows;
                    using (var reader = await merge.ExecuteReaderAsync())
                    {
                        await reader.ReadAsync();
                        facts = reader.GetInt32(0);
                        rows = reader.GetInt32(1);
                        await reader.NextResultAsync();
                        while (await reader.ReadAsync())
                        {
                            if (reader.IsDBNull(0)) missingIngestionTime = reader.GetInt32(1);
                            else unmatched.Add(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), reader.GetInt32(1));
                        }
                    }
                    transaction.Commit();
                    var result = new SearchSaveResult(rows, facts, skipped, unmatched, missingIngestionTime);
                    logger.LogInformation($"Search merge completed in {sw.Elapsed.TotalSeconds:N1}s - {rows:n0} new rows, " +
                        $"{facts:n0} new searches, {searches.Count - facts - result.Unmatched:n0} replayed, {result.Unmatched:n0} without a matching visit.");
                    return result;
                }
            }
        }
        static string FixSearchScript(string sql)
        {
            return sql.Replace("${STAGING_TABLE_SEARCHES}", AppInsightsImporterConstants.STAGING_TABLE_SEARCHES);
        }

    }
}
