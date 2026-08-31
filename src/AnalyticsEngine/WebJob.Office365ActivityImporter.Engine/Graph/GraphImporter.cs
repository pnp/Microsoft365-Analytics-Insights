using Common.Entities;
using Common.Entities.ActivityReports;
using Common.Entities.Config;
using Common.Entities.Entities.UsageReports;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;
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
        // Negative cache of users with no Exchange mailbox, so the sent-email import stops re-checking
        // them (and logging a 404) on every cycle. Must be process-lifetime, hence injected.
        private readonly ISentEmailMailboxSkipList _sentEmailMailboxSkipList;

        public GraphImporter(AnalyticsLogger logger, UserGroupsCache userGroupsCache, GraphAppIndentityOAuthContext graphAppIndentityOAuthContext, GraphServiceClient graphClient, AppConfig settings, ISingleDateStore activityReportsLastImportedStore = null, IImportLastRunStore lastRunStore = null, ISentEmailMailboxSkipList sentEmailMailboxSkipList = null)
            : base(logger, settings)
        {
            _userGroupsCache = userGroupsCache;
            _graphAppIndentityOAuthContext = graphAppIndentityOAuthContext;
            _graphClient = graphClient;
            _activityReportsLastImportedStore = activityReportsLastImportedStore;

            // Defensive: a per-instance in-memory store still works (just doesn't persist the gate
            // across cycles). Production passes the process-lifetime store hoisted in Program.cs.
            _lastRunStore = lastRunStore ?? new InMemoryImportLastRunStore();
            _sentEmailMailboxSkipList = sentEmailMailboxSkipList ?? new InMemorySentEmailMailboxSkipList();
        }

        // Keys for the per-section "last run" timestamps used to daily-gate the non-fresh Graph imports.
        // Stored verbatim (unprefixed) in Redis db 0, so they can be cleared manually with e.g.
        // `redis-cli DEL GraphUsersMetadataLastImported`.
        private const string GraphUsersMetadataLastImportedKey = "GraphUsersMetadataLastImported";
        private const string GraphTeamsLastImportedKey = "GraphTeamsLastImported";
        private const string GraphCopilotUsageReportsLastImportedKey = "GraphCopilotUsageReportsLastImported";
        private const string CopilotInteractionHistoryLastImportedKey = "CopilotInteractionHistoryLastImported";

        /// <summary>
        /// Runs a "non-fresh" Graph import section at most once per <paramref name="intervalHours"/>.
        /// The last-run timestamp is persisted via <see cref="IImportLastRunStore"/> (Redis when
        /// configured, otherwise in-memory) so the gate survives the per-cycle recreation of this
        /// importer. An interval of 0 disables the gate (runs every cycle); <c>ForceGraphMetadataImport</c>
        /// bypasses it for one run. Redis failures are fail-open (the section still runs).
        /// </summary>
        private Task RunGraphSectionIfDueAsync(string key, int intervalHours, string sectionName, Func<Task> sectionWork)
        {
            return RunGraphSectionIfDueAsync(key, intervalHours, sectionName, async () =>
            {
                await sectionWork();
                return true;
            });
        }

        /// <summary>
        /// As above, for a section that reports success itself instead of throwing. Returning false records
        /// the section as not done, so the cadence gate lets it retry next cycle, without an exception
        /// unwinding out of <see cref="GetAndSaveAllGraphData"/> and skipping the sections that come after it.
        /// </summary>
        private async Task RunGraphSectionIfDueAsync(string key, int intervalHours, string sectionName, Func<Task<bool>> sectionWork)
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

            var succeeded = await sectionWork();

            // Only claim the section finished when it actually did; otherwise a permanently failing section
            // would emit a healthy "finished" event on every cycle.
            if (succeeded)
            {
                timer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
            }
            else
            {
                _logger.LogWarning($"{sectionName} did not complete successfully; it will be retried on the next cycle.");
            }

            // Record the run only after success (so a failure doesn't suppress the next attempt) and only
            // when gating is active (interval > 0).
            if (intervalHours > 0 && succeeded)
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

                if (settings.ImportJobSettings.GraphCopilotUsageReports)
                {
                    // Refreshed daily by default. Microsoft publishes these reports roughly 48 hours behind,
                    // so polling more often costs a full re-download and re-process of every licensed user
                    // and returns the same numbers. This uses its own interval rather than the shared
                    // non-fresh Graph one, whose High-preset default is "every cycle".
                    await RunGraphSectionIfDueAsync(GraphCopilotUsageReportsLastImportedKey, _settings.GraphCopilotUsageReportsIntervalHours, "Copilot usage reports",
                        () => ImportCopilotUsageReports(httpClient, graphUserGroupsCache, userGroupsFilter));
                }
                else
                    _logger.LogInformation("Skipping Graph Copilot usage reports import", graphUserGroupsCache);

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

                    var sentEmailImporter = new SentEmailImporter(_logger, _settings, httpClient, deltaTokenStore, _graphAppIndentityOAuthContext, _sentEmailMailboxSkipList);
                    await sentEmailImporter.ImportSentEmails();

                    sentEmailsTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                }
                else
                    _logger.LogInformation("Skipping sent emails import", graphUserGroupsCache);

                if (settings.ImportJobSettings.CopilotInteractionHistory)
                {
                    // Cadence-gated like the other non-fresh Graph sections, but for a different reason: this
                    // one costs a Graph call per in-scope user, so running it every cycle would be expensive
                    // even for a modest pilot group. Defaults to daily.
                    //
                    // Uses the bool overload deliberately. ImportAsync returns null when it declined to run
                    // (no UserGroupsFilter, or the app registration has no AiEnterpriseInteraction.Read.All
                    // consent) and sets Error on the run log when it caught one. Reporting those as success
                    // would stamp the daily gate on a cycle that imported nothing, so enabling the feature
                    // before admin consent is granted would silently do nothing for another 24 hours.
                    await RunGraphSectionIfDueAsync(CopilotInteractionHistoryLastImportedKey, _settings.CopilotInteractionHistoryIntervalHours, "Copilot interaction history import", async () =>
                    {
                        var interactionImporter = new CopilotInteractionHistoryImporter(
                            _logger,
                            _settings,
                            new GraphAiInteractionSourceLoader(httpClient, _graphAppIndentityOAuthContext, _logger),
                            InteractionCognitiveEnricherFactory.Create(_settings, _logger),
                            new GraphPilotGroupMemberResolver(httpClient, _logger),
                            userGroupsFilter);

                        var interactionLog = await interactionImporter.ImportAsync();
                        return interactionLog != null && string.IsNullOrEmpty(interactionLog.Error);
                    });
                }
                else
                    _logger.LogInformation("Skipping Copilot interaction history import", graphUserGroupsCache);

            }
        }


        /// <summary>
        /// Imports the three Graph Microsoft 365 Copilot usage reports. Returns true only if all three
        /// succeeded, so the cadence gate retries next cycle otherwise.
        ///
        /// Order is deliberate: the two tenant-aggregate reports go first because they are cheap (a few
        /// thousand rows whatever the tenant size), need no per-user joins, and are unaffected by the tenant's
        /// concealed-user-information setting - so even where the per-user report is unusable, the customer
        /// still gets adoption numbers that line up with the Microsoft 365 admin centre.
        ///
        /// Each report is attempted independently, and a failure is logged rather than thrown: a missing
        /// Reports.Read.All grant or a non-global-cloud tenant (where these endpoints simply don't exist)
        /// must not take down the Teams and sent-email imports that run after this section. Each report also
        /// gets its own DbContext so a failed SaveChanges can't poison the next one.
        /// </summary>
        private async Task<bool> ImportCopilotUsageReports(ManualGraphCallClient httpClient, UserGroupsCache userGroupsCache, UserGroupsFilterModel userGroupsFilterModel)
        {
            // Same client, throttling and paging as every other Graph usage report in this solution.
            var reportSource = new GraphCopilotReportSource(httpClient, _logger);

            // First run gets the widest window Graph offers. This is history we cannot get any other way:
            // the audit pipeline has a hard 7-day retrieval ceiling, so without this backfill a new
            // install starts with an empty Copilot adoption trend.
            //
            // The decision is based on whether a D180 TREND import has ever completed - not on whether any
            // Copilot row exists. Keying it off "any row" meant a successful summary import alongside a
            // failed D180 trend permanently downgraded every later run to D28, silently losing the
            // backfill for good.
            var backfillDone = await HasCompletedTrendBackfill();
            var trendPeriod = backfillDone ? CopilotReportRequest.DefaultRefreshPeriod : CopilotReportRequest.MaxHistoryPeriod;
            if (!backfillDone)
            {
                _logger.LogInformation($"No completed Copilot trend backfill on record - requesting the maximum window ({trendPeriod}).");
            }

            var allSucceeded = true;

            allSucceeded &= await RunCopilotReport("Copilot user-count trend", db =>
                new CopilotUserCountReportLoader(reportSource, _logger).LoadAndSaveTrendAsync(db, trendPeriod));

            allSucceeded &= await RunCopilotReport("Copilot user-count summary", db =>
                new CopilotUserCountReportLoader(reportSource, _logger).LoadAndSaveSummaryAsync(db, CopilotReportRequest.DefaultRefreshPeriod));

            allSucceeded &= await RunCopilotReport("Copilot per-user usage detail", db =>
                new CopilotUsageUserDetailLoader(reportSource, _logger, userGroupsCache, userGroupsFilterModel)
                    .LoadAndSaveAsync(db, CopilotReportRequest.DefaultRefreshPeriod));

            return allSucceeded;
        }

        /// <summary>
        /// True when a maximum-window trend import has previously completed without error, which is what
        /// proves the one-off history backfill actually landed. Never throws: this is only a decision about
        /// which window to request, so if the check itself fails the safe answer is "not done" (request the
        /// wide window) rather than unwinding and skipping the Graph sections that follow.
        /// </summary>
        private async Task<bool> HasCompletedTrendBackfill()
        {
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    return await db.CopilotUsageReportImportLogs.AnyAsync(l =>
                        l.ReportName == CopilotUsageReportNames.UserCountTrend
                        && l.ReportPeriod == CopilotReportRequest.MaxHistoryPeriod
                        && l.Error == null
                        && l.RowsRead > 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Couldn't check whether the Copilot trend backfill has run ({ex.Message}); assuming it hasn't and requesting the maximum window.");
                return false;
            }
        }

        private async Task<bool> RunCopilotReport(string reportDescription, Func<AnalyticsEntitiesContext, Task<int>> import)
        {
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var rows = await import(db);
                    _logger.LogInformation($"{reportDescription}: wrote {rows.ToString("N0")} row(s) to SQL.");
                }
                return true;
            }
            catch (GraphHttpException ex)
            {
                // Typed so the log names the actual HTTP status. A 403 here is nearly always a missing or
                // ungranted Reports.Read.All application permission, and saying so beats "an error occurred".
                var advice = ex.StatusCode == System.Net.HttpStatusCode.Forbidden || ex.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Graph refused the call: grant the app registration the Reports.Read.All APPLICATION permission and admin-consent it. "
                    : "Graph returned an error rather than a report. ";

                _logger.LogError(ex, $"{reportDescription} failed with HTTP {(int)ex.StatusCode} ({ex.StatusCode}): {ex.Message} {advice}" +
                    "The report was NOT imported and has not been recorded as up to date; it will be retried on the next cycle. " +
                    "The other Copilot reports and the remaining Graph imports are unaffected.");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{reportDescription} failed: {ex.Message}. " +
                    "Check the app registration has the Reports.Read.All application permission granted, and that this is a global-cloud tenant (these reports don't exist in the US Government or 21Vianet clouds). " +
                    "The other Copilot reports and the remaining Graph imports are unaffected; this one will be retried on the next cycle.");
                return false;
            }
        }

        public async Task<bool> GetAndSaveActivityReportsMultiThreaded(int daysBackMax, ManualGraphCallClient client, UserGroupsCache userGroupsCache, UserGroupsFilterModel userGroupsFilterModel)        {
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

                // Parallel-load all, each one with own DB context.
                //
                // Each report is run through RunReportSafely so ONE failing report cannot discard the work
                // of the others: whatever downloaded successfully is still saved. Failures are collected and
                // decide whether this phase is allowed to stamp itself complete below.
                var importTasks = new List<Task>();
                var failedReports = new System.Collections.Concurrent.ConcurrentBag<string>();

                async Task RunReportSafely(string reportName, Func<Task> work)
                {
                    try
                    {
                        await work();
                    }
                    catch (GraphHttpException ex)
                    {
                        failedReports.Add($"{reportName} (HTTP {(int)ex.StatusCode} {ex.StatusCode})");
                        _logger.LogError(ex, $"{reportName} failed with HTTP {(int)ex.StatusCode} ({ex.StatusCode}): {ex.Message} " +
                            "The other activity reports are unaffected and keep whatever they downloaded; this report saved nothing at all. " +
                            "This phase will NOT be recorded as complete, so it retries on the next cycle. " +
                            "A 401/403 here almost always means the Reports.Read.All application permission is missing or not admin-consented.");
                    }
                    catch (Exception ex)
                    {
                        failedReports.Add($"{reportName} ({ex.GetType().Name})");
                        _logger.LogError(ex, $"{reportName} failed: {ex.Message}. The other activity reports are unaffected; " +
                            "this phase will NOT be recorded as complete, so it retries on the next cycle.");
                    }
                }

                var lookupIdCache = new ConcurrentLookupDbIdsCache();

                // Daily imports
                var teamsUserUsageLoader = new TeamsUserUsageLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(RunReportSafely("Teams user activity", () => LoadAndSaveDailyImportReport(teamsUserUsageLoader, daysBackMax, "Teams user activity", _logger, lookupIdCache, lastImportedDate)));

                var teamsUserDeviceLoader = new TeamsUserDeviceLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(RunReportSafely("Teams user device", () => LoadAndSaveDailyImportReport(teamsUserDeviceLoader, daysBackMax, "Teams user device", _logger, lookupIdCache, lastImportedDate)));

                var outlookLoader = new OutlookUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(RunReportSafely("Outlook activity", () => LoadAndSaveDailyImportReport(outlookLoader, daysBackMax, "Outlook activity", _logger, lookupIdCache, lastImportedDate)));

                var oneDriveUsageLoader = new OneDriveUsageLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(RunReportSafely("OneDrive usage", () => LoadAndSaveDailyImportReport(oneDriveUsageLoader, daysBackMax, "OneDrive usage", _logger, lookupIdCache, lastImportedDate)));

                var oneDriveUserActivityLoader = new OneDriveUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(RunReportSafely("OneDrive activity", () => LoadAndSaveDailyImportReport(oneDriveUserActivityLoader, daysBackMax, "OneDrive activity", _logger, lookupIdCache, lastImportedDate)));

                var sharePointUserActivityLoader = new SharePointUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(RunReportSafely("SharePoint user activity", () => LoadAndSaveDailyImportReport(sharePointUserActivityLoader, daysBackMax, "SharePoint user activity", _logger, lookupIdCache, lastImportedDate)));

                var yammerUserActivityLoader = new YammerUserUsageLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(RunReportSafely("Yammer user activity", () => LoadAndSaveDailyImportReport(yammerUserActivityLoader, daysBackMax, "Yammer user activity", _logger, lookupIdCache, lastImportedDate)));

                var yammerGroupsActivityLoader = new YammerGroupUsageLoader(client, _logger);
                importTasks.Add(RunReportSafely("Yammer group activity", () => LoadAndSaveDailyImportReport(yammerGroupsActivityLoader, daysBackMax, "Yammer group activity", _logger, lookupIdCache, lastImportedDate)));

                var yammerDeviceActivityLoader = new YammerDeviceUsageLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(RunReportSafely("Yammer device activity", () => LoadAndSaveDailyImportReport(yammerDeviceActivityLoader, daysBackMax, "Yammer device activity", _logger, lookupIdCache, lastImportedDate)));

                var userPlatActivityLoader = new AppPlatformUserActivityLoader(client, userGroupsCache, userGroupsFilterModel, _logger);
                importTasks.Add(RunReportSafely("Apps & platform activity", () => LoadAndSaveDailyImportReport(userPlatActivityLoader, daysBackMax, "Apps & platform activity", _logger, lookupIdCache, lastImportedDate)));

                // Weekly imports
                using (var db = new AnalyticsEntitiesContext())
                {
                    var sharePointSitesWeeklyUsageReportLoader = new SharePointSitesWeeklyUsageReportLoader(db, client, _logger, new GraphSPSiteIdToUrlCache(_graphClient, db, _logger));

                    importTasks.Add(RunReportSafely("SharePoint sites weekly usage", () => sharePointSitesWeeklyUsageReportLoader.LoadAndSaveLastWeeksReportsIfRefreshOnDay(System.DayOfWeek.Sunday)));
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

                // Remember last import date so the next cycle within the throttle window is skipped - but
                // ONLY when every report actually completed. The timestamp is the proof that the phase
                // finished, so stamping it after a failure is what used to make a broken import look
                // healthy and idle for 24 hours (issue #285). Anything that downloaded successfully has
                // already been saved above, so a retry next cycle is cheap and picks up only what is missing.
                if (failedReports.Count > 0)
                {
                    _logger.LogError($"Activity reports did NOT fully import - {failedReports.Count} of {importTasks.Count} report(s) failed: " +
                        string.Join(", ", failedReports.OrderBy(r => r)) + ". " +
                        "Reports that succeeded have been saved; each failed report saved nothing. This phase has NOT been marked complete and will retry on the next cycle - " +
                        "note that until it succeeds the once-a-day throttle stays disarmed, so the phase re-runs every cycle and re-downloads the full window. " +
                        "If this repeats, check the runtime account has the Reports.Read.All application permission granted and admin-consented.");
                    return false;
                }

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

                // Keep the columnstore index (ColumnstoreUsageReportMetrics) compacted. The upserts above
                // land in the rowstore delta store, which is scanned uncompressed, so without this the
                // licence-opportunity report gets slower every cycle. A no-op where no columnstore exists.
                // Never allowed to fail the import: this is maintenance, not data.
                try
                {
                    await abstractActivityLoader.CompactColumnstoreAsync(db);
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"{thingWeAreImporting}: could not compact the columnstore index "
                        + $"({ex.Message}). The import succeeded; the licence-opportunity report may be "
                        + "slower until this is compacted.");
                }
            }

            var total = abstractActivityLoader.LoadedReportPages.SelectMany(r => r.Value).Count();
            logger.LogInformation($"Imported {total.ToString("N0")} {thingWeAreImporting} reports.");

            return total;
        }
    }
}
