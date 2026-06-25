using Common.Entities;
using Common.Entities.ActivityReports;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Reads and saves all data read from Graph
    /// </summary>
    public class GraphImporter : AbstractApiLoader
    {
        private readonly UserGroupsCache _userGroupsCache;
        private readonly GraphAppIndentityOAuthContext _graphAppIndentityOAuthContext;
        private readonly GraphServiceClient _graphClient;
        private readonly IImportLastRunStore _lastRunStore;

        public GraphImporter(AnalyticsLogger telemetry, UserGroupsCache userGroupsCache, GraphAppIndentityOAuthContext graphAppIndentityOAuthContext, GraphServiceClient graphClient, AppConfig settings, IImportLastRunStore lastRunStore)
            : base(telemetry, settings)
        {
            _userGroupsCache = userGroupsCache;
            _graphAppIndentityOAuthContext = graphAppIndentityOAuthContext;
            _graphClient = graphClient;

            // Defensive: a per-instance in-memory store still works (just doesn't persist the gate
            // across cycles). Production passes the process-lifetime store hoisted in Program.cs.
            _lastRunStore = lastRunStore ?? new InMemoryImportLastRunStore();
        }

        // Keys for the per-section "last run" timestamps used to daily-gate the non-fresh Graph imports.
        // Stored verbatim (unprefixed) in Redis db 0, so they can be cleared manually with e.g.
        // `redis-cli DEL GraphUsersMetadataLastImported`.
        private const string GraphUsersMetadataLastImportedKey = "GraphUsersMetadataLastImported";
        private const string GraphUserAppsLastImportedKey = "GraphUserAppsLastImported";
        private const string GraphTeamsLastImportedKey = "GraphTeamsLastImported";

        /// <summary>
        /// Runs a "non-fresh" Graph import section at most once per <paramref name="intervalHours"/>.
        /// The last-run timestamp is persisted via <see cref="IImportLastRunStore"/> (Redis when
        /// configured, otherwise in-memory) so the gate survives the per-cycle recreation of this
        /// importer. An interval of 0 disables the gate (runs every cycle); <c>ForceGraphMetadataImport</c>
        /// bypasses it for one run. Redis failures are fail-open (the section still runs).
        /// </summary>
        private async Task RunGraphSectionIfDueAsync(string key, int intervalHours, string sectionName, Func<Task> sectionWork)
        {
            var force = _settings.ForceGraphMetadataImport;
            var lastRun = await _lastRunStore.GetLastRunUtc(key);

            if (!ImportCadenceGate.ShouldRun(lastRun, intervalHours, force, DateTime.UtcNow))
            {
                _telemetry.LogInformation($"Skipping {sectionName}: ran recently ({lastRun:u} UTC). " +
                    $"Next run after {lastRun?.AddHours(intervalHours):u} UTC (interval {intervalHours}h). " +
                    $"Set ForceGraphMetadataImport=true or clear the '{key}' cache key to override.");
                return;
            }

            if (force)
            {
                _telemetry.LogInformation($"ForceGraphMetadataImport=true; bypassing the cadence gate for {sectionName}.");
            }

            var timer = new JobTimer(_telemetry, sectionName);
            timer.Start();

            await sectionWork();

            timer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);

            // Record the run only after success (so a failure doesn't suppress the next attempt) and only
            // when gating is active (interval > 0).
            if (intervalHours > 0)
            {
                await _lastRunStore.SetLastRunUtc(key, DateTime.UtcNow);
            }
        }


        /// <summary>
        /// Main entry-point
        /// </summary>
        public async Task GetAndSaveAllGraphData(AppConfig settings)
        {
            var httpClient = new ManualGraphCallClient(_graphAppIndentityOAuthContext, _telemetry);
            var userGroupsFilter = new UserGroupsFilterModel(_settings.UserGroupsFilter);

            var graphUserGroupsCache = new GraphUserGroupsCache(httpClient, _telemetry);

            if (settings.ImportJobSettings.GraphUsersMetadata)
            {
                await RunGraphSectionIfDueAsync(GraphUsersMetadataLastImportedKey, _settings.GraphMetadataImportIntervalHours, "User metadata refresh", async () =>
                {
                    // Update Graph users first
                    var userUpdater = new UserMetadataUpdater(_telemetry, _settings, _graphAppIndentityOAuthContext.Creds, httpClient);
                    await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();
                });
            }
            else
                _telemetry.LogInformation("Skipping user metadata import", graphUserGroupsCache);


            using (var db = new AnalyticsEntitiesContext())
            {
                // Process Teams data
                if (settings.ImportJobSettings.GraphUserApps)
                {
                    await RunGraphSectionIfDueAsync(GraphUserAppsLastImportedKey, _settings.GraphMetadataImportIntervalHours, "User Teams apps refresh", async () =>
                    {
                        var userAppsLogUpdater = new UserAppLogUpdater(_telemetry, _settings);
                        await userAppsLogUpdater.UpdateUserInstalledApps(_graphClient, graphUserGroupsCache, userGroupsFilter);
                    });
                }
                else
                    _telemetry.LogInformation("Skipping user Teams apps import", graphUserGroupsCache);


                if (settings.ImportJobSettings.GraphUsageReports)
                {
                    var usageActivityTimer = new JobTimer(_telemetry, "Usage reports");
                    usageActivityTimer.Start();

                    // Global user activity report. Each thread creates own context.
                    var imported = await GetAndSaveActivityReportsMultiThreaded(settings.DaysBeforeNowToDownload, httpClient, graphUserGroupsCache, userGroupsFilter);

                    // Track finished event 
                    if (imported)
                    {
                        usageActivityTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                    }
                }
                else
                    _telemetry.LogInformation("Skipping usage reports import", graphUserGroupsCache);

                if (settings.ImportJobSettings.GraphTeams)
                {
                    await RunGraphSectionIfDueAsync(GraphTeamsLastImportedKey, _settings.GraphTeamsImportIntervalHours, "Teams import", async () =>
                    {
                        var teamsImporter = new TeamsImporter(_telemetry, _settings, _graphClient);

                        var teamsConfig = await TeamsCrawlConfig.LoadFromDb(db);
                        await teamsImporter.RefreshAndSaveAllTeamsData(teamsConfig);
                    });
                }
                else
                    _telemetry.LogInformation("Skipping Teams import", graphUserGroupsCache);

                if (settings.ImportJobSettings.SentEmails)
                {
                    var sentEmailsTimer = new JobTimer(_telemetry, "Sent emails import");
                    sentEmailsTimer.Start();

                    IDeltaTokenStore deltaTokenStore;
                    if (!string.IsNullOrEmpty(_settings.ConnectionStrings.RedisConnectionString))
                    {
                        deltaTokenStore = new RedisDeltaTokenStore(_settings.ConnectionStrings.RedisConnectionString, tenantId: _settings.TenantGUID.ToString(), clientId: _settings.ClientID, clientSecret: _settings.ClientSecret);
                    }
                    else
                    {
                        deltaTokenStore = new InMemoryDeltaTokenStore();
                    }

                    var sentEmailImporter = new SentEmailImporter(_telemetry, _settings, httpClient, deltaTokenStore, _graphAppIndentityOAuthContext);
                    await sentEmailImporter.ImportSentEmails();

                    sentEmailsTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                }
                else
                    _telemetry.LogInformation("Skipping sent emails import", graphUserGroupsCache);

            }
        }


        public async Task<bool> GetAndSaveActivityReportsMultiThreaded(int daysBackMax, ManualGraphCallClient client, UserGroupsCache userGroupsCache, UserGroupsFilterModel userGroupsFilterModel)
        {
            var MIN_WAIT = TimeSpan.FromDays(1);

            DateTime? lastImportedDate = null;
            UserActivityLastImportedRedisSingleDateLoader lastImportedDateLoader = null;
            if (!string.IsNullOrEmpty(_settings.ConnectionStrings.RedisConnectionString))
            {
                lastImportedDateLoader = new UserActivityLastImportedRedisSingleDateLoader(_settings.ConnectionStrings.RedisConnectionString, _settings.TenantGUID.ToString(), _settings.ClientID, _settings.ClientSecret);

                // Clear "last imported" date in redis if no data in DB
                using (var db = new AnalyticsEntitiesContext())
                {
                    var teamsActivityCountAll = await db.TeamUserActivityLogs.CountAsync();
                    if (teamsActivityCountAll == 0)
                    {
                        await lastImportedDateLoader.DeleteDt();
                    }
                }

                lastImportedDate = await lastImportedDateLoader.GetLastDT();
            }
            else
            {
                _telemetry.LogWarning("No Redis connection string - cannot find last date for imported for activity reports.");
            }

            var runImport = (lastImportedDate == null || DateTime.Now.Subtract(lastImportedDate.Value) > MIN_WAIT);
            if (_settings.ForceUsageReportsImport)
            {
                _telemetry.LogInformation("ForceUsageReportsImport=true; bypassing recently-imported gate.");
                runImport = true;
            }
            if (runImport)
            {
                _telemetry.LogInformation($"Reading all activity reports from {daysBackMax} days back...");

                // Parallel-load all, each one with own DB context
                var importTasks = new List<Task>();

                var lookupIdCache = new ConcurrentLookupDbIdsCache();

                // Daily imports
                var teamsUserUsageLoader = new TeamsUserUsageLoader(client, userGroupsCache, userGroupsFilterModel, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(teamsUserUsageLoader, daysBackMax, "Teams user activity", _telemetry, lookupIdCache));

                var teamsUserDeviceLoader = new TeamsUserDeviceLoader(client, userGroupsCache, userGroupsFilterModel, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(teamsUserDeviceLoader, daysBackMax, "Teams user device", _telemetry, lookupIdCache));

                var outlookLoader = new OutlookUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(outlookLoader, daysBackMax, "Outlook activity", _telemetry, lookupIdCache));

                var oneDriveUsageLoader = new OneDriveUsageLoader(client, userGroupsCache, userGroupsFilterModel, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(oneDriveUsageLoader, daysBackMax, "OneDrive usage", _telemetry, lookupIdCache));

                var oneDriveUserActivityLoader = new OneDriveUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(oneDriveUserActivityLoader, daysBackMax, "OneDrive activity", _telemetry, lookupIdCache));

                var sharePointUserActivityLoader = new SharePointUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(sharePointUserActivityLoader, daysBackMax, "SharePoint user activity", _telemetry, lookupIdCache));

                var yammerUserActivityLoader = new YammerUserUsageLoader(client, userGroupsCache, userGroupsFilterModel, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(yammerUserActivityLoader, daysBackMax, "Yammer user activity", _telemetry, lookupIdCache));

                var yammerGroupsActivityLoader = new YammerGroupUsageLoader(client, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(yammerGroupsActivityLoader, daysBackMax, "Yammer group activity", _telemetry, lookupIdCache));

                var yammerDeviceActivityLoader = new YammerDeviceUsageLoader(client, userGroupsCache, userGroupsFilterModel, _telemetry);
                importTasks.Add(LoadAndSaveDailyImportReport(yammerDeviceActivityLoader, daysBackMax, "Yammer device activity", _telemetry, lookupIdCache));

                var userPlatActivityLoader = new AppPlatformUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _telemetry);
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
                        _telemetry.LogError($"IMPORTANT: Usage reports have associated user email concealed - we won't be able to link any activity back to users. See Office 365 Advanced Analytics Engine prerequisites.\n");
                    }
                }

                // Remember last import date
                if (lastImportedDateLoader != null)
                {
                    await lastImportedDateLoader.SaveDT();
                }

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
