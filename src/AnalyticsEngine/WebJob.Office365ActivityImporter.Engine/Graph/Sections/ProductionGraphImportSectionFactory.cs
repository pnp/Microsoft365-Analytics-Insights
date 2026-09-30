using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.UsageReports;
using Common.Entities.UserScope;
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

namespace WebJob.Office365ActivityImporter.Engine.Graph.Sections
{
    /// <summary>
    /// Runs the activity/usage-report phase. Implemented by
    /// <c>GraphImporter.GetAndSaveActivityReportsMultiThreaded</c>, which stays where it is because it is a
    /// public entry point with its own test coverage; the factory only wires it in as a section.
    /// </summary>
    public delegate Task<bool> ActivityReportsImport(int daysBackMax, ManualGraphCallClient client, UserImportScope userScope);

    /// <summary>
    /// The production composition root for the Graph import (issue #376). Every collaborator that
    /// <c>GraphImporter.GetAndSaveAllGraphData</c> used to <c>new</c> inline is built here, leaving
    /// <see cref="GraphImporter"/> as a pure orchestrator over <see cref="IGraphImportSection"/>.
    ///
    /// Construction is deliberately <b>lazy</b>: <see cref="CreateSections"/> only builds the shared Graph
    /// client / caches (which the old code also built unconditionally at the top of the method) and the
    /// section descriptors. Everything else is built inside a section's <c>RunAsync</c>, exactly as before -
    /// so a disabled or gated-off section still constructs nothing. That is load-bearing for the sent-email
    /// section, whose <see cref="PersistedDeltaTokenStore"/> opens the runtime state table.
    /// </summary>
    public class ProductionGraphImportSectionFactory : IGraphImportSectionFactory
    {
        // Keys for the per-section "last run" timestamps used to daily-gate the non-fresh Graph imports.
        // Stored verbatim as row keys in the ImportSchedule partition of the AnalyticsState Azure Table, so they can be
        // cleared by hand (delete the row) to make that import run on the next cycle. The user import's key is shared
        // with the web portal's User import page, which clears it for the same reason.
        public const string GraphUsersMetadataLastImportedKey = Common.Entities.State.UserImportCheckpointKeys.LastCompleted;
        public const string GraphTeamsLastImportedKey = "GraphTeamsLastImported";
        public const string GraphCopilotUsageReportsLastImportedKey = "GraphCopilotUsageReportsLastImported";
        public const string GraphCopilotUsageReportUserCountTrendLastImportedKey = GraphCopilotUsageReportsLastImportedKey + ":UserCountTrend";
        public const string GraphCopilotUsageReportUserCountSummaryLastImportedKey = GraphCopilotUsageReportsLastImportedKey + ":UserCountSummary";
        public const string GraphCopilotUsageReportUsageUserDetailLastImportedKey = GraphCopilotUsageReportsLastImportedKey + ":UsageUserDetail";
        public const string GraphCopilotUsageReportCoworkUsageUserDetailLastImportedKey = GraphCopilotUsageReportsLastImportedKey + ":CoworkUsageUserDetail";
        public const string CopilotInteractionHistoryLastImportedKey = "CopilotInteractionHistoryLastImported";

        private readonly AnalyticsLogger _logger;
        private readonly AppConfig _settings;
        private readonly GraphAppIndentityOAuthContext _graphAppIndentityOAuthContext;
        private readonly GraphServiceClient _graphClient;
        private readonly ISentEmailMailboxSkipList _sentEmailMailboxSkipList;
        private readonly IAnalyticsDbContextFactory _dbContextFactory;
        private readonly IClock _clock;
        private readonly IImportLastRunStore _lastRunStore;
        private readonly ActivityReportsImport _activityReportsImport;
        private readonly IUserImportScopeProvider _userScopeProvider;

        public ProductionGraphImportSectionFactory(
            AnalyticsLogger logger,
            AppConfig settings,
            GraphAppIndentityOAuthContext graphAppIndentityOAuthContext,
            GraphServiceClient graphClient,
            ISentEmailMailboxSkipList sentEmailMailboxSkipList,
            ActivityReportsImport activityReportsImport,
            IAnalyticsDbContextFactory dbContextFactory,
            IClock clock,
            IImportLastRunStore lastRunStore = null,
            IUserImportScopeProvider userScopeProvider = null)
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
            _lastRunStore = lastRunStore ?? new InMemoryImportLastRunStore();

            // Unfiltered when none is supplied, which is what every caller that predates UserGroupsFilter being
            // applied everywhere expects. Production passes the web job's process-lifetime provider.
            _userScopeProvider = userScopeProvider ?? UserImportScopeProvider.Unfiltered();
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
            // Shared across sections and built unconditionally, exactly as before. The UserGroupsFilter scope is NOT
            // built here: each section asks the process-lifetime provider when it runs, so every import in the process
            // applies the same, cached resolution.
            var httpClient = new ManualGraphCallClient(_graphAppIndentityOAuthContext, _logger);

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
                        // Update Graph users first. Only people in the UserGroupsFilter scope are written.
                        var userUpdater = new UserMetadataUpdater(_logger, _settings, _graphAppIndentityOAuthContext.Creds, httpClient,
                            clock: _clock, userScopeProvider: _userScopeProvider, lastRunStore: _lastRunStore);

                        // False when the /users/delta read did not complete (#664). The cadence gate is then not
                        // stamped and no "finished section" event is sent, so the import is retried next cycle
                        // rather than a failed read passing for a tenant in which nothing changed. Returning
                        // instead of throwing also lets the sections after this one run.
                        return await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();
                    }),

                // Not cadence-gated: the activity/usage-report phase owns its own once-a-day throttle via
                // ISingleDateStore, and reports "did I import" itself.
                DelegateGraphImportSection.Ungated(
                    "Usage reports",
                    "Skipping usage reports import",
                    s => s.GraphUsageReports,
                    // Global user activity report. Each thread creates own context.
                    async () => await _activityReportsImport(settings.DaysBeforeNowToDownload, httpClient, await _userScopeProvider.GetScopeAsync())),

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
                    () => ImportCopilotUsageReports(httpClient)),

                DelegateGraphImportSection.Gated(
                    "Teams import",
                    "Skipping Teams import",
                    GraphTeamsLastImportedKey,
                    _settings.GraphTeamsImportIntervalHours,
                    s => s.GraphTeams,
                    async () =>
                    {
                        var teamsImporter = new TeamsImporter(_logger, _settings, _graphClient, await _userScopeProvider.GetScopeAsync());

                        // TeamsCrawlConfig is a detached POCO (two lists of ids), so the context is only
                        // needed for the load itself. It used to be created before the usage-report section
                        // and disposed after the last section, even though this query - part way through
                        // that span - was its only reader.
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
                        var sentEmailStateStore = Common.Entities.State.StateStore.TryOpen(
                            _settings, Common.Entities.State.StatePartitions.SentEmails, _logger);
                        if (sentEmailStateStore != null)
                        {
                            deltaTokenStore = new PersistedDeltaTokenStore(sentEmailStateStore);
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
                            noMailboxRetryHours: _settings?.SentEmailNoMailboxRetryHours ?? 0,
                            userScopeProvider: _userScopeProvider);

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
                            // The shared scope, read FAIL-CLOSED: this import never widens when the groups cannot
                            // be resolved, unlike every other import (see UserImportScopePilotGroupMemberResolver).
                            new UserImportScopePilotGroupMemberResolver(_userScopeProvider),
                            _userScopeProvider.Filter,
                            dbContextFactory: _dbContextFactory,
                            clock: _clock);

                        var interactionLog = await interactionImporter.ImportAsync();
                        return interactionLog != null && string.IsNullOrEmpty(interactionLog.Error);
                    }),
            };
        }

        /// <summary>
        /// Imports the four Graph Microsoft 365 Copilot usage reports. Each report has its own cadence
        /// stamp behind the section-level gate: if one optional report fails, the failed report retries on
        /// the next cycle while reports that already succeeded are skipped until their interval elapses.
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
        private async Task<bool> ImportCopilotUsageReports(ManualGraphCallClient httpClient)
        {
            // Same client, throttling and paging as every other Graph usage report in this solution.
            var reportSource = new GraphCopilotReportSource(httpClient, _logger);
            var userScope = await _userScopeProvider.GetScopeAsync();

            var reportGate = new CopilotUsageReportCadenceRunner(
                _logger,
                _lastRunStore,
                _clock,
                _settings.GraphCopilotUsageReportsIntervalHours,
                _settings.ForceGraphMetadataImport);

            return await reportGate.RunAsync(new[]
            {
                new CopilotUsageReportCadenceRunner.Report(
                    "Copilot user-count trend",
                    GraphCopilotUsageReportUserCountTrendLastImportedKey,
                    async () =>
                    {
                        // First run gets the widest window Graph offers. This is history we cannot get any other way:
                        // the audit pipeline has a hard 7-day retrieval ceiling, so without this backfill a new
                        // install starts with an empty Copilot adoption trend.
                        //
                        // The decision is based on whether a D180 TREND import has ever completed - not on whether any
                        // Copilot row exists. Keying it off "any row" meant a successful summary import alongside a
                        // failed D180 trend permanently downgraded every later run to D28, silently losing the
                        // backfill for good.
                        var dueBackfillDone = await HasCompletedTrendBackfill();
                        var dueTrendPeriod = dueBackfillDone ? CopilotReportRequest.DefaultRefreshPeriod : CopilotReportRequest.MaxHistoryPeriod;
                        if (!dueBackfillDone)
                        {
                            _logger.LogInformation($"No completed Copilot trend backfill on record - requesting the maximum window ({dueTrendPeriod}).");
                        }

                        return await RunCopilotReport("Copilot user-count trend", db =>
                            new CopilotUserCountReportLoader(reportSource, _logger).LoadAndSaveTrendAsync(db, dueTrendPeriod));
                    }),

                new CopilotUsageReportCadenceRunner.Report(
                    "Copilot user-count summary",
                    GraphCopilotUsageReportUserCountSummaryLastImportedKey,
                    () => RunCopilotReport("Copilot user-count summary", db =>
                        new CopilotUserCountReportLoader(reportSource, _logger).LoadAndSaveSummaryAsync(db, CopilotReportRequest.DefaultRefreshPeriod))),

                new CopilotUsageReportCadenceRunner.Report(
                    "Copilot per-user usage detail",
                    GraphCopilotUsageReportUsageUserDetailLastImportedKey,
                    () => RunCopilotReport("Copilot per-user usage detail", db =>
                        new CopilotUsageUserDetailLoader(reportSource, _logger, userScope)
                            .LoadAndSaveAsync(db, CopilotReportRequest.DefaultRefreshPeriod))),

                new CopilotUsageReportCadenceRunner.Report(
                    "Cowork per-user usage detail",
                    GraphCopilotUsageReportCoworkUsageUserDetailLastImportedKey,
                    () => RunCopilotReport("Cowork per-user usage detail", db =>
                        new CoworkUsageUserDetailLoader(reportSource, _logger, userScope)
                            .LoadAndSaveAsync(db, CopilotReportRequest.DefaultRefreshPeriod))),
            });
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

                _logger.LogError($"{reportDescription} failed with HTTP {(int)ex.StatusCode} ({ex.StatusCode}): {ex.Message} {advice}" +
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

        internal class CopilotUsageReportCadenceRunner
        {
            private readonly ILogger _logger;
            private readonly IImportLastRunStore _lastRunStore;
            private readonly IClock _clock;
            private readonly int _intervalHours;
            private readonly bool _force;

            public CopilotUsageReportCadenceRunner(ILogger logger, IImportLastRunStore lastRunStore, IClock clock, int intervalHours, bool force)
            {
                _logger = logger;
                _lastRunStore = lastRunStore ?? new InMemoryImportLastRunStore();
                _clock = clock ?? SystemClock.Instance;
                _intervalHours = intervalHours;
                _force = force;
            }

            public async Task<bool> RunAsync(IEnumerable<Report> reports)
            {
                var allDueReportsSucceeded = true;

                foreach (var report in reports)
                {
                    var lastRun = await _lastRunStore.GetLastRunUtc(report.CadenceKey);
                    if (!ImportCadenceGate.ShouldRun(lastRun, _intervalHours, _force, _clock.UtcNow))
                    {
                        _logger.LogInformation($"Skipping {report.Description}: ran recently ({lastRun:u} UTC). " +
                            $"Next run after {lastRun?.AddHours(_intervalHours):u} UTC (interval {_intervalHours}h). " +
                            $"Set ForceGraphMetadataImport=true, or delete the '{report.CadenceKey}' row (partition '{Common.Entities.State.StatePartitions.ImportSchedule}') " +
                            $"from the '{Common.Entities.State.StateStore.TableName}' table, to override.");
                        continue;
                    }

                    if (_force)
                    {
                        _logger.LogInformation($"ForceGraphMetadataImport=true; bypassing the cadence gate for {report.Description}.");
                    }

                    var succeeded = await report.Import();
                    allDueReportsSucceeded &= succeeded;

                    if (_intervalHours > 0 && succeeded)
                    {
                        await _lastRunStore.SetLastRunUtc(report.CadenceKey, _clock.UtcNow);
                    }
                }

                return allDueReportsSucceeded;
            }

            public class Report
            {
                public Report(string description, string cadenceKey, Func<Task<bool>> import)
                {
                    if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException($"'{nameof(description)}' is required.", nameof(description));
                    if (string.IsNullOrWhiteSpace(cadenceKey)) throw new ArgumentException($"'{nameof(cadenceKey)}' is required.", nameof(cadenceKey));
                    Description = description;
                    CadenceKey = cadenceKey;
                    Import = import ?? throw new ArgumentNullException(nameof(import));
                }

                public string Description { get; }
                public string CadenceKey { get; }
                public Func<Task<bool>> Import { get; }
            }
        }
    }
}
