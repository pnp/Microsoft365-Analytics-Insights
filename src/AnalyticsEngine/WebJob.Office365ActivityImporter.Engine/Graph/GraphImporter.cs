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
        // Two independent markers: one proves the usage-report phase completed (so finalized dates can be
        // skipped safely), the other gates how often the non-fresh Graph sections re-run.
        private readonly ISingleDateStore _activityReportsLastImportedStore;
        private readonly IImportLastRunStore _lastRunStore;

        public GraphImporter(AnalyticsLogger logger, UserGroupsCache userGroupsCache, GraphAppIndentityOAuthContext graphAppIndentityOAuthContext, GraphServiceClient graphClient, AppConfig settings, ISingleDateStore activityReportsLastImportedStore = null, IImportLastRunStore lastRunStore = null)
            : base(logger, settings)
        {
            _userGroupsCache = userGroupsCache;
            _graphAppIndentityOAuthContext = graphAppIndentityOAuthContext;
            _graphClient = graphClient;
            _activityReportsLastImportedStore = activityReportsLastImportedStore;

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
                _logger.LogInformation($"Skipping {sectionName}: ran recently ({lastRun:u} UTC). " +
                    $"Next run after {lastRun?.AddHours(intervalHours):u} UTC (interval {intervalHours}h). " +
                    $"Set ForceGraphMetadataImport=true or clear the '{key}' cache key to override.");
                return;
            }

            if (force)
            {
                _logger.LogInformation($"ForceGraphMetadataImport=true; bypassing the cadence gate for {sectionName}.");
            }

            var timer = new JobTimer(_logger, sectionName);
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
            var httpClient = new ManualGraphCallClient(_graphAppIndentityOAuthContext, _logger);
            var userGroupsFilter = new UserGroupsFilterModel(_settings.UserGroupsFilter);

            var graphUserGroupsCache = new GraphUserGroupsCache(httpClient, _logger);

            if (settings.ImportJobSettings.GraphUsersMetadata)
            {
                await RunGraphSectionIfDueAsync(GraphUsersMetadataLastImportedKey, _settings.GraphMetadataImportIntervalHours, "User metadata refresh", async () =>
                {
                    // Update Graph users first
                    var userUpdater = new UserMetadataUpdater(_logger, _settings, _graphAppIndentityOAuthContext.Creds, httpClient);
                    await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();
                });
            }
            else
                _logger.LogInformation("Skipping user metadata import", graphUserGroupsCache);


            using (var db = new AnalyticsEntitiesContext())
            {
                // Process Teams data
                if (settings.ImportJobSettings.GraphUserApps)
                {
                    await RunGraphSectionIfDueAsync(GraphUserAppsLastImportedKey, _settings.GraphMetadataImportIntervalHours, "User Teams apps refresh", async () =>
                    {
                        var userAppsLogUpdater = new UserAppLogUpdater(_logger, _settings);
                        await userAppsLogUpdater.UpdateUserInstalledApps(_graphClient, graphUserGroupsCache, userGroupsFilter);
                    });
                }
                else
                    _logger.LogInformation("Skipping user Teams apps import", graphUserGroupsCache);


                if (settings.ImportJobSettings.GraphUsageReports)
                {
                    var usageActivityTimer = new JobTimer(_logger, "Usage reports");
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
                    _logger.LogInformation("Skipping usage reports import", graphUserGroupsCache);

                if (settings.ImportJobSettings.GraphTeams)
                {
                    await RunGraphSectionIfDueAsync(GraphTeamsLastImportedKey, _settings.GraphTeamsImportIntervalHours, "Teams import", async () =>
                    {
                        var teamsImporter = new TeamsImporter(_logger, _settings, _graphClient);

                        var teamsConfig = await TeamsCrawlConfig.LoadFromDb(db);
                        await teamsImporter.RefreshAndSaveAllTeamsData(teamsConfig);
                    });
                }
                else
                    _logger.LogInformation("Skipping Teams import", graphUserGroupsCache);

                if (settings.ImportJobSettings.SentEmails)
                {
                    var sentEmailsTimer = new JobTimer(_logger, "Sent emails import");
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

                    var sentEmailImporter = new SentEmailImporter(_logger, _settings, httpClient, deltaTokenStore, _graphAppIndentityOAuthContext);
                    await sentEmailImporter.ImportSentEmails();

                    sentEmailsTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                }
                else
                    _logger.LogInformation("Skipping sent emails import", graphUserGroupsCache);

            }
        }


        public async Task<bool> GetAndSaveActivityReportsMultiThreaded(int daysBackMax, ManualGraphCallClient client, UserGroupsCache userGroupsCache, UserGroupsFilterModel userGroupsFilterModel)
        {
            var MIN_WAIT = TimeSpan.FromDays(1);

            // Throttle the whole activity/usage-report phase (all daily loaders + the weekly SharePoint sites
            // loader) to run at most once a day. The store is injected so it survives across import cycles:
            // Redis when configured, otherwise an in-memory fallback (see ActivityReportsLastImportedStoreFactory).
            // When no store is supplied (e.g. unit tests) we don't throttle and always import.
            DateTime? lastImportedDate = null;
            var lastImportedStore = _activityReportsLastImportedStore;
            if (lastImportedStore != null)
            {
                // Clear the "last imported" date if there's no activity data in the DB at all, so a fresh or
                // wiped database imports immediately instead of waiting out a stale timestamp. EXISTS (AnyAsync)
                // is used rather than COUNT(*) because this only needs "is the table empty" and runs every cycle.
                using (var db = new AnalyticsEntitiesContext())
                {
                    var anyActivityData = await db.TeamUserActivityLogs.AnyAsync();
                    if (!anyActivityData)
                    {
                        await lastImportedStore.DeleteDt();
                    }
                }

                lastImportedDate = await lastImportedStore.GetLastDT();
            }
            else
            {
                _logger.LogWarning("No activity-reports throttle store configured - cannot check last import date; will import this cycle.");
            }

            var runImport = (lastImportedDate == null || DateTime.Now.Subtract(lastImportedDate.Value) > MIN_WAIT);
            if (_settings.ForceUsageReportsImport)
            {
                _logger.LogInformation("ForceUsageReportsImport=true; bypassing recently-imported gate.");
                runImport = true;
            }
            if (runImport)
            {
                // The timestamp is both the once-a-day throttle and the proof that every loader
                // completed. Clear it before any batched writes: if this phase fails after a
                // partial save, the next cycle must re-import rather than trusting the old marker.
                // Keep lastImportedDate locally so rows covered by that prior successful phase can
                // still be skipped safely during this run.
                if (lastImportedStore != null)
                {
                    await lastImportedStore.DeleteDt();
                }

                _logger.LogInformation($"Reading all activity reports from {daysBackMax} days back...");

                // Parallel-load all, each one with own DB context
                var importTasks = new List<Task>();

                var lookupIdCache = new ConcurrentLookupDbIdsCache();

                // Daily imports
                var teamsUserUsageLoader = new TeamsUserUsageLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(LoadAndSaveDailyImportReport(teamsUserUsageLoader, daysBackMax, "Teams user activity", _logger, lookupIdCache, lastImportedDate));

                var teamsUserDeviceLoader = new TeamsUserDeviceLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(LoadAndSaveDailyImportReport(teamsUserDeviceLoader, daysBackMax, "Teams user device", _logger, lookupIdCache, lastImportedDate));

                var outlookLoader = new OutlookUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(LoadAndSaveDailyImportReport(outlookLoader, daysBackMax, "Outlook activity", _logger, lookupIdCache, lastImportedDate));

                var oneDriveUsageLoader = new OneDriveUsageLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(LoadAndSaveDailyImportReport(oneDriveUsageLoader, daysBackMax, "OneDrive usage", _logger, lookupIdCache, lastImportedDate));

                var oneDriveUserActivityLoader = new OneDriveUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(LoadAndSaveDailyImportReport(oneDriveUserActivityLoader, daysBackMax, "OneDrive activity", _logger, lookupIdCache, lastImportedDate));

                var sharePointUserActivityLoader = new SharePointUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(LoadAndSaveDailyImportReport(sharePointUserActivityLoader, daysBackMax, "SharePoint user activity", _logger, lookupIdCache, lastImportedDate));

                var yammerUserActivityLoader = new YammerUserUsageLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(LoadAndSaveDailyImportReport(yammerUserActivityLoader, daysBackMax, "Yammer user activity", _logger, lookupIdCache, lastImportedDate));

                var yammerGroupsActivityLoader = new YammerGroupUsageLoader(client, _logger);
                importTasks.Add(LoadAndSaveDailyImportReport(yammerGroupsActivityLoader, daysBackMax, "Yammer group activity", _logger, lookupIdCache, lastImportedDate));

                var yammerDeviceActivityLoader = new YammerDeviceUsageLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(LoadAndSaveDailyImportReport(yammerDeviceActivityLoader, daysBackMax, "Yammer device activity", _logger, lookupIdCache, lastImportedDate));

                var userPlatActivityLoader = new AppPlatformUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(LoadAndSaveDailyImportReport(userPlatActivityLoader, daysBackMax, "Apps & platform activity", _logger, lookupIdCache, lastImportedDate));

                // Weekly imports
                using (var db = new AnalyticsEntitiesContext())
                {
                    var sharePointSitesWeeklyUsageReportLoader = new SharePointSitesWeeklyUsageReportLoader(db, client, _logger, new GraphSPSiteIdToUrlCache(_graphClient, db, _logger));

                    importTasks.Add(sharePointSitesWeeklyUsageReportLoader.LoadAndSaveLastWeeksReportsIfRefreshOnDay(System.DayOfWeek.Sunday));
                    await Task.WhenAll(importTasks);
                }

                // Check for anonimised data
                var allTeamsData = teamsUserUsageLoader.LoadedReportPages.SelectMany(r => r.Value).ToList();
                if (allTeamsData.Count > 0)
                {
                    if (!StringUtils.IsEmail(allTeamsData[0].UserPrincipalName))
                    {
                        _logger.LogError($"IMPORTANT: Usage reports have associated user email concealed - we won't be able to link any activity back to users. See Office 365 Advanced Analytics Engine prerequisites.\n");
                    }
                }

                // Remember last import date so the next cycle within the throttle window is skipped.
                if (lastImportedStore != null)
                {
                    await lastImportedStore.SaveDT();
                }

                _logger.LogInformation($"Activity reports imported. Will run again in {MIN_WAIT.TotalHours} hours");
                return true;
            }
            else
            {
                _logger.LogInformation($"Skipping activity reports as have processed recently (less than {MIN_WAIT.TotalHours} hours ago). " +
                    $"Will import again after {lastImportedDate.Value.Add(MIN_WAIT)}.");
                return false;
            }
        }

        async Task<int> LoadAndSaveDailyImportReport<TReportDbType, TUserActivityUserDetail, TLookupType, CACHETYPE>
            (AbstractDailyActivityLoader<TReportDbType, TUserActivityUserDetail, TLookupType, CACHETYPE> abstractActivityLoader,
            int daysBackMax, string thingWeAreImporting, ILogger logger, ConcurrentLookupDbIdsCache userEmailToDbIdCache,
            DateTime? lastSuccessfulImport)
            where TReportDbType : AbstractUsageActivityLog, new()
            where TUserActivityUserDetail : AbstractActivityRecord<TLookupType>
            where TLookupType : AbstractEFEntity
            where CACHETYPE : DBLookupCache<TLookupType>
        {
            logger.LogInformation($"Importing {thingWeAreImporting} reports...");

            // Graph usage data is stable once finalized (~2-3 day latency), so days we already hold and that can no
            // longer change don't need re-downloading or re-writing. Skip them unless a forced full re-import is set.
            ISet<DateTime> datesToSkip = null;
            if (!_settings.ForceUsageReportsImport)
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    datesToSkip = await abstractActivityLoader.GetFinalizedStoredDatesToSkipAsync(
                        db,
                        daysBackMax,
                        lastSuccessfulImport);
                }
                if (datesToSkip.Count > 0)
                {
                    logger.LogInformation($"{thingWeAreImporting}: skipping {datesToSkip.Count} already-stored finalized day(s); " +
                        $"only re-importing the most recent {abstractActivityLoader.RefreshableRecentDays} day(s) that can still change in Graph.");
                }
            }

            await abstractActivityLoader.PopulateLoadedReportPagesFromGraph(daysBackMax, datesToSkip);

            using (var db = new AnalyticsEntitiesContext())
            {
                _logger.LogInformation($"{this.GetType().Name} read {abstractActivityLoader.LoadedReportPages.SelectMany(p => p.Value).Count().ToString("N0")} {thingWeAreImporting} records from Graph API");
                await abstractActivityLoader.SaveLoadedReportsToSql(userEmailToDbIdCache, DBLookupCache<TLookupType>.Create<CACHETYPE>(db));
            }

            var total = abstractActivityLoader.LoadedReportPages.SelectMany(r => r.Value).Count();
            logger.LogInformation($"Imported {total.ToString("N0")} {thingWeAreImporting} reports.");

            return total;
        }
    }
}
