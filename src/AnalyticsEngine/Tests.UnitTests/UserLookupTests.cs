using Common.Entities;
using Common.Entities.LookupCaches;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class UserLookupTests
    {

        /// <summary>
        /// Test save and loading of users without anonymising PII
        /// </summary>
        [TestMethod]
        public async Task UserLookupManagerTest()
        {
            string randomUserName1 = $"unit-testing1{DateTime.Now.Ticks}@whatevs.com";
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                var userCache = new UserCache(db);

                // Save 
                var loadedUserLookup1 = await userCache.GetOrCreateUser(randomUserName1, true);

                // load & check names match
                var loadedUserManual1 = await db.users.Where(u => u.ID == loadedUserLookup1.ID).SingleOrDefaultAsync();
                Assert.AreEqual(loadedUserManual1.UserPrincipalName, randomUserName1);

            }
        }

    }
}
