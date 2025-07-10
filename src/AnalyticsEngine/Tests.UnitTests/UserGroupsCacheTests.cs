using Common.Entities.Config;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
    }
}
