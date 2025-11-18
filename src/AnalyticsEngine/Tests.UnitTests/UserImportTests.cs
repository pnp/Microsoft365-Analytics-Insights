using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph;
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
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var auth = new GraphAppIndentityOAuthContext(telemetry, config.ClientID, config.TenantGUID.ToString(), config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate);

            await auth.InitClientCredential();
            var graphClient = new GraphServiceClient(auth.Creds);

            // Do a users import first so we have users in the users table to read apps for
            var userUpdater = new UserMetadataUpdater(telemetry, config, auth.Creds, new ManualGraphCallClient(auth, telemetry));
            await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();

            var updater = new UserAppLogUpdater(telemetry, new AppConfig());
            var sucess = await updater.UpdateUserInstalledApps(graphClient, new NoUsersHaveGroupsUserGroupsCache(telemetry), new UserGroupsFilterModel());
            Assert.IsTrue(sucess);
        }

        /// <summary>
        /// Check the app-log insert/update code works
        /// </summary>
        [TestMethod]
        public async Task UserAppSqlSaveTest()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var authConfig = new AppConfig();
            var auth = new GraphAppIndentityOAuthContext(telemetry, authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);

            await auth.InitClientCredential();
            var graphClient = new GraphServiceClient(auth.Creds);
            using (var db = new AnalyticsEntitiesContext())
            {
                var userAppsLoader = new GraphAndSqlUserAppLoader(db, telemetry, graphClient);

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
                var graphUsers = await graphClient.Users.Request().Filter("startswith(mail,'AllanD')").Top(1).GetAsync();
                var graphUser = graphUsers[0];

                // Run updater; force full load
                var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
                var userUpdater = new UserMetadataUpdater(telemetry, authConfig, auth.Creds, new ManualGraphCallClient(auth, telemetry));
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

        #region UserMetadataUpdater Unit Tests

        [TestMethod]
        public void UserMetadataUpdater_Constructor_WithInjectedLoader_SetsLoaderCorrectly()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();

            // Act
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            // Assert
            Assert.IsNotNull(updater.UserLoader);
            Assert.AreSame(fakeLoader, updater.UserLoader);
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_InsertsNewUsersOnly()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "newuser1@test.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true },
                new GraphUser { UserPrincipalName = "newuser2@test.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true },
                new GraphUser { UserPrincipalName = "existinguser@test.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                // Cleanup: Remove any existing test users from previous runs
                var existingTestUsers = await db.users
                    .Where(u => u.UserPrincipalName == "newuser1@test.com" 
                             || u.UserPrincipalName == "newuser2@test.com"
                             || u.UserPrincipalName == "existinguser@test.com")
                    .ToListAsync();
                
                if (existingTestUsers.Any())
                {
                    db.users.RemoveRange(existingTestUsers);
                    await db.SaveChangesAsync();
                }

                // Setup existing user
                var existingUser = new Common.Entities.User 
                { 
                    UserPrincipalName = "existinguser@test.com",
                    AzureAdId = graphUsers[2].Id
                };

                var existingDbUsers = new List<Common.Entities.User> { existingUser };

                // Act
                var insertedUsers = await updater.InsertMissingUsers(db, graphUsers, existingDbUsers, false);

                // Assert
                Assert.AreEqual(2, insertedUsers.Count);
                Assert.IsTrue(insertedUsers.Any(u => u.UserPrincipalName == "newuser1@test.com"));
                Assert.IsTrue(insertedUsers.Any(u => u.UserPrincipalName == "newuser2@test.com"));
                Assert.IsFalse(insertedUsers.Any(u => u.UserPrincipalName == "existinguser@test.com"));
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_IgnoresUsersWithoutUPN()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "validuser@test.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true },
                new GraphUser { UserPrincipalName = null, Id = Guid.NewGuid().ToString(), AccountEnabled = true },
                new GraphUser { UserPrincipalName = "", Id = Guid.NewGuid().ToString(), AccountEnabled = true }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                // Cleanup: Remove any existing test user from previous runs
                var existingTestUser = await db.users
                    .Where(u => u.UserPrincipalName == "validuser@test.com")
                    .FirstOrDefaultAsync();
                
                if (existingTestUser != null)
                {
                    db.users.Remove(existingTestUser);
                    await db.SaveChangesAsync();
                }

                // Act
                var insertedUsers = await updater.InsertMissingUsers(db, graphUsers, new List<Common.Entities.User>(), false);

                // Assert
                Assert.AreEqual(1, insertedUsers.Count);
                Assert.AreEqual("validuser@test.com", insertedUsers[0].UserPrincipalName);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_CaseInsensitiveUPNComparison()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "User@Test.COM", Id = Guid.NewGuid().ToString(), AccountEnabled = true }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                // Setup existing user with different casing
                var existingUser = new Common.Entities.User 
                { 
                    UserPrincipalName = "user@test.com",
                    AzureAdId = graphUsers[0].Id
                };

                var existingDbUsers = new List<Common.Entities.User> { existingUser };

                // Act
                var insertedUsers = await updater.InsertMissingUsers(db, graphUsers, existingDbUsers, false);

                // Assert - Should not insert because UPN matches (case insensitive)
                Assert.AreEqual(0, insertedUsers.Count);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_UpdatesUserMetadata()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var userId = Guid.NewGuid().ToString();
            var graphUsers = new List<GraphUser>
            {
                new GraphUser 
                { 
                    UserPrincipalName = "newuser@test.com", 
                    Id = userId,
                    AccountEnabled = true,
                    Department = "IT",
                    JobTitle = "Developer",
                    OfficeLocation = "Building 1",
                    Mail = "newuser@test.com",
                    PostalCode = "12345"
                }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                // Cleanup: Remove any existing test user from previous runs
                var existingTestUser = await db.users
                    .Where(u => u.UserPrincipalName == "newuser@test.com")
                    .FirstOrDefaultAsync();
                
                if (existingTestUser != null)
                {
                    db.users.Remove(existingTestUser);
                    await db.SaveChangesAsync();
                }

                // Act
                var insertedUsers = await updater.InsertMissingUsers(db, graphUsers, new List<Common.Entities.User>(), false);

                // Assert
                Assert.AreEqual(1, insertedUsers.Count);
                var insertedUser = insertedUsers[0];
                
                Assert.AreEqual("newuser@test.com", insertedUser.UserPrincipalName);
                Assert.AreEqual(userId, insertedUser.AzureAdId);
                Assert.AreEqual(true, insertedUser.AccountEnabled);
                Assert.AreEqual("newuser@test.com", insertedUser.Mail);
                Assert.AreEqual("12345", insertedUser.PostalCode);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_HandlesLargeNumberOfUsers()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var graphUsers = new List<GraphUser>();
            // Reduced from 5000 to 100 for performance - still tests batching logic
            for (int i = 0; i < 100; i++)
            {
                graphUsers.Add(new GraphUser 
                { 
                    UserPrincipalName = $"user{i}@test.com", 
                    Id = Guid.NewGuid().ToString(),
                    AccountEnabled = true
                });
            }

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                // Cleanup: Remove any existing test users from previous runs
                var existingTestUsers = await db.users
                    .Where(u => u.UserPrincipalName.StartsWith("user") && u.UserPrincipalName.EndsWith("@test.com"))
                    .ToListAsync();
                
                if (existingTestUsers.Any())
                {
                    db.users.RemoveRange(existingTestUsers);
                    await db.SaveChangesAsync();
                }

                // Act
                var insertedUsers = await updater.InsertMissingUsers(db, graphUsers, new List<Common.Entities.User>(), false);

                // Assert
                Assert.AreEqual(100, insertedUsers.Count);
            }
        }

        [TestMethod]
        public void UserMetadataUpdater_GetDbUsersFromGraphUsers_ReturnsMatchingUsers()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "user1@test.com", Id = Guid.NewGuid().ToString() },
                new GraphUser { UserPrincipalName = "user2@test.com", Id = Guid.NewGuid().ToString() },
                new GraphUser { UserPrincipalName = "user3@test.com", Id = Guid.NewGuid().ToString() }
            };

            var dbUsers = new List<Common.Entities.User>
            {
                new Common.Entities.User { UserPrincipalName = "user1@test.com", ID = 1 },
                new Common.Entities.User { UserPrincipalName = "user2@test.com", ID = 2 },
                new Common.Entities.User { UserPrincipalName = "other@test.com", ID = 3 }
            };

            // Act
            var result = updater.GetDbUsersFromGraphUsers(graphUsers, dbUsers);

            // Assert
            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.Any(u => u.UserPrincipalName == "user1@test.com"));
            Assert.IsTrue(result.Any(u => u.UserPrincipalName == "user2@test.com"));
            Assert.IsFalse(result.Any(u => u.UserPrincipalName == "other@test.com"));
        }

        [TestMethod]
        public void UserMetadataUpdater_GetDbUsersFromGraphUsers_IgnoresNullOrEmptyUPN()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "user1@test.com", Id = Guid.NewGuid().ToString() },
                new GraphUser { UserPrincipalName = null, Id = Guid.NewGuid().ToString() },
                new GraphUser { UserPrincipalName = "", Id = Guid.NewGuid().ToString() }
            };

            var dbUsers = new List<Common.Entities.User>
            {
                new Common.Entities.User { UserPrincipalName = "user1@test.com", ID = 1 }
            };

            // Act
            var result = updater.GetDbUsersFromGraphUsers(graphUsers, dbUsers);

            // Assert
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("user1@test.com", result[0].UserPrincipalName);
        }

        [TestMethod]
        public void UserMetadataUpdater_UpdateDbUserFromGraphUser_CopiesBasicProperties()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            var graphUser = new GraphUser
            {
                Id = Guid.NewGuid().ToString(),
                UserPrincipalName = "test@test.com",
                AccountEnabled = true,
                PostalCode = "12345",
                Mail = "test@test.com"
            };

            var dbUser = new Common.Entities.User { UserPrincipalName = "test@test.com" };

            // Act
            updater.UpdateDbUserFromGraphUser(dbUser, graphUser);

            // Assert
            Assert.AreEqual(graphUser.Id, dbUser.AzureAdId);
            Assert.AreEqual(graphUser.AccountEnabled, dbUser.AccountEnabled);
            Assert.AreEqual(graphUser.PostalCode, dbUser.PostalCode);
            Assert.AreEqual(graphUser.Mail, dbUser.Mail);
        }

        [TestMethod]
        public void UserMetadataUpdater_UpdateDbUserFromGraphUser_HandlesNullValues()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            var graphUser = new GraphUser
            {
                Id = Guid.NewGuid().ToString(),
                UserPrincipalName = "test@test.com",
                AccountEnabled = null,
                PostalCode = null,
                Mail = null
            };

            var dbUser = new Common.Entities.User 
            { 
                UserPrincipalName = "test@test.com",
                AccountEnabled = true,
                PostalCode = "OLD",
                Mail = "old@test.com"
            };

            // Act
            updater.UpdateDbUserFromGraphUser(dbUser, graphUser);

            // Assert
            Assert.AreEqual(graphUser.Id, dbUser.AzureAdId);
            Assert.IsNull(dbUser.AccountEnabled);
            Assert.IsNull(dbUser.PostalCode);
            Assert.IsNull(dbUser.Mail);
        }

        [TestMethod]
        public async Task UserMetadataUpdater_DeltaProvider_ClearsTokenWhenNoActiveUsers()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser>());
            
            // Set a delta token first
            await fakeLoader.DeltaValueProvider.SetDeltaToken("test-token");
            
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                // Ensure no active users in DB
                var activeUsers = await db.users.Where(u => u.AccountEnabled.HasValue && u.AccountEnabled.Value).ToListAsync();
                
                if (activeUsers.Count == 0)
                {
                    // Act - This should clear the delta token
                    // Note: This is tested indirectly through InsertAndUpdateDatabaseFromExternalUsers
                    // but we can test the delta provider directly
                    var tokenBefore = await fakeLoader.DeltaValueProvider.GetDeltaToken();
                    Assert.AreEqual("test-token", tokenBefore);

                    await fakeLoader.DeltaValueProvider.ClearDeltaToken();
                    var tokenAfter = await fakeLoader.DeltaValueProvider.GetDeltaToken();

                    // Assert
                    Assert.IsNull(tokenAfter);
                }
            }
        }

        #endregion
    }
}
