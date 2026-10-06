using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;

namespace Tests.UnitTests
{
    /// <summary>
    /// The daily usage-report save resolves each report's existing users in set-based batches before its row loop,
    /// and shares the ids between loaders by LOOKUP type rather than report type (#705). Every test except the SQL
    /// one runs with no SQL Server: the "users table" is <see cref="SyntheticUsersTable"/>.
    /// </summary>
    [TestClass]
    public class UsageReportLookupBatchingTests
    {
        private static readonly DateTime Day1 = new DateTime(2026, 5, 10);
        private static readonly DateTime Day2 = new DateTime(2026, 5, 11);

        private static FakeUserActivityDetail Page(string upn, int thingCount)
            => new FakeUserActivityDetail { UserPrincipalName = upn, ThingCount = thingCount };

        [TestMethod]
        public async Task ColdSave_ResolvesExistingUsersInBatchesOfAtMostOneThousand_NotOneQueryPerUser()
        {
            const int users = 2500;
            var table = new SyntheticUsersTable();
            for (var i = 1; i <= users; i++)
            {
                table.Seed($"user{i}@contoso.com", 10000 + i);
            }

            var instrumentation = new RecordingInstrumentation();
            var loader = new InMemoryDailyActivityLoader(NullLogger.Instance) { SaveInstrumentation = instrumentation };
            // Both days list every user; day 2 spells every tenth one in a different case, as Graph can.
            loader.LoadedReportPages[Day1] = Enumerable.Range(1, users).Select(i => Page($"user{i}@contoso.com", i)).ToList();
            loader.LoadedReportPages[Day2] = Enumerable.Range(1, users)
                .Select(i => Page(i % 10 == 0 ? $"User{i}@Contoso.com" : $"user{i}@contoso.com", i)).ToList();
            var store = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            loader.ReportStore = store;

            await loader.SaveLoadedReportsToSql(new ConcurrentLookupDbIdsCache(), table.NewCache());

            CollectionAssert.AreEqual(new[] { 1000, 1000, 500 }, table.BatchSizes.ToArray(),
                "2,500 distinct users (a case variant is the same user) must be three queries of at most 1,000, not one per user per day.");
            Assert.AreEqual(0, table.PerKeyCalls, "Every user exists, so no row may fall back to a per-user query.");
            Assert.AreEqual(users * 2, store.Stored.Count);
            foreach (var row in store.Stored)
            {
                Assert.AreEqual(10000 + row.ThingCount, row.UserID, "Each row must be saved against its own user's id.");
            }

            var completed = instrumentation.Single(UsageReportSaveStageIds.SaveCompleted);
            AssertMetric(completed, "LookupBatchCount", 3);
            AssertMetric(completed, "LookupDatabaseCallCount", 3);
            AssertMetric(completed, "LookupBatchKeyCount", users);
            AssertMetric(completed, "LookupBatchResolvedCount", users);
            AssertMetric(completed, "LookupCacheMissCount", 0);
            AssertMetric(completed, "LookupCacheHitCount", users * 2);
        }

        [TestMethod]
        public async Task LookupBatchSize_IsHonoured_AndCappedAtOneThousand()
        {
            var table = new SyntheticUsersTable();
            for (var i = 1; i <= 7; i++)
            {
                table.Seed($"user{i}@contoso.com", i);
            }
            var small = new InMemoryDailyActivityLoader(NullLogger.Instance) { LookupBatchSize = 3, ReportStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>() };
            small.LoadedReportPages[Day1] = Enumerable.Range(1, 7).Select(i => Page($"user{i}@contoso.com", i)).ToList();

            await small.SaveLoadedReportsToSql(new ConcurrentLookupDbIdsCache(), table.NewCache());

            CollectionAssert.AreEqual(new[] { 3, 3, 1 }, table.BatchSizes.ToArray());

            // SQL Server allows 2,100 parameters a command; a larger setting must never reach the query.
            var oversized = new SyntheticUsersTable();
            for (var i = 1; i <= 1500; i++)
            {
                oversized.Seed($"user{i}@contoso.com", i);
            }
            var big = new InMemoryDailyActivityLoader(NullLogger.Instance) { LookupBatchSize = 5000, ReportStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>() };
            big.LoadedReportPages[Day1] = Enumerable.Range(1, 1500).Select(i => Page($"user{i}@contoso.com", i)).ToList();

            await big.SaveLoadedReportsToSql(new ConcurrentLookupDbIdsCache(), oversized.NewCache());

            CollectionAssert.AreEqual(new[] { 1000, 500 }, oversized.BatchSizes.ToArray());
            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
                () => new UserCache(null).LoadExistingIdsAsync(Enumerable.Range(0, 1001).Select(i => $"u{i}@contoso.com").ToList()));
        }

        [TestMethod]
        public async Task UserMissingFromTheDatabase_IsCreatedOnce_AndReusedByLaterRowsAndByAnotherReport()
        {
            var table = new SyntheticUsersTable();
            table.Seed("existing@contoso.com", 501);
            var phaseCache = new ConcurrentLookupDbIdsCache();

            var first = new InMemoryDailyActivityLoader(NullLogger.Instance);
            var firstStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            first.ReportStore = firstStore;
            first.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page("existing@contoso.com", 1), Page("newstarter@contoso.com", 2) };
            first.LoadedReportPages[Day2] = new List<FakeUserActivityDetail> { Page("NewStarter@Contoso.com", 3), Page("existing@contoso.com", 4) };

            await first.SaveLoadedReportsToSql(phaseCache, table.NewCache());

            CollectionAssert.AreEqual(new[] { "newstarter@contoso.com" }, table.Created.ToArray(),
                "A user not in SQL is created exactly once, under the lower-cased UPN, by the per-row path as before.");
            var newStarterId = table.IdOf("newstarter@contoso.com");
            Assert.AreEqual(1, table.BatchSizes.Count, "Both users were asked for in one batch.");
            CollectionAssert.AreEquivalent(new[] { 501, newStarterId, newStarterId, 501 }, firstStore.Stored.Select(r => r.UserID).ToArray());

            // A second report of a DIFFERENT type in the same phase: both users are already in the phase cache.
            var second = new OutlookUserActivityLoader(null, Common.Entities.UserScope.UserImportScope.Unfiltered, NullLogger.Instance);
            var secondStore = new InMemoryUsageReportStore<OutlookUsageActivityLog>();
            second.ReportStore = secondStore;
            second.LoadedReportPages[Day1] = new List<OutlookUserActivityUserDetail>
            {
                new OutlookUserActivityUserDetail { UserPrincipalName = "existing@contoso.com", ReadCount = 1 },
                new OutlookUserActivityUserDetail { UserPrincipalName = "newstarter@contoso.com", ReadCount = 2 },
            };
            var batchesBefore = table.BatchSizes.Count;
            var perKeyBefore = table.PerKeyCalls;

            await second.SaveLoadedReportsToSql(phaseCache, table.NewCache());

            Assert.AreEqual(batchesBefore, table.BatchSizes.Count, "Users resolved by one report must not be queried again by another (#705).");
            Assert.AreEqual(perKeyBefore, table.PerKeyCalls);
            Assert.AreEqual(1, table.Created.Count, "The new user must not be created a second time.");
            CollectionAssert.AreEquivalent(new[] { 501, newStarterId }, secondStore.Stored.Select(r => r.UserID).ToArray());
        }

        [TestMethod]
        public async Task PhaseCache_IsSharedByLookupType_SoASecondReportTypeSendsNoQueries()
        {
            var table = new SyntheticUsersTable();
            table.Seed("alice@contoso.com", 11);
            table.Seed("bob@contoso.com", 22);
            var phaseCache = new ConcurrentLookupDbIdsCache();

            var outlook = new OutlookUserActivityLoader(null, Common.Entities.UserScope.UserImportScope.Unfiltered, NullLogger.Instance)
            {
                ReportStore = new InMemoryUsageReportStore<OutlookUsageActivityLog>(),
            };
            outlook.LoadedReportPages[Day1] = new List<OutlookUserActivityUserDetail>
            {
                new OutlookUserActivityUserDetail { UserPrincipalName = "alice@contoso.com", ReadCount = 1 },
                new OutlookUserActivityUserDetail { UserPrincipalName = "bob@contoso.com", ReadCount = 2 },
            };
            await outlook.SaveLoadedReportsToSql(phaseCache, table.NewCache());
            Assert.AreEqual(1, table.BatchSizes.Count);

            var instrumentation = new RecordingInstrumentation();
            var fake = new InMemoryDailyActivityLoader(NullLogger.Instance) { SaveInstrumentation = instrumentation };
            var fakeStore = new InMemoryUsageReportStore<FakeUserUsageActivityLog>();
            fake.ReportStore = fakeStore;
            fake.LoadedReportPages[Day1] = new List<FakeUserActivityDetail> { Page("alice@contoso.com", 1), Page("bob@contoso.com", 2) };

            await fake.SaveLoadedReportsToSql(phaseCache, table.NewCache());

            Assert.AreEqual(1, table.BatchSizes.Count, "The second report type must reuse the first one's ids, not query again.");
            Assert.AreEqual(0, table.PerKeyCalls);
            var completed = instrumentation.Single(UsageReportSaveStageIds.SaveCompleted);
            AssertMetric(completed, "LookupDatabaseCallCount", 0);
            AssertMetric(completed, "LookupCacheHitCount", 2);
            CollectionAssert.AreEquivalent(new[] { 11, 22 }, fakeStore.Stored.Select(r => r.UserID).ToArray());
            Assert.AreEqual(11, phaseCache.GetCachedIdForName<User>("alice@contoso.com"), "The phase cache is keyed by the lookup type.");
        }

        [TestMethod]
        public async Task YammerGroupLoader_StillResolvesPerRow_AndCannotCollideWithAUser()
        {
            var phaseCache = new ConcurrentLookupDbIdsCache();
            // A user whose UPN happens to equal a group's name: keyed by lookup type, the two cannot be confused.
            phaseCache.AddOrUpdateForName<User>("Contoso Engineering", 999);

            var groups = new SyntheticYammerGroups();
            var instrumentation = new RecordingInstrumentation();
            var loader = new YammerGroupUsageLoader(null, NullLogger.Instance) { SaveInstrumentation = instrumentation };
            var store = new InMemoryUsageReportStore<YammerGroupActivityLog>();
            loader.ReportStore = store;
            loader.LoadedReportPages[Day1] = new List<YammerGroupActivityDetail>
            {
                new YammerGroupActivityDetail { GroupName = "Contoso Engineering", PostedCount = 3 },
                new YammerGroupActivityDetail { GroupName = "Καλημέρα κόσμε", PostedCount = 4 },
            };
            loader.LoadedReportPages[Day2] = new List<YammerGroupActivityDetail>
            {
                new YammerGroupActivityDetail { GroupName = "Contoso Engineering", PostedCount = 5 },
            };

            await loader.SaveLoadedReportsToSql(phaseCache, groups.NewCache());

            CollectionAssert.AreEquivalent(new[] { "Contoso Engineering", "Καλημέρα κόσμε" }, groups.Created.ToArray());
            Assert.AreEqual(2, groups.PerKeyCalls, "One per-row resolution per group; the repeat on day 2 is a cache hit.");
            Assert.AreEqual(1, groups.BatchAttempts, "Groups have no batch query: the one attempt answers null and the save falls back to per row.");

            var engineeringId = groups.IdOf("Contoso Engineering");
            var greekId = groups.IdOf("Καλημέρα κόσμε");
            Assert.AreNotEqual(999, engineeringId);
            CollectionAssert.AreEquivalent(new[] { engineeringId, greekId, engineeringId }, store.Stored.Select(r => r.YammerGroupID).ToArray());
            Assert.AreEqual(999, phaseCache.GetCachedIdForName<User>("Contoso Engineering"), "The user entry must be untouched.");
            Assert.AreEqual(engineeringId, phaseCache.GetCachedIdForName<YammerGroup>("Contoso Engineering"));
            Assert.AreEqual(greekId, phaseCache.GetCachedIdForName<YammerGroup>("Καλημέρα κόσμε"));

            var completed = instrumentation.Single(UsageReportSaveStageIds.SaveCompleted);
            AssertMetric(completed, "LookupBatchCount", 0);
            AssertMetric(completed, "LookupDatabaseCallCount", 2);
        }

        /// <summary>
        /// Against SQL Server: the batched query must find exactly the user <see cref="UserCache.Load"/> finds for every
        /// key - case differences, a trailing space, duplicates (lowest id wins) and a user that does not exist.
        /// Production has a unique index on <c>user_name</c>, so the duplicates are inserted inside a transaction
        /// that drops it and is rolled back; nothing is left behind.
        /// </summary>
        [TestMethod]
        public async Task UserCache_BatchedLookup_FindsTheSameIdsAsLoad_ForCaseTrailingSpaceAndDuplicates()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
                await db.Database.Connection.OpenAsync();
                using (var transaction = db.Database.BeginTransaction())
                {
                    await db.Database.ExecuteSqlCommandAsync(@"
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += CASE WHEN i.is_unique_constraint = 1
        THEN N'ALTER TABLE dbo.users DROP CONSTRAINT ' + QUOTENAME(i.name) + N';'
        ELSE N'DROP INDEX ' + QUOTENAME(i.name) + N' ON dbo.users;' END
FROM sys.indexes AS i
WHERE i.object_id = OBJECT_ID('dbo.users') AND i.is_unique = 1 AND i.is_primary_key = 0
  AND EXISTS (SELECT 1 FROM sys.index_columns AS ic
              INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = 'user_name');
EXEC sp_executesql @sql;");

                    var token = Guid.NewGuid().ToString("N").Substring(0, 12);
                    async Task<int> InsertUser(string upn)
                        => await db.Database.SqlQuery<int>("INSERT INTO dbo.users (user_name) OUTPUT INSERTED.id VALUES (@p0)", upn).SingleAsync();

                    var mixedId = await InsertUser($"Mixed.{token}@Contoso.com");
                    var lowestDuplicateId = await InsertUser($"DUPE.{token}@CONTOSO.COM");
                    await InsertUser($"dupe.{token}@contoso.com");
                    await InsertUser($"Dupe.{token}@Contoso.com");
                    var trailingId = await InsertUser($"trailing.{token}@contoso.com ");

                    var keys = new List<string>
                    {
                        $"mixed.{token}@contoso.com",
                        $"MIXED.{token}@CONTOSO.COM",
                        $"dupe.{token}@contoso.com",
                        $"trailing.{token}@contoso.com",
                        $"missing.{token}@contoso.com",
                    };

                    var batched = await new UserCache(db).LoadExistingIdsAsync(keys);

                    foreach (var key in keys)
                    {
                        var viaLoad = await new UserCache(db).Load(key);
                        if (viaLoad == null)
                        {
                            Assert.IsFalse(batched.ContainsKey(key), $"'{key}' does not exist, so the batch must not resolve it either.");
                        }
                        else
                        {
                            Assert.IsTrue(batched.TryGetValue(key, out var batchedId), $"'{key}' exists, so the batch must resolve it.");
                            Assert.AreEqual(viaLoad.ID, batchedId, $"'{key}' must resolve to the same user as Load.");
                        }
                    }

                    Assert.AreEqual(mixedId, batched[$"mixed.{token}@contoso.com"], "Matching is case-insensitive, as the column's collation is.");
                    Assert.AreEqual(mixedId, batched[$"MIXED.{token}@CONTOSO.COM"]);
                    Assert.AreEqual(lowestDuplicateId, batched[$"dupe.{token}@contoso.com"], "Duplicate UPNs resolve to the lowest id, as Load's OrderBy(ID) does.");
                    Assert.AreEqual(trailingId, batched[$"trailing.{token}@contoso.com"], "SQL ignores trailing spaces in '=', in both paths.");
                    Assert.AreEqual(4, batched.Count);

                    transaction.Rollback();
                }
            }
        }

        private static void AssertMetric(UsageReportSaveTelemetryPoint point, string name, double expected)
            => Assert.AreEqual(expected, point.Metrics.TryGetValue(name, out var value) ? value : 0, $"Metric {name}");

        private sealed class RecordingInstrumentation : IUsageReportSaveInstrumentation
        {
            private readonly object _lock = new object();
            public bool IsEnabled => true;
            public List<UsageReportSaveTelemetryPoint> Events { get; } = new List<UsageReportSaveTelemetryPoint>();
            public void Track(UsageReportSaveTelemetryPoint point)
            {
                lock (_lock) Events.Add(point);
            }
            public UsageReportSaveTelemetryPoint Single(string stage) => Events.Single(e => e.Stage == stage);
        }

        /// <summary>
        /// Stands in for <c>dbo.users</c>: case-insensitive like the column's collation, shared by every
        /// <see cref="UserCache"/> it hands out (one per loader, as in <c>GraphImporter</c>), and counting every query.
        /// </summary>
        private sealed class SyntheticUsersTable
        {
            private readonly Dictionary<string, int> _ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private int _nextId = 100000;

            public List<int> BatchSizes { get; } = new List<int>();
            public int PerKeyCalls { get; private set; }
            public List<string> Created { get; } = new List<string>();

            public void Seed(string upn, int id) => _ids[upn] = id;
            public int IdOf(string upn) => _ids[upn];
            public UserCache NewCache() => new Cache(this);

            private sealed class Cache : UserCache
            {
                private readonly SyntheticUsersTable _table;
                public Cache(SyntheticUsersTable table) : base(null) => _table = table;

                public override Task<IReadOnlyDictionary<string, int>> LoadExistingIdsAsync(IReadOnlyList<string> keys)
                {
                    lock (_table)
                    {
                        _table.BatchSizes.Add(keys.Count);
                        var found = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var key in keys)
                        {
                            if (_table._ids.TryGetValue(key, out var id)) found[key] = id;
                        }
                        return Task.FromResult<IReadOnlyDictionary<string, int>>(found);
                    }
                }

                // The per-row path: find, or create and "save" to get an id.
                public override Task<User> GetOrCreateNewResource(string key, User newTemplate, bool commitChangeOnSaveNew)
                {
                    lock (_table)
                    {
                        _table.PerKeyCalls++;
                        key = key.Trim();
                        if (!_table._ids.TryGetValue(key, out var id))
                        {
                            id = ++_table._nextId;
                            _table._ids[key] = id;
                            _table.Created.Add(newTemplate.UserPrincipalName);
                        }
                        return Task.FromResult(new User { ID = id, UserPrincipalName = key });
                    }
                }
            }
        }

        /// <summary>Stands in for <c>dbo.yammer_groups</c>, which has no batch query.</summary>
        private sealed class SyntheticYammerGroups
        {
            private readonly Dictionary<string, int> _ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private int _nextId = 700;

            public int BatchAttempts { get; private set; }
            public int PerKeyCalls { get; private set; }
            public List<string> Created { get; } = new List<string>();

            public int IdOf(string name) => _ids[name];
            public YammerGroupCache NewCache() => new Cache(this);

            private sealed class Cache : YammerGroupCache
            {
                private readonly SyntheticYammerGroups _groups;
                public Cache(SyntheticYammerGroups groups) : base(null) => _groups = groups;

                public override Task<IReadOnlyDictionary<string, int>> LoadExistingIdsAsync(IReadOnlyList<string> keys)
                {
                    _groups.BatchAttempts++;
                    return base.LoadExistingIdsAsync(keys);
                }

                public override Task<YammerGroup> GetOrCreateNewResource(string key, YammerGroup newTemplate, bool commitChangeOnSaveNew)
                {
                    _groups.PerKeyCalls++;
                    if (!_groups._ids.TryGetValue(key, out var id))
                    {
                        id = ++_groups._nextId;
                        _groups._ids[key] = id;
                        _groups.Created.Add(newTemplate.Name);
                    }
                    return Task.FromResult(new YammerGroup { ID = id, Name = key });
                }
            }
        }
    }
}
