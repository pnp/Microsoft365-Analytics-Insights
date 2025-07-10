using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    }
}
