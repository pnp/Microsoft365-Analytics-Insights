using Common.Entities.LookupCaches;
using Common.Entities.CopilotAdoption;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Common.Entities;
using Common.Entities.State;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Rules;

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
            // Pre-seeded so lookup resolution never needs the DB-backed lookup cache. Keyed by the LOOKUP type
            // (User) and the RAW LookupFieldValue, exactly as ResolveLookupIdAsync does (#705).
            var cache = new ConcurrentLookupDbIdsCache();
            cache.AddOrUpdateForName<User>(UserA, UserAId);
            cache.AddOrUpdateForName<User>(UserB, UserBId);
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
        public async Task DailyActivityLoader_SkippedRows_AreReportedOncePerDate_NeverPerRow_AndNeverByIdentifier()
        {
            // With a pilot UserGroupsFilter nearly every row of a full window is out of scope: that used to be one Information line
            // per row, carrying the UPN - millions of lines and a bulk list of identities in the log.
            var logger = new RecordingLogger();
            var loader = new InMemoryDailyActivityLoader(logger) { InScopeRule = upn => upn == UserA };
            var day1 = new List<FakeUserActivityDetail> { Page(UserA, 1) };
            day1.AddRange(Enumerable.Range(1, 50).Select(i => Page($"pilot.{i}@contoso.com", i)));
            day1.AddRange(new[] { Page(null, 1), Page(" ", 2), Page("", 3) });
            loader.LoadedReportPages[Day1] = day1;
            loader.LoadedReportPages[Day2] = Enumerable.Range(1, 20).Select(i => Page($"pilot.{i}@contoso.com", i)).ToList();

            var store = await SaveAsync(loader, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());

            Assert.AreEqual(1, store.Stored.Count);
            var all = string.Join(" | ", logger.Entries.Select(e => e.Message));
            Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("pilot.") || e.Message.Contains("@contoso.com")), "No identifier is logged: " + all);

            var outOfScope = logger.Entries.Where(e => e.Message.Contains("outside the configured user scope")).ToList();
            Assert.AreEqual(2, outOfScope.Count, "One line per date: " + all);
            Assert.AreEqual(LogLevel.Information, outOfScope[0].Level);
            StringAssert.Contains(outOfScope[0].Message, "skipped 50 of 54 row(s) for 2026-05-10");
            StringAssert.Contains(outOfScope[1].Message, "skipped 20 of 20 row(s) for 2026-05-11");

            var missing = logger.Entries.Where(e => e.Message.Contains("no user or group identifier")).ToList();
            Assert.AreEqual(1, missing.Count, "Aggregated into one line for the date, not one per row: " + all);
            Assert.AreEqual(LogLevel.Warning, missing[0].Level);
            StringAssert.Contains(missing[0].Message, "skipped 3 row(s) for 2026-05-10");
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
                idCache.AddOrUpdateForName<User>(upn, user);
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
        public async Task DailyCoverage_StoredButUncertifiedDatesAreReimportedOnce()
        {
            // #899: a day stamped "partial" while it was still refreshable (or imported before the ledger
            // existed) must be downloaded once more after it settles, or it can never be certified.
            var now = new DateTime(2026, 6, 20, 11, 15, 0, DateTimeKind.Utc);
            var today = now.Date;
            var certified = today.AddDays(-6);
            var uncertified = today.AddDays(-5);
            var wronglyEmpty = today.AddDays(-7);
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            await coverage.CompleteDayAsync("teams", certified, true, true, false, now, 3);
            await coverage.CompleteDayAsync("teams", uncertified, true, true, false, uncertified, 3);
            await coverage.CompleteDayAsync("teams", wronglyEmpty, true, true, true, now, 3);
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                Clock = new FixedClock(now),
                StorageInspector = new FakeUsageReportStorageInspector(hasLeadingDateIndex: true),
                CoverageStore = coverage, CoverageSource = "teams", CoverageWholeTenant = true,
            };
            loader.ReportStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>()
                .Seed(StoredRow(certified, UserAId, 1), StoredRow(uncertified, UserAId, 1), StoredRow(wronglyEmpty, UserAId, 1));

            var skip = await loader.GetFinalizedStoredDatesToSkipAsync(null, daysBackMax: 10, lastSuccessfulImport: now);

            CollectionAssert.AreEqual(new[] { certified }, skip.ToArray());

            loader.CoverageStore = new UsageReportCoverageStore(new InMemoryKeyValueStore(), false);
            skip = await loader.GetFinalizedStoredDatesToSkipAsync(null, daysBackMax: 10, lastSuccessfulImport: now);
            CollectionAssert.AreEquivalent(new[] { certified, uncertified, wronglyEmpty }, skip.ToArray(),
                "Without durable state nothing can be certified, so the existing skip behaviour is kept.");
        }

        [TestMethod]
        public async Task ALedgerReadFailureDoesNotForgetAlreadyCheckedOutcomesOrAssumeAnUnreadDayWasEmpty()
        {
            var now = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
            var day = now.Date.AddDays(-7);
            var state = new InMemoryKeyValueStore();
            var seed = new UsageReportCoverageStore(state, true);
            await seed.BeginDayAsync("teams", day);
            await seed.CompleteDayAsync("teams", day.AddDays(1), true, true, true, now, 3);
            var failing = new CoverageFailureStore(state) { FailReadOn = 3 };
            var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>().Seed(
                StoredRow(day, UserAId, 1), StoredRow(day.AddDays(2), UserBId, 1));
            var loader = CoverageLoader(now, new UsageReportCoverageStore(failing, true), sql);
            loader.PreviousWindow = new DailyReportWindow
            {
                HasRows = new Dictionary<DateTime, bool> { [day] = true, [day.AddDays(1)] = false,
                    [day.AddDays(2)] = true, [day.AddDays(3)] = false },
            };

            var skip = await loader.GetFinalizedStoredDatesToSkipAsync(null, 7, now);
            CollectionAssert.AreEquivalent(new[] { day.AddDays(1), day.AddDays(2) }, skip.ToArray(),
                "Keep the verified empty and the unread stored-row fallback, not a known partial or an unconfirmed empty.");
        }

        private static InMemoryDailyActivityLoader CoverageLoader(DateTime nowUtc, UsageReportCoverageStore coverage,
            InMemoryUsageReportStore<FakeUserUsageActivityLog> sql, bool wholeTenant = true)
        {
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                Clock = new FixedClock(nowUtc),
                StorageInspector = new FakeUsageReportStorageInspector(hasLeadingDateIndex: true),
                CoverageStore = coverage, CoverageSource = "teams", CoverageWholeTenant = wholeTenant,
            };
            loader.ReportStore = sql;
            return loader;
        }

        private static async Task ImportCycleAsync(InMemoryDailyActivityLoader loader, int daysBack, DateTime? lastSuccessfulImport)
        {
            var skip = await loader.GetFinalizedStoredDatesToSkipAsync(null, daysBack, lastSuccessfulImport);
            await loader.LoadAndSaveEachDateAsync(daysBack, skip, null, SeededIdCache(), new UserCache(null));
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task ADayCertifiedAtAgeTwoStillGetsItsMatureRefreshAfterAMissedRun(bool indexed)
        {
            var day = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc);
            var clock = new FixedClock(day.AddDays(3).AddMinutes(-1));
            var schedule = new InMemoryKeyValueStore();
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            var matureEmpty = day.AddDays(-1);
            var matureWithRows = day.AddDays(-2);

            async Task<InMemoryDailyActivityLoader> Run(bool early = false)
            {
                // A fresh scheduler and loader per cycle, with only the persisted state and SQL surviving.
                var importer = new GraphImporter(AnalyticsLogger.ConsoleOnlyTracer(), null, null, null, new Common.Entities.Config.AppConfig(),
                    null, null, null, clock, new PersistedReportCompletionStore(schedule));
                var loader = CoverageLoader(clock.UtcNow, coverage, sql);
                loader.Clock = clock;
                loader.StorageInspector = new FakeUsageReportStorageInspector(indexed);
                loader.PagesByDate[matureWithRows] = new List<FakeUserActivityDetail> { Page(UserA, 2) };
                loader.PagesByDate[day] = early ? new List<FakeUserActivityDetail> { Page(UserA, 1) }
                    : new List<FakeUserActivityDetail> { Page(UserA, 3), Page(UserB, 4) };
                if (early)
                    loader.AfterGraphRequest = date => { if (date == day) clock.Advance(TimeSpan.FromMinutes(2)); };
                await importer.LoadAndSaveDailyImportReport(loader, 8, "Synthetic report", NullLogger.Instance, SeededIdCache(), null);
                return loader;
            }

            await Run(early: true);
            Assert.IsTrue(await coverage.IsDayVerifiedAsync("teams", day), "Dashboards can use the complete early response.");
            Assert.AreEqual(day.AddDays(3), clock.UtcNow.Date, "The request was D+2 but its completion stamp was already D+3.");
            Assert.AreEqual("empty", await TeamsStatusAsync(coverage, matureEmpty));

            clock.Advance(TimeSpan.FromDays(1)); // no new request on D+3
            var late = await Run();
            CollectionAssert.Contains(late.GraphRequests, day, "The D+2 response was too early to freeze permanently.");
            CollectionAssert.DoesNotContain(late.GraphRequests, matureWithRows);
            CollectionAssert.DoesNotContain(late.GraphRequests, matureEmpty, "A mature, verified empty can settle without a fake SQL row.");
            CollectionAssert.AreEquivalent(new[] { 3, 4 }, sql.Stored.Where(row => row.Date == day).Select(row => row.ThingCount).ToArray());

            clock.Advance(TimeSpan.FromDays(1));
            var settled = await Run();
            CollectionAssert.DoesNotContain(settled.GraphRequests, day, "The successful mature refresh is final.");
            CollectionAssert.DoesNotContain(settled.GraphRequests, matureEmpty, "A skipped empty remains final on subsequent cycles.");

            sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>().Seed(
                sql.Stored.Where(row => row.Date != matureWithRows).Concat(new[] { StoredRow(matureEmpty, UserAId, 1) }).ToArray());
            clock.Advance(TimeSpan.FromDays(1));
            var restored = await Run();
            CollectionAssert.Contains(restored.GraphRequests, matureWithRows, "A missing SQL day is not the mature nonempty day that was imported.");
            CollectionAssert.Contains(restored.GraphRequests, matureEmpty, "A day with retained rows cannot use an old empty certificate.");
            Assert.IsFalse(await coverage.IsDayVerifiedAsync("teams", matureEmpty));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ATimestampWithoutItsMatchingMatureWindowIsRecheckedOnceWithinTheConfiguredLookback(bool timestampOnlyStore)
        {
            var now = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
            var day = now.Date.AddDays(-6);
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(now);
            var publicStamp = new KeyValueSingleDateStore(state, PersistedReportCompletionStore.KeyPrefix + nameof(InMemoryDailyActivityLoader));
            var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>().Seed(StoredRow(day, UserAId, 1));
            GraphImporter Importer(IReportCompletionStore store) =>
                new GraphImporter(AnalyticsLogger.ConsoleOnlyTracer(), null, null, null, new Common.Entities.Config.AppConfig(),
                    null, null, null, clock, store);
            // A custom timestamp-only store can reuse its matching window only within this process.
            var processLocal = Importer(new TimestampOnlyStore(new PersistedReportCompletionStore(state)));

            async Task<List<DateTime>> Run(int value)
            {
                var importer = timestampOnlyStore ? processLocal : Importer(new PersistedReportCompletionStore(state));
                var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
                {
                    Clock = clock, StorageInspector = new FakeUsageReportStorageInspector(), ReportStore = sql,
                };
                loader.PagesByDate[day] = new List<FakeUserActivityDetail> { Page(UserA, value) };
                await importer.LoadAndSaveDailyImportReport(loader, 6, "Synthetic report", NullLogger.Instance, SeededIdCache(), null);
                return loader.GraphRequests;
            }

            var configuredDates = Enumerable.Range(1, 6).Select(back => now.Date.AddDays(-back)).ToArray();
            await publicStamp.SaveDT(now.AddHours(-2)); // timestamp-only installation
            CollectionAssert.AreEqual(configuredDates, await Run(2));
            Assert.AreEqual(2, sql.Stored.Single().ThingCount);
            CollectionAssert.AreEqual(configuredDates.Take(3).ToArray(), await Run(3));

            await publicStamp.SaveDT(now.AddHours(-1)); // an older build rewrote only the public stamp
            CollectionAssert.AreEqual(configuredDates, await Run(4));
            Assert.AreEqual(4, sql.Stored.Single().ThingCount);
            CollectionAssert.AreEqual(configuredDates.Take(3).ToArray(), await Run(5));
        }

        private sealed class TimestampOnlyStore : IReportCompletionStore
        {
            private readonly IReportCompletionStore _inner;
            public TimestampOnlyStore(IReportCompletionStore inner) => _inner = inner;
            public Task<DateTime?> GetLastSuccessAsync(string reportKey) => _inner.GetLastSuccessAsync(reportKey);
            public Task SaveSuccessAsync(string reportKey) => _inner.SaveSuccessAsync(reportKey);
            public Task ClearAsync(string reportKey) => _inner.ClearAsync(reportKey);
        }

        [TestMethod]
        public async Task DailyCoverage_AnalysisOnDayT_BeforeDayTsImport_SeesTheNewestAssessedDayVerified()
        {
            // #899 review: the analysis on day T reads through T - UsageReportLagDays. The import of day T - 1
            // must already have certified that day, or every workload is unverified until day T's import ends.
            var dayT = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            var yesterday = CoverageLoader(dayT.AddDays(-1).AddHours(10), coverage, sql);
            for (var day = 2; day <= 10; day++)
                yesterday.PagesByDate[dayT.AddDays(-day)] = new List<FakeUserActivityDetail> { Page(UserA, day) };
            await ImportCycleAsync(yesterday, 9, null);

            var settled = CopilotAdoptionAgentGrowth.LastSettledDay(dayT.AddMinutes(30), CopilotAdoptionOptions.Default);
            Assert.AreEqual(dayT.AddDays(-3), settled);
            var teams = (await coverage.ReadAsync(dayT.AddDays(-9), settled)).Single(c => c.Source == "teams");
            Assert.IsTrue(teams.IsComplete, $"Expected the whole assessed window verified, got {teams.Status}/{teams.Reason}.");
            var withUnsettled = (await coverage.ReadAsync(dayT.AddDays(-9), dayT.AddDays(-2))).Single(c => c.Source == "teams");
            Assert.AreEqual(1, withUnsettled.ExpectedDays - withUnsettled.VerifiedDays,
                "T - 2 is not settled for the analysis yet and stays uncertified.");

        }

        [TestMethod]
        public async Task DailyCoverage_AnEmptyDayInsideTheLag_IsNotCertifiedEmpty_UntilItSettles()
        {
            // Graph answers a day it has not published yet with 200 and no rows, so zero rows inside the lag
            // cannot be told apart from "not available yet".
            var dayT = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            await ImportCycleAsync(CoverageLoader(dayT.AddDays(-1).AddHours(10), coverage, sql), 3, null);
            var newest = (await coverage.ReadAsync(dayT.AddDays(-3), dayT.AddDays(-2))).Single(c => c.Source == "teams");
            Assert.AreEqual(0, newest.VerifiedDays);
            Assert.AreEqual(0, newest.EmptyDays);
            Assert.AreEqual(2, newest.PartialDays);

            await ImportCycleAsync(CoverageLoader(dayT.AddHours(10), coverage, sql), 3, null);
            var settled = (await coverage.ReadAsync(dayT.AddDays(-3), dayT.AddDays(-3))).Single(c => c.Source == "teams");
            Assert.AreEqual("empty", settled.Status, "Once fully settled, a day Graph still reports empty is successfully empty.");
        }

        /// <summary>
        /// Downloads <paramref name="day"/> alone with the clock at <paramref name="requestedUtc"/>, then moves the
        /// clock on by <paramref name="saveTakes"/> before the save - as pre-resolution and the upsert of a large
        /// tenant's day can - and returns the day's coverage status.
        /// </summary>
        private static async Task<string> CoverageStatusAfterSlowSaveAsync(
            DateTime day, DateTime requestedUtc, TimeSpan saveTakes, bool withRows)
        {
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var loader = CoverageLoader(requestedUtc, coverage, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());
            if (withRows) loader.PagesByDate[day] = new List<FakeUserActivityDetail> { Page(UserA, 4) };
            var clock = (FixedClock)loader.Clock;
            loader.AfterGraphRequest = requested => { if (requested == day) clock.Advance(saveTakes); };
            var daysBack = (int)(requestedUtc.Date - day).TotalDays;
            // MinDaysBack can widen the window past the day under test; skip every other date in it.
            var skip = new HashSet<DateTime>(Enumerable.Range(1, UsageReportRefreshPolicy.MaxDaysBack)
                .Select(back => requestedUtc.Date.AddDays(-back)).Where(date => date != day));

            await loader.LoadAndSaveEachDateAsync(daysBack, skip, null, SeededIdCache(), new UserCache(null));

            CollectionAssert.AreEqual(new[] { day }, loader.GraphRequests.ToArray(), "Only the day under test is downloaded.");
            return (await coverage.ReadAsync(day, day)).Single(c => c.Source == "teams").Status;
        }

        /// <summary>Answers each Graph request with the next scripted status and body.</summary>
        private sealed class ScriptedGraphHandler : System.Net.Http.HttpMessageHandler
        {
            private readonly Queue<Tuple<System.Net.HttpStatusCode, string>> _responses;
            public int CallCount { get; private set; }

            public ScriptedGraphHandler(params Tuple<System.Net.HttpStatusCode, string>[] responses)
            {
                _responses = new Queue<Tuple<System.Net.HttpStatusCode, string>>(responses);
            }

            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CallCount++;
                Assert.IsTrue(_responses.Count > 0, "Graph was asked for more pages than the test scripted.");
                var next = _responses.Dequeue();
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(next.Item1)
                {
                    Content = new System.Net.Http.StringContent(next.Item2),
                    RequestMessage = request,
                });
            }
        }

        private const string GraphPage2 = "https://graph.microsoft.com/beta/reports/thisReportDoesNotExist?$skiptoken=page2";

        private static readonly Tuple<System.Net.HttpStatusCode, string> GraphNotFound = Tuple.Create(
            System.Net.HttpStatusCode.NotFound, "{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"Synthetic not found.\"}}");

        /// <summary>A 200 report page holding one row per UPN, with an @odata.nextLink when <paramref name="nextLink"/> is set.</summary>
        private static Tuple<System.Net.HttpStatusCode, string> GraphPage(string nextLink, params string[] upns)
        {
            var rows = string.Join(",", upns.Select(upn => "{\"userPrincipalName\":\"" + upn + "\",\"thingCount\":4}"));
            var next = nextLink == null ? string.Empty : "\"@odata.nextLink\":\"" + nextLink + "\",";
            return Tuple.Create(System.Net.HttpStatusCode.OK, "{" + next + "\"value\":[" + rows + "]}");
        }

        /// <summary>
        /// Downloads <paramref name="day"/> alone through the production Graph paging over <paramref name="graph"/>, as the
        /// import does (LoadAndSaveEachDateAsync), and saves it to <paramref name="sql"/>.
        /// </summary>
        private static async Task<InMemoryUsageReportStore<FakeUserUsageActivityLog>> ImportOneDayThroughGraphAsync(
            ScriptedGraphHandler graph, UsageReportCoverageStore coverage, DateTime day, DateTime nowUtc,
            InMemoryUsageReportStore<FakeUserUsageActivityLog> sql = null)
        {
            sql = sql ?? new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            using (var client = new WebJob.Office365ActivityImporter.Engine.Graph.ManualGraphCallClient(graph, NullLogger.Instance))
            {
                var loader = new InMemoryDailyActivityLoader(NullLogger.Instance, client)
                {
                    Clock = new FixedClock(nowUtc),
                    StorageInspector = new FakeUsageReportStorageInspector(hasLeadingDateIndex: true),
                    CoverageStore = coverage, CoverageSource = "teams", CoverageWholeTenant = true,
                };
                loader.ReportStore = sql;
                var skip = new HashSet<DateTime>(Enumerable.Range(1, UsageReportRefreshPolicy.MaxDaysBack)
                    .Select(back => nowUtc.Date.AddDays(-back)).Where(date => date != day));

                await loader.LoadAndSaveEachDateAsync((int)(nowUtc.Date - day).TotalDays, skip, null, SeededIdCache(), new UserCache(null));

                CollectionAssert.AreEqual(new[] { day }, loader.GraphRequests.ToArray(), "Only the day under test is downloaded.");
            }
            return sql;
        }

        private static async Task<string> TeamsStatusAsync(UsageReportCoverageStore coverage, DateTime day)
            => (await coverage.ReadAsync(day, day)).Single(c => c.Source == "teams").Status;

        [DataTestMethod]
        [DataRow(2)]  // downloaded again, unchanged
        [DataRow(5)]  // skipped when a mature download still matches SQL
        [DataRow(40)] // retained history outside the Graph window
        public async Task AHealthyZeroWriteReplayStillDetectsTheLossOfItsLastFact(int age)
        {
            var clock = new FixedClock(new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc));
            var day = clock.UtcNow.Date.AddDays(-age);
            var phase = new InMemorySingleDateStore();
            var completion = new InMemoryReportCompletionStore();
            var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>().Seed(StoredRow(day, UserAId, 4));
            GraphImporter.DailyReportOutcome outcome = null;
            List<DateTime> requested = null;
            Task<bool> Run()
            {
                var importer = new GraphImporter(AnalyticsLogger.ConsoleOnlyTracer(), null, null, null, new Common.Entities.Config.AppConfig(),
                    phase, null, null, clock, completion);
                var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
                    { Clock = clock, ReportStore = sql, StorageInspector = new FakeUsageReportStorageInspector() };
                loader.PagesByDate[day] = new List<FakeUserActivityDetail> { Page(UserA, 4) };
                requested = loader.GraphRequests;
                return importer.RunActivityReportPhaseAsync(5, () => Task.FromResult(sql.Stored.Any()), (_, stamp) => new[]
                {
                    new GraphImporter.ActivityReportRun("Synthetic report", async () => outcome = await importer.LoadAndSaveDailyImportReport(
                        loader, 5, "Synthetic report", NullLogger.Instance, SeededIdCache(), stamp)),
                }, _ => Task.CompletedTask);
            }

            Assert.IsTrue(await Run());
            await phase.DeleteDt(); // run the next phase, keeping the per-report skip list
            Assert.IsTrue(await Run());
            Assert.AreEqual(0, outcome.RowsWritten, "This replay must not depend on INSERT/UPDATE counts.");
            Assert.AreEqual(age == 2, requested.Contains(day), "Only a recent retained date is downloaded again.");
            Assert.IsFalse(await Run(), "Unchanged SQL facts keep their normal throttle.");
            sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>(); // a SQL restore that lost its facts
            Assert.IsTrue(await Run(), "Zero writes last time cannot excuse the loss of an existing row.");
            Assert.AreEqual(age <= 5 ? 1 : 0, sql.Stored.Count, "Only facts still in Graph's look-back can be recovered.");
            Assert.IsFalse(await Run(), "Recovery establishes a new, matching result.");
        }

        [DataTestMethod]
        [DataRow("unavailable")]
        [DataRow("blank identity")]
        [DataRow("missing value")]
        [DataRow("missing continuation")]
        [DataRow("forbidden")]
        public async Task AnUnavailableOrIncompleteReportCannotExplainAnEmptyDatabase(string problem)
        {
            var clock = new FixedClock(new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc));
            var phase = (IActivityReportsPhaseStore)new InMemorySingleDateStore();
            var completion = new InMemoryReportCompletionStore();
            var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            var empty = GraphPage(null);
            async Task<bool> Run(bool force, params Tuple<System.Net.HttpStatusCode, string>[] pages)
            {
                var graph = new ScriptedGraphHandler(pages);
                using (var client = new ManualGraphCallClient(graph, NullLogger.Instance))
                {
                    var importer = new GraphImporter(AnalyticsLogger.ConsoleOnlyTracer(), null, null, null,
                        new Common.Entities.Config.AppConfig { ForceUsageReportsImport = force }, phase, null, null, clock, completion);
                    var loader = new InMemoryDailyActivityLoader(NullLogger.Instance, client)
                        { Clock = clock, ReportStore = sql, StorageInspector = new FakeUsageReportStorageInspector() };
                    var result = await importer.RunActivityReportPhaseAsync(3, () => Task.FromResult(sql.Stored.Any()), (_, stamp) => new[]
                    {
                        new GraphImporter.ActivityReportRun("Synthetic report", () => importer.LoadAndSaveDailyImportReport(
                            loader, 3, "Synthetic report", NullLogger.Instance, SeededIdCache(), stamp)),
                    }, _ => Task.CompletedTask);
                    Assert.AreEqual(pages.Length, graph.CallCount, "Run the real pager, not just a simulated report outcome.");
                    return result;
                }
            }

            Assert.IsTrue(await Run(false, empty, empty, empty));
            var soft = problem == "unavailable" || problem == "blank identity";
            var badDay = problem == "unavailable" ? new[] { GraphNotFound }
                : problem == "blank identity" ? new[] { GraphPage(null, "") }
                : problem == "missing continuation" ? new[] { GraphPage(GraphPage2), GraphNotFound }
                : new[] { Tuple.Create(problem == "forbidden" ? System.Net.HttpStatusCode.Forbidden : System.Net.HttpStatusCode.OK, "{}") };
            Assert.AreEqual(soft, await Run(true, new[] { empty, empty }.Concat(badDay).ToArray()));
            var completedAt = await phase.GetLastDT();
            if (soft)
                Assert.AreEqual(DailyPhaseFacts.Unverified, await phase.ReadFactsAsync(completedAt.Value), "An unavailable or unusable report is not a successful empty.");
            else
                Assert.IsNull(completedAt, "A failed download cannot inherit the previous empty success, even at the same tick.");
            Assert.IsTrue(await Run(false, empty, empty, empty), "The still-empty database must be rechecked immediately.");
            Assert.IsFalse(await Run(false), "A genuinely successful retry waits out its day.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AContinuation404KeepsItsRowsButCannotStampTheReportOrPhase(bool ledger)
        {
            var now = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
            var day = now.Date.AddDays(-5);
            var clock = new FixedClock(now);
            var completion = new InMemoryReportCompletionStore();
            var phase = new InMemorySingleDateStore();
            var importer = new GraphImporter(AnalyticsLogger.ConsoleOnlyTracer(), null, null, null, new Common.Entities.Config.AppConfig(),
                phase, null, null, clock, completion);
            var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            var coverage = ledger ? new UsageReportCoverageStore(new InMemoryKeyValueStore(), true) : null;
            var key = GraphImporter.GetReportKey(typeof(InMemoryDailyActivityLoader));

            async Task<bool> Run(params Tuple<System.Net.HttpStatusCode, string>[] pages)
            {
                var graph = new ScriptedGraphHandler(pages);
                using (var client = new ManualGraphCallClient(graph, NullLogger.Instance))
                {
                    var loader = new InMemoryDailyActivityLoader(NullLogger.Instance, client)
                    {
                        Clock = clock, StorageInspector = new FakeUsageReportStorageInspector(), ReportStore = sql,
                        CoverageStore = coverage, CoverageSource = "teams", CoverageWholeTenant = true,
                    };
                    var success = await importer.RunActivityReportPhaseAsync(5, () => Task.FromResult(true),
                        (_, stamp) => new[] { new GraphImporter.ActivityReportRun("Synthetic report", () =>
                            importer.LoadAndSaveDailyImportReport(loader, 5, "Synthetic report", NullLogger.Instance, SeededIdCache(), stamp)) },
                        _ => Task.CompletedTask);
                    Assert.AreEqual(pages.Length, graph.CallCount, "Every scripted request should actually have been needed.");
                    return success;
                }
            }

            var fullPage = GraphPage(null, UserA);
            var start = GraphPage(GraphPage2, UserA);
            var completed = await Run(GraphNotFound, fullPage, fullPage, fullPage, start, GraphNotFound);
            Assert.AreEqual(UserAId, sql.Stored.Single(row => row.Date == day).UserID, "Keep the good first page.");
            Assert.IsFalse(completed, "A missing continuation must not look like a successfully imported window.");
            Assert.IsNull(await completion.GetLastSuccessAsync(key));
            Assert.IsNull(await phase.GetLastDT());
            if (ledger) Assert.IsFalse(await coverage.IsDayVerifiedAsync("teams", day));

            Assert.IsTrue(await Run(GraphNotFound, fullPage, fullPage, fullPage, GraphNotFound),
                "The old day is now unavailable on page one. This is not an interrupted continuation and doesn't disarm the phase.");
            Assert.IsNotNull(await completion.GetLastSuccessAsync(key));
            if (ledger) Assert.IsFalse(await coverage.IsDayVerifiedAsync("teams", day));
            await phase.DeleteDt(); // reopen just the phase gate; keep the real per-report skip decision

            Assert.IsTrue(await Run(GraphNotFound, fullPage, fullPage, start, GraphPage(null, UserB)),
                "The earlier partial SQL row plus an unavailable first page never proves the old date final.");
            CollectionAssert.AreEquivalent(new[] { UserAId, UserBId }, sql.Stored.Where(row => row.Date == day).Select(row => row.UserID).ToArray());
            Assert.IsNotNull(await completion.GetLastSuccessAsync(key));
            Assert.IsNotNull(await phase.GetLastDT());
            if (ledger) Assert.IsTrue(await coverage.IsDayVerifiedAsync("teams", day));

            await phase.DeleteDt();
            Assert.IsTrue(await Run(GraphNotFound, fullPage, fullPage), "Both mature dates now settle; only the three recent dates are requested.");

            await phase.DeleteDt();
            Assert.IsFalse(await Run(GraphPage(GraphPage2, "hash@hash"), GraphNotFound),
                "A concealed first page with a missing continuation does not prove tenant-wide concealment.");
            Assert.IsNull(await phase.GetLastDT(), "Do not throttle a retryable continuation failure as a genuine refusal.");
            Assert.IsNull(await completion.GetLastSuccessAsync(key));
        }

        [TestMethod]
        public async Task DailyCoverage_AGraph404PartWayThroughADay_SavesTheRowsButNeverCertifiesTheDay()
        {
            // Release critique of #909 (R5-1): the daily usage reports page leniently on a 404 - the pager returns the
            // rows gathered so far, as it always has. The coverage ledger then certified that truncated day "complete"
            // (a first-page 404, "empty"), and NeedsReimportAsync skipped it from then on: a false "verified".
            var now = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
            var day = now.Date.AddDays(-5);

            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var graph = new ScriptedGraphHandler(GraphPage(GraphPage2, UserA), GraphNotFound);
            var sql = await ImportOneDayThroughGraphAsync(graph, coverage, day, now);
            Assert.AreEqual(2, graph.CallCount, "Page 1 answered with a next link; page 2 answered 404.");
            Assert.AreEqual(1, sql.Stored.Count, "The rows received are still saved, as before.");
            Assert.AreEqual("partial", await TeamsStatusAsync(coverage, day),
                "A day Graph stopped answering part-way through is not complete.");
            Assert.IsTrue(await coverage.NeedsReimportAsync("teams", day, true), "It is downloaded again next cycle.");

            coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            sql = await ImportOneDayThroughGraphAsync(new ScriptedGraphHandler(GraphNotFound), coverage, day, now);
            Assert.AreEqual(0, sql.Stored.Count);
            Assert.AreEqual("partial", await TeamsStatusAsync(coverage, day), "A first-page 404 is not a successfully empty day.");

            // The guard stays silent on whole downloads.
            coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            sql = await ImportOneDayThroughGraphAsync(
                new ScriptedGraphHandler(GraphPage(GraphPage2, UserA), GraphPage(null, UserB)), coverage, day, now);
            Assert.AreEqual(2, sql.Stored.Count);
            Assert.AreEqual("complete", await TeamsStatusAsync(coverage, day), "Every page answered: the day is certified.");
            Assert.IsFalse(await coverage.NeedsReimportAsync("teams", day, true));

            coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            await ImportOneDayThroughGraphAsync(new ScriptedGraphHandler(GraphPage(null)), coverage, day, now);
            Assert.AreEqual("empty", await TeamsStatusAsync(coverage, day), "A settled 200 with no rows is still successfully empty.");
        }

        [TestMethod]
        public async Task DailyCoverage_AnUnfinishedRedownload_InvalidatesPreviousCompleteAndEmptyOutcomes()
        {
            var now = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
            var day = now.Date.AddDays(-5);

            // Old rows remain useful positive observations, but cannot prove that no new people are missing.
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var sql = await ImportOneDayThroughGraphAsync(
                new ScriptedGraphHandler(GraphPage(GraphPage2, UserA), GraphPage(null, UserB)), coverage, day, now);
            await ImportOneDayThroughGraphAsync(
                new ScriptedGraphHandler(GraphPage(GraphPage2, UserA), GraphNotFound), coverage, day, now, sql);
            Assert.AreEqual(2, sql.Stored.Count);
            Assert.AreEqual("partial", await TeamsStatusAsync(coverage, day));

            // Certified empty: the unfinished re-download stored a row, so "nobody used this workload" is no longer true.
            coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            sql = await ImportOneDayThroughGraphAsync(new ScriptedGraphHandler(GraphPage(null)), coverage, day, now);
            Assert.AreEqual("empty", await TeamsStatusAsync(coverage, day));
            await ImportOneDayThroughGraphAsync(
                new ScriptedGraphHandler(GraphPage(GraphPage2, UserA), GraphNotFound), coverage, day, now, sql);
            Assert.AreEqual(1, sql.Stored.Count);
            Assert.AreEqual("partial", await TeamsStatusAsync(coverage, day));
        }

        [TestMethod]
        public async Task DailyCoverage_SettlementIsJudgedWhenGraphWasAsked_NotWhenTheSaveFinished()
        {
            // Release critique of #909: CompleteDayAsync was given the clock at the END of the save. A day
            // requested just before midnight UTC and saved just after was judged one day older than it was
            // when Graph answered - so an empty answer for a day Graph had not yet published was certified
            // "empty" for good, and rows from a day still being revised were certified "complete".
            var day = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc);
            var twoMinutes = TimeSpan.FromMinutes(2);

            Assert.AreEqual("partial",
                await CoverageStatusAfterSlowSaveAsync(day, day.AddDays(3).AddMinutes(-1), twoMinutes, withRows: false),
                "An empty answer requested at age 2 is not settled, however late the save finishes.");
            Assert.AreEqual("partial",
                await CoverageStatusAfterSlowSaveAsync(day, day.AddDays(2).AddMinutes(-1), twoMinutes, withRows: true),
                "Rows requested at age 1 are not settled, however late the save finishes.");

            // The guard must stay silent on days that WERE settled when requested.
            Assert.AreEqual("empty",
                await CoverageStatusAfterSlowSaveAsync(day, day.AddDays(3).AddMinutes(1), twoMinutes, withRows: false),
                "An empty answer requested at age 3 is settled.");
            Assert.AreEqual("complete",
                await CoverageStatusAfterSlowSaveAsync(day, day.AddDays(2).AddMinutes(1), twoMinutes, withRows: true),
                "Rows requested at age 2 are settled.");
            Assert.AreEqual("complete",
                await CoverageStatusAfterSlowSaveAsync(day, day.AddDays(2).AddMinutes(1), TimeSpan.FromHours(30), withRows: true),
                "A save that runs on for longer still certifies a day that was settled when requested.");
        }

        [TestMethod]
        public async Task DailyCoverage_TheWholeWindowSave_AlsoJudgesSettlementAtTheRequest()
        {
            // The same rule on SaveLoadedReportsToSql, which saves every downloaded date after the whole
            // window has been read: the dates were requested well before the save reaches them.
            var day = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc);
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var loader = CoverageLoader(day.AddDays(3).AddMinutes(-1), coverage, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());
            var clock = (FixedClock)loader.Clock;
            // Every date is requested at 23:59; the window's last request takes the clock past midnight.
            loader.AfterGraphRequest = requested => { if (requested == day.AddDays(-1)) clock.Advance(TimeSpan.FromMinutes(5)); };

            await loader.PopulateLoadedReportPagesFromGraph(3);
            CollectionAssert.AreEqual(new[] { day.AddDays(1), day, day.AddDays(-1) }, loader.GraphRequests.ToArray());
            Assert.IsTrue(clock.UtcNow >= day.AddDays(3), "The test must save after midnight to mean anything.");
            await loader.SaveLoadedReportsToSql(SeededIdCache(), new UserCache(null));

            Assert.AreEqual("partial", (await coverage.ReadAsync(day, day)).Single(c => c.Source == "teams").Status,
                "Requested at age 2 and empty: not settled, though saved at age 3.");
            Assert.AreEqual("empty", (await coverage.ReadAsync(day.AddDays(-1), day.AddDays(-1))).Single(c => c.Source == "teams").Status,
                "Requested at age 3 and empty: settled.");

            // A page seeded without a request has no request time and is judged at the save, as before.
            var seeded = CoverageLoader(day.AddDays(3).AddMinutes(1), coverage, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());
            seeded.LoadedReportPages[day] = new List<FakeUserActivityDetail>();
            await seeded.SaveLoadedReportsToSql(SeededIdCache(), new UserCache(null));
            Assert.AreEqual("empty", (await coverage.ReadAsync(day, day)).Single(c => c.Source == "teams").Status);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DailyCoverage_InterruptedNonemptySave_IsNotCertified(bool previouslyEmpty)
        {
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var sql = new FailOnSaveNumberUsageReportStore(2);
            var loader = CoverageLoader(Day1.AddDays(5), coverage, sql);
            loader.SaveBatchSize = 1;
            if (previouslyEmpty)
            {
                loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail>();
                await loader.SaveLoadedReportsToSql(SeededIdCache(), new UserCache(null));
                Assert.AreEqual("empty", await TeamsStatusAsync(coverage, Day1));
            }
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 1), Page(UserB, 2) };
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => loader.SaveLoadedReportsToSql(SeededIdCache(), new UserCache(null)));
            Assert.AreEqual(1, sql.Stored.Count, "The first batch committed before the second failed.");
            Assert.AreEqual("partial", await TeamsStatusAsync(coverage, Day1));
            Assert.IsTrue(await coverage.NeedsReimportAsync("teams", Day1, true));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DailyCoverage_ConcealedRefreshCannotLeaveAPreviouslyEmptyDayVerified(bool streamDates)
        {
            var now = Day1.AddDays(5);
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            var loader = CoverageLoader(now, coverage, sql);
            var rows = new List<FakeUserActivityDetail>();
            loader.PagesByDate[Day1] = rows;
            loader.LoadedReportPages[Day1] = rows;
            var skip = new HashSet<DateTime>(Enumerable.Range(1, 4).Select(back => now.AddDays(-back)));
            Task Save() => streamDates
                ? loader.LoadAndSaveEachDateAsync(5, skip, null, SeededIdCache(), new UserCache(null))
                : loader.SaveLoadedReportsToSql(SeededIdCache(), new UserCache(null));

            await Save();
            Assert.AreEqual("empty", await TeamsStatusAsync(coverage, Day1));
            rows.Add(Page("hash@hash", 1));
            await Assert.ThrowsExceptionAsync<ConcealedUsageReportException>(Save);
            Assert.AreEqual(0, sql.Stored.Count);
            Assert.AreEqual("partial", await TeamsStatusAsync(coverage, Day1));

            rows[0] = Page(UserA, 1);
            await Save();
            Assert.AreEqual(1, sql.Stored.Count);
            Assert.AreEqual("complete", await TeamsStatusAsync(coverage, Day1));
        }

        [TestMethod]
        public async Task DailyCoverage_ExpandedRefreshWithFailedBatchStaysRetryableUntilEveryRowIsSaved()
        {
            var now = Day1.AddDays(5);
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var firstSql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            var first = CoverageLoader(now, coverage, firstSql);
            var skip = new HashSet<DateTime>(Enumerable.Range(1, 4).Select(back => now.AddDays(-back)));
            first.PagesByDate[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 1) };
            await first.LoadAndSaveEachDateAsync(5, skip, null, SeededIdCache(), new UserCache(null));
            Assert.AreEqual("complete", await TeamsStatusAsync(coverage, Day1));

            // The first batch updates A; the second would add B but fails. A new context sees only committed rows.
            var failedSql = new FailOnSaveNumberUsageReportStore(2);
            failedSql.Seed(firstSql.Stored.Single());
            var retry = CoverageLoader(now, coverage, failedSql);
            retry.SaveBatchSize = 1;
            var expanded = new List<FakeUserActivityDetail> { Page(UserA, 2), Page(UserB, 1) };
            retry.PagesByDate[Day1] = expanded;
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                retry.LoadAndSaveEachDateAsync(5, skip, null, SeededIdCache(), new UserCache(null)));
            Assert.AreEqual(1, failedSql.SaveCount);
            Assert.AreEqual(UserAId, failedSql.Stored.Single().UserID);
            Assert.AreEqual("partial", await TeamsStatusAsync(coverage, Day1));
            Assert.IsTrue(await coverage.NeedsReimportAsync("teams", Day1, true));

            var recoveredSql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>().Seed(failedSql.Stored.Single());
            var recovered = CoverageLoader(now, coverage, recoveredSql);
            recovered.PagesByDate[Day1] = expanded;
            await recovered.LoadAndSaveEachDateAsync(5, skip, null, SeededIdCache(), new UserCache(null));
            Assert.AreEqual(2, recoveredSql.Stored.Count);
            Assert.AreEqual("complete", await TeamsStatusAsync(coverage, Day1));
        }

        [TestMethod]
        public async Task DailyCoverage_FilteredOrConcealingTenant_KeepsTheSkipWindow_AndStaysUnverified()
        {
            // #899 review: final, never-certifiable outcomes must not defeat #375 by re-downloading the whole
            // skip window every cycle.
            var now = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
            var today = now.Date;
            foreach (var filtered in new[] { true, false })
            {
                var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
                var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
                var loader = CoverageLoader(now, coverage, sql, wholeTenant: !filtered);
                if (filtered) loader.InScopeRule = upn => upn != UserB;
                for (var day = 1; day <= 10; day++)
                {
                    loader.PagesByDate[today.AddDays(-day)] = new List<FakeUserActivityDetail>
                    {
                        Page(UserA, day), Page(filtered || day != 6 ? UserB : "hash@hash", day),
                    };
                }

                await ImportCycleAsync(loader, 10, null);
                Assert.AreEqual(10, loader.GraphRequests.Count);
                loader.GraphRequests.Clear();
                await ImportCycleAsync(loader, 10, now);
                CollectionAssert.AreEquivalent(new[] { today.AddDays(-1), today.AddDays(-2), today.AddDays(-3) },
                    loader.GraphRequests.ToArray(), "Only the refresh window is downloaded again.");

                var teams = (await coverage.ReadAsync(today.AddDays(-10), today.AddDays(-3))).Single(c => c.Source == "teams");
                Assert.IsFalse(teams.IsComplete);
                Assert.AreEqual(filtered ? UsageReportCoverageStore.ScopeUnverified : UsageReportCoverageStore.Incomplete, teams.Reason);
                Assert.AreEqual(filtered ? 0 : 1, teams.IncompleteDays);
            }
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
            Assert.AreEqual(3, loader.LastSaveDbWriteCount);

            var completed = instrumentation.Single(UsageReportSaveStageIds.SaveCompleted);
            AssertMetric(completed, "LookupDatabaseCallCount", 2);
            AssertMetric(completed, "LookupCacheMissCount", 2);
            AssertMetric(completed, "LookupCacheHitCount", 1);
            AssertMetric(completed, "MissingLookupValueRowCount", 1);
            AssertMetric(completed, "OutOfScopeRowCount", 1);
            AssertMetric(completed, "AddedRowCount", 2);
            AssertMetric(completed, "ChangedRowCount", 1);
            var firstDay = instrumentation.Events.Single(e =>
                e.Stage == UsageReportSaveStageIds.RowsProcessed && e.ReportDateUtc == Day1);
            AssertMetric(firstDay, "InputRowCount", 4);
            AssertMetric(firstDay, "MissingLookupValueRowCount", 1);
            AssertMetric(firstDay, "OutOfScopeRowCount", 1);
            Assert.IsTrue(instrumentation.Events.Any(e =>
                e.Stage == UsageReportSaveStageIds.RowsProcessed && e.ReportDateUtc == Day2));

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
        public async Task DailyActivityLoader_EmptyDayHasDatedDiagnosticsWithoutInventingStoredRows()
        {
            var instrumentation = new RecordingUsageReportSaveInstrumentation();
            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                SaveInstrumentation = instrumentation, ReportStore = store
            };
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail>();
            await loader.SaveLoadedReportsToSql(new ConcurrentLookupDbIdsCache(), new UserCache(null));
            var day = instrumentation.Single(UsageReportSaveStageIds.RowsProcessed);
            Assert.AreEqual(Day1, day.ReportDateUtc);
            AssertMetric(day, "InputRowCount", 0);
            AssertMetric(day, "AddedRowCount", 0);
            Assert.AreEqual(0, store.Stored.Count);
            Assert.IsTrue(instrumentation.Events.Any(e => e.Stage == UsageReportSaveStageIds.SaveCompleted));
        }

        [TestMethod]
        public async Task DailyCoverage_EmptySuccessIsDurable_NotAFactRow()
        {
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                CoverageStore = coverage, CoverageSource = "teams", CoverageWholeTenant = true,
            };
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail>();
            var sql = await SaveAsync(loader, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());
            Assert.AreEqual(0, sql.Stored.Count);
            var result = (await coverage.ReadAsync(Day1, Day1)).Single(c => c.Source == "teams");
            Assert.AreEqual("empty", result.Status);
            Assert.AreEqual(1, result.EmptyDays);
        }

        [TestMethod]
        public async Task DailyCoverage_RetainedRowsOrFilteredScopeCannotCertifySuccessfulEmpty()
        {
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                CoverageStore = coverage, CoverageSource = "teams", CoverageWholeTenant = true,
            };
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail>();
            var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            sql.Seed(StoredRow(Day1, UserAId, 5));
            await SaveAsync(loader, sql);
            var retained = (await coverage.ReadAsync(Day1, Day1)).Single(c => c.Source == "teams");
            Assert.AreEqual("partial", retained.Status);
            Assert.AreEqual(UsageReportCoverageStore.Incomplete, retained.Reason);
            loader.CoverageWholeTenant = false;
            await SaveAsync(loader, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());
            Assert.AreEqual("scopeUnverified", (await coverage.ReadAsync(Day1, Day1)).Single(c => c.Source == "teams").Reason);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("hash@hash")]
        [DataRow(UserB)]
        public async Task DailyCoverage_SkippedIdentitiesLeavePartialEvidence(string skippedIdentity)
        {
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                CoverageStore = coverage, CoverageSource = "teams", CoverageWholeTenant = true,
                InScopeRule = upn => upn != UserB,
            };
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail>
            {
                Page(UserA, 5), Page(skippedIdentity, 2),
            };
            var sql = await SaveAsync(loader, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());
            Assert.AreEqual(1, sql.Stored.Count);
            Assert.AreEqual("partial", (await coverage.ReadAsync(Day1, Day1)).Single(c => c.Source == "teams").Status);
        }

        [TestMethod]
        public async Task DailyCoverage_FailedInvalidationPreventsSqlWritesAndStaleSuccess()
        {
            var state = new CoverageFailureStore();
            var coverage = new UsageReportCoverageStore(state, true);
            state.FailWrites = true;
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                CoverageStore = coverage, CoverageSource = "teams", CoverageWholeTenant = true,
            };
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 10) };
            var sql = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => SaveAsync(loader, sql));
            Assert.AreEqual(0, sql.RowsLoadedForDates.Count, "Invalidation must precede even loading rows for modification.");
            Assert.AreEqual(0, sql.SaveCount);
            Assert.IsFalse((await coverage.ReadAsync(Day1, Day1)).Single(c => c.Source == "teams").IsComplete,
                "A first download that could not mark itself in progress wrote nothing and certified nothing.");
        }

        private sealed class CoverageFailureStore : IKeyValueStore
        {
            private readonly InMemoryKeyValueStore _inner;
            public CoverageFailureStore(InMemoryKeyValueStore inner = null) => _inner = inner ?? new InMemoryKeyValueStore();
            private int _reads;
            public int FailReadOn;
            public bool FailWrites;
            public string Description => "Synthetic coverage failure";
            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
                => Interlocked.Increment(ref _reads) == FailReadOn ? Task.FromException<string>(new InvalidOperationException("Synthetic read outage"))
                    : _inner.GetStringAsync(key, cancellationToken);
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
                => FailWrites ? Task.FromException(new InvalidOperationException("Synthetic write outage"))
                    : _inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
                => _inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
                => _inner.ExistsAsync(key, cancellationToken);
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
        public async Task DailyActivityLoader_InstrumentationSumsSubMillisecondPerRowTimings_InsteadOfTruncatingEachToZero()
        {
            // 1,000 rows, each spending ~20 microseconds in the scope filter and ~20 in the lookup: real time, all of
            // it far below a millisecond per row. The per-row timings used to be truncated to whole milliseconds
            // BEFORE they were summed, so a save like this reported ~0 ms for both stages.
            if (!Stopwatch.IsHighResolution)
            {
                Assert.Inconclusive("Needs a high-resolution Stopwatch to time sub-millisecond intervals.");
            }
            const int rows = 1000;
            var spinTicks = Math.Max(1, Stopwatch.Frequency / 50000);
            long scopeTicks = 0, lookupTicks = 0;
            void Spin(ref long measuredTicks)
            {
                var start = Stopwatch.GetTimestamp();
                while (Stopwatch.GetTimestamp() - start < spinTicks) { }
                measuredTicks += Stopwatch.GetTimestamp() - start;
            }

            CountingUserActivityDetail.ResolveAsync = upn =>
            {
                Spin(ref lookupTicks);
                return Task.FromResult(int.Parse(upn.Substring(4, 4)));
            };
            var instrumentation = new RecordingUsageReportSaveInstrumentation();
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                SaveInstrumentation = instrumentation,
                InScopeRule = upn =>
                {
                    Spin(ref scopeTicks);
                    return true;
                },
            };
            loader.LoadedReportPages[Day1] = Enumerable.Range(1000, rows)
                .Select(i => new CountingUserActivityDetail($"user{i}@contoso.com", i))
                .Cast<FakeUserActivityDetail>()
                .ToList();
            loader.ReportStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();

            // A cold cache and a lookup cache with no batch query: every row resolves through the per-row lookup.
            await loader.SaveLoadedReportsToSql(new ConcurrentLookupDbIdsCache(), new UserCache(null));

            // Each loader interval encloses the callback that measured itself, so a total that is summed without
            // truncation can never be less than what the callbacks measured.
            var scopeMs = scopeTicks * 1000.0 / Stopwatch.Frequency;
            var lookupMs = lookupTicks * 1000.0 / Stopwatch.Frequency;
            Assert.IsTrue(scopeMs > 0 && lookupMs > 0);
            var completed = instrumentation.Single(UsageReportSaveStageIds.SaveCompleted);
            var perDate = instrumentation.Single(UsageReportSaveStageIds.RowsProcessed);
            foreach (var point in new[] { completed, perDate })
            {
                AssertMetric(point, "LookupDatabaseCallCount", rows);
                Assert.IsTrue(Metric(point, "ScopeFilterMs") >= scopeMs,
                    $"{point.Stage}: ScopeFilterMs {Metric(point, "ScopeFilterMs")} must include the {scopeMs} ms the scope filter took.");
                Assert.IsTrue(Metric(point, "LookupDatabaseCallMs") >= lookupMs,
                    $"{point.Stage}: LookupDatabaseCallMs {Metric(point, "LookupDatabaseCallMs")} must include the {lookupMs} ms the lookups took.");
                Assert.IsTrue(Metric(point, "LookupResolveMs") >= Metric(point, "LookupDatabaseCallMs"),
                    $"{point.Stage}: resolving a lookup includes its database call.");
                foreach (var stage in new[] { "DateParseMs", "ProjectionMs", "DirtyCheckMs", "LookupSynchronizationWaitMs" })
                {
                    Assert.IsTrue(Metric(point, stage) > 0, $"{point.Stage}: {stage} ran for {rows} rows, so it cannot total exactly 0 ms.");
                }
            }
        }

        [TestMethod]
        public async Task DailyActivityLoader_RetryAfterIntermediateBatchFailure_IsIdempotentAndStampedByHealthyRun()
        {
            var coverage = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            CountingUserActivityDetail.ResolveAsync = upn => Task.FromResult(int.Parse(upn.Substring(4, 1)));
            var failingInstrumentation = new RecordingUsageReportSaveInstrumentation();
            var failingLoader = new InMemoryDailyActivityLoader(NullLogger.Instance) { SaveBatchSize = 2, SaveInstrumentation = failingInstrumentation };
            failingLoader.CoverageStore = coverage;
            failingLoader.CoverageSource = "teams";
            failingLoader.CoverageWholeTenant = true;
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
            Assert.AreEqual("partial", (await coverage.ReadAsync(Day1, Day1)).Single(c => c.Source == "teams").Status);

            var retryInstrumentation = new RecordingUsageReportSaveInstrumentation();
            var retryLoader = new InMemoryDailyActivityLoader(NullLogger.Instance) { SaveBatchSize = 2, SaveInstrumentation = retryInstrumentation };
            retryLoader.CoverageStore = coverage;
            retryLoader.CoverageSource = "teams";
            retryLoader.CoverageWholeTenant = true;
            retryLoader.LoadedReportPages[Day1] = rows;
            var retryStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>()
                .Seed(failingStore.Stored.Select(CloneRow).ToArray());
            retryLoader.ReportStore = retryStore;

            await retryLoader.SaveLoadedReportsToSql(new ConcurrentLookupDbIdsCache(), new UserCache(null));
            Assert.AreEqual("complete", (await coverage.ReadAsync(Day1, Day1)).Single(c => c.Source == "teams").Status);

            Assert.AreEqual(5, retryStore.Stored.Count);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, retryStore.Stored.OrderBy(r => r.UserID).Select(r => r.UserID).ToArray());
            Assert.AreEqual(3, retryLoader.LastSaveDbWriteCount, "The retry must not rewrite the two rows already committed by the failed attempt.");
            Assert.IsTrue(retryInstrumentation.Events.Any(e => e.Stage == UsageReportSaveStageIds.SaveCompleted));
            Assert.IsFalse(retryInstrumentation.Events.Any(e => e.Stage == UsageReportSaveStageIds.SaveFailed));
        }



        [TestMethod]
        public async Task DailyActivityLoader_TwoRowsForOneNewUserOnOneDate_KeepTheLastRowInsteadOfFailingTheReport()
        {
            // Graph is meant to send one row per user per date, but OneDrive keys its rows by owner UPN alone, so a
            // re-hired owner can appear twice. The second row used to reach EF6's OriginalValues for an entity still in
            // the Added state - the in-memory store throws there exactly as EF6 does - which failed the whole report on
            // every cycle.
            var instrumentation = new RecordingUsageReportSaveInstrumentation();
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance) { SaveInstrumentation = instrumentation };
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 5), Page(UserB, 7), Page(UserA, 9) };

            var store = await SaveAsync(loader, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());

            Assert.AreEqual(2, store.Stored.Count, "One row per user and date, not one per Graph row.");
            Assert.AreEqual(9, store.Stored.Single(r => r.UserID == UserAId).ThingCount, "The last row for a user wins.");
            Assert.AreEqual(2, store.Inserted.Count);
            Assert.AreEqual(0, store.Updated.Count, "The duplicate is folded into the queued insert, not written separately.");
            Assert.AreEqual(2, loader.LastSaveDbWriteCount);
            AssertMetric(instrumentation.Single(UsageReportSaveStageIds.SaveCompleted), "DuplicateLookupRowCount", 1);
        }

        [TestMethod]
        public async Task DailyActivityLoader_DuplicateRowAfterAFlush_UpdatesTheRowJustWritten()
        {
            // Batch size 1 puts the flush between the two rows: the duplicate is then dirty-checked against what was
            // just written, so it is an UPDATE of that row - never a second INSERT.
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance) { SaveBatchSize = 1 };
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 5), Page(UserA, 9) };

            var store = await SaveAsync(loader, new InMemoryUsageReportStore<FakeUserUsageActivityLog>());

            Assert.AreEqual(1, store.Stored.Count);
            Assert.AreEqual(9, store.Stored[0].ThingCount, "The last row for a user wins on this side of the flush too.");
            Assert.AreEqual(1, store.Inserted.Count);
            Assert.AreEqual(1, store.Updated.Count);
        }

        [TestMethod]
        public async Task DailyActivityLoader_TwoRowsForAStoredRow_UpdateItOnceWithTheLastValue()
        {
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance);
            loader.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page(UserA, 6), Page(UserA, 8) };

            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>()
                .Seed(StoredRow(Day1, UserAId, thingCount: 5));

            await SaveAsync(loader, store);

            Assert.AreEqual(1, store.Stored.Count);
            Assert.AreEqual(0, store.Inserted.Count, "The stored row is reused, not duplicated.");
            Assert.AreEqual(1, store.Updated.Count);
            Assert.AreEqual(8, store.Stored[0].ThingCount);
            Assert.AreEqual(1, loader.LastSaveDbWriteCount, "One row is written once, however many Graph rows named it.");
        }

        [TestMethod]
        public async Task DailyActivityLoader_ADateWhoseEveryIdentityIsConcealed_StopsTheRestOfTheDownload()
        {
            // A tenant that conceals identities conceals every row of every date, and the save refuses such a report,
            // so paging through the rest of the window would only fetch rows that cannot be used.
            var now = new DateTime(2026, 6, 20, 11, 15, 0, DateTimeKind.Utc);
            var concealed = new InMemoryDailyActivityLoader(NullLogger.Instance) { Clock = new FixedClock(now) };
            for (var daysBack = 2; daysBack <= 5; daysBack++)
            {
                concealed.PagesByDate[now.Date.AddDays(-daysBack)] = new List<FakeUserActivityDetail>
                {
                    Page("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08", 1),
                    Page("hash@hash", 2),
                };
            }

            await concealed.PopulateLoadedReportPagesFromGraph(daysBackMax: 5);

            CollectionAssert.AreEqual(new[] { now.Date.AddDays(-1), now.Date.AddDays(-2) }, concealed.GraphRequests.ToArray(),
                "An empty date (not yet published) says nothing; the first fully concealed date ends the download.");

            // One real identity anywhere earlier in the window means the report is importable: download it all.
            var mixed = new InMemoryDailyActivityLoader(NullLogger.Instance) { Clock = new FixedClock(now) };
            mixed.PagesByDate[now.Date.AddDays(-1)] = new List<FakeUserActivityDetail> { Page(UserA, 1) };
            for (var daysBack = 2; daysBack <= 5; daysBack++)
            {
                mixed.PagesByDate[now.Date.AddDays(-daysBack)] = new List<FakeUserActivityDetail> { Page("hash@hash", 2) };
            }

            await mixed.PopulateLoadedReportPagesFromGraph(daysBackMax: 5);

            Assert.AreEqual(5, mixed.GraphRequests.Count);
        }

        /// <summary>
        /// The import downloads and saves one date at a time. The rows it writes are the ones the whole-window save wrote -
        /// duplicates folded, blank identities skipped, a stored row updated - but each date is written before the next is
        /// downloaded, and none is kept once saved.
        /// </summary>
        [TestMethod]
        public async Task LoadAndSaveEachDate_WritesWhatTheWholeWindowSaveWrote_HoldingOneDateAtATime()
        {
            var now = new DateTime(2026, 6, 20, 11, 15, 0, DateTimeKind.Utc);
            var pages = new Dictionary<DateTime, List<FakeUserActivityDetail>>
            {
                [now.Date.AddDays(-1)] = new List<FakeUserActivityDetail> { Page(UserA, 5, "2026-06-19"), Page(UserB, 7) },
                [now.Date.AddDays(-2)] = new List<FakeUserActivityDetail> { Page(UserA, 3), Page(UserA, 4), Page(null, 1) },
                [now.Date.AddDays(-3)] = new List<FakeUserActivityDetail> { Page(UserB, 2, "not-a-date") },
            };
            var stored = StoredRow(now.Date.AddDays(-3), UserBId, thingCount: 1);

            var wholeWindow = new InMemoryDailyActivityLoader(NullLogger.Instance) { Clock = new FixedClock(now), SaveBatchSize = 2 };
            var wholeWindowStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>().Seed(CloneRow(stored));
            wholeWindow.ReportStore = wholeWindowStore;
            foreach (var page in pages) wholeWindow.PagesByDate[page.Key] = page.Value;
            await wholeWindow.PopulateLoadedReportPagesFromGraph(4);
            await wholeWindow.SaveLoadedReportsToSql(SeededIdCache(), new UserCache(null));

            var eachDate = new WriteObservingLoader { Clock = new FixedClock(now), SaveBatchSize = 2 };
            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>().Seed(CloneRow(stored));
            eachDate.ReportStore = eachDate.Store = store;
            foreach (var page in pages) eachDate.PagesByDate[page.Key] = page.Value;
            var stampClears = 0;
            await eachDate.LoadAndSaveEachDateAsync(4, null, () => { stampClears++; return Task.CompletedTask; }, SeededIdCache(), new UserCache(null));

            CollectionAssert.AreEqual(RowsOf(wholeWindowStore), RowsOf(store), "The same rows with the same values.");
            Assert.AreEqual(wholeWindow.LastSaveDbWriteCount, eachDate.LastSaveDbWriteCount);
            CollectionAssert.AreEqual(new[] { 0, 2, 3, 4 }, eachDate.WritesBeforeEachDownload.ToArray(),
                "Each date is written before the next is downloaded.");
            Assert.AreEqual(0, eachDate.LoadedReportPages.Count, "No date is kept.");
            Assert.AreEqual(4, eachDate.LastLoadedDateCount);
            Assert.AreEqual(6, eachDate.LastLoadedRowCount);
            Assert.AreEqual(1, stampClears, "The completion stamp is cleared once, before the first write.");
            Assert.IsTrue(store.AllWritesInsideBulkScope);
        }

        [TestMethod]
        public async Task LoadAndSaveEachDate_AConcealedTenantIsRefusedBeforeAnythingIsWritten_AndAMixedOneIsSaved()
        {
            var now = new DateTime(2026, 6, 20, 11, 15, 0, DateTimeKind.Utc);
            var concealed = new InMemoryDailyActivityLoader(NullLogger.Instance) { Clock = new FixedClock(now) };
            var concealedStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            concealed.ReportStore = concealedStore;
            for (var daysBack = 2; daysBack <= 5; daysBack++)
            {
                concealed.PagesByDate[now.Date.AddDays(-daysBack)] = new List<FakeUserActivityDetail>
                {
                    Page("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08", 1),
                    Page("hash@hash", 2),
                };
            }
            var stampClears = 0;

            await Assert.ThrowsExceptionAsync<ConcealedUsageReportException>(() => concealed.LoadAndSaveEachDateAsync(
                5, null, () => { stampClears++; return Task.CompletedTask; }, SeededIdCache(), new UserCache(null)));

            CollectionAssert.AreEqual(new[] { now.Date.AddDays(-1), now.Date.AddDays(-2) }, concealed.GraphRequests.ToArray(),
                "The first fully concealed date ends the download, as before.");
            Assert.AreEqual(0, concealedStore.SaveCount, "Nothing is written for a refused report.");
            Assert.AreEqual(1, stampClears, "Its stamp is cleared, as it was before.");

            var mixed = new InMemoryDailyActivityLoader(NullLogger.Instance) { Clock = new FixedClock(now) };
            var mixedStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            mixed.ReportStore = mixedStore;
            mixed.PagesByDate[now.Date.AddDays(-1)] = new List<FakeUserActivityDetail> { Page(UserA, 1) };
            for (var daysBack = 2; daysBack <= 5; daysBack++)
            {
                mixed.PagesByDate[now.Date.AddDays(-daysBack)] = new List<FakeUserActivityDetail> { Page("hash@hash", 2) };
            }

            await mixed.LoadAndSaveEachDateAsync(5, null, null, SeededIdCache(), new UserCache(null));

            Assert.AreEqual(5, mixed.GraphRequests.Count);
            Assert.AreEqual(1, mixedStore.Stored.Count, "The real identity is saved; the concealed rows are skipped.");
        }

        private static string[] RowsOf(InMemoryUsageReportStore<FakeUserUsageActivityLog> store)
            => store.Stored
                .Select(r => $"{r.Date:yyyy-MM-dd}|{r.UserID}|{r.ThingCount}|{r.LastActivityDate:yyyy-MM-dd}")
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToArray();

        /// <summary>
        /// A download that runs past midnight UTC still asks for each date of the window once: the window is fixed when the
        /// download starts, not re-read before every date.
        /// </summary>
        [TestMethod]
        public async Task ADownloadRunningPastMidnight_AsksForEachDateOfTheWindowOnce()
        {
            var start = new DateTime(2026, 6, 20, 22, 0, 0, DateTimeKind.Utc);
            var populated = new InMemoryDailyActivityLoader(NullLogger.Instance) { Clock = new SteppingClock(start, TimeSpan.FromHours(3)) };
            await populated.PopulateLoadedReportPagesFromGraph(4);

            var eachDate = new InMemoryDailyActivityLoader(NullLogger.Instance)
            {
                Clock = new SteppingClock(start, TimeSpan.FromHours(3)),
                ReportStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>(),
            };
            await eachDate.LoadAndSaveEachDateAsync(4, null, null, SeededIdCache(), new UserCache(null));

            var window = Enumerable.Range(1, 4).Select(daysBack => start.Date.AddDays(-daysBack)).ToArray();
            CollectionAssert.AreEqual(window, populated.GraphRequests.ToArray());
            CollectionAssert.AreEqual(window, eachDate.GraphRequests.ToArray());
        }

        /// <summary>A clock that moves on by a fixed step every time it is read.</summary>
        private sealed class SteppingClock : IClock
        {
            private readonly TimeSpan _step;
            private DateTime _next;

            public SteppingClock(DateTime start, TimeSpan step)
            {
                _next = start;
                _step = step;
            }

            public DateTime UtcNow
            {
                get
                {
                    var now = _next;
                    _next = _next.Add(_step);
                    return now;
                }
            }
        }

        /// <summary>
        /// Opt-in (<c>DAILY_REPORT_MEMORY_SCALE=1</c>, users from <c>DAILY_REPORT_MEMORY_SCALE_USERS</c>, default 50,000):
        /// how many downloaded rows one report holds, and the managed heap they take, with the whole 28-date window
        /// downloaded before saving and with each date saved as it is downloaded. Nothing is asserted about bytes; the
        /// figures go to the test output.
        /// </summary>
        [TestMethod]
        [TestCategory("Performance")]
        public async Task DailyReportDownload_RowsHeld_AtSyntheticScale()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("DAILY_REPORT_MEMORY_SCALE"), "1", StringComparison.Ordinal))
            {
                Assert.Inconclusive("Set DAILY_REPORT_MEMORY_SCALE=1 (and optionally DAILY_REPORT_MEMORY_SCALE_USERS) to run the synthetic-scale measurement.");
            }

            var users = int.TryParse(Environment.GetEnvironmentVariable("DAILY_REPORT_MEMORY_SCALE_USERS"), out var configured) && configured > 0
                ? configured
                : 50000;
            const int Dates = 28;
            var idCache = new ConcurrentLookupDbIdsCache();
            for (var i = 0; i < users; i++) idCache.AddOrUpdateForName<User>($"user{i}@contoso.com", i + 1);
            var now = new DateTime(2026, 6, 20, 11, 15, 0, DateTimeKind.Utc);

            var baseline = GC.GetTotalMemory(true);
            var wholeWindow = new GeneratingLoader(users) { Clock = new FixedClock(now), ReportStore = new DiscardingUsageReportStore() };
            wholeWindow.MeasureHeld = () => GC.GetTotalMemory(true) - baseline;
            await wholeWindow.PopulateLoadedReportPagesFromGraph(Dates);
            var wholeWindowRows = wholeWindow.LoadedReportPages.Sum(p => p.Value.Count);
            await wholeWindow.SaveLoadedReportsToSql(idCache, new UserCache(null));
            var wholeWindowPeak = wholeWindow.MaxBytesHeld;
            wholeWindow.LoadedReportPages.Clear();
            wholeWindow = null;

            baseline = GC.GetTotalMemory(true);
            var eachDate = new GeneratingLoader(users) { Clock = new FixedClock(now), ReportStore = new DiscardingUsageReportStore() };
            eachDate.MeasureHeld = () => GC.GetTotalMemory(true) - baseline;
            await eachDate.LoadAndSaveEachDateAsync(Dates, null, null, idCache, new UserCache(null));

            Console.WriteLine("{0:N0} users, {1} dates, one report; managed heap held by the download, at its largest:", users, Dates);
            Console.WriteLine("  whole window before saving: {0:N0} rows, {1:N1} MB", wholeWindowRows, wholeWindowPeak / 1048576.0);
            Console.WriteLine("  each date as downloaded:    {0:N0} rows, {1:N1} MB", users, eachDate.MaxBytesHeld / 1048576.0);
            Assert.AreEqual(users * Dates, wholeWindowRows);
            Assert.AreEqual(users * Dates, eachDate.LastLoadedRowCount);
        }

        /// <summary>"Graph" that makes a fresh day of rows, one per user, for every date asked for.</summary>
        private sealed class GeneratingLoader : InMemoryDailyActivityLoader
        {
            private readonly int _users;

            public GeneratingLoader(int users) : base(NullLogger.Instance)
            {
                _users = users;
            }

            /// <summary>Measures the heap while the date just made is held, with everything the loader still holds.</summary>
            public Func<long> MeasureHeld { get; set; }

            public long MaxBytesHeld { get; private set; }

            protected override Task<List<FakeUserActivityDetail>> LoadReportPageForDateFromGraph(DateTime date)
            {
                var rows = new List<FakeUserActivityDetail>(_users);
                for (var i = 0; i < _users; i++)
                {
                    rows.Add(new FakeUserActivityDetail { UserPrincipalName = $"user{i}@contoso.com", ThingCount = i % 7, LastActivityDateString = date.ToString("yyyy-MM-dd") });
                }
                if (MeasureHeld != null)
                {
                    MaxBytesHeld = Math.Max(MaxBytesHeld, MeasureHeld());
                }
                GC.KeepAlive(rows);
                return Task.FromResult(rows);
            }
        }

        /// <summary>A report table that keeps nothing, so only what the loader itself holds is measured.</summary>
        private sealed class DiscardingUsageReportStore : IUsageReportStore<FakeUserUsageActivityLog>
        {
            public int TrackedEntityCount => 0;
            public Task<bool> HasAnyRowForDateAsync(DateTime date) => Task.FromResult(false);
            public Task<IReadOnlyList<DateTime>> GetStoredDatesInRangeAsync(DateTime fromInclusive, DateTime toExclusive) => Task.FromResult<IReadOnlyList<DateTime>>(new List<DateTime>());
            public Task<IReadOnlyList<FakeUserUsageActivityLog>> GetRowsForDateAsync(DateTime date) => Task.FromResult<IReadOnlyList<FakeUserUsageActivityLog>>(new List<FakeUserUsageActivityLog>());
            public void AddRow(FakeUserUsageActivityLog row) { }
            public bool MarkUpdatedIfChanged(FakeUserUsageActivityLog row) => false;
            public Task SaveChangesAsync() => Task.CompletedTask;
            public void ReleaseSavedRows() { }
            public IDisposable BeginBulkWrite() => new NoScope();

            private sealed class NoScope : IDisposable
            {
                public void Dispose() { }
            }
        }

        /// <summary>Records, as each date is requested from "Graph", how many rows have been written so far.</summary>
        private sealed class WriteObservingLoader : InMemoryDailyActivityLoader
        {
            public WriteObservingLoader() : base(NullLogger.Instance)
            {
            }

            public InMemoryUsageReportStore<FakeUserUsageActivityLog> Store { get; set; }

            public List<int> WritesBeforeEachDownload { get; } = new List<int>();

            protected override Task<List<FakeUserActivityDetail>> LoadReportPageForDateFromGraph(DateTime date)
            {
                WritesBeforeEachDownload.Add(Store.Inserted.Count + Store.Updated.Count);
                return base.LoadReportPageForDateFromGraph(date);
            }
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
