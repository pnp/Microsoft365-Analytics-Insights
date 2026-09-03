using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the batched manager resolution introduced by #371.
    ///
    /// <c>UserDataMapper.UpdateUserManager</c> resolves a user's manager through a five-branch
    /// precedence chain, and its fourth branch went to the database <b>per user</b>. On a first
    /// import that is the branch nearly every user takes - the insert-enrichment phase builds its
    /// Entra-id dictionary from pre-existing users only - so a brand new ~200,000-user tenant
    /// issued up to 200,000 individual <c>SELECT ... WHERE user_name = @p</c> round trips in a
    /// single cycle. These tests pin the replacement: one chunked query per batch.
    ///
    /// Zero SQL Server and zero Graph dependency.
    /// </summary>
    [TestClass]
    public class UserManagerResolutionTests
    {
        private static GraphUser User(string upn, string aadId, string managerAadId = null)
        {
            var user = new GraphUser { Id = aadId, UserPrincipalName = upn };
            if (managerAadId != null)
            {
                user.ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = managerAadId } };
            }
            return user;
        }

        private static Dictionary<string, GraphUser> ByAadId(params GraphUser[] users)
        {
            var d = new Dictionary<string, GraphUser>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in users) d[u.Id] = u;
            return d;
        }

        #region Which UPNs a batch needs

        [TestMethod]
        public void ManagerPrefetch_CollectsTheUpnOfEachManagerReferencedByTheBatch()
        {
            var bossA = User("boss.a@contoso.com", "aad-boss-a");
            var bossB = User("boss.b@contoso.com", "aad-boss-b");
            var batch = new[]
            {
                User("one@contoso.com", "aad-1", "aad-boss-a"),
                User("two@contoso.com", "aad-2", "aad-boss-b"),
            };

            var upns = ManagerResolutionRules.CollectManagerUpnsToPrefetch(batch, ByAadId(bossA, bossB));

            CollectionAssert.AreEquivalent(new[] { "boss.a@contoso.com", "boss.b@contoso.com" }, upns);
        }

        [TestMethod]
        public void ManagerPrefetch_SharedManager_IsRequestedOnlyOnce()
        {
            // The realistic shape: a few hundred reports under one manager. Without the de-dup the
            // "one query per batch" claim would still send one parameter per user and hit SQL
            // Server's 2,100-parameter limit far sooner than the chunk size suggests.
            var boss = User("boss@contoso.com", "aad-boss");
            var batch = Enumerable.Range(0, 500)
                .Select(i => User($"user{i}@contoso.com", $"aad-{i}", "aad-boss"))
                .ToList();

            var upns = ManagerResolutionRules.CollectManagerUpnsToPrefetch(batch, ByAadId(boss));

            Assert.AreEqual(1, upns.Count, "500 reports under one manager is one UPN to look up, not 500.");
            Assert.AreEqual("boss@contoso.com", upns[0]);
        }

        [TestMethod]
        public void ManagerPrefetch_SharedManagerDifferingOnlyByCase_IsRequestedOnlyOnce()
        {
            var bossLower = User("boss@contoso.com", "aad-boss-1");
            var bossUpper = User("BOSS@contoso.com", "aad-boss-2");
            var batch = new[]
            {
                User("one@contoso.com", "aad-1", "aad-boss-1"),
                User("two@contoso.com", "aad-2", "aad-boss-2"),
            };

            var upns = ManagerResolutionRules.CollectManagerUpnsToPrefetch(batch, ByAadId(bossLower, bossUpper));

            Assert.AreEqual(1, upns.Count,
                "SQL Server's default collation is case-insensitive, so two casings are one row - de-dup must be too.");
        }

        [TestMethod]
        public void ManagerPrefetch_UsersWithNoManager_ContributeNothing()
        {
            var batch = new[] { User("one@contoso.com", "aad-1"), User("two@contoso.com", "aad-2") };

            var upns = ManagerResolutionRules.CollectManagerUpnsToPrefetch(batch, ByAadId());

            Assert.AreEqual(0, upns.Count);
        }

        [TestMethod]
        public void ManagerPrefetch_ManagerNotInTheGraphBatch_ContributesNothing()
        {
            // The database-by-UPN branch reads the UPN off the cached Graph user, so a manager who
            // is not in the batch can never reach it and prefetching them would be wasted work.
            var batch = new[] { User("one@contoso.com", "aad-1", "aad-unknown-boss") };

            var upns = ManagerResolutionRules.CollectManagerUpnsToPrefetch(batch, ByAadId());

            Assert.AreEqual(0, upns.Count);
        }

        [TestMethod]
        public void ManagerPrefetch_ManagerWithNoUpn_ContributesNothing()
        {
            var boss = User(null, "aad-boss");
            var batch = new[] { User("one@contoso.com", "aad-1", "aad-boss") };

            var upns = ManagerResolutionRules.CollectManagerUpnsToPrefetch(batch, ByAadId(boss));

            Assert.AreEqual(0, upns.Count);
        }

        #endregion

        #region Indexing what came back

        [TestMethod]
        public void ManagerPrefetch_DuplicateUpnRows_ResolveToTheLowestId()
        {
            // dbo.users has no unique constraint on user_name and real databases do contain
            // duplicates - which is why UserCache.Load orders by id and takes the first. The
            // prefetch has to agree with it, or a duplicated manager would resolve to a different
            // row depending on which code path asked.
            var index = ManagerResolutionRules.IndexByUpn(new[]
            {
                new Common.Entities.User { ID = 90, UserPrincipalName = "boss@contoso.com" },
                new Common.Entities.User { ID = 12, UserPrincipalName = "boss@contoso.com" },
                new Common.Entities.User { ID = 45, UserPrincipalName = "boss@contoso.com" },
            });

            Assert.AreEqual(1, index.Count);
            Assert.AreEqual(12, index["boss@contoso.com"].ID);
        }

        [TestMethod]
        public void ManagerPrefetch_IndexIsCaseInsensitive()
        {
            var index = ManagerResolutionRules.IndexByUpn(new[]
            {
                new Common.Entities.User { ID = 5, UserPrincipalName = "Boss@Contoso.com" },
            });

            Assert.IsTrue(index.TryGetValue("boss@contoso.com", out var found));
            Assert.AreEqual(5, found.ID);
        }

        [TestMethod]
        public void ManagerPrefetch_RowsWithNoUpn_AreSkippedRatherThanThrowing()
        {
            var index = ManagerResolutionRules.IndexByUpn(new[]
            {
                new Common.Entities.User { ID = 1, UserPrincipalName = null },
                new Common.Entities.User { ID = 2, UserPrincipalName = "" },
                new Common.Entities.User { ID = 3, UserPrincipalName = "boss@contoso.com" },
            });

            Assert.AreEqual(1, index.Count);
            Assert.IsTrue(index.ContainsKey("boss@contoso.com"));
        }

        #endregion

        #region The N+1 guard

        [TestMethod]
        public async Task ManagerPrefetch_ResolvesTheWholeBatchInOneLookup_NotOnePerUser()
        {
            // 300 users, 300 distinct managers: the cache must ask the store exactly once, with all
            // 300 UPNs, and answer every subsequent lookup without going back.
            //
            // What this pins is the cache, not the call sites - a regression that removed the
            // PrefetchManagersForBatchAsync call from UserBatchProcessor or UserInsertProcessor
            // would leave it green. That wiring is covered by the database-backed
            // UserMetadataUpdater* tests, which exercise the real batch loops.
            const int userCount = 300;

            var managers = Enumerable.Range(0, userCount)
                .Select(i => User($"boss{i}@contoso.com", $"aad-boss-{i}"))
                .ToList();
            var batch = Enumerable.Range(0, userCount)
                .Select(i => User($"user{i}@contoso.com", $"aad-{i}", $"aad-boss-{i}"))
                .ToList();

            var store = new InMemoryUserLookupStore();
            for (var i = 0; i < userCount; i++)
            {
                store.Add(1000 + i, $"boss{i}@contoso.com");
            }

            var cache = new ManagerPrefetchCache(store);
            await cache.LoadForBatchAsync(batch, ByAadId(managers.ToArray()));

            Assert.AreEqual(1, store.CallCount,
                $"{userCount} users must cost ONE store call, whatever the batch size.");
            Assert.AreEqual(userCount, store.RequestedUpnBatches[0].Count);
            Assert.AreEqual(userCount, cache.Count);

            for (var i = 0; i < userCount; i++)
            {
                Assert.IsTrue(cache.TryGet($"boss{i}@contoso.com", out var manager), $"boss{i} should be cached");
                Assert.AreEqual(1000 + i, manager.ID);
            }
            Assert.AreEqual(1, store.CallCount, "Reading the cache must not go back to the store.");
        }

        [TestMethod]
        public async Task ManagerPrefetch_ManagerMissingFromTheDatabase_IsSimplyAbsent()
        {
            // A miss has to fall through to the existing per-user query (which then creates the
            // placeholder user), so the cache must report "not found" rather than a null entry.
            var boss = User("boss@contoso.com", "aad-boss");
            var batch = new[] { User("one@contoso.com", "aad-1", "aad-boss") };

            var cache = new ManagerPrefetchCache(new InMemoryUserLookupStore());
            await cache.LoadForBatchAsync(batch, ByAadId(boss));

            Assert.AreEqual(0, cache.Count);
            Assert.IsFalse(cache.TryGet("boss@contoso.com", out var manager));
            Assert.IsNull(manager);
        }

        [TestMethod]
        public async Task ManagerPrefetch_NoManagersInTheBatch_DoesNotTouchTheStore()
        {
            var store = new InMemoryUserLookupStore();
            var cache = new ManagerPrefetchCache(store);

            await cache.LoadForBatchAsync(new[] { User("one@contoso.com", "aad-1") }, ByAadId());

            Assert.AreEqual(0, store.CallCount, "A batch with no managers has nothing to look up.");
        }

        [TestMethod]
        public async Task ManagerPrefetch_EachBatchReplacesThePreviousOne()
        {
            // Load-bearing: the entities are tracked by the import's context and every batch ends by
            // detaching them, so carrying a manager over into the next batch would hand EF a
            // detached entity and it would try to INSERT the manager.
            var bossA = User("boss.a@contoso.com", "aad-boss-a");
            var bossB = User("boss.b@contoso.com", "aad-boss-b");
            var store = new InMemoryUserLookupStore().Add(1, "boss.a@contoso.com").Add(2, "boss.b@contoso.com");
            var cache = new ManagerPrefetchCache(store);

            await cache.LoadForBatchAsync(new[] { User("one@contoso.com", "aad-1", "aad-boss-a") }, ByAadId(bossA, bossB));
            Assert.IsTrue(cache.TryGet("boss.a@contoso.com", out _));

            await cache.LoadForBatchAsync(new[] { User("two@contoso.com", "aad-2", "aad-boss-b") }, ByAadId(bossA, bossB));

            Assert.IsTrue(cache.TryGet("boss.b@contoso.com", out _), "The new batch's manager should be cached.");
            Assert.IsFalse(cache.TryGet("boss.a@contoso.com", out _),
                "The previous batch's manager must be dropped - it is detached by now.");
        }

        [TestMethod]
        public async Task ManagerPrefetch_BatchWithNothingToLookUp_StillClearsThePreviousBatch()
        {
            // The case the test above cannot reach: when a batch has no managers the store is never
            // called, so nothing overwrites the cache. Without an explicit reset the previous
            // batch's entities would survive - and by now the import has detached them, so handing
            // one to EF would make it try to INSERT the manager.
            var boss = User("boss.a@contoso.com", "aad-boss-a");
            var store = new InMemoryUserLookupStore().Add(1, "boss.a@contoso.com");
            var cache = new ManagerPrefetchCache(store);

            await cache.LoadForBatchAsync(new[] { User("one@contoso.com", "aad-1", "aad-boss-a") }, ByAadId(boss));
            Assert.AreEqual(1, cache.Count, "Precondition: the first batch populated the cache.");

            await cache.LoadForBatchAsync(new[] { User("two@contoso.com", "aad-2") }, ByAadId(boss));

            Assert.AreEqual(0, cache.Count, "A batch with no managers must leave the cache empty, not stale.");
            Assert.IsFalse(cache.TryGet("boss.a@contoso.com", out _));
            Assert.AreEqual(1, store.CallCount, "Precondition: the second batch never reached the store.");
        }

        [TestMethod]
        public async Task ManagerPrefetch_WithNoLookupStore_StaysEmptySoTheOriginalQueryIsUsed()
        {
            var boss = User("boss@contoso.com", "aad-boss");
            var cache = new ManagerPrefetchCache(null);

            await cache.LoadForBatchAsync(new[] { User("one@contoso.com", "aad-1", "aad-boss") }, ByAadId(boss));

            Assert.AreEqual(0, cache.Count);
            Assert.IsFalse(cache.TryGet("boss@contoso.com", out _));
        }

        [TestMethod]
        public async Task ManagerPrefetch_StoreFailure_SurfacesToTheCaller()
        {
            // The fake fails as a faulted Task rather than throwing synchronously, so this also
            // proves the prefetch is awaited: a dropped await would leave the exception unobserved
            // and this test would fail.
            var boss = User("boss@contoso.com", "aad-boss");
            var store = new FailingUserLookupStore();
            var cache = new ManagerPrefetchCache(store);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                cache.LoadForBatchAsync(new[] { User("one@contoso.com", "aad-1", "aad-boss") }, ByAadId(boss)));

            Assert.AreEqual(1, store.CallCount);
        }

        #endregion
    }
}
