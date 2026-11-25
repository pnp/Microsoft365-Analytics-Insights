using Common.Entities;
using Common.Entities.Config;
using Common.Entities.SpoWebActivity;
using Common.Entities.State;
using Common.Entities.UserScope;
using DataUtils;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql;

namespace Tests.UnitTests
{
    [TestClass]
    [TestCategory("SqlIntegration")]
    [DoNotParallelize]
    public class AppInsightsSearchReconciliationTests
    {
        private static readonly DateTime Start = new DateTime(2000, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        private const string Reader = "reader@contoso.com";
        private static ScratchDatabase _database;
        private static ContextFactory _factory;

        [ClassInitialize]
        public static void CreateDatabase(TestContext context)
        {
            _database = ScratchDatabase.Create("SearchArrival");
            new DbMigrator(new Common.Entities.Migrations.Configuration
            {
                TargetDatabase = new DbConnectionInfo(_database.ConnectionString, "Microsoft.Data.SqlClient"),
            }).Update();
            _factory = new ContextFactory(_database.ConnectionString);
        }

        [ClassCleanup]
        public static void DropDatabase() => _database?.Dispose();

        [TestInitialize]
        public void ClearVisits() => _database.Execute("DELETE dbo.searches; DELETE dbo.hits; DELETE dbo.sessions; DELETE dbo.search_terms; DELETE dbo.org_urls;");

        private sealed class ContextFactory : IAnalyticsDbContextFactory
        {
            private readonly string _connection;
            public ContextFactory(string connection) => _connection = connection;
            public AnalyticsEntitiesContext Create() => new AnalyticsEntitiesContext(_connection, true, false);
        }

        [TestMethod]
        public async Task SearchReport_RecoversSearchesWhenEligibleVisitsArriveInsideAndOutsideCursorOverlap()
        {
            var source = new FakeAppInsightsSourceLoader();
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start);
            var seedTime = Start.AddMinutes(-10);
            var nearTime = Start.AddSeconds(-5);
            var farTime = Start.Date.AddSeconds(-5);
            source.PageViewAt = time => Page(time, time == seedTime ? "seed" : time == nearTime ? "near" : "far");
            source.EventAt = time => Search(time, time == nearTime ? "near" : "far", "Καλημέρα", Reader);
            Task<AppInsightsImportCycleResult> Run() => RunCycle(clock, source, state);

            source.ExtraPageViews.Add((seedTime, seedTime));
            Assert.AreEqual(Start, (await Run()).CursorAdvancedToUtc, "Bootstrap must write a real hit and the cursor.");
            source.ExtraCustomEvents.Add((nearTime, Start.AddMinutes(1)));
            source.ExtraCustomEvents.Add((farTime, Start.AddMinutes(1)));
            source.ExtraPageViews.Add((nearTime, Start.AddMinutes(17)));
            source.ExtraPageViews.Add((Start, Start.AddMinutes(35)));
            clock.Advance(TimeSpan.FromMinutes(10));
            var first = await Run();
            Assert.AreEqual((0L, 0L), SearchKpis());
            Assert.AreEqual(0, first.Events, "Neither unmatched search is saved.");
            Assert.AreEqual(2, first.Days.Sum(d => d.UnmatchedSearches));
            Assert.AreEqual(1, first.PendingSearchHours, "Both events arrived in the same ingestion hour despite different timestamp days.");
            var marker = (JObject)JArray.Parse(await state.GetStringAsync("IngestionCursorSearchRetries:contoso-test")).Single();
            CollectionAssert.AreEquivalent(new[] { "HourUtc", "RetryAfterUtc" }, marker.Properties().Select(p => p.Name).ToArray(),
                "No identity, search text, event or other raw telemetry belongs in the durable checkpoint.");
            Assert.AreEqual(AppInsightsCycleOutcome.Degraded, first.Outcome);
            Assert.AreEqual(clock.UtcNow, first.CursorAdvancedToUtc, "Other telemetry need not wait for either visit.");
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms"));
            Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.sessions"), "A search must not invent a visit.");
            clock.Advance(TimeSpan.FromMinutes(10));
            await Run();
            Assert.AreEqual((1L, 1L), SearchKpis(), "The visit arriving at 10:17 is inside the ordinary overlap.");
            clock.Advance(TimeSpan.FromMinutes(10));
            await Run();
            clock.Advance(TimeSpan.FromMinutes(10));
            var recovered = await Run();
            Assert.AreEqual(3, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"), "Both delayed views reached SQL.");
            Assert.AreEqual((2L, 2L), SearchKpis(), "The visit arriving at 10:35 must also recover its search.");
            Assert.AreEqual(1, recovered.ReconciledSearches, "The near search was replayed, not counted as recovered.");
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, recovered.Outcome);
            clock.Advance(TimeSpan.FromMinutes(10));
            await Run();
            Assert.AreEqual(1, source.CountRangesRequested.Count(w => w.SearchesOnly), "A completed retry must be removed from the checkpoint.");
            Assert.AreEqual((2L, 2L), SearchKpis());
        }

        [TestMethod]
        public async Task HistoricalCatchUp_VisitsOnLaterTimestampDaysResolveEarlierSearchesBeforeTheyExpire()
        {
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = time => Page(time, "old"),
                EventAt = time => Search(time, "old", "Old", Reader)
            };
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start.AddDays(6));
            source.ExtraCustomEvents.Add((Start.Date.AddSeconds(-1), Start.AddMinutes(1)));
            source.ExtraPageViews.Add((Start, Start.AddMinutes(35)));

            var caughtUp = await RunCycle(clock, source, state);
            Assert.AreEqual(1, caughtUp.Days.Sum(d => d.UnmatchedSearches));
            Assert.AreEqual(1, caughtUp.ReconciledSearches, "The search's three-day retry window elapsed before this first import.");
            Assert.AreEqual(0, caughtUp.PendingSearchHours);
            Assert.AreEqual(clock.UtcNow, caughtUp.CursorAdvancedToUtc);
            Assert.AreEqual((1L, 1L), SearchKpis());
        }

        [DataTestMethod]
        [DataRow("page-view")]
        [DataRow("normal-search")]
        [DataRow("page-update-after-search")]
        [DataRow("search-only")]
        public async Task StopDuringBlockedMerge_PreservesEarlierCommitsAndReplaysOnce(string boundary)
        {
            var searchesOnly = boundary == "search-only";
            var stopInPageView = boundary == "page-view";
            var stopAfterSearch = boundary == "page-update-after-search";
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => Page(t, "eligible"),
                EventAt = t => Search(t, "eligible", "Example", Reader)
            };
            var state = new InMemoryKeyValueStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var clock = new FixedClock(Start);
            source.ExtraPageViews.Add((Start.AddMinutes(-5), Start.AddMinutes(-5)));
            await RunCycle(clock, source, state);
            var arrival = searchesOnly ? Start.AddHours(-1) : Start.AddHours(1);
            if (searchesOnly)
                await cursor.WriteSearchRetriesAsync(new[] { new AppInsightsSearchRetry(arrival, Start) });
            source.ExtraCustomEvents.Add((arrival, arrival));
            source.ExtraPageViews.Add((Start.AddHours(1), Start.AddHours(1)));
            if (stopAfterSearch)
            {
                var updateTime = arrival.AddSeconds(1);
                source.ExtraCustomEvents.Add((updateTime, updateTime));
                source.EventAt = t => t == updateTime
                    ? new PageUpdateEventAppInsightsQueryResult { AppInsightsTimestamp = t, Username = Reader,
                        CustomProperties = new PageUpdateEventCustomProps { Url = Page(t, "eligible").Url, PropsString = "{\"ExampleField\":\"Example value\"}" } }
                    : (BaseCustomEventAppInsightsQueryResult)Search(t, "eligible", "Example", Reader);
            }
            clock.Advance(TimeSpan.FromHours(2));

            using (var stop = new CancellationTokenSource())
            using (var blocker = new SqlConnection(_database.ConnectionString))
            {
                await blocker.OpenAsync();
                Task<AppInsightsImportCycleResult> import;
                bool interrupted;
                using (var tx = blocker.BeginTransaction())
                using (var command = blocker.CreateCommand())
                {
                    command.Transaction = tx;
                    command.CommandText = stopInPageView ? "SELECT COUNT(*) FROM dbo.page_titles WITH (TABLOCKX, HOLDLOCK);"
                        : stopAfterSearch ? "SELECT COUNT(*) FROM dbo.file_field_definitions WITH (TABLOCKX, HOLDLOCK);"
                        : "SELECT COUNT(*) FROM dbo.searches WITH (TABLOCKX, HOLDLOCK);";
                    await command.ExecuteScalarAsync();
                    import = RunCycle(clock, source, state, token: stop.Token);
                    try
                    {
                        // Cancel only after the production merge is blocked, not while starting EF.
                        command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @@SPID AND database_id = DB_ID();";
                        var wait = Stopwatch.StartNew();
                        while ((int)await command.ExecuteScalarAsync() == 0)
                        {
                            Assert.IsFalse(import.IsCompleted || wait.Elapsed > TimeSpan.FromSeconds(30), "The merge did not reach the lock.");
                            await Task.Delay(25);
                        }
                        stop.Cancel();
                        interrupted = await Task.WhenAny(import, Task.Delay(TimeSpan.FromSeconds(5))) == import;
                    }
                    finally
                    {
                        tx.Rollback();
                        stop.Cancel();
                        try { await import; } catch (OperationCanceledException) { }
                    }
                }
                Assert.IsTrue(interrupted, "The host stop must interrupt the blocked SQL command without releasing its lock.");
                await Assert.ThrowsAsync<OperationCanceledException>(() => import);
            }
            Assert.AreEqual(Start, await cursor.ReadAsync());
            var markers = await cursor.ReadSearchRetriesAsync();
            Assert.AreEqual(searchesOnly ? 1 : 0, markers.Count);
            if (searchesOnly) Assert.AreEqual(Start, markers.Single().RetryAfterUtc, "The stop must not reschedule the hour.");
            Assert.AreEqual(stopInPageView ? 1 : 2, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"), "Earlier SQL commits survive.");
            var committed = stopAfterSearch ? 1 : 0;
            Assert.AreEqual(committed, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms"), "A lookup and its search fact commit together.");
            Assert.AreEqual(((long)committed, (long)committed), SearchKpis());
            if (stopAfterSearch) Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.file_metadata_property_values"));

            var recovered = await RunCycle(clock, source, state);
            Assert.AreEqual(clock.UtcNow, recovered.CursorAdvancedToUtc);
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual(searchesOnly ? 1 : 0, recovered.ReconciledSearches);
            Assert.AreEqual(2, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"));
            Assert.AreEqual((1L, 1L), SearchKpis());
            if (stopAfterSearch)
            {
                Assert.AreEqual(1, recovered.Events, "Only the page update counts; the search was already saved.");
                Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.file_metadata_property_values WHERE field_value = N'Example value'"));
            }
        }

        [TestMethod]
        public async Task StopAfterFirstSearchWindow_RetriesTheSameHourWithoutCountingCommittedFactsAgain()
        {
            var hour = Start.AddHours(-2);
            var earlier = Start.Date.AddSeconds(-1);
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => Page(t, "eligible"),
                EventAt = t => Search(t, "eligible", t == earlier ? "Earlier" : "Later", Reader)
            };
            var state = new InMemoryKeyValueStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var clock = new FixedClock(Start);
            source.ExtraPageViews.Add((Start.AddMinutes(-5), Start.AddMinutes(-5)));
            await RunCycle(clock, source, state);
            await cursor.WriteSearchRetriesAsync(new[] { new AppInsightsSearchRetry(hour, Start) });
            source.ExtraCustomEvents.Add((earlier, hour));
            source.ExtraCustomEvents.Add((hour, hour));
            clock.Advance(TimeSpan.FromMinutes(20));

            using (var stop = new CancellationTokenSource())
            {
                var windows = 0;
                source.OnEventWindow = w => { if (w.SearchesOnly && ++windows == 2) stop.Cancel(); };
                await Assert.ThrowsAsync<OperationCanceledException>(() => RunCycle(clock, source, state, token: stop.Token));
                Assert.AreEqual(2, windows);
            }
            Assert.AreEqual(Start, await cursor.ReadAsync());
            Assert.AreEqual(Start, (await cursor.ReadSearchRetriesAsync()).Single().RetryAfterUtc);
            Assert.AreEqual((1L, 1L), SearchKpis());
            source.OnEventWindow = null;
            var recovered = await RunCycle(clock, source, state);
            Assert.AreEqual(1, recovered.ReconciledSearches, "Only the second window inserted a fact on replay.");
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual((2L, 1L), SearchKpis());
        }

        [TestMethod]
        public async Task Retry_DoesNotClaimAnEmptySourceOrSaveSearchesForSomeoneNowOutsideTheFilter()
        {
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start);
            var seed = Start.AddMinutes(-5);
            var search = Start.AddMinutes(1);
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = time => Page(time, time == seed ? "seed" : "later"),
                EventAt = time => Search(time, "later", "Later", Reader),
            };
            source.ExtraPageViews.Add((seed, seed));
            await RunCycle(clock, source, state);
            source.ExtraCustomEvents.Add((search, search));
            source.ExtraPageViews.Add((search, Start.AddMinutes(33)));
            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.AreEqual(1, (await RunCycle(clock, source, state)).PendingSearchHours);
            clock.Advance(TimeSpan.FromMinutes(15));
            await RunCycle(clock, source, state);
            clock.Advance(TimeSpan.FromMinutes(10));
            await RunCycle(clock, source, state);
            Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.sessions WHERE ai_session_id = 'later'"));

            source.ExtraCustomEvents.Clear();
            clock.Advance(TimeSpan.FromMinutes(5));
            var empty = await RunCycle(clock, source, state);
            Assert.IsTrue(empty.Reasons.Contains("SearchRetrySourceEmpty"));
            Assert.AreEqual(1, empty.PendingSearchHours);
            source.ExtraCustomEvents.Add((search, search));
            clock.Advance(TimeSpan.FromDays(4));
            var members = new UserScopeMembers();
            members.Add(null, "member@contoso.com", null);
            var removed = await RunCycle(clock, source, state, UserImportScope.ForMembers(members, "Contoso pilot"));
            Assert.AreEqual(0, removed.PendingSearchHours, "A positively excluded user does not need further retries.");
            Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, removed.Outcome);
            Assert.AreEqual((0L, 0L), SearchKpis(), "The visit exists, but the user is no longer in scope.");
        }

        [DataTestMethod]
        [DataRow("marker-read")]
        [DataRow("marker-write")]
        [DataRow("between-writes")]
        [DataRow("after-newest-hit")]
        [DataRow("cursor-write")]
        [DataRow("after-cursor")]
        public async Task StopAtCheckpoint_RestartReadsOnlyDurableProgressAndDoesNotDuplicateFacts(string boundary)
        {
            var source = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "eligible") };
            var state = new CheckpointStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var clock = new FixedClock(Start);
            source.ExtraPageViews.Add((Start.AddMinutes(-5), Start.AddMinutes(-5)));
            await RunCycle(clock, source, state);
            var matched = Start.AddMinutes(1);
            var missing = Start.AddMinutes(2);
            source.EventAt = t => Search(t, t == matched ? "eligible" : "delayed", "Example", Reader);
            source.ExtraCustomEvents.Add((matched, matched));
            source.ExtraCustomEvents.Add((missing, missing));
            clock.Advance(TimeSpan.FromMinutes(10));
            bool markerCommitted = boundary != "marker-read" && boundary != "marker-write";
            using (var stop = new CancellationTokenSource())
            {
                bool receivedToken = false;
                state.AtBoundary = (operation, token) =>
                {
                    if (operation != boundary) return;
                    receivedToken = token == stop.Token;
                    stop.Cancel();
                    // A provider may throw its own error, throw cancellation, or finish a write despite the stop.
                    if (operation == "marker-read") throw new IOException("synthetic interrupted read");
                    if (operation == "marker-write" || operation == "cursor-write")
                        token.ThrowIfCancellationRequested();
                };
                var output = await AppInsightsImporterOrchestrationTests.CaptureConsole(async () =>
                    await Assert.ThrowsAsync<OperationCanceledException>(() => RunCycle(clock, source, state, token: stop.Token)));
                Assert.IsTrue(receivedToken, "The production checkpoint must receive the cycle token.");
                Assert.IsFalse(output.Contains("checkpoint could not") || output.Contains("AppInsightsCursor: Outcome=WriteFailed")
                    || output.Contains("OperationName=Hits import") || output.Contains("AppInsightsCursor: Outcome=Advanced"));
            }
            state.AtBoundary = null;
            Assert.AreEqual(markerCommitted ? 1 : 0, (await cursor.ReadSearchRetriesAsync()).Count);
            Assert.AreEqual(boundary == "after-cursor" ? clock.UtcNow : Start, await cursor.ReadAsync());
            Assert.AreEqual(boundary == "marker-read" ? (0L, 0L) : (1L, 1L), SearchKpis());

            var replay = await RunCycle(clock, source, state);
            Assert.AreEqual(boundary == "marker-read" ? 1 : 0, replay.Events, "An existing search fact is not a new save.");
            Assert.AreEqual(clock.UtcNow, replay.CursorAdvancedToUtc);
            Assert.AreEqual(1, replay.PendingSearchHours);
            Assert.AreEqual((1L, 1L), SearchKpis());
            clock.Advance(TimeSpan.FromMinutes(15));
            Assert.AreEqual(1, (await RunCycle(clock, source, state)).PendingSearchHours);
            source.PageViewAt = t => Page(t, "delayed");
            source.ExtraPageViews.Clear();
            source.ExtraPageViews.Add((Start.AddMinutes(32), Start.AddMinutes(32)));
            clock.Advance(TimeSpan.FromMinutes(16));
            var recovered = await RunCycle(clock, source, state);
            Assert.AreEqual(1, recovered.ReconciledSearches);
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual((2L, 2L), SearchKpis());
        }

        private sealed class CheckpointStore : IKeyValueStore
        {
            private readonly InMemoryKeyValueStore _state = new InMemoryKeyValueStore();
            public Action<string, CancellationToken> AtBoundary;
            public string Description => _state.Description;

            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                if (key.StartsWith("IngestionCursorSearchRetries:", StringComparison.Ordinal)) AtBoundary?.Invoke("marker-read", cancellationToken);
                if (key.StartsWith("IngestionCursorReplayStart:", StringComparison.Ordinal)) AtBoundary?.Invoke("replay-read", cancellationToken);
                return _state.GetStringAsync(key, cancellationToken);
            }

            public async Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                bool marker = key.StartsWith("IngestionCursorSearchRetries:", StringComparison.Ordinal);
                bool cursor = key.StartsWith(KeyValueIngestionCursorStore.KeyPrefix + ":", StringComparison.Ordinal);
                if (marker || cursor) AtBoundary?.Invoke(marker ? "marker-write" : "cursor-write", cancellationToken);
                if (key.StartsWith("IngestionCursorReplayStart:", StringComparison.Ordinal)) AtBoundary?.Invoke("replay-write", cancellationToken);
                await _state.SetStringAsync(key, value, timeToLive, cancellationToken);
                if (marker || cursor) AtBoundary?.Invoke(marker ? "between-writes" : "after-cursor", cancellationToken);
                if (key.StartsWith(KeyValueIngestionCursorStore.NewestHitKeyPrefix + ":", StringComparison.Ordinal))
                    AtBoundary?.Invoke("after-newest-hit", cancellationToken);
            }

            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _state.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _state.ExistsAsync(key, cancellationToken);
        }

        [DataTestMethod]
        [DataRow("replay-read", "ReplayStartReadFailed")]
        [DataRow("replay-write", "ReplayStartWriteFailed")]
        public async Task BootstrapStartStoreOutage_DoesNotLetSqlChangeTheOnlyResumePoint(string fault, string reason)
        {
            var clock = new FixedClock(Start.AddMinutes(1));
            var source = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "example") };
            source.ExtraPageViews.Add((Start, Start));
            var state = new CheckpointStore { AtBoundary = (op, _) => { if (op == fault) throw new IOException("synthetic outage"); } };
            var failed = await RunCycle(clock, source, state);
            Assert.AreEqual(AppInsightsCycleOutcome.Failed, failed.Outcome);
            CollectionAssert.Contains(failed.Reasons, reason);
            Assert.IsNull(failed.CursorAdvancedToUtc);
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"));
            state.AtBoundary = null;
            Assert.AreEqual(clock.UtcNow, (await RunCycle(clock, source, state)).CursorAdvancedToUtc);
            Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"));
        }

        [DataTestMethod]
        [DataRow("bootstrap")]
        [DataRow("empty-restore")]
        [DataRow("rewind")]
        public async Task BootstrapOrRestoreStopBeforeMarker_NewerSqlHitDoesNotHideAnOlderSearch(string mode)
        {
            var clock = new FixedClock(Start.AddHours(1));
            var state = new CheckpointStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            DateTime? previousCursor = null;
            if (mode != "bootstrap")
            {
                previousCursor = Start.AddMinutes(20);
                await cursor.WriteAsync(previousCursor.Value, Start);
            }
            if (mode == "rewind")
            {
                var old = Start.AddDays(-6);
                var restoredSource = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "old") };
                restoredSource.ExtraPageViews.Add((old, old));
                await RunCycle(new FixedClock(old.AddMinutes(1)), restoredSource, new InMemoryKeyValueStore());
            }
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => Page(t, t == Start ? "seed" : "delayed"),
                EventAt = t => Search(t, "delayed", "Example", Reader)
            };
            source.ExtraCustomEvents.Add((Start.AddDays(-1), Start.AddMinutes(-2)));
            source.ExtraPageViews.Add((Start, Start.AddMinutes(-1)));

            using (var stop = new CancellationTokenSource())
            {
                state.AtBoundary = (operation, token) =>
                {
                    if (operation == "marker-write") { stop.Cancel(); token.ThrowIfCancellationRequested(); }
                };
                await Assert.ThrowsAsync<OperationCanceledException>(() => RunCycle(clock, source, state, token: stop.Token));
            }
            Assert.AreEqual(previousCursor, await cursor.ReadAsync());
            Assert.AreEqual(0, (await cursor.ReadSearchRetriesAsync()).Count);
            Assert.AreEqual(mode == "rewind" ? 2 : 1, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"), "The later page view is already committed.");
            state.AtBoundary = null;
            source.ExtraPageViews.Add((Start.AddHours(1), Start.AddHours(1)));
            clock.Advance(TimeSpan.FromMinutes(10));
            var replay = await RunCycle(clock, source, state);
            Assert.AreEqual(mode == "rewind" ? AppInsightsReadMode.Incremental : AppInsightsReadMode.Bootstrap, replay.Mode);
            Assert.AreEqual(1, replay.PendingSearchHours, "The newer stored hit must not hide an uncheckpointed search from an earlier timestamp day.");
            clock.Advance(TimeSpan.FromMinutes(31));
            var recovered = await RunCycle(clock, source, state);
            Assert.AreEqual(1, recovered.ReconciledSearches);
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual((1L, 1L), SearchKpis());
        }

        [TestMethod]
        public async Task RetryMerge_TimeoutRollsBackTheLookupAndALaterAttemptCanSaveTheSearch()
        {
            var source = new FakeAppInsightsSourceLoader { PageViewAt = time => Page(time, "eligible") };
            source.ExtraPageViews.Add((Start.AddMinutes(-5), Start.AddMinutes(-5)));
            await RunCycle(new FixedClock(Start), source, new InMemoryKeyValueStore());
            var batch = new CustomEventsResultCollection();
            batch.Rows.Add(Search(Start, "eligible", "New search", Reader));
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            using (var db = _factory.Create())
            using (var blocker = new SqlConnection(_database.ConnectionString))
            {
                await blocker.OpenAsync();
                using (var transaction = blocker.BeginTransaction())
                using (var takeLock = new SqlCommand("SELECT COUNT(*) FROM dbo.searches WITH (TABLOCKX, HOLDLOCK);", blocker, transaction))
                {
                    await takeLock.ExecuteScalarAsync();
                    var ex = await Assert.ThrowsExceptionAsync<SqlException>(() => batch.SaveSearchBatchAsync(logger, db, mergeTimeoutSeconds: 1));
                    Assert.AreEqual(-2, ex.Number, "A blocked retry merge has a finite SQL command deadline.");
                }
                Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms"));
                var recovered = await batch.SaveSearchBatchAsync(logger, db, mergeTimeoutSeconds: 5);
                Assert.AreEqual(2, recovered.RowsAffected);
                Assert.AreEqual((1L, 1L), SearchKpis());
            }
        }

        [TestMethod]
        public async Task PermanentlyMissingVisit_ExpiresWithoutCreatingUsersOrBypassingTheSiteOrUserFilter()
        {
            var source = new FakeAppInsightsSourceLoader();
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start);
            var members = new UserScopeMembers();
            members.Add(null, Reader, null);
            var scope = UserImportScope.ForMembers(members, "Contoso pilot");
            Task<AppInsightsImportCycleResult> Run() => RunCycle(clock, source, state, scope);
            const string other = "outside@contoso.com";
            var seed = Start.AddMinutes(-5);
            var deniedSite = Start.AddMinutes(1);
            var deniedUser = Start.AddMinutes(2);
            var late = Start.AddMinutes(3);
            source.PageViewAt = time =>
            {
                var pv = Page(time, time == seed ? "seed" : time == deniedSite ? "no-site" : time == deniedUser ? "no-user" : "late");
                if (time == deniedSite) pv.Url = pv.CustomProperties.SiteUrl = "https://contoso.sharepoint.com/sites/elsewhere";
                if (time == deniedUser) pv.Username = other;
                return pv;
            };
            source.EventAt = time => time == deniedSite ? Search(time, "no-site", "No site", Reader)
                : time == deniedUser ? Search(time, "no-user", "No user", other) : Search(time, "late", "Later", Reader);
            _database.Execute("INSERT dbo.org_urls(url_base) VALUES(N'https://contoso.sharepoint.com/sites/example');");
            source.ExtraPageViews.Add((seed, seed));
            await Run();
            foreach (var time in new[] { deniedSite, deniedUser, late }) source.ExtraCustomEvents.Add((time, time));
            source.ExtraPageViews.Add((deniedSite, Start.AddMinutes(35)));
            source.ExtraPageViews.Add((deniedUser, Start.AddMinutes(35)));
            source.ExtraPageViews.Add((late, Start.AddHours(12)));
            clock.Advance(TimeSpan.FromMinutes(10));
            var missed = await Run();
            Assert.AreEqual(2, missed.Days.Sum(d => d.UnmatchedSearches), "An explicitly excluded user is not a retryable search.");
            clock.Advance(TimeSpan.FromMinutes(30));
            var early = await Run();
            Assert.AreEqual(0, early.ReconciledSearches, "Neither an out-of-scope page view nor an excluded user creates an eligible visit.");
            Assert.AreEqual(1, early.PendingSearchHours);
            Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.sessions"));
            clock.Advance(TimeSpan.FromHours(12));
            await Run();
            clock.Advance(TimeSpan.FromDays(3));
            var final = await Run();
            Assert.AreEqual(1, final.ReconciledSearches, "The final pass recovers the visit that arrived after the first retry.");
            Assert.IsTrue(final.Reasons.Contains("SearchRetriesStopped"));
            Assert.AreEqual(0, final.PendingSearchHours);
            Assert.AreEqual(clock.UtcNow, final.CursorAdvancedToUtc);
            Assert.AreEqual((1L, 1L), SearchKpis());
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms WHERE search_term IN (N'No site', N'No user')"));
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.users WHERE user_name = 'outside@contoso.com'"));
            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, (await Run()).Outcome, "A permanently absent visit does not poison later cycles.");
            Assert.AreEqual(2, source.CountRangesRequested.Count(w => w.SearchesOnly));
        }

        private static Task<AppInsightsImportCycleResult> RunCycle(FixedClock clock, FakeAppInsightsSourceLoader source,
            IKeyValueStore state, UserImportScope scope = null, CancellationToken token = default) =>
            new AppInsightsImporter(new AppConfig { CognitiveEndpoint = null }, AnalyticsLogger.ConsoleOnlyTracer(), clock, source,
                contextFactory: _factory, userScope: scope, cursorStore: new KeyValueIngestionCursorStore(state, "contoso-test"))
                .ImportAndSave(false, null, token);

        private static SearchEventAppInsightsQueryResult Search(DateTime sourceTime, string session, string term, string user) =>
            new SearchEventAppInsightsQueryResult
            {
                AppInsightsTimestamp = sourceTime, Username = user,
                CustomProperties = new SearchCustomProps { SessionId = session, SearchText = term, EventTimestamp = Start.AddMilliseconds(3) }
            };

        private static PageViewAppInsightsQueryResult Page(DateTime time, string session) => new PageViewAppInsightsQueryResult
        {
            AppInsightsTimestamp = time, Username = Reader, Browser = "Edge", DeviceModel = "PC", ClientOS = "Windows",
            Url = "https://contoso.sharepoint.com/sites/example/SitePages/Home.aspx",
            CustomProperties = new PageViewCustomProps
            {
                PageRequestId = new Guid((int)(time - Start.Date).TotalSeconds, 0, 0, new byte[8]),
                SessionId = session, SiteUrl = "https://contoso.sharepoint.com/sites/example"
            }
        };

        private static (long Searches, long Visits) SearchKpis()
        {
            using (var connection = new SqlConnection(_database.ConnectionString))
            using (var command = new SqlCommand(WebActivitySql.SearchKpis, connection))
            {
                command.Parameters.AddWithValue("@from", Start.Date);
                command.Parameters.AddWithValue("@to", Start.Date.AddDays(1));
                command.Parameters.AddWithValue("@searchGrace", WebActivitySql.SearchDeadEndGraceSeconds);
                command.Parameters.AddWithValue("@strugglingSearches", 3);
                connection.Open();
                using (var r = command.ExecuteReader())
                {
                    Assert.IsTrue(r.Read());
                    return ((long)r["Searches"], (long)r["SessionsWithSearch"]);
                }
            }
        }
    }
}
