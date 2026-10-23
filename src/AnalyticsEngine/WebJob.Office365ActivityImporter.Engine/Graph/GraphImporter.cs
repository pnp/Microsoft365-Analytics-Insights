using Common.Entities;
using Common.Entities.ActivityReports;
using Common.Entities.Config;
using Common.Entities.UserScope;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;
using WebJob.Office365ActivityImporter.Engine.Graph.Sections;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Rules;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Orchestrates the Graph import: selects the enabled sections, applies the per-section cadence gate and
    /// records the last-run time. It does not build any of them - composition lives behind
    /// <see cref="IGraphImportSectionFactory"/> (issue #376), which is what makes this loop testable with fake
    /// sections and no SQL Server, Graph, Azure Storage or Service Bus.
    /// </summary>
    public class GraphImporter : AbstractApiLoader
    {
        private readonly GraphServiceClient _graphClient;
        // Two independent markers: one proves the usage-report phase completed (so finalized dates can be
        // skipped safely), the other gates how often the non-fresh Graph sections re-run.
        private readonly ISingleDateStore _activityReportsLastImportedStore;
        // Per-report completion stamps. The phase marker above cannot answer "is THIS report's stored data
        // complete?" - withholding it after one report fails also emptied the skip list for the ten that
        // succeeded, making them re-download their full window every cycle (issue #311).
        private readonly IReportCompletionStore _reportCompletionStore;
        private readonly IImportLastRunStore _lastRunStore;

        private readonly IGraphImportSectionFactory _sectionFactory;

        private readonly IClock _clock;
        internal static readonly TimeSpan WeeklyReportNoSundayBackoff = WeeklyUsageReportRetryPolicy.RetryInterval;

        /// <summary>
        /// Production constructor: builds the <see cref="ProductionGraphImportSectionFactory"/> that holds all
        /// the section wiring.
        /// </summary>
        /// <param name="userScopeProvider">
        /// The process-lifetime <c>UserGroupsFilter</c> scope, shared with every other import in the process so they
        /// all apply the same scope. Null means unfiltered.
        /// </param>
        public GraphImporter(AnalyticsLogger logger, IUserImportScopeProvider userScopeProvider, GraphAppIndentityOAuthContext graphAppIndentityOAuthContext, GraphServiceClient graphClient, AppConfig settings, ISingleDateStore activityReportsLastImportedStore = null, IImportLastRunStore lastRunStore = null, ISentEmailMailboxSkipList sentEmailMailboxSkipList = null, IClock clock = null)
            : this(logger, userScopeProvider, graphAppIndentityOAuthContext, graphClient, settings, activityReportsLastImportedStore, lastRunStore, sentEmailMailboxSkipList, reportCompletionStore: null, clock: clock)
        {
        }

        public GraphImporter(AnalyticsLogger logger, IUserImportScopeProvider userScopeProvider, GraphAppIndentityOAuthContext graphAppIndentityOAuthContext, GraphServiceClient graphClient, AppConfig settings, ISingleDateStore activityReportsLastImportedStore, IImportLastRunStore lastRunStore, ISentEmailMailboxSkipList sentEmailMailboxSkipList, IClock clock, IReportCompletionStore reportCompletionStore)
            : base(logger, settings)
        {
            _clock = clock ?? SystemClock.Instance;
            _graphClient = graphClient;
            _activityReportsLastImportedStore = activityReportsLastImportedStore;
            _reportCompletionStore = reportCompletionStore;

            // Defensive: a per-instance in-memory store still works (just doesn't persist the gate
            // across cycles). Production passes the process-lifetime store hoisted in Program.cs.
            _lastRunStore = lastRunStore ?? new InMemoryImportLastRunStore();

            _sectionFactory = new ProductionGraphImportSectionFactory(
                logger,
                settings,
                graphAppIndentityOAuthContext,
                graphClient,
                sentEmailMailboxSkipList ?? new InMemorySentEmailMailboxSkipList(),
                GetAndSaveActivityReportsMultiThreaded,
                DefaultAnalyticsDbContextFactory.Instance,
                _clock,
                _lastRunStore,
                userScopeProvider);
        }

        /// <summary>
        /// Orchestration-only constructor: the sections are supplied, so nothing here touches Graph, SQL or
        /// Azure Storage. Separate from the production constructor rather than another optional parameter on it,
        /// because a trailing optional argument is baked in by the calling compiler and so is binary-breaking
        /// for already-compiled callers.
        ///
        /// <b>Internal deliberately.</b> An instance built this way is composed only for the section passes
        /// (<see cref="GetAndSaveAllGraphData"/>, <see cref="GetAndSaveNonDeferredGraphData"/> and
        /// <see cref="GetAndSaveDeferredGraphData"/>): it has no <see cref="GraphServiceClient"/> and no
        /// <see cref="ISingleDateStore"/>, so the still-public
        /// <see cref="GetAndSaveActivityReportsMultiThreaded"/> is not supported on it.
        /// <c>InternalsVisibleTo("Tests.UnitTests")</c> makes it reachable from the test project, which
        /// is its only intended caller; production composes through the constructor above.
        /// </summary>
        internal GraphImporter(AnalyticsLogger logger, AppConfig settings, IGraphImportSectionFactory sectionFactory, IImportLastRunStore lastRunStore, IClock clock)
            : base(logger, settings)
        {
            _clock = clock ?? SystemClock.Instance;
            _sectionFactory = sectionFactory ?? throw new ArgumentNullException(nameof(sectionFactory));
            _lastRunStore = lastRunStore ?? new InMemoryImportLastRunStore();
        }


        /// <summary>
        /// Runs a "non-fresh" Graph import section at most once per <paramref name="intervalHours"/>.
        /// The last-run timestamp is persisted via <see cref="IImportLastRunStore"/> (the runtime state table when a
        /// Storage connection string is configured, otherwise in-memory) so the gate survives the per-cycle recreation of this
        /// importer. An interval of 0 disables the gate (runs every cycle); <c>ForceGraphMetadataImport</c>
        /// bypasses it for one run. Store failures are fail-open (the section still runs).
        ///
        /// The section reports success itself instead of throwing. Returning false records the section as not
        /// done, so the cadence gate lets it retry next cycle, without an exception unwinding out of the pass
        /// that is running it (<see cref="GetAndSaveAllGraphData"/>, <see cref="GetAndSaveNonDeferredGraphData"/>
        /// or <see cref="GetAndSaveDeferredGraphData"/>) and skipping the sections that come after it.
        /// </summary>
        private async Task RunGraphSectionIfDueAsync(string key, int intervalHours, string sectionName, Func<Task<bool>> sectionWork)
        {
            var force = _settings.ForceGraphMetadataImport;
            var lastRun = await _lastRunStore.GetLastRunUtc(key);

            if (!ImportCadenceGate.ShouldRun(lastRun, intervalHours, force, _clock.UtcNow))
            {
                _logger.LogInformation($"Skipping {sectionName}: ran recently ({lastRun:u} UTC). " +
                    $"Next run after {lastRun?.AddHours(intervalHours):u} UTC (interval {intervalHours}h). " +
                    $"Set ForceGraphMetadataImport=true, or delete the '{key}' row (partition '{Common.Entities.State.StatePartitions.ImportSchedule}') " +
                    $"from the '{Common.Entities.State.StateStore.TableName}' table, to override.");
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

            // The portal also uses this stamp as last completed, even when cadence gating is disabled.
            if (succeeded)
            {
                await _lastRunStore.SetLastRunUtc(key, _clock.UtcNow);
            }
        }

        /// <summary>
        /// Runs a section that is not cadence-gated: it either has no throttle at all or owns one of its own,
        /// as the activity/usage-report phase does. There is deliberately no "did not complete" warning here -
        /// for that phase, returning false is the ordinary "throttled, nothing to do" answer and would warn on
        /// every cycle inside the window.
        /// </summary>
        private async Task RunUngatedSectionAsync(IGraphImportSection section)
        {
            var timer = new JobTimer(_logger, section.Name);
            timer.Start();

            if (await section.RunAsync())
            {
                timer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
            }
        }

        /// <summary>
        /// One-shot entry point: runs every enabled section in a single call - the main pass, then the deferred
        /// pass - for a caller that does not run them separately. The WebJob calls
        /// <see cref="GetAndSaveNonDeferredGraphData"/> in its import cycle and starts
        /// <see cref="GetAndSaveDeferredGraphData"/> in the background (issue #706).
        ///
        /// A section that returns false is recorded as not done and the following sections still run. A
        /// section that <b>throws</b> unwinds out of here and the sections after it, deferred ones included, are
        /// skipped for this cycle - that is the long-standing behaviour and is left unchanged: adding per-section
        /// exception isolation would silently convert a hard failure into a logged warning, which is a behavioural
        /// change and belongs in its own issue.
        /// </summary>
        public async Task GetAndSaveAllGraphData(AppConfig settings)
        {
            var sections = _sectionFactory.CreateSections(settings);

            await RunSectionsAsync(sections, settings, deferredPass: false);
            await RunSectionsAsync(sections, settings, deferredPass: true);
        }

        /// <summary>
        /// The main pass: every enabled section that is not <see cref="IGraphImportSection.IsDeferred"/>, in factory
        /// order, user metadata first. The WebJob runs it at the start of each import cycle. Error handling is that of
        /// <see cref="GetAndSaveAllGraphData"/>, limited to this pass.
        /// </summary>
        public async Task GetAndSaveNonDeferredGraphData(AppConfig settings)
        {
            await RunSectionsAsync(_sectionFactory.CreateSections(settings), settings, deferredPass: false);
        }

        /// <summary>
        /// The deferred pass: only the sections marked <see cref="IGraphImportSection.IsDeferred"/> - the once-a-day
        /// usage-report phase. The WebJob starts it in the background, single-flight, and does not wait for it, so the
        /// hours that phase can take on a large tenant no longer hold up the import cycle and its audit import (issue
        /// #706). Sections are selected, gated, timed and logged exactly as in the main pass. Builds its own sections,
        /// so it does not depend on the main pass having run or succeeded.
        /// </summary>
        public async Task GetAndSaveDeferredGraphData(AppConfig settings)
        {
            await RunSectionsAsync(_sectionFactory.CreateSections(settings), settings, deferredPass: true);
        }

        /// <summary>
        /// Runs the enabled sections of one pass, in factory order. A section belongs to exactly one pass, so its
        /// "Skipping ..." line is still logged once per cycle.
        /// </summary>
        private async Task RunSectionsAsync(IReadOnlyList<IGraphImportSection> sections, AppConfig settings, bool deferredPass)
        {
            foreach (var section in sections)
            {
                if (section.IsDeferred != deferredPass)
                {
                    continue;
                }

                if (!section.IsEnabled(settings.ImportJobSettings))
                {
                    _logger.LogInformation(section.DisabledMessage);
                    continue;
                }

                if (section.CadenceKey != null)
                {
                    await RunGraphSectionIfDueAsync(section.CadenceKey, section.IntervalHours, section.Name, section.RunAsync);
                }
                else
                {
                    await RunUngatedSectionAsync(section);
                }
            }
        }


        /// <summary>
        /// Stable per-report key for <see cref="IReportCompletionStore"/>. Derived from the loader type name
        /// rather than the human-readable report label, so renaming a log message cannot silently orphan a
        /// stamp and trigger a full re-download.
        /// </summary>
        internal static string GetReportKey(Type loaderType) => loaderType.Name;

        /// <summary>
        /// The completion date that feeds one report's finalized-date skip list.
        /// </summary>
        /// <remarks>
        /// Deliberately does NOT fall back to the phase-level marker when a per-report stamp is absent. That
        /// marker is an unversioned key that predates the strict-paging fixes (#285 / #310); an older
        /// build could write it after a report had saved a partial day, and skipping a partially-stored date
        /// loses rows permanently once Graph's ~28-day retention passes. One extra full download per report on
        /// the first upgraded cycle is a bounded, one-off cost.
        ///
        /// The phase marker is still used when no per-report store is supplied at all, which keeps existing
        /// behaviour for those callers.
        /// </remarks>
        internal async Task<DateTime?> ResolveReportSkipListInputAsync(string reportKey, DateTime? phaseMarker)
        {
            if (_reportCompletionStore == null) return phaseMarker;

            return await _reportCompletionStore.GetLastSuccessAsync(reportKey);
        }

        /// <summary>
        /// Whether a report that has no finalized-date skip list of its own is due to run again.
        /// </summary>
        internal async Task<bool> IsReportDueAsync(string reportKey, TimeSpan minWait)
        {
            if (_reportCompletionStore == null) return true;
            if (_settings.ForceUsageReportsImport) return true;

            if (_reportCompletionStore is IReportAttemptScheduleStore scheduleStore)
            {
                var nextAttemptUtc = await scheduleStore.GetNextAttemptUtcAsync(reportKey);
                if (nextAttemptUtc.HasValue)
                {
                    return _clock.UtcNow >= nextAttemptUtc.Value.ToUniversalTime();
                }
            }

            var lastSuccess = await _reportCompletionStore.GetLastSuccessAsync(reportKey);
            return lastSuccess == null || _clock.UtcNow.Subtract(lastSuccess.Value.ToUniversalTime()) >= minWait;
        }

        internal Task RunWeeklyReportIfDueAsync(SharePointSitesWeeklyUsageReportLoader loader, TimeSpan minWait)
            => RunWeeklyReportIfDueAsync(loader, minWait, logWhenNotDue: true);

        /// <param name="logWhenNotDue">
        /// False for the check made on every cycle between the daily phase's runs, where "not due" is the normal
        /// answer and logging it would add a line to every cycle.
        /// </param>
        internal async Task RunWeeklyReportIfDueAsync(SharePointSitesWeeklyUsageReportLoader loader, TimeSpan minWait, bool logWhenNotDue)
        {
            var reportKey = GetReportKey(loader.GetType());

            if (!await IsReportDueAsync(reportKey, minWait))
            {
                if (logWhenNotDue)
                {
                    _logger.LogInformation(
                        "SharePoint sites weekly usage: skipping - it is not due yet: it runs at most every " +
                        $"{minWait.TotalHours} hours, and waits for Graph to publish Sunday's report. It has no finalized-date skip list, " +
                        "so re-running it while another report retries would re-download the whole report for nothing.");
                }
                return;
            }

            if (_reportCompletionStore != null)
            {
                await _reportCompletionStore.ClearAsync(reportKey);
            }

            WeeklyUsageReportSaveResult result;
            try
            {
                result = await loader.LoadAndSaveLastWeeksReportsIfRefreshOnDayWithResult(System.DayOfWeek.Sunday);
            }
            catch
            {
                // The caller surfaces the failure. Retry on this report's own short schedule: it no longer waits for
                // the daily phase, and a failure must not leave it due on every cycle either.
                await TryScheduleReportAttemptAtAsync(reportKey, _clock.UtcNow.Add(WeeklyReportNoSundayBackoff));
                throw;
            }

            if (_reportCompletionStore != null)
            {
                if (result.ObservedRefreshDayReport)
                {
                    await _reportCompletionStore.SaveSuccessAsync(reportKey);
                    await ScheduleNextReportAttemptAsync(reportKey, minWait);
                }
                else
                {
                    var nowUtc = _clock.UtcNow;
                    var nextAttemptUtc = WeeklyUsageReportRetryPolicy.NextAttemptAfterMissedRefreshDayUtc(
                        nowUtc, result.LatestRefreshDate, System.DayOfWeek.Sunday, noUsableRows: result.NoUsableRows);
                    var newestRefresh = result.LatestRefreshDate?.ToString("yyyy-MM-dd") ?? "none";
                    string message;
                    if (!result.NoUsableRows)
                    {
                        message = $"SharePoint sites weekly usage: Graph returned {result.RowsReturned:N0} row(s), {result.UsableRows:N0} with a usable site URL, " +
                            $"but none of those had a Sunday refresh date (newest refresh date: {newestRefresh}). " +
                            $"Not recording a data-success stamp; retrying at {nextAttemptUtc:u} UTC, then every " +
                            $"{WeeklyReportNoSundayBackoff.TotalMinutes} minutes until Sunday's report is stored - on its own schedule, " +
                            "not the daily reports' once-a-day one.";
                    }
                    else if (WeeklyUsageReportRetryPolicy.IsRefreshDayReportStillCurrent(nowUtc, result.LatestRefreshDate, System.DayOfWeek.Sunday))
                    {
                        message = $"SharePoint sites weekly usage: Graph returned {result.RowsReturned:N0} row(s) of Sunday's report ({newestRefresh}), but none " +
                            "could be used - none had a site URL. Not recording a data-success stamp. Sunday's report can only be downloaded until Graph " +
                            $"replaces it with Monday's, so it is retried every {WeeklyUsageReportRetryPolicy.UnusableRefreshDayReportRetryInterval.TotalHours} " +
                            $"hours until then - by {WeeklyUsageReportRetryPolicy.ReportReplacedByUtc(result.LatestRefreshDate.Value):u} UTC at the latest - " +
                            $"in case the cause is fixed in time. The next attempt is at {nextAttemptUtc:u} UTC.";
                    }
                    else
                    {
                        message = $"SharePoint sites weekly usage: Graph returned {result.RowsReturned:N0} row(s), but none could be used - none had a site URL " +
                            $"(newest refresh date: {newestRefresh}). Not recording a data-success stamp; the next attempt is at {nextAttemptUtc:u} UTC, " +
                            "since retrying sooner cannot change that.";
                    }
                    _logger.LogInformation(message);
                    await ScheduleReportAttemptAtAsync(reportKey, nextAttemptUtc);
                }
            }
        }

        private async Task ScheduleNextReportAttemptAsync(string reportKey, TimeSpan interval)
        {
            if (!(_reportCompletionStore is IReportAttemptScheduleStore scheduleStore)) return;

            var now = _clock.UtcNow;
            var previousDue = await scheduleStore.GetNextAttemptUtcAsync(reportKey);
            DateTime nextDueUtc;
            if (previousDue.HasValue && previousDue.Value.ToUniversalTime() <= now)
            {
                nextDueUtc = previousDue.Value.ToUniversalTime();
                do
                {
                    nextDueUtc = nextDueUtc.Add(interval);
                }
                while (nextDueUtc <= now);
            }
            else
            {
                nextDueUtc = now.Add(interval);
            }

            await scheduleStore.SaveNextAttemptUtcAsync(reportKey, DateTime.SpecifyKind(nextDueUtc, DateTimeKind.Utc));
        }

        private async Task ScheduleReportAttemptAtAsync(string reportKey, DateTime nextAttemptUtc)
        {
            if (_reportCompletionStore is IReportAttemptScheduleStore scheduleStore)
            {
                await scheduleStore.SaveNextAttemptUtcAsync(reportKey, DateTime.SpecifyKind(nextAttemptUtc, DateTimeKind.Utc));
            }
        }

        private async Task TryScheduleReportAttemptAtAsync(string reportKey, DateTime nextAttemptUtc)
        {
            try
            {
                await ScheduleReportAttemptAtAsync(reportKey, nextAttemptUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not record when to retry the '{reportKey}' usage report ({ex.GetType().Name}: {ex.Message}); " +
                    "it will be retried on the next cycle instead.");
            }
        }

        /// <summary>One report of the usage-report phase: its name for log lines, and the work that imports it.</summary>
        internal sealed class ActivityReportRun
        {
            public ActivityReportRun(string name, Func<Task> run)
            {
                Name = name;
                Run = run;
            }

            public string Name { get; }
            public Func<Task> Run { get; }
        }

        /// <param name="userScope">The <c>UserGroupsFilter</c> scope for this cycle; null means unfiltered.</param>
        public Task<bool> GetAndSaveActivityReportsMultiThreaded(int daysBackMax, ManualGraphCallClient client, UserImportScope userScope)
        {
            return RunActivityReportPhaseAsync(
                daysBackMax,
                HasAnyActivityDataAsync,
                (lookupIdCache, lastImportedDate) => CreateDailyReports(daysBackMax, client, userScope, lookupIdCache, lastImportedDate),
                run => WithSharePointSitesWeeklyLoaderAsync(client, run));
        }

        private static async Task<bool> HasAnyActivityDataAsync()
        {
            // EXISTS (AnyAsync) rather than COUNT(*): this only needs "is the table empty", and runs every cycle.
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.TeamUserActivityLogs.AnyAsync();
            }
        }

        private IReadOnlyList<ActivityReportRun> CreateDailyReports(int daysBackMax, ManualGraphCallClient client, UserImportScope userScope,
            ConcurrentLookupDbIdsCache lookupIdCache, DateTime? lastImportedDate)
        {
            // Parallel-load all, each one with own DB context.
            var teamsUserUsageLoader = new TeamsUserUsageLoader(client, userScope, _logger);
            var teamsUserDeviceLoader = new TeamsUserDeviceLoader(client, userScope, _logger);
            var outlookLoader = new OutlookUserActivityLoader(client, userScope, _logger);
            var oneDriveUsageLoader = new OneDriveUsageLoader(client, userScope, _logger);
            var oneDriveUserActivityLoader = new OneDriveUserActivityLoader(client, userScope, _logger);
            var sharePointUserActivityLoader = new SharePointUserActivityLoader(client, userScope, _logger);
            var yammerUserActivityLoader = new YammerUserUsageLoader(client, userScope, _logger);
            var yammerGroupsActivityLoader = new YammerGroupUsageLoader(client, _logger);
            var yammerDeviceActivityLoader = new YammerDeviceUsageLoader(client, userScope, _logger);
            var userPlatActivityLoader = new AppPlatformUserActivityLoader(client, userScope, _logger);

            return new List<ActivityReportRun>
            {
                new ActivityReportRun("Teams user activity", () => LoadAndSaveDailyImportReport(teamsUserUsageLoader, daysBackMax, "Teams user activity", _logger, lookupIdCache, lastImportedDate)),
                new ActivityReportRun("Teams user device", () => LoadAndSaveDailyImportReport(teamsUserDeviceLoader, daysBackMax, "Teams user device", _logger, lookupIdCache, lastImportedDate)),
                new ActivityReportRun("Outlook activity", () => LoadAndSaveDailyImportReport(outlookLoader, daysBackMax, "Outlook activity", _logger, lookupIdCache, lastImportedDate)),
                new ActivityReportRun("OneDrive usage", () => LoadAndSaveDailyImportReport(oneDriveUsageLoader, daysBackMax, "OneDrive usage", _logger, lookupIdCache, lastImportedDate)),
                new ActivityReportRun("OneDrive activity", () => LoadAndSaveDailyImportReport(oneDriveUserActivityLoader, daysBackMax, "OneDrive activity", _logger, lookupIdCache, lastImportedDate)),
                new ActivityReportRun("SharePoint user activity", () => LoadAndSaveDailyImportReport(sharePointUserActivityLoader, daysBackMax, "SharePoint user activity", _logger, lookupIdCache, lastImportedDate)),
                new ActivityReportRun("Yammer user activity", () => LoadAndSaveDailyImportReport(yammerUserActivityLoader, daysBackMax, "Yammer user activity", _logger, lookupIdCache, lastImportedDate)),
                new ActivityReportRun("Yammer group activity", () => LoadAndSaveDailyImportReport(yammerGroupsActivityLoader, daysBackMax, "Yammer group activity", _logger, lookupIdCache, lastImportedDate)),
                new ActivityReportRun("Yammer device activity", () => LoadAndSaveDailyImportReport(yammerDeviceActivityLoader, daysBackMax, "Yammer device activity", _logger, lookupIdCache, lastImportedDate)),
                new ActivityReportRun("Apps & platform activity", () => LoadAndSaveDailyImportReport(userPlatActivityLoader, daysBackMax, "Apps & platform activity", _logger, lookupIdCache, lastImportedDate)),
            };
        }

        private async Task WithSharePointSitesWeeklyLoaderAsync(ManualGraphCallClient client, Func<SharePointSitesWeeklyUsageReportLoader, Task> run)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var loader = new SharePointSitesWeeklyUsageReportLoader(db, client, _logger, new GraphSPSiteIdToUrlCache(_graphClient, db, _logger));
                await run(loader);
            }
        }

        /// <summary>
        /// The usage-report phase's orchestration: the once-a-day gate, the weekly report's own schedule, and what
        /// each outcome stamps. The SQL- and Graph-bound parts are passed in, so this runs in a test with neither.
        /// </summary>
        /// <param name="hasAnyActivityData">
        /// Whether any usage data is stored at all; when not, the gate is reset - unless a refusal for concealed
        /// identities (<see cref="ConcealedIdentityRefusalKey"/>) stands, which is why the table is empty.
        /// </param>
        /// <param name="createDailyReports">The daily reports, given the shared lookup cache and the previous phase stamp.</param>
        /// <param name="withWeeklyLoader">Builds the weekly SharePoint sites loader, runs the callback with it, and disposes it.</param>
        internal async Task<bool> RunActivityReportPhaseAsync(
            int daysBackMax,
            Func<Task<bool>> hasAnyActivityData,
            Func<ConcurrentLookupDbIdsCache, DateTime?, IReadOnlyList<ActivityReportRun>> createDailyReports,
            Func<Func<SharePointSitesWeeklyUsageReportLoader, Task>, Task> withWeeklyLoader)
        {
            var MIN_WAIT = TimeSpan.FromDays(1);

            // Throttle the whole activity/usage-report phase (all daily loaders + the weekly SharePoint sites
            // loader) to run at most once a day. The store is injected so it survives across import cycles:
            // the runtime state table when Storage is configured, otherwise an in-memory fallback (see ActivityReportsLastImportedStoreFactory).
            // When no store is supplied (e.g. unit tests) we don't throttle and always import.
            DateTime? lastImportedDate = null;
            var lastImportedStore = _activityReportsLastImportedStore;
            if (lastImportedStore != null)
            {
                // Clear the "last imported" date if there's no activity data in the DB at all, so a fresh or
                // wiped database imports immediately instead of waiting out a stale timestamp. Not while a refusal for
                // concealed identities stands: such a tenant never stores a row, so the table stays empty and the reset
                // would re-run the whole phase - every report downloading at least a day - on every cycle.
                if (!await IsConcealedIdentityRefusalCurrentAsync() && !await hasAnyActivityData())
                {
                    await lastImportedStore.DeleteDt();
                }

                lastImportedDate = await lastImportedStore.GetLastDT();
            }
            else
            {
                _logger.LogWarning("No activity-reports throttle store configured - cannot check last import date; will import this cycle.");
            }

            // The weekly SharePoint sites report keeps its own schedule when the completion store can hold one. Graph only
            // offers its current refresh, so a Sunday missed while it is the newest refresh is lost for good - and the
            // daily phase's gate, which opens 24 hours after the phase last COMPLETED, drifts later every day and steps
            // over that window. So the weekly report is checked on every invocation, not only when the daily reports run;
            // and a failure of one no longer changes the other's cadence.
            var weeklyHasOwnSchedule = _reportCompletionStore is IReportAttemptScheduleStore;

            // Compare instants, not wall-clock readings: the store stamps DateTime.Now, so lastImportedDate
            // comes back as a LOCAL time, and the old `DateTime.Now.Subtract(lastImportedDate)` compared two
            // local readings - which is the wall-clock difference, not the elapsed time, across a DST change.
            // ActivityReportsCadenceGate normalises to UTC and takes "now" from the injected clock.
            var runImport = ActivityReportsCadenceGate.ShouldRun(lastImportedDate, MIN_WAIT, _clock.UtcNow);
            if (_settings.ForceUsageReportsImport)
            {
                _logger.LogInformation("ForceUsageReportsImport=true; bypassing recently-imported gate.");
                runImport = true;
            }
            if (runImport)
            {
                // The timestamp is both the once-a-day throttle and the proof that every daily report
                // completed. Clear it before any batched writes: if this phase fails after a
                // partial save, the next cycle must re-import rather than trusting the old marker.
                // Keep lastImportedDate locally so rows covered by that prior successful phase can
                // still be skipped safely during this run.
                if (lastImportedStore != null)
                {
                    await lastImportedStore.DeleteDt();
                }

                _logger.LogInformation($"Reading all activity reports from {daysBackMax} days back (the UsageReportsDaysBack setting; " +
                    "the audit import's DaysBeforeNowToDownload window is separate)...");

                // Each report is run through RunReportSafely so ONE failing report cannot discard the work
                // of the others: whatever downloaded successfully is still saved. Failures are collected and
                // decide whether this phase is allowed to stamp itself complete below.
                var importTasks = new List<Task>();
                var failedReports = new System.Collections.Concurrent.ConcurrentBag<string>();
                // Refused because the tenant conceals identities. Reported as failures, but they do not disarm the
                // once-a-day throttle: retrying cannot succeed until an administrator changes the tenant setting, and
                // re-downloading every report each cycle until then would only add Graph load.
                var concealedReports = new System.Collections.Concurrent.ConcurrentBag<string>();
                // A weekly failure retries on the weekly report's own schedule, so it does not hold back the daily stamp.
                var weeklyFailures = weeklyHasOwnSchedule ? new System.Collections.Concurrent.ConcurrentBag<string>() : failedReports;

                const string PhaseRetries = "This phase will NOT be recorded as complete, so it retries on the next cycle.";

                async Task RunReportSafely(string reportName, Func<Task> work, System.Collections.Concurrent.ConcurrentBag<string> failures, string retryConsequence)
                {
                    try
                    {
                        await work();
                    }
                    catch (ConcealedUsageReportException ex)
                    {
                        concealedReports.Add($"{reportName} (identities concealed)");
                        _logger.LogError($"{reportName}: {ex.Message} The other activity reports are unaffected. Retrying sooner cannot help, " +
                            "so this report is tried again with the next once-a-day run of this phase.");
                    }
                    catch (GraphHttpException ex)
                    {
                        failures.Add($"{reportName} (HTTP {(int)ex.StatusCode} {ex.StatusCode})");
                        _logger.LogError($"{reportName} failed with HTTP {(int)ex.StatusCode} ({ex.StatusCode}): {ex.Message} " +
                            "The other activity reports are unaffected and keep whatever they downloaded; this report saved nothing at all. " +
                            retryConsequence + " " +
                            "A 401/403 here almost always means the Reports.Read.All application permission is missing or not admin-consented.");
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{reportName} ({ex.GetType().Name})");
                        _logger.LogError(ex, $"{reportName} failed: {ex.Message}. The other activity reports are unaffected; " +
                            char.ToLowerInvariant(retryConsequence[0]) + retryConsequence.Substring(1));
                    }
                }

                var lookupIdCache = new ConcurrentLookupDbIdsCache();

                // Daily imports
                foreach (var report in createDailyReports(lookupIdCache, lastImportedDate))
                {
                    importTasks.Add(RunReportSafely(report.Name, report.Run, failedReports, PhaseRetries));
                }

                // Weekly import
                importTasks.Add(RunReportSafely(WeeklyReportName,
                    () => withWeeklyLoader(loader => RunWeeklyReportIfDueAsync(loader, MIN_WAIT)),
                    weeklyFailures, weeklyHasOwnSchedule ? WeeklyRetriesOnItsOwnSchedule : PhaseRetries));

                await Task.WhenAll(importTasks);

                // Recorded whatever else failed, so the next cycles know an empty table is the tenant's privacy
                // setting at work, not a fresh database; and cleared by a run that refused nothing.
                await RecordConcealedIdentityRefusalAsync(concealedReports.Count > 0, MIN_WAIT);

                // Remember last import date so the next cycle within the throttle window is skipped - but
                // ONLY when every daily report actually completed. The timestamp is the proof that the phase
                // finished, so stamping it after a failure is what used to make a broken import look
                // healthy and idle for 24 hours (issue #285). Anything that downloaded successfully has
                // already been saved above, so a retry next cycle is cheap and picks up only what is missing.
                if (failedReports.Count > 0)
                {
                    var everyFailure = failedReports.Concat(concealedReports).Concat(weeklyHasOwnSchedule ? weeklyFailures : Enumerable.Empty<string>()).ToList();
                    _logger.LogError($"Activity reports did NOT fully import - {everyFailure.Count} of {importTasks.Count} report(s) failed: " +
                        string.Join(", ", everyFailure.OrderBy(r => r)) + ". " +
                        "Reports that succeeded have been saved AND stamped complete, so on the next cycle they re-import only the most recent " +
                        "days that Graph can still change - the failed report(s) retry their own full window. This phase has NOT been marked " +
                        "complete, so the once-a-day throttle stays disarmed and it re-runs every cycle until every report succeeds. " +
                        "If this repeats, check the runtime account has the Reports.Read.All application permission granted and admin-consented.");
                    return false;
                }

                if (lastImportedStore != null)
                {
                    await lastImportedStore.SaveDT();
                }

                if (concealedReports.Count > 0 || weeklyFailures.Count > 0)
                {
                    _logger.LogError($"Activity reports did NOT fully import - {concealedReports.Count + weeklyFailures.Count} of {importTasks.Count} report(s) failed: " +
                        string.Join(", ", concealedReports.Concat(weeklyFailures).OrderBy(r => r)) + ". " +
                        "Every other report has been saved and stamped complete, and the phase runs again in " +
                        $"{MIN_WAIT.TotalHours} hours. " +
                        (concealedReports.Count > 0
                            ? "Reports refused for concealed identities import nothing until 'Display concealed user, group, and site names in all reports' " +
                              "is turned off in the Microsoft 365 admin centre (Settings > Org settings > Services > Reports). "
                            : string.Empty) +
                        (weeklyFailures.Count > 0 ? WeeklyRetriesOnItsOwnSchedule : string.Empty));
                    return false;
                }

                _logger.LogInformation($"Activity reports imported. Will run again in {MIN_WAIT.TotalHours} hours");
                return true;
            }
            else
            {
                _logger.LogInformation($"Skipping activity reports as have processed recently (less than {MIN_WAIT.TotalHours} hours ago). " +
                    $"Will import again after {ActivityReportsCadenceGate.NextRunUtc(lastImportedDate.Value, MIN_WAIT).ToLocalTime()}.");

                // Between the daily runs, only the weekly report can be due - on its own schedule. Quiet when it is not,
                // which is the normal answer on almost every cycle.
                if (weeklyHasOwnSchedule)
                {
                    try
                    {
                        await withWeeklyLoader(loader => RunWeeklyReportIfDueAsync(loader, MIN_WAIT, logWhenNotDue: false));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"{WeeklyReportName} failed: {ex.Message}. The daily activity reports are unaffected. " + WeeklyRetriesOnItsOwnSchedule);
                    }
                }
                return false;
            }
        }

        private const string WeeklyReportName = "SharePoint sites weekly usage";

        /// <summary>
        /// Schedule-store key holding until when the last phase run's refusal of a report for concealed identities
        /// stands (one phase interval). Not a loader type name, so it cannot collide with a report's own schedule.
        /// </summary>
        internal const string ConcealedIdentityRefusalKey = "ConcealedIdentityRefusal";

        private async Task<bool> IsConcealedIdentityRefusalCurrentAsync()
        {
            if (!(_reportCompletionStore is IReportAttemptScheduleStore scheduleStore)) return false;

            try
            {
                var standsUntilUtc = await scheduleStore.GetNextAttemptUtcAsync(ConcealedIdentityRefusalKey);
                return standsUntilUtc.HasValue && _clock.UtcNow < standsUntilUtc.Value.ToUniversalTime();
            }
            catch (Exception ex)
            {
                // Fail open to the long-standing behaviour: the empty-table check runs as it always did.
                _logger.LogWarning($"Could not read whether usage reports were refused for concealed identities ({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

        private async Task RecordConcealedIdentityRefusalAsync(bool refused, TimeSpan standsFor)
        {
            if (!(_reportCompletionStore is IReportAttemptScheduleStore scheduleStore)) return;

            try
            {
                if (refused)
                {
                    await scheduleStore.SaveNextAttemptUtcAsync(ConcealedIdentityRefusalKey, DateTime.SpecifyKind(_clock.UtcNow.Add(standsFor), DateTimeKind.Utc));
                }
                else
                {
                    await scheduleStore.ClearNextAttemptUtcAsync(ConcealedIdentityRefusalKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not record whether usage reports were refused for concealed identities ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private static readonly string WeeklyRetriesOnItsOwnSchedule =
            $"The SharePoint sites weekly usage report retries on its own schedule, in {WeeklyReportNoSundayBackoff.TotalMinutes} minutes, " +
            "whether or not the daily reports are due.";

        internal async Task<int> LoadAndSaveDailyImportReport<TReportDbType, TUserActivityUserDetail, TLookupType, CACHETYPE>
            (AbstractDailyActivityLoader<TReportDbType, TUserActivityUserDetail, TLookupType, CACHETYPE> abstractActivityLoader,
            int daysBackMax, string thingWeAreImporting, ILogger logger, ConcurrentLookupDbIdsCache userEmailToDbIdCache,
            DateTime? lastSuccessfulPhaseImport)
            where TReportDbType : AbstractUsageActivityLog, new()
            where TUserActivityUserDetail : AbstractActivityRecord<TLookupType>
            where TLookupType : AbstractEFEntity
            where CACHETYPE : DBLookupCache<TLookupType>
        {
            logger.LogInformation($"Importing {thingWeAreImporting} reports...");

            var instrumentation = abstractActivityLoader.SaveInstrumentation ?? NullUsageReportSaveInstrumentation.Instance;
            var instrumentationEnabled = instrumentation.IsEnabled;
            var activeLoaderCountAtStart = 0;
            var phaseWatch = instrumentationEnabled ? Stopwatch.StartNew() : null;
            var reportId = abstractActivityLoader.GetType().Name;
            if (instrumentationEnabled)
            {
                activeLoaderCountAtStart = UsageReportSaveInstrumentationRuntime.IncrementActiveDailyLoaders();
                TrackDailyReportPhaseStage(instrumentation, UsageReportSaveStageIds.ReportPhaseStarted, reportId, thingWeAreImporting, "Started", m =>
                {
                    m["ActiveLoaderCount"] = activeLoaderCountAtStart;
                    m["DaysBackMax"] = daysBackMax;
                    UsageReportSaveInstrumentationRuntime.AddRuntimeMetrics(m);
                });
            }

            try
            {
                var reportKey = GetReportKey(abstractActivityLoader.GetType());
                var lastSuccessfulImport = await ResolveReportSkipListInputAsync(reportKey, lastSuccessfulPhaseImport);

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

                if (_reportCompletionStore != null)
                {
                    // Clear before any writes, for the same reason the phase marker is cleared: if this report
                    // fails part-way through its save, the next cycle must re-import rather than trust a stamp
                    // that claims a window it only partly wrote. Not before the download, though: a download that
                    // fails has written nothing, so the previous stamp - and with it this report's finalized-date
                    // skip list - still holds, and the retry fetches only what is missing instead of the whole window.
                    await _reportCompletionStore.ClearAsync(reportKey);
                }

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

                // Only reached when the download AND the save both completed - anything else threw and is caught
                // by RunReportSafely. A failed save leaves this report's stamp cleared, so its window is retried next
                // cycle; a failed download leaves the previous stamp in place.
                if (_reportCompletionStore != null)
                {
                    await _reportCompletionStore.SaveSuccessAsync(reportKey);
                }

                if (instrumentationEnabled)
                {
                    phaseWatch.Stop();
                    TrackDailyReportPhaseStage(instrumentation, UsageReportSaveStageIds.ReportPhaseCompleted, reportId, thingWeAreImporting, "Completed", m =>
                    {
                        m["DurationMs"] = phaseWatch.ElapsedMilliseconds;
                        m["GraphRowCount"] = total;
                        m["DbWriteCount"] = abstractActivityLoader.LastSaveDbWriteCount;
                        // The completed event repeats the concurrency observed when this loader started, not
                        // a racy post-completion snapshot that depends on which sibling reports finish first.
                        m["ActiveLoaderCount"] = activeLoaderCountAtStart;
                        UsageReportSaveInstrumentationRuntime.AddRuntimeMetrics(m);
                    });
                }

                return total;
            }
            catch (Exception ex)
            {
                if (instrumentationEnabled)
                {
                    phaseWatch.Stop();
                    TrackDailyReportPhaseStage(instrumentation, UsageReportSaveStageIds.ReportPhaseFailed, reportId, thingWeAreImporting, "Failed", m =>
                    {
                        m["DurationMs"] = phaseWatch.ElapsedMilliseconds;
                        m["ActiveLoaderCount"] = activeLoaderCountAtStart;
                        UsageReportSaveInstrumentationRuntime.AddRuntimeMetrics(m);
                    }, ex.GetType().Name);
                }
                throw;
            }
            finally
            {
                if (instrumentationEnabled)
                {
                    UsageReportSaveInstrumentationRuntime.DecrementActiveDailyLoaders();
                }
            }
        }

        private static void TrackDailyReportPhaseStage(
            IUsageReportSaveInstrumentation instrumentation,
            string stage,
            string reportId,
            string reportName,
            string outcome,
            Action<Dictionary<string, double>> populateMetrics,
            string exceptionType = null)
        {
            var point = new UsageReportSaveTelemetryPoint
            {
                Stage = stage,
                ReportId = reportId,
                LoaderType = reportId,
                ReportTable = reportName,
                Outcome = outcome,
                ExceptionType = exceptionType,
            };
            populateMetrics?.Invoke(point.Metrics);
            instrumentation.Track(point);
        }
    }
}
