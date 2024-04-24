using Common.DataUtils;
using Common.Entities;
using Common.Entities.ActivityReports;
using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Reads and saves all data read from Graph
    /// </summary>
    public class GraphImporter : AbstractApiLoader
    {
        private readonly GraphAppIndentityOAuthContext _graphAppIndentityOAuthContext;
        private GraphServiceClient _graphClient = null;

        public GraphImporter(AnalyticsLogger telemetry, AppConfig settings) : base(telemetry, settings)
        {
            _graphAppIndentityOAuthContext = new GraphAppIndentityOAuthContext(telemetry, _settings.ClientID, settings.TenantGUID.ToString(), settings.ClientSecret, settings.KeyVaultUrl, settings.UseClientCertificate);

        }

        async Task InitAuth()
        {
            await _graphAppIndentityOAuthContext.InitClientCredential();
            _graphClient = new GraphServiceClient(_graphAppIndentityOAuthContext.Creds);
            _graphClient.HttpProvider.OverallTimeout = TimeSpan.FromHours(1);
        }

        /// <summary>
        /// Main entry-point
        /// </summary>
        public async Task GetAndSaveAllGraphData(AppConfig settings)
        {
            await InitAuth();
            var auth = new GraphAppIndentityOAuthContext(_telemetry, _settings.ClientID, _settings.TenantGUID.ToString(), _settings.ClientSecret, _settings.KeyVaultUrl, _settings.UseClientCertificate);
            var httpClient = new ManualGraphCallClient(auth, _telemetry);

            if (settings.ImportJobSettings.GraphUsersMetadata)
            {
                var userMetadaTimer = new JobTimer(_telemetry, "User metadata refresh");
                userMetadaTimer.Start();

                // Update Graph users first
                var userUpdater = new UserMetadataUpdater(_telemetry, _settings, _graphAppIndentityOAuthContext.Creds, httpClient);
                await userUpdater.InsertAndUpdateDatabaseUsersFromGraph();

                // Track finished event 
                userMetadaTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
            }
            else
                _telemetry.LogInformation("Skipping user metadata import");


            using (var db = new AnalyticsEntitiesContext())
            {
                // Process Teams data
                if (settings.ImportJobSettings.GraphUserApps)
                {
                    var userAppsTimer = new JobTimer(_telemetry, "User Teams apps refresh");
                    userAppsTimer.Start();
                    var userAppsLogUpdater = new UserAppLogUpdater(_telemetry, _settings);

                    await userAppsLogUpdater.UpdateUserInstalledApps(_graphClient);

                    // Track finished event 
                    userAppsTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                }
                else
                    _telemetry.LogInformation("Skipping user Teams apps import");


                if (settings.ImportJobSettings.GraphUsageReports)
                {
                    var usageActivityTimer = new JobTimer(_telemetry, "Usage reports");
                    usageActivityTimer.Start();

                    // Global user activity report. Each thread creates own context.
                    var imported = await GetAndSaveActivityReportsMultiThreaded(settings.DaysBeforeNowToDownload, httpClient);

                    // Track finished event 
                    if (imported)
                    {
                        usageActivityTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                    }
                }
                else
                    _telemetry.LogInformation("Skipping usage reports import");

                if (settings.ImportJobSettings.GraphTeams)
                {
                    var teamsTimer = new JobTimer(_telemetry, "Teams import");
                    teamsTimer.Start();

                    var teamsImporter = new TeamsImporter(_telemetry, _settings, _graphClient);

                    var teamsConfig = await TeamsCrawlConfig.LoadFromDb(db);
                    await teamsImporter.RefreshAndSaveAllTeamsData(teamsConfig);

                    // Track finished event 
                    teamsTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                }
                else
                    _telemetry.LogInformation("Skipping Teams import");

            }
        }


        public async Task<bool> GetAndSaveActivityReportsMultiThreaded(int daysBackMax, ManualGraphCallClient client)
        {
            await InitAuth();
            var MIN_WAIT = TimeSpan.FromDays(1);

            var lastImportedDateLoader = new UserActivityLastImportedRedisSingleDateLoader(_settings.ConnectionStrings.RedisConnectionString);

            // Clear "last imported" date in redis if no data in DB
            using (var db = new AnalyticsEntitiesContext())
            {
                var teamsActivityCountAll = await db.TeamUserActivityLogs.CountAsync();
                if (teamsActivityCountAll == 0)
                {
                    await lastImportedDateLoader.DeleteDt();
                }
            }

            var lastImportedDate = await lastImportedDateLoader.GetLastDT();
            var runImport = (lastImportedDate == null || DateTime.Now.Subtract(lastImportedDate.Value) > MIN_WAIT);
#if DEBUG
            runImport = true;
#endif
            if (runImport)
            {
                _telemetry.LogInformation($"\nReading all activity reports from {daysBackMax} days back...");

                // Parallel-load all, each one with own DB context
                var importTasks = new List<Task>();

                var lookupIdCache = new ConcurrentLookupDbIdsCache();

                // Daily imports
                var teamsUserUsageLoader = new TeamsUserUsageLoader(client, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(teamsUserUsageLoader, daysBackMax, "Teams user activity", _telemetry, lookupIdCache));

                var teamsUserDeviceLoader = new TeamsUserDeviceLoader(client, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(teamsUserDeviceLoader, daysBackMax, "Teams user device", _telemetry, lookupIdCache));

                var outlookLoader = new OutlookUserActivityLoader(client, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(outlookLoader, daysBackMax, "Outlook activity", _telemetry, lookupIdCache));

                var oneDriveUsageLoader = new OneDriveUsageLoader(client, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(oneDriveUsageLoader, daysBackMax, "OneDrive usage", _telemetry, lookupIdCache));

                var oneDriveUserActivityLoader = new OneDriveUserActivityLoader(client, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(oneDriveUserActivityLoader, daysBackMax, "OneDrive activity", _telemetry, lookupIdCache));

                var sharePointUserActivityLoader = new SharePointUserActivityLoader(client, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(sharePointUserActivityLoader, daysBackMax, "SharePoint user activity", _telemetry, lookupIdCache));

                var yammerUserActivityLoader = new YammerUserUsageLoader(client, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(yammerUserActivityLoader, daysBackMax, "Yammer user activity", _telemetry, lookupIdCache));

                var yammerGroupsActivityLoader = new YammerGroupUsageLoader(client, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(yammerGroupsActivityLoader, daysBackMax, "Yammer group activity", _telemetry, lookupIdCache));

                var yammerDeviceActivityLoader = new YammerDeviceUsageLoader(client, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(yammerDeviceActivityLoader, daysBackMax, "Yammer device activity", _telemetry, lookupIdCache));

                var userPlatActivityLoader = new AppPlatformUserActivityLoader(client, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(userPlatActivityLoader, daysBackMax, "Apps & platform activity", _telemetry, lookupIdCache));

                // Weekly imports
                using (var db = new AnalyticsEntitiesContext())
                {
                    var sharePointSitesWeeklyUsageReportLoader = new SharePointSitesWeeklyUsageReportLoader(db, client, _telemetry, new GraphSPSiteIdToUrlCache(_graphClient, db, _telemetry));

                    importTasks.Add(sharePointSitesWeeklyUsageReportLoader.LoadAndSaveLastWeeksReportsIfRefreshOnDay(System.DayOfWeek.Sunday));
                    await Task.WhenAll(importTasks);
                }

                // Check for anonimised data
                var allTeamsData = teamsUserUsageLoader.LoadedReportPages.SelectMany(r => r.Value).ToList();
                if (allTeamsData.Count > 0)
                {
                    if (!StringUtils.IsEmail(allTeamsData[0].UserPrincipalName))
                    {
                        _telemetry.LogInformation($"\nWARNING: Usage reports have associated user email concealed - we won't be able to link any activity back to users. See Office 365 Advanced Analytics Engine prerequisites.\n");
                    }
                }

                // Remember last import date
                await lastImportedDateLoader.SaveDT();
                _telemetry.LogInformation($"Activity reports imported. Will run again in {MIN_WAIT.TotalHours} hours");
                return true;
            }
            else
            {
                _telemetry.LogInformation($"Skipping activity reports as have processed recently (less than {MIN_WAIT.TotalHours} hours ago). " +
                    $"Will import again after {lastImportedDate.Value.Add(MIN_WAIT)}.");
                return false;
            }
        }

        async Task<int> LoadAndSaveDailyImportReport<TReportDbType, TUserActivityUserDetail, TLookupType, CACHETYPE>
            (AbstractDailyActivityLoader<TReportDbType, TUserActivityUserDetail, TLookupType, CACHETYPE> abstractActivityLoader,
            int daysBackMax, string thingWeAreImporting, ILogger telemetry, ConcurrentLookupDbIdsCache userEmailToDbIdCache)
            where TReportDbType : AbstractUsageActivityLog, new()
            where TUserActivityUserDetail : AbstractActivityRecord<TLookupType>
            where TLookupType : AbstractEFEntity
            where CACHETYPE : DBLookupCache<TLookupType>
        {
            telemetry.LogInformation($"Importing {thingWeAreImporting} reports...");
            await abstractActivityLoader.PopulateLoadedReportPagesFromGraph(daysBackMax);

            using (var db = new AnalyticsEntitiesContext())
            {
                _telemetry.LogInformation($"{this.GetType().Name} read {abstractActivityLoader.LoadedReportPages.SelectMany(p => p.Value).Count().ToString("N0")} {thingWeAreImporting} records from Graph API");
                await abstractActivityLoader.SaveLoadedReportsToSql(userEmailToDbIdCache, DBLookupCache<TLookupType>.Create<CACHETYPE>(db));
            }

            var total = abstractActivityLoader.LoadedReportPages.SelectMany(r => r.Value).Count();
            telemetry.LogInformation($"Imported {total.ToString("N0")} {thingWeAreImporting} reports.");

            return total;
        }
    }
}
