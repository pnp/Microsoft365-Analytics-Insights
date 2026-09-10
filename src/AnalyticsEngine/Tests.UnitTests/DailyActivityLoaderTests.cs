using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Common.Entities;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Graph daily usage-report save loop and finalized-date scan, driven entirely through the
    /// <c>IUsageReportStore</c> port extracted by issue #375. Zero SQL Server, zero Graph, zero HTTP.
    ///
    /// <para>
    /// This loop had no unit tests at all before: the (date, lookup) upsert, the dirty check that decides
    /// whether a re-fetched finalized day costs an UPDATE, the batch boundary that keeps EF6 from
    /// OutOfMemory-ing at 200k users, and the two skip-scan query shapes were all only reachable through a
    /// live database.
    /// </para>
    /// </summary>
    [TestClass]
    public class DailyActivityLoaderTests
    {
        private const string UserA = "alice@contoso.com";
        private const string UserB = "bob@contoso.com";
        private const int UserAId = 11;
        private const int UserBId = 22;

        private static readonly DateTime Day1 = new DateTime(2026, 5, 10);
        private static readonly DateTime Day2 = new DateTime(2026, 5, 11);

        private static ConcurrentLookupDbIdsCache SeededIdCache()
        {
            // Pre-seeded so lookup resolution never needs the DB-backed lookup cache. Keyed by the report
            // ENTITY type and the RAW LookupFieldValue, exactly as ResolveLookupIdAsync does.
            var cache = new ConcurrentLookupDbIdsCache();
            cache.AddOrUpdateForName<FakeUserUsageActivityLog>(UserA, UserAId);
            cache.AddOrUpdateForName<FakeUserUsageActivityLog>(UserB, UserBId);
            return cache;
        }

        private static FakeUserActivityDetail Page(string upn, int thingCount, string lastActivity = null)
            => new FakeUserActivityDetail { UserPrincipalName = upn, ThingCount = thingCount, LastActivityDateString = lastActivity };

        private static FakeUserUsageActivityLog StoredRow(DateTime date, int userId, int thingCount, DateTime? lastActivity = null)
            => new FakeUserUsageActivityLog { ID = userId * 1000, Date = date, UserID = userId, ThingCount = thingCount, LastActivityDate = lastActivity };

        // Runs the save loop against an in-memory store, with no context and no lookup-cache DB access.
        private static async Task<InMemoryUsageReportStore<FakeUserUsageActivityLog>> SaveAsync(
            InMemoryDailyActivityLoader loader, InMemoryUsageReportStore<FakeUserUsageActivityLog> store)
        {
            loader.ReportStore = store;
            await loader.SaveLoadedReportsToSql(SeededIdCache(), new UserCache(null));
            return store;
        }

        [TestMethod]
        public async Task DailyActivityLoader_RunsWithFakeSourceAndFakeStore_WithoutSqlOrGraph()
        {
            // Day 1's key deliberately carries a TIME: Graph pages are keyed by DateTime.UtcNow.AddDays(-n),
            // so the loop must truncate to the date both for the stored value and for the existing-row lookup.
            var day1WithTime = Day1.AddHours(13).AddMinutes(37);

            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance);
            loader.LoadedReportPages[day1WithTime] = new List<FakeUserActivityDetail> { Page(UserA, 5), Page(UserB, 7) };
            loader.LoadedReportPages[Day2] = new List<FakeUserActivityDetail> { Page(UserA, 9) };

            var store = await SaveAsync(loader, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());

            Assert.AreEqual(3, store.Stored.Count, "Every report row with a resolvable lookup should be inserted.");
            Assert.AreEqual(0, store.Updated.Count, "Nothing was stored beforehand, so nothing can be an update.");
            Assert.AreEqual(3, loader.LastSaveDbWriteCount);

            var day1A = store.Stored.Single(r => r.Date == Day1 && r.UserID == UserAId);
            Assert.AreEqual(5, day1A.ThingCount, "Report-specific metadata must reach the stored row.");
            Assert.AreEqual(9, store.Stored.Single(r => r.Date == Day2 && r.UserID == UserAId).ThingCount);

            // Each day is read and released on its own, which is what bounds EF's change tracker.
            CollectionAssert.AreEquivalent(new[] { Day1, Day2 }, store.RowsLoadedForDates.ToArray(),
                "The existing-row query must use the DATE, not the page key's timestamp.");
            Assert.AreEqual(2, store.ReleaseCount);
        }

        [TestMethod]
        public async Task DailyActivityLoader_UnchangedExistingRow_IsNotRewritten()
        {
            // The dominant cost of a re-import at large-tenant scale: a finalized day re-fetched by the
            // recent-window rule is almost always identical to what is stored, so it must cost no UPDATE.
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance);
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 5) };

            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>()
                .Seed(StoredRow(Day1, UserAId, thingCount: 5));

            await SaveAsync(loader, store);

            Assert.AreEqual(0, store.Updated.Count, "An identical row must not be rewritten.");
            Assert.AreEqual(0, store.Inserted.Count, "The stored row must be reused, not duplicated.");
            Assert.AreEqual(0, loader.LastSaveDbWriteCount);
            Assert.AreEqual(0, store.SaveCount, "With nothing to write there is nothing to flush.");
            Assert.AreEqual(1, store.Stored.Count);
        }

        [TestMethod]
        public async Task DailyActivityLoader_ChangedExistingRow_IsUpdatedNotDuplicated()
        {
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance);
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 42) };

            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>()
                .Seed(StoredRow(Day1, UserAId, thingCount: 5));

            await SaveAsync(loader, store);

            Assert.AreEqual(1, store.Updated.Count, "A changed row must be written.");
            Assert.AreEqual(0, store.Inserted.Count, "The (date, lookup) row already exists - updating it must not insert a second one.");
            Assert.AreEqual(1, store.Stored.Count);
            Assert.AreEqual(42, store.Stored[0].ThingCount);
            Assert.AreEqual(1, loader.LastSaveDbWriteCount);
        }

        [TestMethod]
        public async Task DailyActivityLoader_EmptyLastActivityDate_LeavesTheStoredValueAlone()
        {
            // Three different Graph shapes for lastActivityDate, asserted against an EXISTING row that
            // already holds a date - on a new row "left alone" and "set to null" are indistinguishable.
            var stored = new DateTime(2026, 1, 2);

            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance);
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 5, lastActivity: "") };

            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>()
                .Seed(StoredRow(Day1, UserAId, thingCount: 5, lastActivity: stored));

            await SaveAsync(loader, store);

            Assert.AreEqual(stored, store.Stored[0].LastActivityDate,
                "An empty lastActivityDate means 'Graph said nothing', which must not erase what is stored.");
            Assert.AreEqual(0, loader.LastSaveDbWriteCount, "Nothing changed, so there is nothing to write.");
            Assert.AreEqual(0, store.SaveCount);
        }

        [TestMethod]
        public async Task DailyActivityLoader_ValidLastActivityDate_IsParsed_InvalidIsNulled()
        {
            // The invalid case is asserted against an EXISTING row that already holds a date: on a new row
            // "nulled" and "never set" both read as null, so it would pass even if the null-out were removed.
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance);
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail>
            {
                Page(UserA, 1, lastActivity: "2026-05-04"),
                Page(UserB, 2, lastActivity: "04/05/2026"),   // right day, wrong format - Graph only ever sends ISO
            };

            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>()
                .Seed(StoredRow(Day1, UserBId, thingCount: 2, lastActivity: new DateTime(2026, 1, 2)));

            await SaveAsync(loader, store);

            Assert.AreEqual(new DateTime(2026, 5, 4), store.Stored.Single(r => r.UserID == UserAId).LastActivityDate);
            Assert.IsNull(store.Stored.Single(r => r.UserID == UserBId).LastActivityDate,
                "An unparseable lastActivityDate must overwrite the stored value with null, not be guessed at or ignored.");
            Assert.AreEqual(1, store.Updated.Count, "Nulling the stored date is a real change and must be written.");
        }

        [TestMethod]
        public async Task DailyActivityLoader_RowWithNoLookupIdentifier_IsSkipped()
        {
            // Graph sends a null userPrincipalName when report anonymisation is on. Such a row cannot be
            // matched to a lookup, and must be dropped rather than taken down the resolve path.
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance);
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail>
            {
                Page(null, 1),
                Page("   ", 2),
                Page(UserA, 3),
            };

            var store = await SaveAsync(loader, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());

            Assert.AreEqual(1, store.Stored.Count, "Only the row with a usable identifier should be saved.");
            Assert.AreEqual(UserAId, store.Stored[0].UserID);
        }

        [TestMethod]
        public async Task DailyActivityLoader_OutOfScopeUser_IsSkipped()
        {
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                InScopeRule = upn => upn == UserA,
            };
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 1), Page(UserB, 2) };

            var store = await SaveAsync(loader, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());

            Assert.AreEqual(1, store.Stored.Count, "The group filter must exclude the out-of-scope user's row.");
            Assert.AreEqual(UserAId, store.Stored[0].UserID);
        }

        [TestMethod]
        public async Task DailyActivityLoader_FlushesOnceTheBatchIsFull_AndAgainForTheRemainder()
        {
            // The batch boundary is why a 200k-user tenant does not OutOfMemory EF6: without it every
            // pending row across every date is built into one command tree.
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance) { SaveBatchSize = 2 };
            var idCache = new ConcurrentLookupDbIdsCache();
            var pages = new List<FakeUserActivityDetail>();
            for (var user = 1; user <= 5; user++)
            {
                var upn = $"user{user}@contoso.com";
                idCache.AddOrUpdateForName<FakeUserUsageActivityLog>(upn, user);
                pages.Add(Page(upn, user));
            }
            loader.LoadedReportPages[Day1] = pages;

            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            loader.ReportStore = store;
            await loader.SaveLoadedReportsToSql(idCache, new UserCache(null));

            Assert.AreEqual(3, store.SaveCount, "5 writes at a batch size of 2 is two full batches plus the remainder.");
            Assert.AreEqual(5, store.Stored.Count);
        }

        [TestMethod]
        public async Task DailyActivityLoader_SaveLoopRunsEntirelyInsideOneBulkWriteScope()
        {
            // EF6 change auto-detection off is what keeps adding a day's rows O(n) instead of O(n^2); no
            // row-count assertion would notice it being dropped.
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance);
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 1) };
            loader.LoadedReportPages[Day2] = new List<FakeUserActivityDetail> { Page(UserA, 2) };

            var store = await SaveAsync(loader, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());

            Assert.AreEqual(1, store.BulkWriteScopesOpened, "One scope for the whole save, not one per day.");
            Assert.AreEqual(1, store.BulkWriteScopesDisposed, "The scope must be left, or the context stays in bulk mode.");
            Assert.IsTrue(store.AllWritesInsideBulkScope, "Every add, dirty check and flush must happen inside the scope.");
        }

        [TestMethod]
        public async Task DailyActivityLoader_SaveThrowing_StillLeavesTheBulkWriteScope()
        {
            // The scope is left on the failure path too - the context may be reused by the next report.
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance);
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 1) };

            var store = new ThrowOnSaveUsageReportStore();
            loader.ReportStore = store;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => loader.SaveLoadedReportsToSql(SeededIdCache(), new UserCache(null)));

            Assert.AreEqual(1, store.BulkWriteScopesOpened);
            Assert.AreEqual(1, store.BulkWriteScopesDisposed, "A failed save must still restore change auto-detection.");
        }

        [TestMethod]
        public async Task DailyActivityLoader_SkippedDates_AreNotRequestedFromGraph()
        {
            // The whole point of the finalized-date rule: a skipped day costs no (often slow) paged download.
            var now = new DateTime(2026, 6, 20, 11, 15, 0, DateTimeKind.Utc);
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance) { Clock = new FixedClock(now) };
            var yesterday = now.Date.AddDays(-1);
            var twoDaysAgo = now.Date.AddDays(-2);
            loader.PagesByDate[yesterday] = new List<FakeUserActivityDetail> { Page(UserA, 1) };
            loader.PagesByDate[twoDaysAgo] = new List<FakeUserActivityDetail> { Page(UserA, 2) };

            await loader.PopulateLoadedReportPagesFromGraph(daysBackMax: 3, datesToSkip: new HashSet<DateTime> { twoDaysAgo });

            CollectionAssert.DoesNotContain(loader.GraphRequests, twoDaysAgo, "A skipped date must not be downloaded.");
            CollectionAssert.Contains(loader.GraphRequests, yesterday, "A non-skipped date must still be downloaded.");
            Assert.IsFalse(loader.LoadedReportPages.Keys.Any(k => k.Date == twoDaysAgo),
                "A skipped date must not appear as a loaded (and therefore savable) page.");
            Assert.IsTrue(loader.LoadedReportPages.Keys.Any(k => k.Date == yesterday));
        }

        [TestMethod]
        public async Task DailyActivityLoader_NoCompletedImportPhase_DoesNotEvenAskStorage()
        {
            // Until a usage-report phase completes, stored rows may be from an interrupted save, so there
            // is nothing to ask about - and asking would cost a scan of the biggest table for nothing.
            var now = new DateTime(2026, 6, 20, 11, 15, 0, DateTimeKind.Utc);
            var inspector = new FakeUsageReportStorageInspector();
            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>()
                .Seed(StoredRow(now.Date.AddDays(-6), UserAId, 1));
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                Clock = new FixedClock(now),
                StorageInspector = inspector,
                ReportStore = store,
            };

            var skip = await loader.GetFinalizedStoredDatesToSkipAsync(null, daysBackMax: 10, lastSuccessfulImport: null);

            Assert.AreEqual(0, skip.Count);
            Assert.AreEqual(0, store.ExistenceProbes.Count);
            Assert.AreEqual(0, store.RangeScans.Count);
            Assert.AreEqual(0, inspector.IndexQuestionsAsked.Count, "The index question is only worth asking if something could be skipped.");
        }

        [TestMethod]
        public void DailyActivityLoader_DefaultClock_IsTheSystemClock()
        {
            // The injected clock must be an opt-in for tests only: production reads DateTime.UtcNow exactly
            // as it did before, so the import window is unchanged. Identity is the whole assertion - that
            // SystemClock reports UTC is already pinned by ClockAndContextFactoryTests.SystemClock_ReturnsUtcKind,
            // and re-checking it here against a tolerance would only add a preemption flake.
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance);

            Assert.AreSame(SystemClock.Instance, loader.Clock);
        }

        [TestMethod]
        public async Task DailyActivityLoader_IndexedTable_ProbesEachCandidateDateInsteadOfScanning()
        {
            // With a date-leading index the scan is one bounded seek per candidate date; a DISTINCT over
            // the range would still touch every user's row for every date (millions at 200k users).
            //
            // The clock is FIXED: the loader reads "now" itself, so a test asserting exact window bounds
            // against its own DateTime.UtcNow would disagree with the loader whenever the two reads
            // straddle UTC midnight.
            var now = new DateTime(2026, 6, 20, 11, 15, 0, DateTimeKind.Utc);
            var today = now.Date;
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                Clock = new FixedClock(now),
                StorageInspector = new FakeUsageReportStorageInspector(hasLeadingDateIndex: true),
            };
            var stored = today.AddDays(-6);
            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>().Seed(StoredRow(stored, UserAId, 1));
            loader.ReportStore = store;

            var skip = await loader.GetFinalizedStoredDatesToSkipAsync(null, daysBackMax: 10, lastSuccessfulImport: now);

            Assert.AreEqual(0, store.RangeScans.Count, "The indexed branch must not fall back to a range scan.");
            CollectionAssert.AreEqual(
                Enumerable.Range(0, 7).Select(i => today.AddDays(-10 + i)).ToArray(),
                store.ExistenceProbes.ToArray(),
                "Every candidate date from the window start up to the 3-day mutability cutoff should be probed, oldest first.");
            CollectionAssert.AreEqual(new[] { stored }, skip.ToArray(), "Only dates that actually have rows can be skipped.");
        }

        [TestMethod]
        public async Task DailyActivityLoader_UnindexedTable_UsesOneRangeScanForTheWholeWindow()
        {
            var now = new DateTime(2026, 6, 20, 11, 15, 0, DateTimeKind.Utc);
            var today = now.Date;
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                Clock = new FixedClock(now),
                StorageInspector = new FakeUsageReportStorageInspector(hasLeadingDateIndex: false),
            };
            var storedInWindow = today.AddDays(-6);
            var storedTooRecent = today.AddDays(-1);
            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>()
                .Seed(StoredRow(storedInWindow, UserAId, 1), StoredRow(storedTooRecent, UserBId, 1));
            loader.ReportStore = store;

            var skip = await loader.GetFinalizedStoredDatesToSkipAsync(null, daysBackMax: 10, lastSuccessfulImport: now);

            Assert.AreEqual(0, store.ExistenceProbes.Count, "Without a date-leading index each probe would scan the table.");
            Assert.AreEqual(1, store.RangeScans.Count, "One scan for the whole window, not one per date.");
            Assert.AreEqual(today.AddDays(-10), store.RangeScans[0].Item1);
            Assert.AreEqual(today.AddDays(-3), store.RangeScans[0].Item2);
            CollectionAssert.AreEqual(new[] { storedInWindow }, skip.ToArray(),
                "A stored date inside the 3-day mutability window can still change in Graph and must be re-imported.");
        }



        [TestMethod]
        public async Task DailyActivityLoader_InstrumentationCountsColdWarmMissingScopeAndRowOutcomes()
        {
            var instrumentation = new RecordingUsageReportSaveInstrumentation();
            var lookupCalls = new ConcurrentDictionary<string, int>();
            CountingUserActivityDetail.ResolveAsync = upn =>
            {
                lookupCalls.AddOrUpdate(upn, 1, (_, old) => old + 1);
                return Task.FromResult(upn == UserA ? UserAId : UserBId);
            };

            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                SaveInstrumentation = instrumentation,
                InScopeRule = upn => upn != "outofscope@contoso.com",
            };
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail>
            {
                new CountingUserActivityDetail(UserA, 10),       // changed existing
                new CountingUserActivityDetail(UserB, 20),       // new
                new CountingUserActivityDetail(null, 30),        // intentionally missing lookup
                new CountingUserActivityDetail("outofscope@contoso.com", 40),
            };
            loader.LoadedReportPages[Day2] = new List<FakeUserActivityDetail>
            {
                new CountingUserActivityDetail(UserA, 10),       // repeated user, cache hit after day 1
            };
            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>()
                .Seed(StoredRow(Day1, UserAId, thingCount: 5));
            loader.ReportStore = store;
            var cache = new ConcurrentLookupDbIdsCache();

            await loader.SaveLoadedReportsToSql(cache, new UserCache(null));

            Assert.AreEqual(1, lookupCalls[UserA], "Cold lookup should hit the backing lookup once; the repeated row is a cache hit.");
            Assert.AreEqual(1, lookupCalls[UserB]);
            Assert.AreEqual(3, store.Stored.Count);
            Assert.AreEqual(2, loader.LastSaveDbWriteCount);

            var completed = instrumentation.Single(UsageReportSaveStageIds.SaveCompleted);
            AssertMetric(completed, "LookupDatabaseCallCount", 2);
            AssertMetric(completed, "LookupCacheMissCount", 2);
            AssertMetric(completed, "LookupCacheHitCount", 1);
            AssertMetric(completed, "MissingLookupValueRowCount", 1);
            AssertMetric(completed, "OutOfScopeRowCount", 1);
            AssertMetric(completed, "AddedRowCount", 2);
            AssertMetric(completed, "ChangedRowCount", 1);

            instrumentation.Events.Clear();
            lookupCalls.Clear();
            await loader.SaveLoadedReportsToSql(cache, new UserCache(null));

            var warm = instrumentation.Single(UsageReportSaveStageIds.SaveCompleted);
            Assert.AreEqual(0, lookupCalls.Count, "Warm lookup-cache run must not call the backing lookup store.");
            AssertMetric(warm, "LookupDatabaseCallCount", 0);
            AssertMetric(warm, "LookupCacheHitCount", 3);
            AssertMetric(warm, "UnchangedRowCount", 3);
            Assert.AreEqual(0, loader.LastSaveDbWriteCount);
        }

        [TestMethod]
        public async Task DailyActivityLoader_InstrumentationCountsDuplicateConcurrentMissesAcrossLoaders()
        {
            var instrumentation = new RecordingUsageReportSaveInstrumentation();
            var lookupCalls = new ConcurrentDictionary<string, int>();
            var ids = new Dictionary<string, int>
            {
                { UserA, UserAId },
                { "loader1only@contoso.com", 101 },
                { "loader2only@contoso.com", 202 },
            };
            CountingUserActivityDetail.ResolveAsync = async upn =>
            {
                lookupCalls.AddOrUpdate(upn, 1, (_, old) => old + 1);
                await Task.Delay(50);
                return ids[upn];
            };

            var cache = new ConcurrentLookupDbIdsCache();
            var loader1 = new InMemoryDailyActivityLoader(NullLogger.Instance) { SaveInstrumentation = instrumentation };
            var loader2 = new InMemoryDailyActivityLoader(NullLogger.Instance) { SaveInstrumentation = instrumentation };
            loader1.ReportStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            loader2.ReportStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            loader1.LoadedReportPages[Day1] = new List<FakeUserActivityDetail>
            {
                new CountingUserActivityDetail(UserA, 1),
                new CountingUserActivityDetail("loader1only@contoso.com", 10),
            };
            loader2.LoadedReportPages[Day1] = new List<FakeUserActivityDetail>
            {
                new CountingUserActivityDetail(UserA, 2),
                new CountingUserActivityDetail("loader2only@contoso.com", 20),
            };

            await Task.WhenAll(
                loader1.SaveLoadedReportsToSql(cache, new UserCache(null)),
                loader2.SaveLoadedReportsToSql(cache, new UserCache(null)));

            Assert.AreEqual(2, lookupCalls[UserA], "Both loaders intentionally miss before either can populate the shared cache.");
            Assert.AreEqual(1, lookupCalls["loader1only@contoso.com"], "Non-overlapping users still resolve independently.");
            Assert.AreEqual(1, lookupCalls["loader2only@contoso.com"], "Non-overlapping users still resolve independently.");
            Assert.AreEqual(1d, instrumentation.Events.Where(e => e.Stage == UsageReportSaveStageIds.SaveCompleted).Sum(e => Metric(e, "DuplicateConcurrentMissCount")),
                "The second resolver must record that another loader populated the cache while it awaited the lookup.");
        }

        [TestMethod]
        public async Task DailyActivityLoader_RetryAfterIntermediateBatchFailure_IsIdempotentAndStampedByHealthyRun()
        {
            CountingUserActivityDetail.ResolveAsync = upn => Task.FromResult(int.Parse(upn.Substring(4, 1)));
            var failingInstrumentation = new RecordingUsageReportSaveInstrumentation();
            var failingLoader = new InMemoryDailyActivityLoader(NullLogger.Instance) { SaveBatchSize = 2, SaveInstrumentation = failingInstrumentation };
            var rows = Enumerable.Range(1, 5)
                .Select(i => new CountingUserActivityDetail($"user{i}@contoso.com", i))
                .Cast<FakeUserActivityDetail>()
                .ToList();
            failingLoader.LoadedReportPages[Day1] = rows;
            var failingStore = new FailOnSaveNumberUsageReportStore(2);
            failingLoader.ReportStore = failingStore;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => failingLoader.SaveLoadedReportsToSql(new ConcurrentLookupDbIdsCache(), new UserCache(null)));
            Assert.IsTrue(failingInstrumentation.Events.Any(e => e.Stage == UsageReportSaveStageIds.SaveFailed));
            Assert.AreEqual(2, failingStore.Stored.Count, "Only the first healthy batch should remain committed after the injected failure.");

            var retryInstrumentation = new RecordingUsageReportSaveInstrumentation();
            var retryLoader = new InMemoryDailyActivityLoader(NullLogger.Instance) { SaveBatchSize = 2, SaveInstrumentation = retryInstrumentation };
            retryLoader.LoadedReportPages[Day1] = rows;
            var retryStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>()
                .Seed(failingStore.Stored.Select(CloneRow).ToArray());
            retryLoader.ReportStore = retryStore;

            await retryLoader.SaveLoadedReportsToSql(new ConcurrentLookupDbIdsCache(), new UserCache(null));

            Assert.AreEqual(5, retryStore.Stored.Count);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, retryStore.Stored.OrderBy(r => r.UserID).Select(r => r.UserID).ToArray());
            Assert.AreEqual(3, retryLoader.LastSaveDbWriteCount, "The retry must not rewrite the two rows already committed by the failed attempt.");
            Assert.IsTrue(retryInstrumentation.Events.Any(e => e.Stage == UsageReportSaveStageIds.SaveCompleted));
            Assert.IsFalse(retryInstrumentation.Events.Any(e => e.Stage == UsageReportSaveStageIds.SaveFailed));
        }



        private static FakeUserUsageActivityLog CloneRow(FakeUserUsageActivityLog row)
            => new FakeUserUsageActivityLog
            {
                ID = row.ID,
                Date = row.Date,
                UserID = row.UserID,
                ThingCount = row.ThingCount,
                LastActivityDate = row.LastActivityDate,
            };

        private static void AssertMetric(UsageReportSaveTelemetryPoint point, string name, double expected)
            => Assert.AreEqual(expected, Metric(point, name), $"Metric {name}");

        private static double Metric(UsageReportSaveTelemetryPoint point, string name)
            => point.Metrics.TryGetValue(name, out var value) ? value : 0;

        private sealed class CountingUserActivityDetail : FakeUserActivityDetail
        {
            public static Func<string, Task<int>> ResolveAsync { get; set; }

            public CountingUserActivityDetail(string upn, int thingCount)
            {
                UserPrincipalName = upn;
                ThingCount = thingCount;
            }

            public override async Task<AbstractEFEntity> GetOrCreateLookup(DBLookupCache<User> lookupCache)
                => new User { ID = await ResolveAsync(UserPrincipalName), UserPrincipalName = UserPrincipalName };
        }

        private sealed class RecordingUsageReportSaveInstrumentation : IUsageReportSaveInstrumentation
        {
            private readonly object _lock = new object();
            public bool IsEnabled => true;
            public List<UsageReportSaveTelemetryPoint> Events { get; } = new List<UsageReportSaveTelemetryPoint>();
            public void Track(UsageReportSaveTelemetryPoint point)
            {
                lock (_lock)
                {
                    Events.Add(point);
                }
            }
            public UsageReportSaveTelemetryPoint Single(string stage) => Events.Single(e => e.Stage == stage);
        }

        private sealed class FailOnSaveNumberUsageReportStore : InMemoryUsageReportStore<FakeUserUsageActivityLog>
        {
            private readonly int _saveNumberToFail;
            private int _saveCalls;

            public FailOnSaveNumberUsageReportStore(int saveNumberToFail) => _saveNumberToFail = saveNumberToFail;

            public override async Task SaveChangesAsync()
            {
                _saveCalls++;
                if (_saveCalls == _saveNumberToFail)
                {
                    throw new InvalidOperationException("Synthetic intermediate batch failure.");
                }
                await base.SaveChangesAsync();
            }
        }


        /// <summary>
        /// Store whose flush always fails, for the "the bulk-write scope is left even on failure" case.
        /// Fails as a FAULTED TASK, never a synchronous throw: a synchronous throw would be caught even by
        /// a caller that forgot to await, so it could not tell the two apart.
        /// </summary>
        private sealed class ThrowOnSaveUsageReportStore : InMemoryUsageReportStore<FakeUserUsageActivityLog>
        {
            public override Task SaveChangesAsync()
                => Task.FromException(new InvalidOperationException("Simulated flush failure."));
        }
    }
}
