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
using WebJob.Office365ActivityImporter.Engine.Graph.Sections;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Orchestrates the Graph import: selects the enabled sections, applies the per-section cadence gate and
    /// records the last-run time. It does not build any of them - composition lives behind
    /// <see cref="IGraphImportSectionFactory"/> (issue #376), which is what makes this loop testable with fake
    /// sections and no SQL Server, Graph, Redis or Service Bus.
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

        /// <summary>
        /// Production constructor: builds the <see cref="ProductionGraphImportSectionFactory"/> that holds all
        /// the section wiring. Kept at its original signature so no call site breaks.
        /// </summary>
        /// <param name="userGroupsCache">
        /// Unused, and was unused before this change too - it was stored in a field that nothing read. The
        /// usage-report and Copilot sections use a <c>GraphUserGroupsCache</c> the factory builds over its own
        /// <c>ManualGraphCallClient</c>, which is not the same instance the caller passes here. Kept on the
        /// signature so no call site breaks.
        /// </param>
        public GraphImporter(AnalyticsLogger logger, UserGroupsCache userGroupsCache, GraphAppIndentityOAuthContext graphAppIndentityOAuthContext, GraphServiceClient graphClient, AppConfig settings, ISingleDateStore activityReportsLastImportedStore = null, IImportLastRunStore lastRunStore = null, ISentEmailMailboxSkipList sentEmailMailboxSkipList = null, IClock clock = null)
            : this(logger, userGroupsCache, graphAppIndentityOAuthContext, graphClient, settings, activityReportsLastImportedStore, lastRunStore, sentEmailMailboxSkipList, reportCompletionStore: null, clock: clock)
        {
        }

        public GraphImporter(AnalyticsLogger logger, UserGroupsCache userGroupsCache, GraphAppIndentityOAuthContext graphAppIndentityOAuthContext, GraphServiceClient graphClient, AppConfig settings, ISingleDateStore activityReportsLastImportedStore, IImportLastRunStore lastRunStore, ISentEmailMailboxSkipList sentEmailMailboxSkipList, IClock clock, IReportCompletionStore reportCompletionStore)
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
                _clock);
        }

        /// <summary>
        /// Orchestration-only constructor: the sections are supplied, so nothing here touches Graph, SQL or
        /// Redis. Separate from the production constructor rather than another optional parameter on it,
        /// because a trailing optional argument is baked in by the calling compiler and so is binary-breaking
        /// for already-compiled callers.
        ///
        /// <b>Internal deliberately.</b> An instance built this way is composed only for
        /// <see cref="GetAndSaveAllGraphData"/>: it has no <see cref="GraphServiceClient"/> and no
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
        /// The last-run timestamp is persisted via <see cref="IImportLastRunStore"/> (Redis when
        /// configured, otherwise in-memory) so the gate survives the per-cycle recreation of this
        /// importer. An interval of 0 disables the gate (runs every cycle); <c>ForceGraphMetadataImport</c>
        /// bypasses it for one run. Redis failures are fail-open (the section still runs).
        ///
        /// The section reports success itself instead of throwing. Returning false records the section as not
        /// done, so the cadence gate lets it retry next cycle, without an exception unwinding out of
        /// <see cref="GetAndSaveAllGraphData"/> and skipping the sections that come after it.
        /// </summary>
        private async Task RunGraphSectionIfDueAsync(string key, int intervalHours, string sectionName, Func<Task<bool>> sectionWork)
        {
            var force = _settings.ForceGraphMetadataImport;
            var lastRun = await _lastRunStore.GetLastRunUtc(key);

            if (!ImportCadenceGate.ShouldRun(lastRun, intervalHours, force, _clock.UtcNow))
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
        /// Main entry-point. Runs every enabled section in factory order.
        ///
        /// A section that returns false is recorded as not done and the following sections still run. A
        /// section that <b>throws</b> unwinds out of here and the sections after it are skipped for this
        /// cycle - that is the long-standing behaviour and is left unchanged: adding per-section exception
        /// isolation would silently convert a hard failure into a logged warning, which is a behavioural
        /// change and belongs in its own issue.
        /// </summary>
        public async Task GetAndSaveAllGraphData(AppConfig settings)
        {
            foreach (var section in _sectionFactory.CreateSections(settings))
            {
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
        /// marker is an unversioned Redis key that predates the strict-paging fixes (#285 / #310); an older
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

            var lastSuccess = await _reportCompletionStore.GetLastSuccessAsync(reportKey);
            return lastSuccess == null || _clock.UtcNow.Subtract(lastSuccess.Value.ToUniversalTime()) > minWait;
        }

        private async Task RunWeeklyReportIfDueAsync(SharePointSitesWeeklyUsageReportLoader loader, TimeSpan minWait)
        {
            var reportKey = GetReportKey(loader.GetType());

            if (!await IsReportDueAsync(reportKey, minWait))
            {
                _logger.LogInformation(
                    "SharePoint sites weekly usage: skipping - it completed within the last " +
                    $"{minWait.TotalHours} hours. It has no finalized-date skip list, so re-running it while " +
                    "another report retries would re-download the whole report for nothing.");
                return;
            }

            if (_reportCompletionStore != null)
            {
                await _reportCompletionStore.ClearAsync(reportKey);
            }

            await loader.LoadAndSaveLastWeeksReportsIfRefreshOnDay(System.DayOfWeek.Sunday);

            if (_reportCompletionStore != null)
            {
                await _reportCompletionStore.SaveSuccessAsync(reportKey);
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

                    importTasks.Add(RunReportSafely("SharePoint sites weekly usage",
                        () => RunWeeklyReportIfDueAsync(sharePointSitesWeeklyUsageReportLoader, MIN_WAIT)));
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

                _logger.LogInformation($"Activity reports imported. Will run again in {MIN_WAIT.TotalHours} hours");
                return true;
            }
            else
            {
                _logger.LogInformation($"Skipping activity reports as have processed recently (less than {MIN_WAIT.TotalHours} hours ago). " +
                    $"Will import again after {ActivityReportsCadenceGate.NextRunUtc(lastImportedDate.Value, MIN_WAIT).ToLocalTime()}.");
                return false;
            }
        }

        async Task<int> LoadAndSaveDailyImportReport<TReportDbType, TUserActivityUserDetail, TLookupType, CACHETYPE>
            (AbstractDailyActivityLoader<TReportDbType, TUserActivityUserDetail, TLookupType, CACHETYPE> abstractActivityLoader,
            int daysBackMax, string thingWeAreImporting, ILogger logger, ConcurrentLookupDbIdsCache userEmailToDbIdCache,
            DateTime? lastSuccessfulPhaseImport)
            where TReportDbType : AbstractUsageActivityLog, new()
            where TUserActivityUserDetail : AbstractActivityRecord<TLookupType>
            where TLookupType : AbstractEFEntity
            where CACHETYPE : DBLookupCache<TLookupType>
        {
            logger.LogInformation($"Importing {thingWeAreImporting} reports...");

            var reportKey = GetReportKey(abstractActivityLoader.GetType());
            var lastSuccessfulImport = await ResolveReportSkipListInputAsync(reportKey, lastSuccessfulPhaseImport);

            if (_reportCompletionStore != null)
            {
                // Clear before any writes, for the same reason the phase marker is cleared: if this report
                // fails part-way through its save, the next cycle must re-import rather than trust a stamp
                // that claims a window it only partly wrote.
                await _reportCompletionStore.ClearAsync(reportKey);
            }

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

            // Only reached when the download AND the save both completed - anything else threw and is caught
            // by RunReportSafely, leaving this report's stamp cleared so its window is retried next cycle.
            if (_reportCompletionStore != null)
            {
                await _reportCompletionStore.SaveSuccessAsync(reportKey);
            }

            return total;
        }
    }
}
