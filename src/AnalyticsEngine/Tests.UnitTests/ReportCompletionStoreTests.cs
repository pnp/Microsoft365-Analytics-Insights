using Common.Entities.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Rules;

namespace Tests.UnitTests
{
    /// <summary>
    /// Per-report completion stamps (issue #311).
    /// </summary>
    /// <remarks>
    /// The phase-level timestamp had to mean two things at once: "don't re-run this once-a-day phase yet" and
    /// "these stored days are proven complete, so the finalized-date skip list can skip them". Withholding it
    /// after a failure is right for the first (issue #285) and wrong for the second - it also emptied the skip
    /// list for the ten reports that had succeeded, so one permanently failing report made all eleven
    /// re-download their full window every cycle, roughly 10x the intended steady-state Graph volume.
    /// </remarks>
    [TestClass]
    public class ReportCompletionStoreTests
    {
        private static readonly string ReportA = GraphImporter.GetReportKey(typeof(TeamsUserUsageLoader));
        private static readonly string ReportB = GraphImporter.GetReportKey(typeof(OutlookUserActivityLoader));

        /// <summary>
        /// A GraphImporter wired to a real completion store, so the production decision methods are exercised
        /// rather than re-implemented in the test.
        /// </summary>
        private static GraphImporter NewImporter(IReportCompletionStore store, bool forceImport = false, DataUtils.IClock clock = null,
            ISingleDateStore phaseStore = null)
        {
            var settings = new AppConfig { ForceUsageReportsImport = forceImport };

            return new GraphImporter(
                DataUtils.AnalyticsLogger.ConsoleOnlyTracer(),
                userScopeProvider: null,
                graphAppIndentityOAuthContext: null,
                graphClient: null,
                settings: settings,
                activityReportsLastImportedStore: phaseStore,
                lastRunStore: null,
                sentEmailMailboxSkipList: null,
                reportCompletionStore: store,
                clock: clock);
        }

        #region The store itself

        [TestMethod]
        public async Task AReportThatHasNeverRunHasNoCompletionStamp()
        {
            var store = new InMemoryReportCompletionStore();

            Assert.IsNull(await store.GetLastSuccessAsync(ReportA));
        }

        [TestMethod]
        public async Task OneReportSucceedingDoesNotStampAnother()
        {
            var store = new InMemoryReportCompletionStore();

            await store.SaveSuccessAsync(ReportA);

            Assert.IsNotNull(await store.GetLastSuccessAsync(ReportA));
            Assert.IsNull(await store.GetLastSuccessAsync(ReportB),
                "A report that has not completed must not inherit another report's completion.");
        }

        [TestMethod]
        public async Task ClearingOneReportLeavesTheOthersIntact()
        {
            var store = new InMemoryReportCompletionStore();
            await store.SaveSuccessAsync(ReportA);
            await store.SaveSuccessAsync(ReportB);

            await store.ClearAsync(ReportA);

            Assert.IsNull(await store.GetLastSuccessAsync(ReportA));
            Assert.IsNotNull(await store.GetLastSuccessAsync(ReportB));
        }

        [TestMethod]
        public async Task ReportKeysAreCaseInsensitive()
        {
            var store = new InMemoryReportCompletionStore();
            await store.SaveSuccessAsync(ReportA);

            Assert.IsNotNull(await store.GetLastSuccessAsync(ReportA.ToUpperInvariant()));
        }

        [TestMethod]
        public void ConcurrentStampsFromParallelReportsAllLand()
        {
            // The reports genuinely run in parallel under Task.WhenAll. The writes must hit the dictionary at
            // the same moment or this would pass even with a plain Dictionary - hence real threads released
            // together by a barrier, rather than awaiting already-completed tasks in sequence.
            //
            // Dedicated threads, not the thread pool: a barrier needs every participant running at once, and
            // the pool injects threads roughly one per second beyond its initial burst, which turned this into
            // a 20-second test.
            var store = new InMemoryReportCompletionStore();
            var keys = Enumerable.Range(0, 16).Select(i => $"Report{i}").ToList();

            using (var startLine = new Barrier(keys.Count))
            {
                var writers = keys.Select(k => new Thread(() =>
                {
                    startLine.SignalAndWait();
                    store.SaveSuccessAsync(k).GetAwaiter().GetResult();
                })
                { IsBackground = true }).ToList();

                writers.ForEach(t => t.Start());
                foreach (var t in writers)
                {
                    Assert.IsTrue(t.Join(TimeSpan.FromSeconds(30)), "Concurrent stamping deadlocked.");
                }
            }

            foreach (var key in keys)
            {
                Assert.IsNotNull(store.GetLastSuccessAsync(key).GetAwaiter().GetResult(), $"Lost the stamp for {key}.");
            }
        }

        [TestMethod]
        public void ReportKeysComeFromTheLoaderTypeNotTheDisplayLabel()
        {
            // Deriving the key from the human-readable label would mean that editing a log message orphaned
            // every customer's stamp and silently triggered one full re-download.
            Assert.AreEqual(nameof(TeamsUserUsageLoader), GraphImporter.GetReportKey(typeof(TeamsUserUsageLoader)));
            Assert.AreNotEqual(
                GraphImporter.GetReportKey(typeof(TeamsUserUsageLoader)),
                GraphImporter.GetReportKey(typeof(TeamsUserDeviceLoader)),
                "Two loaders sharing a key would share one stamp, so one failing would suppress the other's re-import.");
        }

        [TestMethod]
        public void PerReportKeysAreNamespacedAwayFromThePhaseLevelMarker()
        {
            // The phase marker lives at "UserActivityLastImported". A per-report key must not be able to
            // collide with it, or a report's stamp would disarm the once-a-day throttle (or vice versa).
            const string phaseKey = ActivityReportsLastImportedStoreFactory.UserActivityLastImportedKey;
            var fullKey = PersistedReportCompletionStore.KeyPrefix + ReportA;

            Assert.AreEqual("UserActivityLastImported", phaseKey, "Operators clear this row by name - keep it stable.");
            Assert.IsFalse(fullKey.Equals(phaseKey, StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public async Task PersistedStore_RoundTripsCompletionAndNextAttemptPerReport_InTheSameStateStore()
        {
            // One ImportSchedule partition holds the phase marker and every per-report stamp; each report's keys
            // must be independent of the other's, and a next-attempt time must come back as UTC.
            var state = new Common.Entities.State.InMemoryKeyValueStore();
            var store = new PersistedReportCompletionStore(state);

            Assert.IsNull(await store.GetLastSuccessAsync(ReportA));
            await store.SaveSuccessAsync(ReportA);
            Assert.IsNotNull(await store.GetLastSuccessAsync(ReportA));
            Assert.IsNull(await store.GetLastSuccessAsync(ReportB), "One report's stamp must not answer for another.");

            var nextAttempt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            await store.SaveNextAttemptUtcAsync(ReportB, nextAttempt);
            var readBack = await store.GetNextAttemptUtcAsync(ReportB);
            Assert.AreEqual(nextAttempt, readBack.Value.ToUniversalTime());
            Assert.IsNull(await store.GetNextAttemptUtcAsync(ReportA));

            await store.ClearAsync(ReportA);
            await store.ClearNextAttemptUtcAsync(ReportB);
            Assert.IsNull(await store.GetLastSuccessAsync(ReportA));
            Assert.IsNull(await store.GetNextAttemptUtcAsync(ReportB));
            Assert.AreEqual(0, state.Count, "Clearing must delete the rows, not leave empty ones behind.");
        }

        #endregion

        #region ResolveReportSkipListInputAsync - what feeds the finalized-date skip list

        [TestMethod]
        public async Task AFailingReportNoLongerEmptiesTheSkipListOfTheReportsThatSucceeded()
        {
            // The #311 scenario, against the real production method. Report B fails permanently, so the PHASE
            // marker is withheld (null) - which is correct and must stay that way (#285). Report A succeeded
            // last cycle and must still skip its finalized days rather than re-downloading the full window.
            var store = new InMemoryReportCompletionStore();
            await store.SaveSuccessAsync(ReportA);
            var importer = NewImporter(store);

            DateTime? withheldPhaseMarker = null;

            Assert.IsNotNull(await importer.ResolveReportSkipListInputAsync(ReportA, withheldPhaseMarker),
                "The succeeding report must keep its skip list even while another report is broken.");

            Assert.IsNull(await importer.ResolveReportSkipListInputAsync(ReportB, withheldPhaseMarker),
                "The failing report must still re-import its own full window.");
        }

        [TestMethod]
        public async Task TheLegacyPhaseMarkerIsNeverTrustedAsAPerReportCompletion()
        {
            // The phase marker is an unversioned key predating the strict-paging fixes (#285 / #310): an
            // older build could write it after a report had saved a PARTIAL day. Skipping a partially-stored
            // date loses those rows for good once Graph's ~28-day retention passes, so one extra full download
            // per report on the first upgraded cycle is the right trade.
            var store = new InMemoryReportCompletionStore();
            var importer = NewImporter(store);

            Assert.IsNull(
                await importer.ResolveReportSkipListInputAsync(ReportA, DateTime.Now.AddHours(-2)),
                "A report with no stamp of its own must re-import its full window, not inherit the phase marker.");
        }

        [TestMethod]
        public async Task WithNoCompletionStoreTheOldPhaseMarkerBehaviourIsUnchanged()
        {
            // Callers that supply no store (unit tests, and any embedder) must behave exactly as before.
            var importer = NewImporter(store: null);
            var phaseMarker = DateTime.Now.AddHours(-2);

            Assert.AreEqual(phaseMarker, await importer.ResolveReportSkipListInputAsync(ReportA, phaseMarker));
        }

        [TestMethod]
        public async Task ClearingBeforeARunMeansACrashMidSaveReImportsThatReportOnly()
        {
            // Stamps are cleared before the report saves, so a crash part-way through the save cannot leave a stamp
            // claiming a window that was only partly written.
            var store = new InMemoryReportCompletionStore();
            await store.SaveSuccessAsync(ReportA);
            await store.SaveSuccessAsync(ReportB);
            var importer = NewImporter(store);

            // Report A starts, clears its stamp, then crashes before SaveSuccessAsync.
            await store.ClearAsync(ReportA);

            Assert.IsNull(await importer.ResolveReportSkipListInputAsync(ReportA, null),
                "A report that crashed mid-save must re-import its full window.");
            Assert.IsNotNull(await importer.ResolveReportSkipListInputAsync(ReportB, null),
                "The crash must not cost any other report its skip list.");
        }

        #endregion

        #region IsReportDueAsync - the weekly report's independent cadence

        private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

        [TestMethod]
        public async Task AReportThatHasNeverSucceededIsDue()
        {
            var importer = NewImporter(new InMemoryReportCompletionStore());

            Assert.IsTrue(await importer.IsReportDueAsync(ReportA, OneDay));
        }

        [TestMethod]
        public async Task AReportThatSucceededRecentlyIsNotDueEvenWhileAnotherReportRetries()
        {
            // The weekly SharePoint sites report pages the WHOLE report from Graph before it checks what is
            // already stored, so with the phase throttle disarmed by another report's failure it would
            // re-download in full every cycle - exactly the waste #311 is about.
            var store = new InMemoryReportCompletionStore();
            await store.SaveSuccessAsync(ReportA);
            var importer = NewImporter(store);

            Assert.IsFalse(await importer.IsReportDueAsync(ReportA, OneDay));
        }

        [TestMethod]
        public async Task WeeklyReportWithNoSundayRefreshGetsShortBackoffButNoDataSuccessStamp()
        {
            var store = new InMemoryReportCompletionStore();
            var clock = new FixedClock(new DateTime(2026, 9, 10, 4, 0, 0, DateTimeKind.Utc));
            var importer = NewImporter(store, clock: clock);
            var loader = TestSharePointSitesWeeklyUsageReportLoader.WithSingleReport(
                new DateTime(2024, 2, 26),
                "https://contoso.sharepoint.com/sites/non-sunday");
            var reportKey = GraphImporter.GetReportKey(loader.GetType());

            await importer.RunWeeklyReportIfDueAsync(loader, OneDay);

            Assert.AreEqual(1, loader.LoadCount, "The first due run should download the weekly report.");
            Assert.AreEqual(1, loader.LastSaveResult.NotRefreshedOnDay,
                "The loader should expose that it saw data, but not for the required Sunday refresh day.");
            Assert.IsNull(await store.GetLastSuccessAsync(reportKey),
                "A non-Sunday Graph refresh date is not a data success and must not feed the 24-hour success stamp.");

            clock.Advance(TimeSpan.FromMinutes(30));
            await importer.RunWeeklyReportIfDueAsync(loader, OneDay);
            Assert.AreEqual(1, loader.LoadCount,
                "The short backoff must prevent a full re-download on every import cycle while Graph has not published Sunday data.");

            clock.Advance(TimeSpan.FromMinutes(31));
            Assert.IsTrue(await importer.IsReportDueAsync(reportKey, OneDay),
                "The non-Sunday backoff is deliberately short so the importer can retry the same day.");
        }

        [TestMethod]
        public async Task WeeklyReportAlreadyStoredSundayStillGetsFullSuccessCadence()
        {
            var store = new InMemoryReportCompletionStore();
            var clock = new FixedClock(new DateTime(2026, 9, 10, 4, 0, 0, DateTimeKind.Utc));
            var importer = NewImporter(store, clock: clock);
            var sunday = new DateTime(2024, 2, 25);
            var siteUrl = "https://contoso.sharepoint.com/sites/already-current";
            var loader = TestSharePointSitesWeeklyUsageReportLoader.WithSingleReport(sunday, siteUrl);
            loader.Store.SeedWeek(loader.Store.SeedSite(siteUrl), sunday);
            var reportKey = GraphImporter.GetReportKey(loader.GetType());

            await importer.RunWeeklyReportIfDueAsync(loader, OneDay);

            Assert.AreEqual(0, loader.LastSaveResult.ItemsSaved,
                "The row is already stored, so a naive itemsSaved > 0 success check would fail this case.");
            Assert.AreEqual(1, loader.LastSaveResult.AlreadyUpToDate);
            Assert.IsTrue(loader.LastSaveResult.ObservedRefreshDayReport);
            Assert.IsNotNull(await store.GetLastSuccessAsync(reportKey),
                "Observing an already-stored Sunday report is still a data success.");

            clock.Advance(TimeSpan.FromHours(23).Add(TimeSpan.FromMinutes(59)));
            Assert.IsFalse(await importer.IsReportDueAsync(reportKey, OneDay),
                "A Sunday observation receives the full daily cadence, not the short non-Sunday backoff.");
        }

        [TestMethod]
        public async Task WeeklyReportDailyCadenceAnchorsToPreviousDueTimeInsteadOfCompletionTime()
        {
            var store = new InMemoryReportCompletionStore();
            var clock = new FixedClock(new DateTime(2026, 9, 10, 4, 0, 0, DateTimeKind.Utc));
            var importer = NewImporter(store, clock: clock);
            var loader = TestSharePointSitesWeeklyUsageReportLoader.WithSingleReport(
                new DateTime(2024, 2, 25),
                "https://contoso.sharepoint.com/sites/anti-drift");
            var reportKey = GraphImporter.GetReportKey(loader.GetType());

            await importer.RunWeeklyReportIfDueAsync(loader, OneDay);

            clock.Advance(OneDay.Add(TimeSpan.FromMinutes(5)));
            await importer.RunWeeklyReportIfDueAsync(loader, OneDay);

            clock.Advance(OneDay.Subtract(TimeSpan.FromMinutes(4)));

            Assert.IsTrue(await importer.IsReportDueAsync(reportKey, OneDay),
                "The next due time should remain anchored at the original daily schedule. If completion time " +
                "were used, this check one minute after the original due time would still be throttled.");
        }

        [TestMethod]
        public async Task WeeklyReportRetriesOnItsOwnScheduleWhileTheDailyReportsWaitOutTheirDay()
        {
            // Driven through the phase orchestration, not RunWeeklyReportIfDueAsync: the one-hour retry used to sit
            // behind the daily phase's 24-hour gate, so it could only run a day later - by when Sunday's report, which
            // Graph offers for about a day, may be gone for good. Monday 04:00 UTC; the newest refresh is Saturday's.
            var clock = new FixedClock(new DateTime(2026, 9, 14, 4, 0, 0, DateTimeKind.Utc));
            var completion = new InMemoryReportCompletionStore();
            var importer = NewImporter(completion, clock: clock, phaseStore: new ClockedSingleDateStore(clock));
            var weekly = TestSharePointSitesWeeklyUsageReportLoader.WithSingleReport(
                new DateTime(2026, 9, 12), "https://contoso.sharepoint.com/sites/weekly");
            var dailyRuns = 0;

            Task<bool> RunPhase() => importer.RunActivityReportPhaseAsync(25,
                () => Task.FromResult(true),
                (lookupIdCache, lastImported) => new[]
                {
                    new GraphImporter.ActivityReportRun("Teams user activity", () => { dailyRuns++; return Task.CompletedTask; }),
                },
                run => run(weekly));

            Assert.IsTrue(await RunPhase(), "The daily reports completed, and a Sunday report not yet published is not a failure.");
            Assert.AreEqual(1, dailyRuns);
            Assert.AreEqual(1, weekly.LoadCount);

            clock.Advance(TimeSpan.FromMinutes(30));
            await RunPhase();
            Assert.AreEqual(1, dailyRuns, "The daily reports are inside their 24-hour gate.");
            Assert.AreEqual(1, weekly.LoadCount, "The weekly report is inside its one-hour retry.");

            clock.Advance(TimeSpan.FromMinutes(31));
            Assert.IsFalse(await RunPhase(), "The daily phase did not run, so the phase is not reported finished.");
            Assert.AreEqual(1, dailyRuns, "Retrying the weekly report must not make the daily reports hourly.");
            Assert.AreEqual(2, weekly.LoadCount, "The weekly report retries after its own hour, although the daily phase is still gated.");

            // Graph publishes Sunday's report; the next hourly retry stores it.
            weekly.Report = new SharePointSiteUsageDetail { SiteUrl = "https://contoso.sharepoint.com/sites/weekly", ReportRefreshDate = new DateTime(2026, 9, 13), FileCount = 1 };
            clock.Advance(TimeSpan.FromMinutes(61));
            await RunPhase();
            Assert.AreEqual(3, weekly.LoadCount);
            Assert.AreEqual(1, weekly.Store.WeeklyStats.Count, "Sunday's report is stored.");
            Assert.IsNotNull(await completion.GetLastSuccessAsync(GraphImporter.GetReportKey(weekly.GetType())));
            Assert.AreEqual(1, dailyRuns);
        }

        [TestMethod]
        public async Task WeeklyReportSeenMidWeekWaitsForTheNextSundayRatherThanRetryingHourly()
        {
            // Wednesday, and Graph's newest refresh is Monday's: next Sunday's report cannot exist until Sunday has
            // ended. Re-downloading the whole sites report every hour until then would be six days of waste a week.
            var clock = new FixedClock(new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc));
            var completion = new InMemoryReportCompletionStore();
            var importer = NewImporter(completion, clock: clock);
            var weekly = TestSharePointSitesWeeklyUsageReportLoader.WithSingleReport(
                new DateTime(2026, 9, 7), "https://contoso.sharepoint.com/sites/mid-week");
            var reportKey = GraphImporter.GetReportKey(weekly.GetType());

            await importer.RunWeeklyReportIfDueAsync(weekly, OneDay);

            Assert.AreEqual(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc), await completion.GetNextAttemptUtcAsync(reportKey),
                "The next attempt is the start of the Monday after the next Sunday.");
            clock.Advance(TimeSpan.FromDays(4));
            Assert.IsFalse(await importer.IsReportDueAsync(reportKey, OneDay), "Sunday 10:00: its report cannot be published yet.");
            clock.Advance(TimeSpan.FromHours(14));
            Assert.IsTrue(await importer.IsReportDueAsync(reportKey, OneDay), "Monday 00:00: from now on it is retried hourly.");
        }

        [TestMethod]
        public async Task WeeklyReportFailureNeitherBlocksTheDailyStampNorRetriesEveryCycle()
        {
            var clock = new FixedClock(new DateTime(2026, 9, 14, 4, 0, 0, DateTimeKind.Utc));
            var phaseStore = new ClockedSingleDateStore(clock);
            var importer = NewImporter(new InMemoryReportCompletionStore(), clock: clock, phaseStore: phaseStore);
            var weekly = TestSharePointSitesWeeklyUsageReportLoader.WithSingleReport(
                new DateTime(2026, 9, 13), "https://contoso.sharepoint.com/sites/failing");
            weekly.Failure = new InvalidOperationException("Synthetic weekly report failure.");
            var dailyRuns = 0;

            Task<bool> RunPhase() => importer.RunActivityReportPhaseAsync(25,
                () => Task.FromResult(true),
                (lookupIdCache, lastImported) => new[]
                {
                    new GraphImporter.ActivityReportRun("Teams user activity", () => { dailyRuns++; return Task.CompletedTask; }),
                },
                run => run(weekly));

            Assert.IsFalse(await RunPhase(), "A failed report means the phase did not fully import.");
            Assert.IsNotNull(await phaseStore.GetLastDT(), "The daily reports all succeeded, so they are stamped and wait out their day.");

            clock.Advance(TimeSpan.FromMinutes(20));
            await RunPhase();
            Assert.AreEqual(1, dailyRuns, "A weekly failure must not make the daily reports re-run every cycle.");
            Assert.AreEqual(1, weekly.LoadCount, "Nor is the failed weekly report retried on every cycle.");

            weekly.Failure = null;
            clock.Advance(TimeSpan.FromMinutes(41));
            await RunPhase();
            Assert.AreEqual(2, weekly.LoadCount, "It retries after its own hour.");
            Assert.AreEqual(1, weekly.Store.WeeklyStats.Count);
            Assert.AreEqual(1, dailyRuns);
        }

        [TestMethod]
        public async Task AReportRefusedForConcealedIdentitiesDoesNotMakeThePhaseRunEveryCycle_ThoughNothingIsStored()
        {
            // Retrying cannot succeed until an administrator changes the tenant's report-privacy setting, so a refused
            // report fails the phase (no "finished" event) without disarming the once-a-day throttle. Such a tenant never
            // stores a row, so the usage table stays empty - and the "fresh or wiped database" reset must not take that
            // for a reason to run the whole phase, every report downloading a day, again on every cycle.
            var clock = new FixedClock(new DateTime(2026, 9, 14, 4, 0, 0, DateTimeKind.Utc));
            var phaseStore = new ClockedSingleDateStore(clock);
            var importer = NewImporter(new InMemoryReportCompletionStore(), clock: clock, phaseStore: phaseStore);
            var attempts = 0;
            var concealed = true;

            Task<bool> RunPhase() => importer.RunActivityReportPhaseAsync(25,
                () => Task.FromResult(false),
                (lookupIdCache, lastImported) => new[]
                {
                    new GraphImporter.ActivityReportRun("Teams user activity", () =>
                    {
                        attempts++;
                        if (concealed) throw new ConcealedUsageReportException("TeamsUserUsageLoader", 3);
                        return Task.CompletedTask;
                    }),
                },
                run => Task.CompletedTask);

            Assert.IsFalse(await RunPhase(), "A refused report means the phase did not fully import.");
            Assert.IsNotNull(await phaseStore.GetLastDT());

            clock.Advance(TimeSpan.FromMinutes(20));
            await RunPhase();
            Assert.AreEqual(1, attempts, "Tried again with the next daily run, not on every cycle - though nothing is stored.");

            clock.Advance(TimeSpan.FromHours(24));
            await RunPhase();
            Assert.AreEqual(2, attempts, "A day later it is tried again.");

            // The administrator turns concealment off: the next daily run imports and the refusal no longer stands, so an
            // empty table once again means a fresh or wiped database, imported straight away.
            concealed = false;
            clock.Advance(TimeSpan.FromHours(25));
            Assert.IsTrue(await RunPhase());
            Assert.AreEqual(3, attempts);
            clock.Advance(TimeSpan.FromMinutes(20));
            await RunPhase();
            Assert.AreEqual(4, attempts, "With no refusal standing, the empty-table reset works as it always did.");
        }

        [TestMethod]
        public async Task AFailedDownloadKeepsTheReportsCompletionStamp_AFailedSaveClearsIt()
        {
            // A failed download has written nothing, so the stamp - and the finalized-date skip list it feeds - still
            // holds. Clearing it before the download made one failed Graph call cost a download of the whole window next
            // time, all held in memory. A failed save may have written part of the window, so that stamp must go.
            var store = new InMemoryReportCompletionStore();
            var importer = NewImporter(store);
            var now = new DateTime(2026, 9, 14, 4, 0, 0, DateTimeKind.Utc);

            var failingDownload = new FailingDownloadLoader
            {
                Clock = new FixedClock(now),
                StorageInspector = new FakeUsageReportStorageInspector(),
                ReportStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>(),
            };
            var downloadKey = GraphImporter.GetReportKey(failingDownload.GetType());
            await store.SaveSuccessAsync(downloadKey);

            await Assert.ThrowsExceptionAsync<GraphHttpException>(() => importer.LoadAndSaveDailyImportReport(
                failingDownload, 3, "Synthetic report", NullLogger.Instance, new DataUtils.ConcurrentLookupDbIdsCache(), null));

            Assert.IsNotNull(await store.GetLastSuccessAsync(downloadKey), "A failed download must not cost the report its stamp.");

            var failingSave = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                Clock = new FixedClock(now),
                StorageInspector = new FakeUsageReportStorageInspector(),
                ReportStore = new ThrowOnSaveUsageReportStore(),
            };
            failingSave.PagesByDate[now.Date.AddDays(-1)] = new List<FakeUserActivityDetail>
            {
                new FakeUserActivityDetail { UserPrincipalName = "alice@contoso.com", ThingCount = 1 },
            };
            var saveKey = GraphImporter.GetReportKey(failingSave.GetType());
            await store.SaveSuccessAsync(saveKey);
            var idCache = new DataUtils.ConcurrentLookupDbIdsCache();
            idCache.AddOrUpdateForName<Common.Entities.User>("alice@contoso.com", 11);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => importer.LoadAndSaveDailyImportReport(
                failingSave, 3, "Synthetic report", NullLogger.Instance, idCache, null));

            Assert.IsNull(await store.GetLastSuccessAsync(saveKey), "A save that failed part-way must not leave a stamp claiming its window.");
        }

        [TestMethod]
        public async Task AnUnreadableWeeklyReportPageFailsThatReportOnce_TheDailyReportsAreUnaffected()
        {
            // The weekly loader used to re-request an unreadable page for ever, hanging the whole phase. It must fail
            // after one request, be recorded as failed, and leave the daily reports to run and be stamped.
            var clock = new FixedClock(new DateTime(2026, 9, 14, 4, 0, 0, DateTimeKind.Utc));
            var phaseStore = new ClockedSingleDateStore(clock);
            var importer = NewImporter(new InMemoryReportCompletionStore(), clock: clock, phaseStore: phaseStore);
            var weekly = TestSharePointSitesWeeklyUsageReportLoader.WithSingleReport(
                new DateTime(2026, 9, 13), "https://contoso.sharepoint.com/sites/unreadable");
            weekly.UnreadablePage = true;
            var dailyRuns = 0;

            var completed = await importer.RunActivityReportPhaseAsync(25,
                () => Task.FromResult(true),
                (lookupIdCache, lastImported) => new[]
                {
                    new GraphImporter.ActivityReportRun("Teams user activity", () => { dailyRuns++; return Task.CompletedTask; }),
                },
                run => run(weekly));

            Assert.IsFalse(completed, "A failed report means the phase did not fully import.");
            Assert.AreEqual(1, weekly.LoadCount, "One request, not the same URL again and again.");
            Assert.AreEqual(0, weekly.Store.WeeklyStats.Count, "Nothing from the report is saved.");
            Assert.AreEqual(1, dailyRuns);
            Assert.IsNotNull(await phaseStore.GetLastDT(), "The daily reports completed, so they are stamped as usual.");
        }

        private const string ConcealedSiteId = "00000000-0000-0000-0000-000000000000";

        private static SharePointSiteUsageDetail ConcealedSiteRow(DateTime refreshDate)
            => new SharePointSiteUsageDetail { SiteId = ConcealedSiteId, SiteUrl = "", ReportRefreshDate = refreshDate, FileCount = 1 };

        [TestMethod]
        public async Task AWeeklyReportWhoseSitesAreAllConcealed_IsNotLookedUpAndWaitsForTheNextRefreshDay()
        {
            // With "Display concealed user, group, and site names" on, every row has a blank URL and an all-zero site id.
            // Each used to cost a failing Graph site lookup, and a report with no usable row was retried every hour, for
            // good: on a 10,000-site tenant, 10,000 failing calls and logged errors and a full download, every hour.
            var clock = new FixedClock(new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc));   // a Wednesday
            var completion = new InMemoryReportCompletionStore();
            var importer = NewImporter(completion, clock: clock);
            var lookup = new CountingSiteLookup();
            var monday = new DateTime(2026, 9, 7);
            var weekly = TestSharePointSitesWeeklyUsageReportLoader.WithRows(lookup,
                ConcealedSiteRow(monday), ConcealedSiteRow(monday), ConcealedSiteRow(monday));
            var loaderLog = new RecordingLogger();
            weekly.Telemetry = loaderLog;

            await importer.RunWeeklyReportIfDueAsync(weekly, OneDay);

            Assert.AreEqual(0, lookup.Lookups.Count, "A concealed site id can never resolve, so it must not cost a Graph call.");
            Assert.AreEqual(1, loaderLog.Entries.Count(e => e.Message.Contains("concealed")), "One line a run, not one a row.");
            Assert.AreEqual(3, weekly.LastSaveResult.RowsReturned, "What Graph returned is reported, not just what could be used.");
            Assert.AreEqual(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc),
                await completion.GetNextAttemptUtcAsync(GraphImporter.GetReportKey(weekly.GetType())),
                "Rows that cannot be used wait for the next refresh day - never an hour.");
        }

        [TestMethod]
        public async Task AnAllConcealedSundayReport_IsRetriedWithinHoursWhileItCanStillBeDownloaded()
        {
            // Sunday's report can be downloaded only until Graph replaces it with Monday's. Waiting a week for the next one
            // lost that week for good - even when the admin turned concealment off within the hour of the IMPORTANT error.
            var clock = new FixedClock(new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc));   // the Tuesday after
            var completion = new InMemoryReportCompletionStore();
            var importer = NewImporter(completion, clock: clock);
            var sunday = new DateTime(2026, 9, 13);
            var weekly = TestSharePointSitesWeeklyUsageReportLoader.WithRows(new CountingSiteLookup(),
                ConcealedSiteRow(sunday), ConcealedSiteRow(sunday), ConcealedSiteRow(sunday));

            await importer.RunWeeklyReportIfDueAsync(weekly, OneDay);

            var reportKey = GraphImporter.GetReportKey(weekly.GetType());
            Assert.AreEqual(new DateTime(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc), await completion.GetNextAttemptUtcAsync(reportKey),
                "While Sunday's report can still be the newest, it is retried within hours - not 8 days later.");
            Assert.IsNull(await completion.GetLastSuccessAsync(reportKey), "Nothing was stored, so it is not a data success.");
        }

        [TestMethod]
        public async Task AnAllConcealedSundayReport_WaitsForTheNextRefreshDayOnceItCanHaveBeenReplaced()
        {
            // By the Friday, Monday's report can have been published even at Graph's slowest: retrying would only re-download
            // a stalled report, so the wait is for the next Sunday's, as before.
            var clock = new FixedClock(new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc));   // the Friday after
            var completion = new InMemoryReportCompletionStore();
            var importer = NewImporter(completion, clock: clock);
            var sunday = new DateTime(2026, 9, 13);
            var weekly = TestSharePointSitesWeeklyUsageReportLoader.WithRows(new CountingSiteLookup(),
                ConcealedSiteRow(sunday), ConcealedSiteRow(sunday), ConcealedSiteRow(sunday));

            await importer.RunWeeklyReportIfDueAsync(weekly, OneDay);

            Assert.AreEqual(new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
                await completion.GetNextAttemptUtcAsync(GraphImporter.GetReportKey(weekly.GetType())));
        }

        [TestMethod]
        public void WeeklyRetryPolicy_RetriesAnUnusableSundayReportOnlyWhileItCanStillBeTheNewest()
        {
            var sunday = new DateTime(2026, 9, 13);
            var monday = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
            var thursdayNight = new DateTime(2026, 9, 17, 23, 0, 0, DateTimeKind.Utc);
            var friday = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(friday, WeeklyUsageReportRetryPolicy.ReportReplacedByUtc(sunday),
                "Monday's report cannot be published before Tuesday, and Graph can be up to three days later than that.");
            Assert.AreEqual(monday.AddHours(4),
                WeeklyUsageReportRetryPolicy.NextAttemptAfterMissedRefreshDayUtc(monday, sunday, DayOfWeek.Sunday, noUsableRows: true));
            Assert.AreEqual(thursdayNight.AddHours(4),
                WeeklyUsageReportRetryPolicy.NextAttemptAfterMissedRefreshDayUtc(thursdayNight, sunday, DayOfWeek.Sunday, noUsableRows: true),
                "Monday's report may still be on its way.");
            Assert.AreEqual(new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
                WeeklyUsageReportRetryPolicy.NextAttemptAfterMissedRefreshDayUtc(friday, sunday, DayOfWeek.Sunday, noUsableRows: true),
                "Once it can have been replaced, the wait is for the next refresh day's report.");
            Assert.AreEqual(new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
                WeeklyUsageReportRetryPolicy.NextAttemptAfterMissedRefreshDayUtc(new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 9, 14), DayOfWeek.Sunday, noUsableRows: true),
                "Graph has already moved on to Monday's report: Sunday's is gone, whatever the time.");
        }

        [TestMethod]
        public async Task AWeeklyReportMixingConcealedAndNormalSites_SavesTheNormalOnes()
        {
            var clock = new FixedClock(new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc));   // a Tuesday
            var completion = new InMemoryReportCompletionStore();
            var importer = NewImporter(completion, clock: clock);
            var lookup = new CountingSiteLookup();
            var sunday = new DateTime(2026, 9, 13);
            var weekly = TestSharePointSitesWeeklyUsageReportLoader.WithRows(lookup,
                new SharePointSiteUsageDetail { SiteId = "11111111-1111-1111-1111-111111111111", SiteUrl = "https://contoso.sharepoint.com/sites/marketing", ReportRefreshDate = sunday, FileCount = 3 },
                ConcealedSiteRow(sunday),
                new SharePointSiteUsageDetail { SiteId = "22222222-2222-2222-2222-222222222222", SiteUrl = "https://contoso.sharepoint.com/sites/Καλημέρα", ReportRefreshDate = sunday, FileCount = 5 },
                ConcealedSiteRow(sunday));

            await importer.RunWeeklyReportIfDueAsync(weekly, OneDay);

            CollectionAssert.AreEquivalent(
                new[] { "https://contoso.sharepoint.com/sites/marketing", "https://contoso.sharepoint.com/sites/Καλημέρα" },
                weekly.Store.Sites.Select(s => s.UrlBase).ToArray());
            Assert.AreEqual(2, weekly.Store.WeeklyStats.Count);
            Assert.AreEqual(0, lookup.Lookups.Count);
            Assert.IsNotNull(await completion.GetLastSuccessAsync(GraphImporter.GetReportKey(weekly.GetType())),
                "The usable Sunday rows are a data success.");
        }

        [TestMethod]
        public async Task AFailingSiteLookupIsNotRepeatedWithinARun()
        {
            // A failed lookup is cached nowhere, so the same unresolvable id on several rows cost a failing Graph call each.
            const string unresolvable = "33333333-3333-3333-3333-333333333333";
            const string resolvable = "44444444-4444-4444-4444-444444444444";
            var lookup = new CountingSiteLookup(new Dictionary<string, string> { [resolvable] = "https://contoso.sharepoint.com/sites/resolved" });
            var sunday = new DateTime(2026, 9, 13);
            var weekly = TestSharePointSitesWeeklyUsageReportLoader.WithRows(lookup,
                new SharePointSiteUsageDetail { SiteId = unresolvable, SiteUrl = "", ReportRefreshDate = sunday },
                new SharePointSiteUsageDetail { SiteId = resolvable, SiteUrl = "", ReportRefreshDate = sunday },
                new SharePointSiteUsageDetail { SiteId = unresolvable, SiteUrl = "", ReportRefreshDate = sunday });

            var usable = (await weekly.LoadReportData()).ToList();

            CollectionAssert.AreEqual(new[] { unresolvable, resolvable }, lookup.Lookups.ToArray(), "Each id is looked up at most once a run.");
            Assert.AreEqual(1, usable.Count);
            Assert.AreEqual("https://contoso.sharepoint.com/sites/resolved", usable[0].SiteUrl);
        }

        [TestMethod]
        public async Task AWeeklyReportThatFailsToLoadStillRetriesWithinTheHour()
        {
            // A download that went wrong may well work soon - unlike rows that cannot be used.
            var clock = new FixedClock(new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc));
            var completion = new InMemoryReportCompletionStore();
            var importer = NewImporter(completion, clock: clock);
            var weekly = TestSharePointSitesWeeklyUsageReportLoader.WithSingleReport(new DateTime(2026, 9, 7), "https://contoso.sharepoint.com/sites/unreadable");
            weekly.UnreadablePage = true;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => importer.RunWeeklyReportIfDueAsync(weekly, OneDay));

            Assert.AreEqual(clock.UtcNow.AddHours(1), await completion.GetNextAttemptUtcAsync(GraphImporter.GetReportKey(weekly.GetType())));
        }

        [TestMethod]
        public void WeeklyRetryPolicy_WaitsForTheNextRefreshDayButNeverLessThanTheRetryInterval()
        {
            var wednesday = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc),
                WeeklyUsageReportRetryPolicy.NextAttemptAfterMissedRefreshDayUtc(wednesday, new DateTime(2026, 9, 7), DayOfWeek.Sunday));
            Assert.AreEqual(wednesday.AddHours(1),
                WeeklyUsageReportRetryPolicy.NextAttemptAfterMissedRefreshDayUtc(wednesday, new DateTime(2026, 9, 5), DayOfWeek.Sunday),
                "Saturday's refresh: Sunday's report may be published at any time now.");
            Assert.AreEqual(wednesday.AddHours(1),
                WeeklyUsageReportRetryPolicy.NextAttemptAfterMissedRefreshDayUtc(wednesday, null, DayOfWeek.Sunday),
                "An empty report says nothing about when the next refresh falls.");
            Assert.AreEqual(wednesday.AddDays(1),
                WeeklyUsageReportRetryPolicy.NextAttemptAfterMissedRefreshDayUtc(wednesday, new DateTime(2026, 9, 5), DayOfWeek.Sunday, noUsableRows: true),
                "Rows that cannot be used never earn the hourly retry, even when Sunday's report may be due.");
        }

        [TestMethod]
        public async Task AReportThatSucceededLongerAgoThanTheWindowIsDueAgain()
        {
            var store = new StubCompletionStore { LastSuccess = DateTime.Now.AddDays(-2) };
            var importer = NewImporter(store);

            Assert.IsTrue(await importer.IsReportDueAsync(ReportA, OneDay));
        }

        [TestMethod]
        public async Task IsReportDueTreatsALocalStoredTimestampAsAnInstant()
        {
            var lastUtc = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
            var lastLocal = lastUtc.ToLocalTime();
            Assert.AreEqual(DateTimeKind.Local, lastLocal.Kind);

            var store = new StubCompletionStore { LastSuccess = lastLocal };

            Assert.IsFalse(await NewImporter(store, clock: new FixedClock(lastUtc.AddHours(23))).IsReportDueAsync(ReportA, OneDay),
                "A local-kind stamp less than the window old must not be due.");
            Assert.IsTrue(await NewImporter(store, clock: new FixedClock(lastUtc.AddHours(25))).IsReportDueAsync(ReportA, OneDay),
                "A local-kind stamp more than the window old must be due.");
        }

        [TestMethod]
        public async Task ForceUsageReportsImportBypassesThePerReportCadence()
        {
            var store = new InMemoryReportCompletionStore();
            await store.SaveSuccessAsync(ReportA);
            var importer = NewImporter(store, forceImport: true);

            Assert.IsTrue(await importer.IsReportDueAsync(ReportA, OneDay),
                "ForceUsageReportsImport must bypass the per-report gate as well as the phase gate.");
        }

        [TestMethod]
        public async Task WithNoCompletionStoreEveryReportIsAlwaysDue()
        {
            var importer = NewImporter(store: null);

            Assert.IsTrue(await importer.IsReportDueAsync(ReportA, OneDay));
        }

        /// <summary>Lets a completion time be set precisely, without sleeping.</summary>
        private class StubCompletionStore : IReportCompletionStore
        {
            public DateTime? LastSuccess { get; set; }

            public Task<DateTime?> GetLastSuccessAsync(string reportKey) => Task.FromResult(LastSuccess);
            public Task SaveSuccessAsync(string reportKey) { LastSuccess = DateTime.Now; return Task.CompletedTask; }
            public Task ClearAsync(string reportKey) { LastSuccess = null; return Task.CompletedTask; }
        }

        /// <summary>The phase marker, stamped from the test's clock rather than the wall clock.</summary>
        private sealed class ClockedSingleDateStore : ISingleDateStore
        {
            private readonly DataUtils.IClock _clock;
            private DateTime? _last;

            public ClockedSingleDateStore(DataUtils.IClock clock) => _clock = clock;

            public Task<DateTime?> GetLastDT() => Task.FromResult(_last);
            public Task SaveDT() { _last = _clock.UtcNow; return Task.CompletedTask; }
            public Task DeleteDt() { _last = null; return Task.CompletedTask; }
        }

        /// <summary>A daily report whose Graph download fails, as a 500 that outlived its retries would.</summary>
        private sealed class FailingDownloadLoader : InMemoryDailyActivityLoader
        {
            public FailingDownloadLoader() : base(NullLogger.Instance)
            {
            }

            protected override Task<List<FakeUserActivityDetail>> LoadReportPageForDateFromGraph(DateTime date)
                => Task.FromException<List<FakeUserActivityDetail>>(new GraphHttpException(
                    System.Net.HttpStatusCode.InternalServerError, "https://graph.microsoft.com/beta/reports/synthetic", "{}", null));
        }

        private sealed class ThrowOnSaveUsageReportStore : InMemoryUsageReportStore<FakeUserUsageActivityLog>
        {
            public override Task SaveChangesAsync()
                => Task.FromException(new InvalidOperationException("Synthetic save failure."));
        }

        private class TestSharePointSitesWeeklyUsageReportLoader : SharePointSitesWeeklyUsageReportLoader
        {
            private TestSharePointSitesWeeklyUsageReportLoader(InMemorySharePointSiteUsageStore store, SharePointSiteUsageDetail report, SPSiteIdToUrlCache siteLookup = null)
                : base(store, null, DataUtils.AnalyticsLogger.ConsoleOnlyTracer(), siteLookup)
            {
                Store = store;
                Report = report;
            }

            public InMemorySharePointSiteUsageStore Store { get; }
            public int LoadCount { get; private set; }
            public WeeklyUsageReportSaveResult LastSaveResult { get; private set; }

            /// <summary>What "Graph" returns; replaceable, as Graph publishes a new refresh.</summary>
            public SharePointSiteUsageDetail Report { get; set; }

            /// <summary>When set, "Graph" returns these rows instead of <see cref="Report"/>.</summary>
            public List<SharePointSiteUsageDetail> Rows { get; set; }

            /// <summary>When set, the download fails with this.</summary>
            public Exception Failure { get; set; }

            /// <summary>
            /// When set, "Graph" answers with a page that could not be read (a null page). Fails fast after a handful of
            /// requests, so a loader that keeps re-requesting it shows up as a count, not as a hung test.
            /// </summary>
            public bool UnreadablePage { get; set; }

            public static TestSharePointSitesWeeklyUsageReportLoader WithSingleReport(DateTime refreshDate, string siteUrl)
            {
                return new TestSharePointSitesWeeklyUsageReportLoader(
                    new InMemorySharePointSiteUsageStore(),
                    new SharePointSiteUsageDetail { SiteUrl = siteUrl, ReportRefreshDate = refreshDate, FileCount = 1 });
            }

            public static TestSharePointSitesWeeklyUsageReportLoader WithRows(SPSiteIdToUrlCache siteLookup, params SharePointSiteUsageDetail[] rows)
            {
                return new TestSharePointSitesWeeklyUsageReportLoader(new InMemorySharePointSiteUsageStore(), null, siteLookup)
                {
                    Rows = rows.ToList(),
                };
            }

            public override Task<AggregateResourceUsageDetail<SharePointSiteUsageDetail>> LoadReportDataForUrl(string requestUrl)
            {
                LoadCount++;
                if (Failure != null)
                {
                    return Task.FromException<AggregateResourceUsageDetail<SharePointSiteUsageDetail>>(Failure);
                }

                if (UnreadablePage)
                {
                    return LoadCount > 5
                        ? Task.FromException<AggregateResourceUsageDetail<SharePointSiteUsageDetail>>(new TimeoutException("The weekly report is being requested in a loop."))
                        : Task.FromResult<AggregateResourceUsageDetail<SharePointSiteUsageDetail>>(null);
                }

                return Task.FromResult(new AggregateResourceUsageDetail<SharePointSiteUsageDetail>
                {
                    Stats = Rows ?? new List<SharePointSiteUsageDetail> { Report },
                });
            }

            public override async Task<WeeklyUsageReportSaveResult> LoadAndSaveLastWeeksReportsIfRefreshOnDayWithResult(DayOfWeek uptoDay)
            {
                LastSaveResult = await base.LoadAndSaveLastWeeksReportsIfRefreshOnDayWithResult(uptoDay);
                return LastSaveResult;
            }
        }

        /// <summary>
        /// Site-id lookups answered from a script, each one counted - in production each is a Graph call. An id with no
        /// scripted URL fails, as a lookup of a concealed or vanished site does; the cache turns that into "no site".
        /// </summary>
        private sealed class CountingSiteLookup : SPSiteIdToUrlCache
        {
            private readonly Dictionary<string, string> _urlBySiteId;

            public CountingSiteLookup(Dictionary<string, string> urlBySiteId = null)
                : base(new InMemorySiteUrlStore(), NullLogger.Instance)
            {
                _urlBySiteId = urlBySiteId ?? new Dictionary<string, string>();
            }

            public List<string> Lookups { get; } = new List<string>();

            public override Task<Microsoft.Graph.Models.Site> LoadSite(string id)
            {
                Lookups.Add(id);
                return _urlBySiteId.TryGetValue(id, out var url)
                    ? Task.FromResult(new Microsoft.Graph.Models.Site { Id = id, WebUrl = url })
                    : Task.FromException<Microsoft.Graph.Models.Site>(new InvalidOperationException("Synthetic site lookup failure."));
            }
        }

        #endregion
    }
}
