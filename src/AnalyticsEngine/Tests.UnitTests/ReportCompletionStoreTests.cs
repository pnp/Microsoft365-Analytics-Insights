using Common.Entities.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;

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
        private static GraphImporter NewImporter(IReportCompletionStore store, bool forceImport = false, DataUtils.IClock clock = null)
        {
            var settings = new AppConfig { ForceUsageReportsImport = forceImport };

            return new GraphImporter(
                DataUtils.AnalyticsLogger.ConsoleOnlyTracer(),
                userGroupsCache: null,
                graphAppIndentityOAuthContext: null,
                graphClient: null,
                settings: settings,
                activityReportsLastImportedStore: null,
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
        public void RedisKeysAreNamespacedAwayFromThePhaseLevelMarker()
        {
            // The phase marker lives at "UserActivityLastImported". A per-report key must not be able to
            // collide with it, or a report's stamp would disarm the once-a-day throttle (or vice versa).
            const string phaseKey = "UserActivityLastImported";
            var fullKey = RedisReportCompletionStore.KeyPrefix + ReportA;

            Assert.IsFalse(fullKey.Equals(phaseKey, StringComparison.OrdinalIgnoreCase));
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
            // The phase marker is an unversioned Redis key predating the strict-paging fixes (#285 / #310): an
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
            // Stamps are cleared before the report runs, so a crash between download and save cannot leave a
            // stamp claiming a window that was only partly written.
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

        #endregion
    }
}
