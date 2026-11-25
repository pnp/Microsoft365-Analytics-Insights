using Common.Entities;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading;
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
            AnalyticsEntitiesContext database, int? mergeTimeoutSeconds = null, CancellationToken cancellationToken = default,
            ISet<Guid> rejectedPageRequests = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            using (var con = AzureSqlTokenAuth.CreateConnection(database.Database.Connection.ConnectionString, cancellationToken))
            {
                await con.OpenAsync(cancellationToken);
                var timeout = mergeTimeoutSeconds ?? database.Database.CommandTimeout ?? 30;
                var admission = await SearchPageAdmission.ReadAsync(con, searches, timeout, cancellationToken);
                var unmatched = new Dictionary<DateTime, int>();
                var missingIngestionTime = 0;
                var excluded = 0;
                var staged = 0;
                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = FixSearchScript(Resources.Create_Searches_Import_Temp_Table);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                    cmd.CommandText = $"INSERT INTO [{AppInsightsImporterConstants.STAGING_TABLE_SEARCHES}] " +
                        "([ai_session_id], [user_name], [search_term], [date_time], [ingestion_hour], [verified_user_id]) VALUES (@p0, @p1, @p2, @p3, @p4, @p5)";

                    var pSessionId = cmd.Parameters.Add("@p0", SqlDbType.VarChar, 50);
                    var pUserName = cmd.Parameters.Add("@p1", SqlDbType.NVarChar, 250);
                    var pSearchTerm = cmd.Parameters.Add("@p2", SqlDbType.NVarChar, SearchTermRules.MaxSearchTermLength);
                    var pDateTime = cmd.Parameters.Add("@p3", SqlDbType.DateTime);
                    var pIngestedHour = cmd.Parameters.Add("@p4", SqlDbType.DateTime);
                    var pVerifiedUser = cmd.Parameters.Add("@p5", SqlDbType.Int);
                    cmd.Prepare();

                    foreach (var customEvent in searches)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int? userId = null;
                        if (admission != null)
                        {
                            userId = admission.Resolve(customEvent, rejectedPageRequests, out bool pending);
                            if (!userId.HasValue)
                            {
                                if (!pending) excluded++;
                                else if (customEvent.SearchIngestionHourUtc is DateTime hour)
                                {
                                    unmatched.TryGetValue(hour, out var count);
                                    unmatched[hour] = count + 1;
                                }
                                else missingIngestionTime++;
                                continue;
                            }
                        }
                        var searchTerm = SearchTermRules.Truncate(customEvent.CustomProperties.SearchText);

                        pSessionId.Value = customEvent.CustomProperties.SessionId;
                        pUserName.Value = customEvent.Username;
                        pSearchTerm.Value = searchTerm;
                        pDateTime.Value = customEvent.Timestamp;
                        pIngestedHour.Value = (object)customEvent.SearchIngestionHourUtc ?? DBNull.Value;
                        pVerifiedUser.Value = (object)userId ?? DBNull.Value;

                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                        staged++;
                    }

                    if (excluded > 0) logger.LogInformation($"Skipped {excluded:n0} search event(s) not eligible under the site filter.");
                    if (staged == 0) return new SearchSaveResult(0, 0, skipped + excluded, unmatched, missingIngestionTime);
                    logger.LogInformation($"Inserted {staged:n0} searches into staging in {sw.Elapsed.TotalSeconds:N1}s. Running merge script...");
                    sw.Restart();
                }
                // The lookup and fact inserts used to run in EF's implicit transaction. Commit them together.
                cancellationToken.ThrowIfCancellationRequested();
                using (var transaction = con.BeginTransaction())
                using (var merge = con.CreateCommand())
                {
                    merge.Transaction = transaction;
                    merge.CommandText = SearchPageAdmission.VerifyCurrentRulesSql
                        + BuildSearchMergeSql(AppInsightsImporterConstants.STAGING_TABLE_SEARCHES, admission != null);
                    merge.CommandTimeout = timeout;
                    int facts, rows;
                    using (var reader = await merge.ExecuteReaderAsync(cancellationToken))
                    {
                        await reader.ReadAsync(cancellationToken);
                        facts = reader.GetInt32(0);
                        rows = reader.GetInt32(1);
                        await reader.NextResultAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            if (reader.IsDBNull(0)) missingIngestionTime += reader.GetInt32(1);
                            else
                            {
                                var hour = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
                                unmatched.TryGetValue(hour, out var count);
                                unmatched[hour] = count + reader.GetInt32(1);
                            }
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction.Commit();
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = new SearchSaveResult(rows, facts, skipped + excluded, unmatched, missingIngestionTime);
                    logger.LogInformation($"Search merge completed in {sw.Elapsed.TotalSeconds:N1}s - {rows:n0} new rows, " +
                        $"{facts:n0} new searches, {searches.Count - excluded - facts - result.Unmatched:n0} replayed, {result.Unmatched:n0} without a matching visit.");
                    return result;
                }
            }
        }
        static string FixSearchScript(string sql) => sql.Replace("${STAGING_TABLE_SEARCHES}", AppInsightsImporterConstants.STAGING_TABLE_SEARCHES);

        // Keep separate plans: an optional OR on verified_user_id made the unrestricted hot-replay
        // batch do a primary-key lookup per search. Only an admitted page can supply this guard.
        internal static string BuildSearchMergeSql(string stagingTable, bool verifiedOwner) => Resources.Migrate_Searches_Import
            .Replace("${STAGING_TABLE_SEARCHES}", stagingTable)
            .Replace("${SEARCH_OWNER_FILTER}", verifiedOwner
                ? "and s.user_id = imports.verified_user_id and CONVERT(varbinary(50), s.ai_session_id) = CONVERT(varbinary(50), imports.ai_session_id)"
                : string.Empty);

    }
}
