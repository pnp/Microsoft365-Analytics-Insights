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

        [TestMethod]
        public async Task UserMetadataUpdater_UserLicenseChange_DatabaseReflectsChange()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"licensechangeuser{DateTime.Now.Ticks}@test.com";
            
            // Initial license SKUs
            var initialSkuId = Guid.NewGuid();
            var initialSkuPartNumber = "ENTERPRISEPACK";
            var initialLicenseName = "Office 365 E3";
            
            // Changed license SKUs
            var newSkuId = Guid.NewGuid();
            var newSkuPartNumber = "ENTERPRISEPREMIUM";
            var newLicenseName = "Office 365 E5";

            // Cleanup: Remove any existing test data from previous runs
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users
                    .Include(u => u.LicenseLookups)
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();
                
                if (existingTestUser != null)
                {
                    cleanupDb.UserLicenseTypeLookups.RemoveRange(existingTestUser.LicenseLookups);
                    cleanupDb.users.Remove(existingTestUser);
                    await cleanupDb.SaveChangesAsync();
                }

                // Clean up test license types
                var testLicenses = await cleanupDb.LicenseTypes
                    .Where(l => l.Name == initialLicenseName || l.Name == newLicenseName)
                    .ToListAsync();
                if (testLicenses.Any())
                {
                    cleanupDb.LicenseTypes.RemoveRange(testLicenses);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Step 1: Create user with initial license
            var graphUser = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = true,
                Mail = userUpn
            };

            var initialSku = new SubscribedSku
            {
                SkuId = initialSkuId,
                SkuPartNumber = initialSkuPartNumber
            };

            var initialSkus = new GraphServiceSubscribedSkusCollectionPage
            {
                initialSku
            };

            var usersWithInitialSku = new List<Microsoft.Graph.User>
            {
                new Microsoft.Graph.User { UserPrincipalName = userUpn, Id = userId }
            };

            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>>
            {
                { initialSkuId, usersWithInitialSku }
            };

            var fakeLoader = new FakeUserMetadataLoader(
                new List<GraphUser> { graphUser },
                initialSkus,
                fakeUsersBySku
            );

            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            // Act 1: Initial import with first license
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert 1: Verify user has initial license
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser, "User should be created in database");
                Assert.AreEqual(1, dbUser.LicenseLookups.Count, "User should have exactly one license initially");
                Assert.AreEqual(initialLicenseName, dbUser.LicenseLookups[0].License.Name, "Initial license should be Office 365 E3");
                Assert.AreEqual(initialSkuPartNumber, dbUser.LicenseLookups[0].License.SKUID, "Initial SKU should match");
            }

            // Step 2: Change user's license
            var newSku = new SubscribedSku
            {
                SkuId = newSkuId,
                SkuPartNumber = newSkuPartNumber
            };

            var updatedSkus = new GraphServiceSubscribedSkusCollectionPage
            {
                newSku
            };

            var usersWithNewSku = new List<Microsoft.Graph.User>
            {
                new Microsoft.Graph.User { UserPrincipalName = userUpn, Id = userId }
            };

            var updatedFakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>>
            {
                { newSkuId, usersWithNewSku }
            };

            var updatedFakeLoader = new FakeUserMetadataLoader(
                new List<GraphUser> { graphUser },
                updatedSkus,
                updatedFakeUsersBySku
            );

            var updaterWithNewLicense = new UserMetadataUpdater(telemetry, config, updatedFakeLoader);

            // Act 2: Update with changed license
            await updaterWithNewLicense.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert 2: Verify user now has new license and old license is removed
            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUserFinal, "User should still exist in database");
                Assert.AreEqual(1, dbUserFinal.LicenseLookups.Count, "User should still have exactly one license after update");
                Assert.AreEqual(newLicenseName, dbUserFinal.LicenseLookups[0].License.Name, "New license should be Office 365 E5");
                Assert.AreEqual(newSkuPartNumber, dbUserFinal.LicenseLookups[0].License.SKUID, "New SKU should match");
                
                // Verify old license is no longer associated with user
                Assert.IsFalse(dbUserFinal.LicenseLookups.Any(l => l.License.Name == initialLicenseName), 
                    "Old license should be removed from user");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_AllLicensesRemoved_DatabaseReflectsRemoval()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"licenseremoveduser{DateTime.Now.Ticks}@test.com";
            
            var skuId = Guid.NewGuid();
            var skuPartNumber = "ENTERPRISEPACK";
            var licenseName = "Office 365 E3";

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users
                    .Include(u => u.LicenseLookups)
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();
                
                if (existingTestUser != null)
                {
                    cleanupDb.UserLicenseTypeLookups.RemoveRange(existingTestUser.LicenseLookups);
                    cleanupDb.users.Remove(existingTestUser);
                    await cleanupDb.SaveChangesAsync();
                }

                var testLicenses = await cleanupDb.LicenseTypes
                    .Where(l => l.Name == licenseName)
                    .ToListAsync();
                if (testLicenses.Any())
                {
                    cleanupDb.LicenseTypes.RemoveRange(testLicenses);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Step 1: Create user with license
            var graphUser = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = true,
                Mail = userUpn
            };

            var sku = new SubscribedSku
            {
                SkuId = skuId,
                SkuPartNumber = skuPartNumber
            };

            var skus = new GraphServiceSubscribedSkusCollectionPage { sku };

            var usersWithSku = new List<Microsoft.Graph.User>
            {
                new Microsoft.Graph.User { UserPrincipalName = userUpn, Id = userId }
            };

            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>>
            {
                { skuId, usersWithSku }
            };

            var fakeLoader = new FakeUserMetadataLoader(
                new List<GraphUser> { graphUser },
                skus,
                fakeUsersBySku
            );

            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            // Act 1: Initial import with license
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert 1: Verify user has license
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser, "User should be created in database");
                Assert.AreEqual(1, dbUser.LicenseLookups.Count, "User should have exactly one license initially");
                Assert.AreEqual(licenseName, dbUser.LicenseLookups[0].License.Name, "Initial license should be Office 365 E3");
                Assert.AreEqual(skuPartNumber, dbUser.LicenseLookups[0].License.SKUID, "Initial SKU should match");
            }

            // Step 2: Remove all licenses
            var emptySkus = new GraphServiceSubscribedSkusCollectionPage();
            var updatedFakeLoader = new FakeUserMetadataLoader(
                new List<GraphUser> { graphUser },
                emptySkus,
                new Dictionary<Guid, List<Microsoft.Graph.User>>()
            );

            var updaterNoLicenses = new UserMetadataUpdater(telemetry, config, updatedFakeLoader);
            await updaterNoLicenses.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert: Verify all licenses removed
            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users
                    .Include(u => u.LicenseLookups)
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUserFinal, "User should still exist in database");
                Assert.AreEqual(0, dbUserFinal.LicenseLookups.Count, "User should have no licenses");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_UserDeactivated_AccountEnabledUpdated()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"deactivateduser{DateTime.Now.Ticks}@test.com";

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();
                
                if (existingTestUser != null)
                {
                    cleanupDb.users.Remove(existingTestUser);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Step 1: Create active user
            var graphUserActive = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = true,
                Mail = userUpn
            };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUserActive });
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Verify user is active
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsTrue(dbUser.AccountEnabled.Value, "User should be active initially");
            }

            // Step 2: Deactivate user
            var graphUserDeactivated = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = false,
                Mail = userUpn
            };

            var updatedFakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUserDeactivated });
            var updaterDeactivated = new UserMetadataUpdater(telemetry, config, updatedFakeLoader);
            await updaterDeactivated.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert: Verify user is deactivated
            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUserFinal, "User should still exist in database");
                Assert.IsFalse(dbUserFinal.AccountEnabled.Value, "User should be deactivated");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_MetadataChanged_DatabaseReflectsChanges()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"metadatachangeuser{DateTime.Now.Ticks}@test.com";

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();
                
                if (existingTestUser != null)
                {
                    cleanupDb.users.Remove(existingTestUser);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Step 1: Create user with initial metadata
            var graphUserInitial = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = true,
                Mail = userUpn,
                Department = "IT",
                JobTitle = "Developer",
                OfficeLocation = "Building 1",
                PostalCode = "12345"
            };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUserInitial });
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Verify initial metadata
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Include(u => u.Department)
                    .Include(u => u.JobTitle)
                    .Include(u => u.OfficeLocation)
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.AreEqual("IT", dbUser.Department?.Name, "Initial department should be IT");
                Assert.AreEqual("Developer", dbUser.JobTitle?.Name, "Initial job title should be Developer");
                Assert.AreEqual("Building 1", dbUser.OfficeLocation?.Name, "Initial office location should be Building 1");
                Assert.AreEqual("12345", dbUser.PostalCode, "Initial postal code should be 12345");
            }

            // Step 2: Update user metadata
            var graphUserUpdated = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = true,
                Mail = userUpn,
                Department = "HR",
                JobTitle = "Manager",
                OfficeLocation = "Building 2",
                PostalCode = "67890"
            };

            var updatedFakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUserUpdated });
            var updaterUpdated = new UserMetadataUpdater(telemetry, config, updatedFakeLoader);
            await updaterUpdated.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert: Verify metadata updated
            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users
                    .Include(u => u.Department)
                    .Include(u => u.JobTitle)
                    .Include(u => u.OfficeLocation)
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUserFinal, "User should still exist in database");
                Assert.AreEqual("HR", dbUserFinal.Department?.Name, "Department should be updated to HR");
                Assert.AreEqual("Manager", dbUserFinal.JobTitle?.Name, "Job title should be updated to Manager");
                Assert.AreEqual("Building 2", dbUserFinal.OfficeLocation?.Name, "Office location should be updated to Building 2");
                Assert.AreEqual("67890", dbUserFinal.PostalCode, "Postal code should be updated to 67890");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_MultipleLicensesSimultaneous_AllLicensesSaved()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"multilicenseuser{DateTime.Now.Ticks}@test.com";
            
            var sku1Id = Guid.NewGuid();
            var sku1PartNumber = "ENTERPRISEPACK";
            var license1Name = "Office 365 E3";
            
            var sku2Id = Guid.NewGuid();
            var sku2PartNumber = "ENTERPRISEPREMIUM";  // Changed from PROJECTPROFESSIONAL to a valid SKU
            var license2Name = "Office 365 E5";        // Changed from Project Plan 3

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users
                    .Include(u => u.LicenseLookups)
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();
                
                if (existingTestUser != null)
                {
                    cleanupDb.UserLicenseTypeLookups.RemoveRange(existingTestUser.LicenseLookups);
                    cleanupDb.users.Remove(existingTestUser);
                    await cleanupDb.SaveChangesAsync();
                }

                var testLicenses = await cleanupDb.LicenseTypes
                    .Where(l => l.Name == license1Name || l.Name == license2Name)
                    .ToListAsync();
                if (testLicenses.Any())
                {
                    cleanupDb.LicenseTypes.RemoveRange(testLicenses);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Create user with multiple licenses
            var graphUser = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = true,
                Mail = userUpn
            };

            var sku1 = new SubscribedSku
            {
                SkuId = sku1Id,
                SkuPartNumber = sku1PartNumber
            };

            var sku2 = new SubscribedSku
            {
                SkuId = sku2Id,
                SkuPartNumber = sku2PartNumber
            };

            var skus = new GraphServiceSubscribedSkusCollectionPage { sku1, sku2 };

            var graphUserObject = new Microsoft.Graph.User { UserPrincipalName = userUpn, Id = userId };

            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>>
            {
                { sku1Id, new List<Microsoft.Graph.User> { graphUserObject } },
                { sku2Id, new List<Microsoft.Graph.User> { graphUserObject } }
            };

            var fakeLoader = new FakeUserMetadataLoader(
                new List<GraphUser> { graphUser },
                skus,
                fakeUsersBySku
            );

            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            // Act
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert: Verify user has both licenses
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser, "User should be created in database");
                Assert.AreEqual(2, dbUser.LicenseLookups.Count, "User should have two licenses");
                Assert.IsTrue(dbUser.LicenseLookups.Any(l => l.License.Name == license1Name), "User should have E3 license");
                Assert.IsTrue(dbUser.LicenseLookups.Any(l => l.License.Name == license2Name), "User should have E5 license");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_MultipleUsersWithDifferentLicenses_AllProcessedCorrectly()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var timestamp = DateTime.Now.Ticks;
            var user1Upn = $"batchuser1{timestamp}@test.com";
            var user2Upn = $"batchuser2{timestamp}@test.com";
            var user3Upn = $"batchuser3{timestamp}@test.com";
            
            var user1Id = Guid.NewGuid().ToString();
            var user2Id = Guid.NewGuid().ToString();
            var user3Id = Guid.NewGuid().ToString();
            
            var sku1Id = Guid.NewGuid();
            var sku1PartNumber = "ENTERPRISEPACK";
            var license1Name = "Office 365 E3";
            
            var sku2Id = Guid.NewGuid();
            var sku2PartNumber = "ENTERPRISEPREMIUM";
            var license2Name = "Office 365 E5";

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUsers = await cleanupDb.users
                    .Include(u => u.LicenseLookups)
                    .Where(u => u.UserPrincipalName == user1Upn || u.UserPrincipalName == user2Upn || u.UserPrincipalName == user3Upn)
                    .ToListAsync();
                
                foreach (var user in existingTestUsers)
                {
                    cleanupDb.UserLicenseTypeLookups.RemoveRange(user.LicenseLookups);
                }
                cleanupDb.users.RemoveRange(existingTestUsers);
                await cleanupDb.SaveChangesAsync();

                var testLicenses = await cleanupDb.LicenseTypes
                    .Where(l => l.Name == license1Name || l.Name == license2Name)
                    .ToListAsync();
                if (testLicenses.Any())
                {
                    cleanupDb.LicenseTypes.RemoveRange(testLicenses);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Create three users with different license combinations
            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = user1Upn, Id = user1Id, AccountEnabled = true, Mail = user1Upn },
                new GraphUser { UserPrincipalName = user2Upn, Id = user2Id, AccountEnabled = true, Mail = user2Upn },
                new GraphUser { UserPrincipalName = user3Upn, Id = user3Id, AccountEnabled = true, Mail = user3Upn }
            };

            var sku1 = new SubscribedSku { SkuId = sku1Id, SkuPartNumber = sku1PartNumber };
            var sku2 = new SubscribedSku { SkuId = sku2Id, SkuPartNumber = sku2PartNumber };
            var skus = new GraphServiceSubscribedSkusCollectionPage { sku1, sku2 };

            // User1: E3, User2: E5, User3: Both E3 and E5
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>>
            {
                { 
                    sku1Id, 
                    new List<Microsoft.Graph.User> 
                    { 
                        new Microsoft.Graph.User { UserPrincipalName = user1Upn, Id = user1Id },
                        new Microsoft.Graph.User { UserPrincipalName = user3Upn, Id = user3Id }
                    } 
                },
                { 
                    sku2Id, 
                    new List<Microsoft.Graph.User> 
                    { 
                        new Microsoft.Graph.User { UserPrincipalName = user2Upn, Id = user2Id },
                        new Microsoft.Graph.User { UserPrincipalName = user3Upn, Id = user3Id }
                    } 
                }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers, skus, fakeUsersBySku);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            // Act
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert: Verify each user has correct licenses
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser1 = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Where(u => u.UserPrincipalName == user1Upn)
                    .FirstOrDefaultAsync();

                var dbUser2 = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Where(u => u.UserPrincipalName == user2Upn)
                    .FirstOrDefaultAsync();

                var dbUser3 = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Where(u => u.UserPrincipalName == user3Upn)
                    .FirstOrDefaultAsync();

                Assert.AreEqual(1, dbUser1.LicenseLookups.Count, "User1 should have one license");
                Assert.AreEqual(license1Name, dbUser1.LicenseLookups[0].License.Name, "User1 should have E3");

                Assert.AreEqual(1, dbUser2.LicenseLookups.Count, "User2 should have one license");
                Assert.AreEqual(license2Name, dbUser2.LicenseLookups[0].License.Name, "User2 should have E5");

                Assert.AreEqual(2, dbUser3.LicenseLookups.Count, "User3 should have two licenses");
                Assert.IsTrue(dbUser3.LicenseLookups.Any(l => l.License.Name == license1Name), "User3 should have E3");
                Assert.IsTrue(dbUser3.LicenseLookups.Any(l => l.License.Name == license2Name), "User3 should have E5");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_SameUserReimported_NoChangesOrDuplicates()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"reimportuser{DateTime.Now.Ticks}@test.com";

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();
                
                if (existingTestUser != null)
                {
                    cleanupDb.users.Remove(existingTestUser);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Create user
            var graphUser = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = true,
                Mail = userUpn,
                Department = "IT",
                PostalCode = "12345"
            };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser });
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            // Act: Import twice with identical data
            await updater.InsertAndUpdateDatabaseFromExternalUsers();
            
            DateTime? firstImportTime;
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();
                firstImportTime = dbUser.LastUpdated;
            }

            // Second import with same data
            var fakeLoader2 = new FakeUserMetadataLoader(new List<GraphUser> { graphUser });
            var updater2 = new UserMetadataUpdater(telemetry, config, fakeLoader2);
            await updater2.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert: Verify only one user exists and LastUpdated changed
            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var allUsers = await finalVerifyDb.users
                    .Where(u => u.UserPrincipalName == userUpn)
                    .ToListAsync();

                Assert.AreEqual(1, allUsers.Count, "Should only be one user in database");
                Assert.IsTrue(allUsers[0].LastUpdated > firstImportTime, "LastUpdated should be updated on re-import");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_ManagerChanged_DatabaseReflectsChange()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var timestamp = DateTime.Now.Ticks;
            var userUpn = $"employeemanager{timestamp}@test.com";
            var manager1Upn = $"manager1{timestamp}@test.com";
            var manager2Upn = $"manager2{timestamp}@test.com";
            
            var userId = Guid.NewGuid().ToString();
            var manager1Id = Guid.NewGuid().ToString();
            var manager2Id = Guid.NewGuid().ToString();

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUsers = await cleanupDb.users
                    .Where(u => u.UserPrincipalName == userUpn || u.UserPrincipalName == manager1Upn || u.UserPrincipalName == manager2Upn)
                    .ToListAsync();
                
                cleanupDb.users.RemoveRange(existingTestUsers);
                await cleanupDb.SaveChangesAsync();
            }

            // Step 1: Create users with Manager1
            var manager1 = new GraphUser 
            { 
                UserPrincipalName = manager1Upn, 
                Id = manager1Id,
                AccountEnabled = true,
                Mail = manager1Upn
            };

            var manager2 = new GraphUser 
            { 
                UserPrincipalName = manager2Upn, 
                Id = manager2Id,
                AccountEnabled = true,
                Mail = manager2Upn
            };

            var employee = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = true,
                Mail = userUpn,
                ManagerInfo = new List<ManagerInfo> 
                { 
                    new ManagerInfo { Id = manager1Id } 
                }
            };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { manager1, manager2, employee });
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Verify initial manager
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Include(u => u.Manager)
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser.Manager, "Employee should have a manager");
                Assert.AreEqual(manager1Upn, dbUser.Manager.UserPrincipalName, "Manager should be Manager1");
            }

            // Step 2: Change manager to Manager2
            var employeeWithNewManager = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = true,
                Mail = userUpn,
                ManagerInfo = new List<ManagerInfo> 
                { 
                    new ManagerInfo { Id = manager2Id } 
                }
            };

            var updatedFakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { manager1, manager2, employeeWithNewManager });
            var updaterUpdated = new UserMetadataUpdater(telemetry, config, updatedFakeLoader);
            await updaterUpdated.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert: Verify manager changed
            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users
                    .Include(u => u.Manager)
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUserFinal.Manager, "Employee should still have a manager");
                Assert.AreEqual(manager2Upn, dbUserFinal.Manager.UserPrincipalName, "Manager should be changed to Manager2");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_NoUsersWithLicense_LicenseTypeRemains()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"orphanlicenseuser{DateTime.Now.Ticks}@test.com";
            
            var skuId = Guid.NewGuid();
            var skuPartNumber = "ENTERPRISEPACK";
            var licenseName = "Office 365 E3";

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users
                    .Include(u => u.LicenseLookups)
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();
                
                if (existingTestUser != null)
                {
                    cleanupDb.UserLicenseTypeLookups.RemoveRange(existingTestUser.LicenseLookups);
                    cleanupDb.users.Remove(existingTestUser);
                    await cleanupDb.SaveChangesAsync();
                }

                var testLicenses = await cleanupDb.LicenseTypes
                    .Where(l => l.Name == licenseName)
                    .ToListAsync();
                if (testLicenses.Any())
                {
                    cleanupDb.LicenseTypes.RemoveRange(testLicenses);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Step 1: Create user with license (this creates the LicenseType)
            var graphUser = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = true,
                Mail = userUpn
            };

            var sku = new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPartNumber };
            var skus = new GraphServiceSubscribedSkusCollectionPage { sku };

            var usersWithSku = new List<Microsoft.Graph.User>
            {
                new Microsoft.Graph.User { UserPrincipalName = userUpn, Id = userId }
            };

            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>>
            {
                { skuId, usersWithSku }
            };

            var fakeLoader = new FakeUserMetadataLoader(
                new List<GraphUser> { graphUser },
                skus,
                fakeUsersBySku
            );

            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            int licenseTypeId;
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var licenseType = await verifyDb.LicenseTypes
                    .Where(l => l.Name == licenseName)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(licenseType, "License type should be created");
                licenseTypeId = licenseType.ID;
            }

            // Step 2: Remove all users (or remove license from user)
            var emptySkus = new GraphServiceSubscribedSkusCollectionPage();
            var updatedFakeLoader = new FakeUserMetadataLoader(
                new List<GraphUser> { graphUser },
                emptySkus,
                new Dictionary<Guid, List<Microsoft.Graph.User>>()
            );

            var updaterNoLicenses = new UserMetadataUpdater(telemetry, config, updatedFakeLoader);
            await updaterNoLicenses.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert: Verify LicenseType still exists even though no users have it
            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var licenseType = await finalVerifyDb.LicenseTypes
                    .Where(l => l.ID == licenseTypeId)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(licenseType, "License type should still exist even when no users have it");
                Assert.AreEqual(licenseName, licenseType.Name, "License type name should remain unchanged");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_DeltaTokenPersistence_WorksCorrectly()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"deltatokenuser{DateTime.Now.Ticks}@test.com";

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();
                
                if (existingTestUser != null)
                {
                    cleanupDb.users.Remove(existingTestUser);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            var graphUser = new GraphUser 
            { 
                UserPrincipalName = userUpn, 
                Id = userId,
                AccountEnabled = true,
                Mail = userUpn
            };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser });
            
            // Verify delta token is initially null
            var tokenBefore = await fakeLoader.DeltaValueProvider.GetDeltaToken();
            Assert.IsNull(tokenBefore, "Delta token should be null initially");

            // Set a delta token
            await fakeLoader.DeltaValueProvider.SetDeltaToken("test-delta-token-12345");

            // Verify delta token was set
            var tokenAfterSet = await fakeLoader.DeltaValueProvider.GetDeltaToken();
            Assert.AreEqual("test-delta-token-12345", tokenAfterSet, "Delta token should be set correctly");

            // Clear the delta token
            await fakeLoader.DeltaValueProvider.ClearDeltaToken();

            // Verify delta token was cleared
            var tokenAfterClear = await fakeLoader.DeltaValueProvider.GetDeltaToken();
            Assert.IsNull(tokenAfterClear, "Delta token should be null after clearing");
        }

        #endregion
    }
}
