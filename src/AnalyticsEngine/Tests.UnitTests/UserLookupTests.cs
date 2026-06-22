using Common.Entities;
using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.User;
using WebJob.Office365ActivityImporter.Engine.Graph.User.UserApps;

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

        /// <summary>
        /// Regression: GetUserEmailAddressesToFindAppsFor used a predicate of
        /// (AccountEnabled.HasValue &amp;&amp; AccountEnabled.HasValue) which is always true, so
        /// disabled accounts were NOT excluded from the per-user Teams-apps scan. Enabled users and
        /// users with no AccountEnabled value should be included; explicitly disabled users excluded.
        /// </summary>
        [TestMethod]
        public async Task GraphAndSqlUserAppLoader_ExcludesDisabledUsers()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var tick = DateTime.Now.Ticks;
            var enabledUpn = $"appsloader-enabled-{tick}@contoso.com";
            var disabledUpn = $"appsloader-disabled-{tick}@contoso.com";
            var unknownUpn = $"appsloader-unknown-{tick}@contoso.com";

            using (var db = new AnalyticsEntitiesContext())
            {
                var enabled = new User { UserPrincipalName = enabledUpn, AccountEnabled = true };
                var disabled = new User { UserPrincipalName = disabledUpn, AccountEnabled = false };
                var unknown = new User { UserPrincipalName = unknownUpn, AccountEnabled = null };
                db.users.Add(enabled);
                db.users.Add(disabled);
                db.users.Add(unknown);
                await db.SaveChangesAsync();

                try
                {
                    var loader = new GraphAndSqlUserAppLoader(db, telemetry, null);
                    var upns = await loader.GetUserEmailAddressesToFindAppsFor();

                    Assert.IsTrue(upns.Contains(enabledUpn), "Enabled user should be included");
                    Assert.IsTrue(upns.Contains(unknownUpn), "User with no AccountEnabled value should be included");
                    Assert.IsFalse(upns.Contains(disabledUpn), "Explicitly disabled user must be excluded");
                }
                finally
                {
                    db.users.Remove(enabled);
                    db.users.Remove(disabled);
                    db.users.Remove(unknown);
                    await db.SaveChangesAsync();
                }
            }
        }

        /// <summary>
        /// Regression for the user-app paging gap: CollectAllAppPagesAsync must follow every
        /// @odata.nextLink and return apps from all pages (a user with more installed apps than fit on
        /// one Graph page previously lost the later pages). The page fetch is a fake "adaptor" delegate
        /// so multi-page paging is exercised without a live tenant.
        /// </summary>
        [TestMethod]
        public async Task UserApps_CollectAllAppPages_FollowsNextLinkAcrossPages()
        {
            var page1 = new UserTeamAppResponse { OdataNextLink = "https://graph.microsoft.com/next1", Apps = new List<UserTeamApp> { AppWithId("a1") } };
            var page2 = new UserTeamAppResponse { OdataNextLink = "https://graph.microsoft.com/next2", Apps = new List<UserTeamApp> { AppWithId("a2"), AppWithId("a3") } };
            var page3 = new UserTeamAppResponse { OdataNextLink = null, Apps = new List<UserTeamApp> { AppWithId("a4") } };

            var pagesByLink = new Dictionary<string, UserTeamAppResponse>
            {
                { "https://graph.microsoft.com/next1", page2 },
                { "https://graph.microsoft.com/next2", page3 },
            };
            var fetchedLinks = new List<string>();

            var all = await GraphAndSqlUserAppLoader.CollectAllAppPagesAsync(page1, link =>
            {
                fetchedLinks.Add(link);
                return Task.FromResult(pagesByLink[link]);
            });

            CollectionAssert.AreEqual(new[] { "a1", "a2", "a3", "a4" }, all.Select(a => a.Id).ToList());
            CollectionAssert.AreEqual(new[] { "https://graph.microsoft.com/next1", "https://graph.microsoft.com/next2" }, fetchedLinks);
        }

        [TestMethod]
        public async Task UserApps_CollectAllAppPages_SinglePageDoesNotFetch()
        {
            var only = new UserTeamAppResponse { OdataNextLink = null, Apps = new List<UserTeamApp> { AppWithId("solo") } };
            var fetchCalled = false;

            var all = await GraphAndSqlUserAppLoader.CollectAllAppPagesAsync(only, _ =>
            {
                fetchCalled = true;
                return Task.FromResult<UserTeamAppResponse>(null);
            });

            Assert.IsFalse(fetchCalled, "A single page (no @odata.nextLink) must not trigger a fetch.");
            Assert.AreEqual(1, all.Count);
            Assert.AreEqual("solo", all[0].Id);
        }

        private static UserTeamApp AppWithId(string id) => new UserTeamApp { Id = id };

    }
}
