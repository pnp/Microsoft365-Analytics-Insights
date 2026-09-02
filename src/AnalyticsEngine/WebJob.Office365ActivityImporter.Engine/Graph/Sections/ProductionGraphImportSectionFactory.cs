using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.UsageReports;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Sections
{
    /// <summary>
    /// Runs the activity/usage-report phase. Implemented by
    /// <c>GraphImporter.GetAndSaveActivityReportsMultiThreaded</c>, which stays where it is because it is a
    /// public entry point with its own test coverage; the factory only wires it in as a section.
    /// </summary>
    public delegate Task<bool> ActivityReportsImport(int daysBackMax, ManualGraphCallClient client,
        UserGroupsCache userGroupsCache, UserGroupsFilterModel userGroupsFilterModel);

    /// <summary>
    /// The production composition root for the Graph import (issue #376). Every collaborator that
    /// <c>GraphImporter.GetAndSaveAllGraphData</c> used to <c>new</c> inline is built here, leaving
    /// <see cref="GraphImporter"/> as a pure orchestrator over <see cref="IGraphImportSection"/>.
    ///
    /// Construction is deliberately <b>lazy</b>: <see cref="CreateSections"/> only builds the shared Graph
    /// client / caches (which the old code also built unconditionally at the top of the method) and the
    /// section descriptors. Everything else is built inside a section's <c>RunAsync</c>, exactly as before -
    /// so a disabled or gated-off section still constructs nothing. That is load-bearing for the sent-email
    /// section, whose <see cref="RedisDeltaTokenStore"/> opens a Redis connection.
    /// </summary>
    public class ProductionGraphImportSectionFactory : IGraphImportSectionFactory
    {
        // Keys for the per-section "last run" timestamps used to daily-gate the non-fresh Graph imports.
        // Stored verbatim (unprefixed) in Redis db 0, so they can be cleared manually with e.g.
        // `redis-cli DEL GraphUsersMetadataLastImported`.
        public const string GraphUsersMetadataLastImportedKey = "GraphUsersMetadataLastImported";
        public const string GraphTeamsLastImportedKey = "GraphTeamsLastImported";
        public const string GraphCopilotUsageReportsLastImportedKey = "GraphCopilotUsageReportsLastImported";
        public const string CopilotInteractionHistoryLastImportedKey = "CopilotInteractionHistoryLastImported";

        private readonly AnalyticsLogger _logger;
        private readonly AppConfig _settings;
        private readonly GraphAppIndentityOAuthContext _graphAppIndentityOAuthContext;
        private readonly GraphServiceClient _graphClient;
        private readonly ISentEmailMailboxSkipList _sentEmailMailboxSkipList;
        private readonly IAnalyticsDbContextFactory _dbContextFactory;
        private readonly IClock _clock;
        private readonly ActivityReportsImport _activityReportsImport;

        public ProductionGraphImportSectionFactory(
            AnalyticsLogger logger,
            AppConfig settings,
            GraphAppIndentityOAuthContext graphAppIndentityOAuthContext,
            GraphServiceClient graphClient,
            ISentEmailMailboxSkipList sentEmailMailboxSkipList,
            ActivityReportsImport activityReportsImport,
            IAnalyticsDbContextFactory dbContextFactory,
            IClock clock)
        {
            // Deliberately no null guards on logger/settings: GraphImporter's own constructor never had them,
            // and adding them here would move the failure from an NRE inside the import to an
            // ArgumentNullException at construction - a behavioural change for an operator reading a stack
            // trace. activityReportsImport is a new collaborator with no prior behaviour to preserve, and a
            // null one would otherwise surface as an NRE deep inside the usage-report section.
            _logger = logger;
            _settings = settings;
            _graphAppIndentityOAuthContext = graphAppIndentityOAuthContext;
            _graphClient = graphClient;
            _sentEmailMailboxSkipList = sentEmailMailboxSkipList;
            _activityReportsImport = activityReportsImport ?? throw new ArgumentNullException(nameof(activityReportsImport));
            _dbContextFactory = dbContextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
            _clock = clock ?? SystemClock.Instance;
        }

        /// <summary>
        /// Builds the sections for one import cycle, in the order they must run.
        /// </summary>
        /// <param name="settings">
        /// The per-cycle settings passed to <c>GetAndSaveAllGraphData</c>. In production this is the same
        /// object as the <see cref="AppConfig"/> given to the constructor; the distinction is preserved
        /// because the original code read <c>DaysBeforeNowToDownload</c> and the <c>ImportJobSettings</c>
        /// flags from the method argument and everything else from the field.
        /// </param>
        public IReadOnlyList<IGraphImportSection> CreateSections(AppConfig settings)
        {
            // Shared across sections and built unconditionally, exactly as before.
            var httpClient = new ManualGraphCallClient(_graphAppIndentityOAuthContext, _logger);
            var userGroupsFilter = new UserGroupsFilterModel(_settings.UserGroupsFilter);
            var graphUserGroupsCache = new GraphUserGroupsCache(httpClient, _logger);

            return new List<IGraphImportSection>
            {
                DelegateGraphImportSection.Gated(
                    "User metadata refresh",
                    "Skipping user metadata import",
                    GraphUsersMetadataLastImportedKey,
                    _settings.GraphMetadataImportIntervalHours,
                    s => s.GraphUsersMetadata,
                    async () =>
                    {
                        // Update Graph users first
                        var userUpdater = new UserMetadataUpdater(_logger, _settings, _graphAppIndentityOAuthContext.Creds, httpClient);
                        await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();
                        return true;
                    }),

                // Not cadence-gated: the activity/usage-report phase owns its own once-a-day throttle via
                // ISingleDateStore, and reports "did I import" itself.
                DelegateGraphImportSection.Ungated(
                    "Usage reports",
                    "Skipping usage reports import",
                    s => s.GraphUsageReports,
                    // Global user activity report. Each thread creates own context.
                    () => _activityReportsImport(settings.DaysBeforeNowToDownload, httpClient, graphUserGroupsCache, userGroupsFilter)),

                // Refreshed daily by default. Microsoft publishes these reports roughly 48 hours behind,
                // so polling more often costs a full re-download and re-process of every licensed user
                // and returns the same numbers. This uses its own interval rather than the shared
                // non-fresh Graph one, whose High-preset default is "every cycle".
                DelegateGraphImportSection.Gated(
                    "Copilot usage reports",
                    "Skipping Graph Copilot usage reports import",
                    GraphCopilotUsageReportsLastImportedKey,
                    _settings.GraphCopilotUsageReportsIntervalHours,
                    s => s.GraphCopilotUsageReports,
                    () => ImportCopilotUsageReports(httpClient, graphUserGroupsCache, userGroupsFilter)),

                DelegateGraphImportSection.Gated(
                    "Teams import",
                    "Skipping Teams import",
                    GraphTeamsLastImportedKey,
                    _settings.GraphTeamsImportIntervalHours,
                    s => s.GraphTeams,
                    async () =>
                    {
                        var teamsImporter = new TeamsImporter(_logger, _settings, _graphClient);

                        // TeamsCrawlConfig is a detached POCO (two lists of ids), so the context is only
                        // needed for the load itself. It used to be opened around every section from usage
                        // reports onwards even though this was its only reader, which held an EF context -
                        // and its pooled connection - open for the whole multi-hour Graph phase.
                        TeamsCrawlConfig teamsConfig;
                        using (var db = _dbContextFactory.Create())
                        {
                            teamsConfig = await TeamsCrawlConfig.LoadFromDb(db);
                        }

                        await teamsImporter.RefreshAndSaveAllTeamsData(teamsConfig);
                        return true;
                    }),

                DelegateGraphImportSection.Ungated(
                    "Sent emails import",
                    "Skipping sent emails import",
                    s => s.SentEmails,
                    async () =>
                    {
                        IDeltaTokenStore deltaTokenStore;
                        if (!string.IsNullOrEmpty(_settings.ConnectionStrings.RedisConnectionString))
                        {
                            deltaTokenStore = new RedisDeltaTokenStore(_settings.ConnectionStrings.RedisConnectionString, tenantId: _settings.TenantGUID.ToString(), clientId: _settings.ClientID, clientSecret: _settings.ClientSecret);
                        }
                        else
                        {
                            deltaTokenStore = new InMemoryDeltaTokenStore();
                        }

                        // The primary constructor rather than the convenience overload, so the composition
                        // root supplies the DB context factory instead of the importer defaulting it.
                        // The remaining arguments are what the convenience overload passes.
                        var sentEmailImporter = new SentEmailImporter(
                            _logger,
                            _settings,
                            new GraphSentEmailSourceLoader(httpClient, deltaTokenStore, _graphAppIndentityOAuthContext, _logger),
                            SentEmailSentimentScorerFactory.Create(_settings, _logger),
                            dbContextFactory: _dbContextFactory,
                            mailboxSkipList: _sentEmailMailboxSkipList,
                            noMailboxRetryHours: _settings?.SentEmailNoMailboxRetryHours ?? 0);

                        await sentEmailImporter.ImportSentEmails();
                        return true;
                    }),

                // Cadence-gated like the other non-fresh Graph sections, but for a different reason: this
                // one costs a Graph call per in-scope user, so running it every cycle would be expensive
                // even for a modest pilot group. Defaults to daily.
                //
                // Reports success itself rather than throwing. ImportAsync returns null when it declined to
                // run (no UserGroupsFilter, or the app registration has no AiEnterpriseInteraction.Read.All
                // consent) and sets Error on the run log when it caught one. Reporting those as success
                // would stamp the daily gate on a cycle that imported nothing, so enabling the feature
                // before admin consent is granted would silently do nothing for another 24 hours.
                DelegateGraphImportSection.Gated(
                    "Copilot interaction history import",
                    "Skipping Copilot interaction history import",
                    CopilotInteractionHistoryLastImportedKey,
                    _settings.CopilotInteractionHistoryIntervalHours,
                    s => s.CopilotInteractionHistory,
                    async () =>
                    {
                        var interactionImporter = new CopilotInteractionHistoryImporter(
                            _logger,
                            _settings,
                            new GraphAiInteractionSourceLoader(httpClient, _graphAppIndentityOAuthContext, _logger),
                            InteractionCognitiveEnricherFactory.Create(_settings, _logger),
                            new GraphPilotGroupMemberResolver(httpClient, _logger),
                            userGroupsFilter,
                            dbContextFactory: _dbContextFactory,
                            clock: _clock);

                        var interactionLog = await interactionImporter.ImportAsync();
                        return interactionLog != null && string.IsNullOrEmpty(interactionLog.Error);
                    }),
            };
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
                using (var db = _dbContextFactory.Create())
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
                using (var db = _dbContextFactory.Create())
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
    }
}
