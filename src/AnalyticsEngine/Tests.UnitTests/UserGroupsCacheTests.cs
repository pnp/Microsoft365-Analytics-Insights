using Common.Entities.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace Tests.UnitTests
{
    [TestClass]
    public class UserGroupsCacheTests
    {
        [TestMethod]
        public async Task IsInGroupsFilter_ReturnsTrue_WhenUserInMatchingGroup()
        {
            // Arrange
            var mockGroups = new Dictionary<string, List<string>>
            {
                { "user1@contoso.com", new List<string> { "Finance", "HR" } }
            };
            var cache = new MockUserGroupsCache(mockGroups);
            var filter = new UserGroupsFilterModel("Fin*;IT");

            // Act
            var result = await cache.IsInGroupsFilter("user1@contoso.com", filter);

            // Assert
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(result);
        }

        [TestMethod]
        public async Task IsInGroupsFilter_ReturnsFalse_WhenUserNotInMatchingGroup()
        {
            // Arrange
            var mockGroups = new Dictionary<string, List<string>>
            {
                { "user2@contoso.com", new List<string> { "Marketing", "Sales" } }
            };
            var cache = new MockUserGroupsCache(mockGroups);
            var filter = new UserGroupsFilterModel("Fin*;IT");

            // Act
            var result = await cache.IsInGroupsFilter("user2@contoso.com", filter);

            // Assert
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsFalse(result);
        }

        [TestMethod]
        public async Task IsInGroupsFilter_ReturnsTrue_WhenNoFilterConfigured()
        {
            // Arrange
            var mockGroups = new Dictionary<string, List<string>>
            {
                { "user1@contoso.com", new List<string> { "Finance", "HR" } },
                { "user2@contoso.com", new List<string> { "Marketing", "Sales" } },
                { "user3@contoso.com", new List<string> { "IT", "Support" } }
            };
            var cache = new MockUserGroupsCache(mockGroups);
            var filter = new UserGroupsFilterModel(); // No filter configured

            // Act & Assert
            foreach (var user in mockGroups.Keys)
            {
                var result = await cache.IsInGroupsFilter(user, filter);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(result, $"Expected true for user {user} when no filter is configured.");
            }
        }

        /// <summary>
        /// Regression: a failed group load must NOT be cached. Caching an empty list on a transient
        /// Graph error would (because IsInGroupsFilter treats "no groups" as "matches the filter")
        /// silently include the user for the whole TTL; instead the next call should retry.
        /// </summary>
        [TestMethod]
        public async Task GetGroupsForUser_DoesNotCacheFailedLoad()
        {
            var cache = new ThrowNTimesThenSucceedGroupsCache(new List<string> { "Finance" }) { FailTimes = 1 };

            // 1st call: the load throws -> an empty list that must NOT be cached.
            var first = await cache.GetGroupsForUserAsync("u@contoso.com");
            Assert.AreEqual(0, first.Count);
            Assert.AreEqual(0, cache.CachedEntryCount, "A failed load must not be cached.");

            // 2nd call: retries, now succeeds, and caches.
            var second = await cache.GetGroupsForUserAsync("u@contoso.com");
            CollectionAssert.AreEquivalent(new List<string> { "Finance" }, second.ToList());
            Assert.AreEqual(1, cache.CachedEntryCount, "A successful load must be cached.");
            Assert.AreEqual(2, cache.LoadCalls);

            // 3rd call: served from cache - no further load.
            await cache.GetGroupsForUserAsync("u@contoso.com");
            Assert.AreEqual(2, cache.LoadCalls, "A cached result must not trigger another load.");
        }

        private class ThrowNTimesThenSucceedGroupsCache : UserGroupsCache
        {
            private readonly List<string> _groups;
            public int LoadCalls { get; private set; }
            public int FailTimes { get; set; }

            public ThrowNTimesThenSucceedGroupsCache(List<string> groups) : base(null) { _groups = groups; }

            protected override Task<List<string>> LoadGroupsFromExternalAsync(string upn)
            {
                LoadCalls++;
                if (LoadCalls <= FailTimes)
                {
                    throw new Exception("Simulated transient Graph failure");
                }
                return Task.FromResult(_groups);
            }
        }
    }
}
