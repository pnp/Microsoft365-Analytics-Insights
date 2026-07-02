using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.User;
using WebJob.Office365ActivityImporter.Engine.Graph.User.UserApps;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for user import and user-related functionality
    /// </summary>
    [TestClass]
    public class UserImportTests
    {
        [TestMethod]
        public async Task UserAppLoaderFakeTest()
        {
            const int users = 10000;
            var l = new FakeUserAppLoader(AnalyticsLogger.ConsoleOnlyTracer(), users);
            var updates = await l.LoadAndSave(new NoUsersHaveGroupsUserGroupsCache(AnalyticsLogger.ConsoleOnlyTracer()), new UserGroupsFilterModel());
            Assert.IsTrue(updates == users);
        }

        // Removing test as devops environment has too many users and test times out
        //[TestMethod]
        public async Task UserAppLoaderRealTest()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var auth = new GraphAppIndentityOAuthContext(logger, config.ClientID, config.TenantGUID.ToString(), config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate);

            await auth.InitClientCredential();
            var graphClient = new GraphServiceClient(auth.Creds);

            // Do a users import first so we have users in the users table to read apps for
            var userUpdater = new UserMetadataUpdater(logger, config, auth.Creds, new ManualGraphCallClient(auth, logger));
            await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();

            var updater = new UserAppLogUpdater(logger, new AppConfig());
            var sucess = await updater.UpdateUserInstalledApps(graphClient, new NoUsersHaveGroupsUserGroupsCache(logger), new UserGroupsFilterModel());
            Assert.IsTrue(sucess);
        }

        /// <summary>
        /// Check the app-log insert/update code works
        /// </summary>
        [TestMethod]
        public async Task UserAppSqlSaveTest()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var authConfig = new AppConfig();
            var auth = new GraphAppIndentityOAuthContext(logger, authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);

            await auth.InitClientCredential();
            var graphClient = new GraphServiceClient(auth.Creds);
            using (var db = new AnalyticsEntitiesContext())
            {
                var userAppsLoader = new GraphAndSqlUserAppLoader(db, logger, graphClient);

                var testUser = new Common.Entities.User { AzureAdId = Guid.NewGuid().ToString(), UserPrincipalName = $"teamsappsuser{DateTime.Now.Ticks}@unitesting.local" };
                db.users.Add(testUser);

                var newAppDef1 = new Common.Entities.Teams.TeamAddOnDefinition { GraphID = Guid.NewGuid().ToString(), Name = "Test app 1+ " + DateTime.Now.Ticks };
                var newAppDef2 = new Common.Entities.Teams.TeamAddOnDefinition { GraphID = Guid.NewGuid().ToString(), Name = "Test app 2+ " + DateTime.Now.Ticks };
                db.TeamAddOns.AddRange(new Common.Entities.Teams.TeamAddOnDefinition[] { newAppDef1, newAppDef2 });
                await db.SaveChangesAsync();

                var testData = new Dictionary<string, List<UserTeamApp>>
                {
                    {
                        testUser.UserPrincipalName,
                        new List<UserTeamApp>
                        {
                            new UserTeamApp { TeamsAppDefinition = new TeamsAppDefinition { TeamsAppId = newAppDef1.GraphID, DisplayName = newAppDef1.Name } },
                            new UserTeamApp { TeamsAppDefinition = new TeamsAppDefinition { TeamsAppId = newAppDef2.GraphID, DisplayName = newAppDef2.Name } }
                        }
                    }
                };

                await userAppsLoader.Save(testData);

                // Find logs. Should only be two
                var logs = await db.UserAppsLog.Where(l => l.UserID == testUser.ID).ToListAsync();
                Assert.IsTrue(logs.Count == 2);

                // Save again. Should still only be two (same date)
                await userAppsLoader.Save(testData);
                logs = await db.UserAppsLog.Where(l => l.UserID == testUser.ID).ToListAsync();
                Assert.IsTrue(logs.Count == 2);

                // Fake logs as yesterdays
                logs[0].Date = logs[0].Date.AddDays(-1);
                logs[1].Date = logs[1].Date.AddDays(-1);
                await db.SaveChangesAsync();

                // Save should now insert new
                await userAppsLoader.Save(testData);
                logs = await db.UserAppsLog.Where(l => l.UserID == testUser.ID).ToListAsync();
                Assert.IsTrue(logs.Count == 4);
            }
        }

        // Removing test as devops environment has too many users and test times out
        //[TestMethod]
        public async Task UserMetadataUpdaterTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Update users

                var authConfig = new AppConfig();
                var auth = new GraphAppIndentityOAuthContext(AnalyticsLogger.ConsoleOnlyTracer(), authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);
                await auth.InitClientCredential();
                var graphClient = new GraphServiceClient(auth.Creds);

                // Get Allan user from Graph & insert blanks into DB (needs license)
                var graphUsers = await graphClient.Users.GetAsync(rc =>
                {
                    rc.QueryParameters.Filter = "startswith(mail,'AllanD')";
                    rc.QueryParameters.Top = 1;
                });
                var graphUser = graphUsers.Value[0];

                // Run updater; force full load
                var logger = AnalyticsLogger.ConsoleOnlyTracer();
                var userUpdater = new UserMetadataUpdater(logger, authConfig, auth.Creds, new ManualGraphCallClient(auth, logger));
                await userUpdater.UserLoader.DeltaValueProvider.ClearDeltaToken();

                await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();

                // Check our user just updated. Should be updated now with actual data
                var dbTestUser = await db.users
                    .Include(u => u.OfficeLocation)
                    .Include(u => u.UsageLocation)
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Include(u => u.JobTitle)
                    .Include(u => u.Department)
                    .Where(u => u.UserPrincipalName == graphUser.Mail).SingleOrDefaultAsync();

                Assert.IsTrue(dbTestUser.LicenseLookups.Count > 0);
                Assert.IsNotNull(dbTestUser.OfficeLocation);
                Assert.IsNotNull(dbTestUser.UsageLocation);
                Assert.IsNotNull(dbTestUser.Department);
                Assert.IsTrue(dbTestUser.AccountEnabled);

                // Update again. Should use the delta this time
                await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();


                // Update again with no delta. Test logic for updating just existing
                await userUpdater.UserLoader.DeltaValueProvider.ClearDeltaToken();
                await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();
            }
        }

        [TestMethod]
        public void OfficeLicenseNameResolverTest()
        {
            var resolver = new OfficeLicenseNameResolver();
            Assert.IsTrue(resolver.GetDisplayNameFor("DYN365_BUSCENTRAL_ESSENTIAL") == "Dynamics 365 Business Central Essentials");

            Assert.IsNull(resolver.GetDisplayNameFor(""));
        }

    }
}
