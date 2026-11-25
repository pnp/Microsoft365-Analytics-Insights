using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace Tests.UnitTests
{
    public partial class AppInsightsSearchReconciliationTests
    {
        private static SearchEventAppInsightsQueryResult SearchOnRecordedPage(DateTime searchTime, DateTime pageTime, string session)
        {
            var search = Search(searchTime, session, "Example", Reader);
            search.CustomProperties.PageRequestId = Page(pageTime, session).CustomProperties.PageRequestId;
            return search;
        }

        [DataTestMethod]
        [DataRow("clean")]
        [DataRow("marker-write")]
        [DataRow("between-writes")]
        [DataRow("after-newest-hit")]
        [DataRow("restore-after-new-hit")]
        public async Task DetectedRestore_ReplaysAReconciledHourBeforeTheRewindWithoutDuplicatingIt(string interruption)
        {
            var state = new CheckpointStore();
            var clock = new FixedClock(Start.AddMinutes(10));
            var seed = Start.AddMinutes(-5);
            var search = Start.AddMinutes(1);
            var visit = Start.AddHours(2);
            var beforeBackup = Start.AddDays(2);
            var afterBackup = Start.AddDays(3);
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => Page(t, t == visit ? "delayed" : "other"),
                EventAt = t => SearchOnRecordedPage(t, visit, "delayed"),
            };
            source.ExtraPageViews.Add((seed, seed));
            source.ExtraCustomEvents.Add((search, search));
            source.ExtraPageViews.Add((visit, visit));
            source.ExtraPageViews.Add((beforeBackup, beforeBackup));
            source.ExtraPageViews.Add((afterBackup, afterBackup));
            Assert.AreEqual(1, (await RunCycle(clock, source, state)).PendingSearchHours);
            clock.Advance(TimeSpan.FromMinutes(30));
            Assert.AreEqual(1, (await RunCycle(clock, source, state)).PendingSearchHours);
            clock.Advance(TimeSpan.FromDays(2));
            await RunCycle(clock, source, state);
            Assert.AreEqual((0L, 0L), SearchKpis(), "The backup contains the eligible visit but predates the final retry.");
            clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
            if (interruption != "clean" && interruption != "restore-after-new-hit")
            {
                using (var stop = new CancellationTokenSource())
                {
                    state.AtBoundary = (operation, ct) =>
                    {
                        if (operation == interruption) { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
                    };
                    await Assert.ThrowsAsync<OperationCanceledException>(() => RunCycle(clock, source, state, token: stop.Token));
                }
                state.AtBoundary = null;
                Assert.AreEqual((1L, 1L), SearchKpis(), "SQL committed the retry before the interrupted checkpoint.");
            }
            await RunCycle(clock, source, state);
            Assert.AreEqual((1L, 1L), SearchKpis());
            source.CountRangesRequested.Clear();
            clock.Advance(TimeSpan.FromHours(1));
            Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, (await RunCycle(clock, source, state)).Outcome);
            Assert.IsFalse(source.CountRangesRequested.Any(w => w.SearchesOnly), "Dormant restore evidence is not ordinary retry work.");

            // Restore the search, term and hit tables to the relevant contents of the Jan 3 backup.
            _database.Execute("DELETE dbo.searches; DELETE dbo.search_terms; DELETE dbo.hits WHERE hit_timestamp >= '20000104';");
            source.CountRangesRequested.Clear();
            clock.Advance(TimeSpan.FromHours(1));
            if (interruption == "restore-after-new-hit")
            {
                using (var stop = new CancellationTokenSource())
                {
                    source.OnEventWindow = w => { if (w.SearchesOnly) stop.Cancel(); };
                    await Assert.ThrowsAsync<OperationCanceledException>(() => RunCycle(clock, source, state, token: stop.Token));
                }
                Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.hits WHERE hit_timestamp >= '20000104';"));
                Assert.AreEqual((0L, 0L), SearchKpis(), "The restored DB's newest hit no longer reveals the interrupted restore.");
                source.OnEventWindow = null;
                source.CountRangesRequested.Clear();
            }
            var restored = await RunCycle(clock, source, state);
            Assert.IsTrue(source.CountRangesRequested.Single(w => !w.SearchesOnly).IngestedFromUtc > search);
            Assert.AreEqual(1, restored.ReconciledSearches, "The normal rewind starts AFTER the search's ingestion.");
            Assert.AreEqual((1L, 1L), SearchKpis());
            Assert.AreEqual(0, restored.PendingSearchHours);
            Assert.AreEqual(Start, source.CountRangesRequested.Single(w => w.SearchesOnly).IngestedFromUtc);
            clock.Advance(TimeSpan.FromMinutes(10));
            await RunCycle(clock, source, state);
            Assert.AreEqual(1, source.CountRangesRequested.Count(w => w.SearchesOnly));
            Assert.AreEqual((1L, 1L), SearchKpis());
        }

        [TestMethod]
        public async Task FailedCatchUpCursorWrite_RestoreReopensHoursEvenWhenTheOldCursorNeedsNoRewind()
        {
            var state = new CheckpointStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var clock = new FixedClock(Start.AddMinutes(10));
            var visit = Start.AddHours(2);
            var backupHit = Start.AddDays(2);
            var newest = Start.AddDays(4);
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => Page(t, t == visit ? "delayed" : "other"),
                EventAt = t => SearchOnRecordedPage(t, visit, "delayed"),
            };
            foreach (var t in new[] { Start.AddMinutes(-5), visit, backupHit, newest }) source.ExtraPageViews.Add((t, t));
            source.ExtraCustomEvents.Add((Start.AddMinutes(1), Start.AddMinutes(1)));
            await RunCycle(clock, source, state);
            clock.Advance(TimeSpan.FromMinutes(40));
            var oldCursor = (await RunCycle(clock, source, state)).CursorAdvancedToUtc;
            clock.Advance(TimeSpan.FromDays(5));
            state.AtBoundary = (op, _) => { if (op == "cursor-write") throw new IOException("synthetic checkpoint outage"); };
            CollectionAssert.Contains((await RunCycle(clock, source, state)).Reasons, "CursorWriteFailed");
            Assert.AreEqual((1L, 1L), SearchKpis());
            Assert.AreEqual(oldCursor, await cursor.ReadAsync());
            Assert.AreEqual(newest, await cursor.ReadNewestStoredHitAtCursorAsync());
            Assert.AreEqual(0, (await cursor.ReadSearchRetriesAsync()).Count);

            _database.Execute("DELETE dbo.searches; DELETE dbo.search_terms; DELETE dbo.hits WHERE hit_timestamp >= '20000105';");
            state.AtBoundary = (op, _) => { if (op == "marker-write") throw new IOException("synthetic checkpoint outage"); };
            var blocked = await RunCycle(clock, source, state);
            Assert.AreEqual(AppInsightsCycleOutcome.Failed, blocked.Outcome);
            Assert.AreEqual(backupHit, _database.Scalar("SELECT MAX(hit_timestamp) FROM dbo.hits"),
                "Do not let normal SQL conceal the watermark drop before the recovered hours are durable.");
            state.AtBoundary = null;
            using (var stop = new CancellationTokenSource())
            {
                source.OnEventWindow = w => { if (w.SearchesOnly) stop.Cancel(); };
                await Assert.ThrowsAsync<OperationCanceledException>(() => RunCycle(clock, source, state, token: stop.Token));
            }
            Assert.AreEqual(newest, _database.Scalar("SELECT MAX(hit_timestamp) FROM dbo.hits"));
            Assert.AreEqual(Start, (await cursor.ReadSearchRetriesAsync()).Single().HourUtc);
            Assert.AreEqual(oldCursor, await cursor.ReadAsync());
            source.OnEventWindow = null;
            var replay = await RunCycle(clock, source, state);
            Assert.AreEqual(oldCursor - AppInsightsIngestionCursorPolicy.Overlap,
                source.CountRangesRequested.Last(w => !w.SearchesOnly).IngestedFromUtc);
            Assert.AreEqual(1, replay.ReconciledSearches);
            Assert.AreEqual((1L, 1L), SearchKpis());
            Assert.AreEqual(0, replay.PendingSearchHours);
        }

        [TestMethod]
        public async Task EmptyRestore_RebuildsTheVisitForAnOlderRetainedSearchBeforeItsFinalAttempt()
        {
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start.AddMinutes(10));
            var visit = Start.AddDays(1);
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => Page(t, "delayed"),
                EventAt = t => SearchOnRecordedPage(t, visit, "delayed"),
            };
            source.ExtraCustomEvents.Add((Start, Start));
            source.ExtraPageViews.Add((visit, visit));
            _database.Execute("INSERT dbo.org_urls(url_base) VALUES (N'https://contoso.sharepoint.com/');");
            var first = await RunCycle(clock, source, state);
            Assert.AreEqual(1, first.PendingSearchHours);
            Assert.AreEqual(clock.UtcNow.AddDays(-31).Date, source.CountRangesRequested.Single().FromUtc);
            clock.Advance(TimeSpan.FromDays(4));
            Assert.AreEqual(1, (await RunCycle(clock, source, state)).ReconciledSearches);
            Assert.AreEqual((1L, 1L), SearchKpis());

            _database.Execute("DELETE dbo.searches; DELETE dbo.search_terms; DELETE dbo.hits; DELETE dbo.sessions;");
            clock.Advance(TimeSpan.FromDays(41));
            var oldest = clock.UtcNow - AppInsightsIngestionCursorPolicy.MaxLookback;
            source.ExtraPageViews.Add((oldest, oldest.AddSeconds(-1)));
            source.CountRangesRequested.Clear();
            var restored = await RunCycle(clock, source, state);
            Assert.AreEqual(AppInsightsReadMode.Bootstrap, restored.Mode);
            Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"),
                "Rebuild the retained visit, not the page outside ingestion retention.");
            Assert.AreEqual(1, restored.ReconciledSearches);
            Assert.AreEqual((1L, 1L), SearchKpis());
            Assert.AreEqual(0, restored.PendingSearchHours);
            Assert.AreEqual(clock.UtcNow, restored.CursorAdvancedToUtc);
            Assert.IsFalse(restored.Reasons.Contains("SearchRetriesStopped"));
        }

        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(true, true)]
        [DataRow(false, false)]
        [DataRow(false, true)]
        public async Task HistoricalBootstrap_ReconcilesAcrossMidnightEvenAfterAStopBetweenSavedDays(bool oldCursor, bool interrupt)
        {
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start.AddDays(7));
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            if (oldCursor) await cursor.WriteAsync(Start.AddDays(6), Start.AddDays(5));
            var search = Start.Date.AddSeconds(-1);
            var seed = Start.Date.AddDays(1).AddHours(2);
            var visit = Start.Date.AddDays(2).AddMinutes(1);
            var source = new FakeAppInsightsSourceLoader
            {
                EventAt = t => SearchOnRecordedPage(t, visit, "delayed"),
                PageViewAt = t => Page(t, t == visit ? "delayed" : "other"),
            };
            source.ExtraCustomEvents.Add((search, search));
            source.ExtraPageViews.Add((seed, seed));
            source.ExtraPageViews.Add((visit, visit));
            if (interrupt)
            {
                using (var stop = new CancellationTokenSource())
                {
                    source.PageViewAt = t =>
                    {
                        if (t == visit) stop.Cancel();
                        return Page(t, "other");
                    };
                    await Assert.ThrowsAsync<OperationCanceledException>(() => RunCycle(clock, source, state, token: stop.Token));
                }
                Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"));
                Assert.AreEqual((0L, 0L), SearchKpis());
                source.PageViewAt = t => Page(t, t == visit ? "delayed" : "other");
            }
            var recovered = await RunCycle(clock, source, state);
            Assert.AreEqual(AppInsightsReadMode.Bootstrap, recovered.Mode);
            Assert.AreEqual(1, recovered.ReconciledSearches);
            Assert.AreEqual(clock.UtcNow, recovered.CursorAdvancedToUtc);
            Assert.AreEqual((1L, 1L), SearchKpis());
        }

        [TestMethod]
        public async Task PreviouslyEmptyDatabase_DoesNotRearmAnExpiredSearchEveryCycle()
        {
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start.AddDays(7));
            var source = new FakeAppInsightsSourceLoader { EventAt = t => Search(t, "absent", "Example", Reader) };
            source.ExtraCustomEvents.Add((Start, Start));
            // A legacy cursor with no newest-hit record is not confirmation that its DB was already empty.
            await state.SetStringAsync("IngestionCursor:contoso-test", Start.AddDays(6).ToString("o"));
            var first = await RunCycle(clock, source, state);
            Assert.AreEqual(1, source.CountRangesRequested.Count(w => w.SearchesOnly));
            CollectionAssert.Contains(first.Reasons, "SearchRetriesStopped");
            for (var i = 0; i < 3; i++)
            {
                clock.Advance(TimeSpan.FromDays(1));
                var quiet = await RunCycle(clock, source, state);
                Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, quiet.Outcome);
                Assert.AreEqual(clock.UtcNow, quiet.CursorAdvancedToUtc);
            }
            Assert.AreEqual(1, source.CountRangesRequested.Count(w => w.SearchesOnly));
            Assert.AreEqual((0L, 0L), SearchKpis());
        }

        [TestMethod]
        public async Task RestoreEvidence_IsResourceLocalAndOnlyQueriedInsideTheSourceRetentionWindow()
        {
            var clock = new FixedClock(Start.AddMinutes(40));
            var state = new InMemoryKeyValueStore(() => clock.UtcNow);
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var other = new KeyValueIngestionCursorStore(state, "other-resource");
            var edge = Start - AppInsightsIngestionCursorPolicy.MaxLookback;
            await cursor.WriteSearchReplayHoursAsync(new[] { edge.AddHours(-1), edge }, default);
            await other.WriteSearchReplayHoursAsync(new[] { Start.AddDays(-5) }, default);
            await cursor.WriteAsync(Start.AddMinutes(-1), Start.AddMinutes(-5));
            var source = new FakeAppInsightsSourceLoader();
            var restored = await RunCycle(clock, source, state);
            var only = source.CountRangesRequested.Single(w => w.SearchesOnly);
            Assert.AreEqual(edge.AddMinutes(40), only.IngestedFromUtc);
            Assert.AreEqual(edge.AddHours(1), only.IngestedBeforeUtc);
            CollectionAssert.Contains(restored.Reasons, "SearchRetriesStopped");
            Assert.AreEqual(clock.UtcNow, restored.CursorAdvancedToUtc);
        }

        [TestMethod]
        public async Task SearchCheckpoints_FitTheTableValueCodecAtMaximumHourDensity()
        {
            var hours = Enumerable.Range(0, 90 * 24 + 1).Select(i => Start.AddHours(-i)).ToArray();
            var state = new InMemoryKeyValueStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            await cursor.WriteSearchRetriesAsync(hours.Select(h => new AppInsightsSearchRetry(h, h.AddDays(3))).ToArray());
            await cursor.WriteSearchReplayHoursAsync(hours, default);
            foreach (var prefix in new[] { "IngestionCursorSearchRetries", "IngestionCursorSearchReplayHours" })
            {
                var key = prefix + ":contoso-test";
                var value = await state.GetStringAsync(key);
                var entity = new Azure.Data.Tables.TableEntity("AppInsightsImport", key);
                AzureTableKeyValueStore.EncodeValue(entity, key, value);
                var decoded = JArray.Parse(AzureTableKeyValueStore.DecodeValue(entity, key));
                Assert.AreEqual(hours.Length, decoded.Count);
                Assert.IsTrue(decoded.Descendants().OfType<JValue>().All(v => v.Type == JTokenType.Date));
            }
        }

        [TestMethod]
        public async Task MissingRestoreEvidence_FailsClosedOnlyWhenTheDatabaseActuallyNeedsIt()
        {
            var state = new CheckpointStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var clock = new FixedClock(Start.AddDays(1));
            var source = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "seed") };
            source.ExtraPageViews.Add((Start, Start));
            await cursor.WriteAsync(Start, Start);
            state.AtBoundary = (operation, _) =>
            {
                if (operation == "receipt-read") throw new IOException("synthetic checkpoint outage");
            };
            var failed = await RunCycle(clock, source, state);
            Assert.AreEqual(AppInsightsCycleOutcome.Failed, failed.Outcome);
            CollectionAssert.Contains(failed.Reasons, "SearchReplayReadFailed");
            Assert.AreEqual(Start, await cursor.ReadAsync());
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"));
            state.AtBoundary = null;
            Assert.AreEqual(clock.UtcNow, (await RunCycle(clock, source, state)).CursorAdvancedToUtc);
            state.AtBoundary = (operation, _) => Assert.AreNotEqual("receipt-read", operation, "No restore or due retry.");
            clock.Advance(TimeSpan.FromDays(1));
            Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, (await RunCycle(clock, source, state)).Outcome);
        }

        [TestMethod]
        public async Task InterruptedEmptyCursorWrite_DoesNotCertifyTheOldCursorAsAnEmptyDatabase()
        {
            var state = new CheckpointStore();
            var clock = new FixedClock(Start.AddDays(7));
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var oldCursor = Start.AddDays(6);
            await cursor.WriteAsync(oldCursor, Start.AddDays(5));
            var source = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "delayed"), EventAt = t => SearchOnRecordedPage(t, Start, "delayed") };
            state.AtBoundary = (op, _) => { if (op == "cursor-write") throw new IOException("synthetic checkpoint outage"); };
            var failed = await RunCycle(clock, source, state);
            CollectionAssert.Contains(failed.Reasons, "CursorWriteFailed");
            Assert.AreEqual(oldCursor, await cursor.ReadAsync());
            state.AtBoundary = null;
            source.ExtraCustomEvents.Add((Start.Date.AddSeconds(-1), Start));
            source.ExtraPageViews.Add((Start, Start.AddHours(1)));
            var replay = await RunCycle(clock, source, state);
            Assert.AreEqual(AppInsightsReadMode.Bootstrap, replay.Mode);
            Assert.AreEqual(1, replay.ReconciledSearches);
            Assert.AreEqual((1L, 1L), SearchKpis());
        }

        [TestMethod]
        public async Task UnwritableRestoreEvidence_LeavesTheDueHourPendingWithoutAReconciliationInsert()
        {
            var state = new CheckpointStore();
            var clock = new FixedClock(Start);
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var hour = Start.AddDays(-4);
            var source = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "eligible"), EventAt = t => SearchOnRecordedPage(t, hour, "eligible") };
            source.ExtraPageViews.Add((hour, hour));
            await RunCycle(clock, source, state);
            source.ExtraCustomEvents.Add((hour, hour));
            await cursor.WriteSearchRetriesAsync(new[] { new AppInsightsSearchRetry(hour, Start) });
            state.AtBoundary = (op, _) => { if (op == "receipt-write") throw new IOException("synthetic checkpoint outage"); };
            clock.Advance(TimeSpan.FromHours(1));
            var failed = await RunCycle(clock, source, state);
            CollectionAssert.Contains(failed.Reasons, "SearchReplayWriteFailed");
            Assert.AreEqual(1, failed.PendingSearchHours);
            Assert.AreEqual((0L, 0L), SearchKpis());
            state.AtBoundary = null;
            clock.Advance(TimeSpan.FromMinutes(1));
            var recovered = await RunCycle(clock, source, state);
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual((1L, 1L), SearchKpis());
        }
    }
}
