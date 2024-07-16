using Common.Entities;
using Common.Entities.Config;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Properties;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// SQL adaptor for saving activity reports. 
    /// Saves to a staging table, merges everything with a SQL script, then processes workload specific metadata updates seperately.
    /// </summary>
    public class ActivityReportSqlPersistenceManager : IActivityReportPersistenceManager
    {
        private readonly AuditFilterConfig _filterConfig;
        private readonly ILogger _telemetry;
        private readonly AppConfig _appConfig;
        private string _defaultConnectionString = null;

        private static SemaphoreSlim _sqlSaveSemaphore = new SemaphoreSlim(1);      // Make sure we're only saving one thread at a time

        public ActivityReportSqlPersistenceManager(AuditFilterConfig filterConfig, ILogger telemetry, AppConfig appConfig)
        {
            _filterConfig = filterConfig;
            _telemetry = telemetry;
            _appConfig = appConfig;
        }

        /// <summary>
        /// Write all to SQL with a new data cache for the events only in activities content-set
        /// </summary>
        public async Task<ImportStat> CommitAll(ActivityReportSet activities)
        {
            if (activities.Count > 0)
            {
                var cache = ActivityImportCache.GetAndBuildNewCache(activities.OldestContent, activities.NewestContent);

                // Read default connection-string
                if (string.IsNullOrEmpty(_defaultConnectionString))
                {
                    using (var db = new AnalyticsEntitiesContext())
                    {
                        _defaultConnectionString = db.Database.Connection.ConnectionString;
                    }
                }

                return await CommitAllToSQL(activities, cache);
            }
            else return new ImportStat();
        }

        /// <summary>
        /// Write all to SQL with an existing cache
        /// </summary>
        async Task<ImportStat> CommitAllToSQL(ActivityReportSet activities, ActivityImportCache cache)
        {
#if DEBUG
            Console.WriteLine($"DEBUG: Processing {activities.Count.ToString("n0")} activity reports...");
#endif
            var allStats = new ImportStat();

            // Allow only one save at a time otherwise we'll get errors when we try and create the temp table without clearing it down 1st
            await _sqlSaveSemaphore.WaitAsync();

            // Create our own connection & context to use it
            try
            {
                using (var con = new SqlConnection(_defaultConnectionString))
                {
                    con.Open();
                    using (var db = new AnalyticsEntitiesContext(con))
                    {
                        // Add all activity data to staging table
                        var stats = await SaveToSqlAllTheThings(activities, db, con, cache);
                        allStats.AddStats(stats);
                    }
                }
            }
            finally
            {
                _sqlSaveSemaphore.Release();        // Whatever happens, make sure we release the semaphore 
            }

            return allStats;
        }

        /// <summary>
        /// Fill up staging table & return import result
        /// </summary>
        private async Task<ImportStat> SaveToSqlAllTheThings(ActivityReportSet activities, AnalyticsEntitiesContext db, SqlConnection con, ActivityImportCache cache)
        {
            var listOfActivitiesSavedToSQL = new ConcurrentBag<AbstractAuditLogContent>();
            var logsToInsert = new EFInsertBatch<AuditLogTempEntity>(db, _telemetry);
            var processedIds = new ConcurrentBag<Guid>();
            var stats = new ImportStat() { Total = activities.Count };

            foreach (var abtractLog in activities)
            {
                // Don't insert duplicates in same set
                if (!processedIds.Contains(abtractLog.Id) && !cache.HaveSeenInProcessedOrIgnoredEvents(abtractLog))
                {
                    var result = SaveResultEnum.NotSaved;
                    if (_filterConfig.InScope(abtractLog))
                    {
                        logsToInsert.Rows.Add(new AuditLogTempEntity(abtractLog, abtractLog.UserId));

                        // Remember we've done this one now
                        cache.RememberProcessedEvent(abtractLog);
                        result = SaveResultEnum.Imported;
                    }
                    else
                    {
                        // No URL
                        cache.RememberNewlyIgnoredEvent(abtractLog);
                        result = SaveResultEnum.OutOfScope;
                    }

                    // Update stats
                    if (result == SaveResultEnum.Imported)
                    {
                        stats.Imported++;
                        listOfActivitiesSavedToSQL.Add(abtractLog);
                    }
                    else if (result == SaveResultEnum.ProcessedAlready) stats.ProcessedAlready++;
                    else if (result == SaveResultEnum.OutOfScope) stats.URLsOutOfScope++;
                    else throw new InvalidOperationException("Unexpected save result");

                    processedIds.Add(abtractLog.Id);
                }
            }

            // Merge data
#if DEBUG
            Console.WriteLine("\nDEBUG: Merging activity staging table...");
#endif
            // Merge to normal tables
            var mergeSQL = Resources.Insert_Activity_from_Staging_Table.Replace("${STAGING_TABLE_ACTIVITY}", ActivityImportConstants.STAGING_TABLE_ACTIVITY);
            await logsToInsert.SaveToStagingTable(mergeSQL);

            #region Add Extra Metadata

            // Add metadata the traditional way with EF. By now should have all the sites saved. 
            var saveSession = new SaveSession(_telemetry, db, _appConfig);
            await saveSession.Init();

            int metaSaveIdx = 0, changesMadeCount = 0;
#if DEBUG
            Console.WriteLine($"\nDEBUG: Updating metadata for {listOfActivitiesSavedToSQL.Count.ToString("n0")} saved events...");
#endif
            if (listOfActivitiesSavedToSQL.Count > 0)
            {
                var ids = listOfActivitiesSavedToSQL.Select(l => l.Id).ToList();
                var eventsJustSaved = db.AuditEventsCommon
                    .Include(e => e.User)
                    .Where(e => ids.Contains(e.Id)).ToList();

                var spEventsJustSaved = db.sharepoint_events
                    .Include(spe => spe.Event)
                    .Where(e => ids.Contains(e.EventID)).ToList();

                foreach (var e in spEventsJustSaved)
                {
                    saveSession.CachedSpEvents.Add(e.EventID, e);
                }

                foreach (var log in listOfActivitiesSavedToSQL)
                {
#if DEBUG
                    if (metaSaveIdx > 0 && metaSaveIdx % 1000 == 0)
                    {
                        float percentDone = ((float)metaSaveIdx / (float)listOfActivitiesSavedToSQL.Count) * 100;
                        Console.Write($"{Math.Round(percentDone, 0)}%...");
                    }
#endif
                    // Add metadata
                    var changesMade = await log.ProcessExtendedProperties(saveSession, eventsJustSaved.Where(e => e.Id == log.Id).SingleOrDefault());
                    if (changesMade)
                        changesMadeCount++;

                    metaSaveIdx++;
                }
            }
#if DEBUG
            Console.WriteLine($"DEBUG: Updated metadata for {changesMadeCount.ToString("n0")} saved events");
#endif

            // Save metadata updates
            await saveSession.CommitAllChanges();

            #endregion

            return stats;
        }
    }

    /// <summary>
    /// Class for inserting staging data to temp SQL table
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_ACTIVITY)]
    internal class AuditLogTempEntity
    {
        public AuditLogTempEntity(AbstractAuditLogContent abtractLog, string userNameOrHash)
        {

            this.Id = abtractLog.Id;
            this.UserName = userNameOrHash;
            this.OperationName = abtractLog.Operation;
            this.TimeStamp = abtractLog.CreationTime;
            this.TypeName = abtractLog.ItemType;
            this.ObjectId = abtractLog.ObjectId;
            this.Workload = abtractLog.Workload;


            if (abtractLog is SharePointAuditLogContent)
            {
                var spLog = (SharePointAuditLogContent)abtractLog;

                this.FileName = spLog.SourceFileName;
                this.ExtensionName = spLog.SourceFileExtension;
                this.UrlBase = spLog.SiteUrl;
                this.EventData = spLog.EventData;
            }

            if (abtractLog is CopilotAuditLogContent) {
                var copilotLog = (CopilotAuditLogContent)abtractLog;
                this.EventData = copilotLog.EventRaw;
            }
        }

        [Column("log_id")]
        public Guid Id { get; set; }

        [Column("user_name")]
        public string UserName { get; set; }

        [Column("file_name", true)]
        public string FileName { get; set; }

        [Column("extension_name", true)]
        public string ExtensionName { get; set; }

        [Column("operation_name")]
        public string OperationName { get; set; }

        [Column("time_stamp")]
        public DateTime TimeStamp { get; set; }

        [Column("workload")]
        public string Workload { get; set; }

        [Column("url_base", true)]
        public string UrlBase { get; set; }

        [Column("event_data", true)]
        public string EventData { get; set; }

        [Column("type_name", true)]
        public string TypeName { get; set; }

        [Column("object_id", true)]
        public string ObjectId { get; set; }

        [Column("web_url", true)]
        public string WebUrl { get; set; }
    }
}

