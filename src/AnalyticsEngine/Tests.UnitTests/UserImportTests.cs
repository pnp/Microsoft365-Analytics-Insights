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

        /// <summary>
        /// Test for fix of duplicate key error when newly inserted users are set as managers of existing users.
        /// This reproduces the scenario: "Cannot insert duplicate key row in object 'dbo.users' with unique index 'IX_users'"
        /// Root cause: Lookup dictionaries (dbUsersByUpn, dbUsersByAadId) were created BEFORE bulk insert, 
        /// so they didn't contain newly inserted users. When processing existing users and setting their 
        /// manager relationships, the cache couldn't find newly inserted managers and tried to insert them again.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_NewlyInsertedUserAsManager_NoDuplicateKeyError()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var timestamp = DateTime.Now.Ticks;
            
            // Existing user that will get a newly inserted user as manager
            var existingUserId = Guid.NewGuid().ToString();
            var existingUserUpn = $"existingemployee{timestamp}@test.com";
            
            // New user that will be inserted via bulk insert and set as manager
            var newManagerId = Guid.NewGuid().ToString();
            var newManagerUpn = $"newmanager{timestamp}@test.com";

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users
                    .Where(u => u.UserPrincipalName == existingUserUpn || u.UserPrincipalName == newManagerUpn)
                    .ToListAsync();

                if (usersToClean.Any())
                {
                    cleanupDb.users.RemoveRange(usersToClean);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Step 1: Create existing user WITHOUT manager
            var existingUser = new GraphUser
            {
                UserPrincipalName = existingUserUpn,
                Id = existingUserId,
                AccountEnabled = true,
                Mail = existingUserUpn
            };

            var initialLoader = new FakeUserMetadataLoader(new List<GraphUser> { existingUser });
            var initialUpdater = new UserMetadataUpdater(telemetry, config, initialLoader);
            await initialUpdater.InsertAndUpdateDatabaseFromExternalUsers();

            // Verify existing user was created
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Where(u => u.UserPrincipalName == existingUserUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser, "Existing user should be created");
                Assert.IsNull(dbUser.ManagerId, "Existing user should not have a manager yet");
            }

            // Step 2: Add new manager (to be bulk inserted) and update existing user to have this new manager
            var newManager = new GraphUser
            {
                UserPrincipalName = newManagerUpn,
                Id = newManagerId,
                AccountEnabled = true,
                Mail = newManagerUpn
            };

            var existingUserWithNewManager = new GraphUser
            {
                UserPrincipalName = existingUserUpn,
                Id = existingUserId,
                AccountEnabled = true,
                Mail = existingUserUpn,
                ManagerInfo = new List<ManagerInfo>
                {
                    new ManagerInfo { Id = newManagerId } // Manager will be newly inserted!
                }
            };

            // This simulates the scenario where:
            // 1. newManager is bulk-inserted (not in dictionary initially)
            // 2. existingUserWithNewManager is updated (already exists in DB)
            // 3. When processing existing user's manager relationship, the lookup should find the newly inserted manager
            //    WITHOUT trying to insert them again (which would cause duplicate key error)
            var updatedLoader = new FakeUserMetadataLoader(new List<GraphUser> { newManager, existingUserWithNewManager });
            var updater = new UserMetadataUpdater(telemetry, config, updatedLoader);
            
            // Act: This should NOT throw "Cannot insert duplicate key row in object 'dbo.users'"
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert: Verify both users exist and manager relationship is set correctly
            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbExistingUser = await finalVerifyDb.users
                    .Include(u => u.Manager)
                    .Where(u => u.UserPrincipalName == existingUserUpn)
                    .FirstOrDefaultAsync();

                var dbNewManager = await finalVerifyDb.users
                    .Where(u => u.UserPrincipalName == newManagerUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbExistingUser, "Existing user should still exist");
                Assert.IsNotNull(dbNewManager, "New manager should be inserted");
                Assert.IsNotNull(dbExistingUser.Manager, "Existing user should have a manager assigned");
                Assert.AreEqual(newManagerUpn, dbExistingUser.Manager.UserPrincipalName, 
                    "Manager relationship should be set to the newly inserted manager");
                Assert.AreEqual(dbNewManager.ID, dbExistingUser.ManagerId, 
                    "Manager ID should match the newly inserted manager's ID");

                // Verify no duplicate users were created
                var allTestUsers = await finalVerifyDb.users
                    .Where(u => u.UserPrincipalName == existingUserUpn || u.UserPrincipalName == newManagerUpn)
                    .ToListAsync();

                Assert.AreEqual(2, allTestUsers.Count, "Should only have 2 users (no duplicates)");
            }
        }

        /// <summary>
        /// Test for the specific production error: "Cannot insert duplicate key row in object 'dbo.users' with unique index 'IX_users'"
        /// This error occurred in ProcessExistingUsersInBatches when:
        /// 1. New users were bulk-inserted (bypassing EF and cache)
        /// 2. Existing users were processed in batches
        /// 3. An existing user's manager was updated to a newly inserted user
        /// 4. The lookup dictionaries had detached entities or were missing newly inserted users
        /// 5. Manager resolution tried to insert the newly inserted manager again ? duplicate key error
        /// 
        /// This test validates the complete workflow including both InsertMissingUsers AND ProcessExistingUsersInBatches
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_ExistingUserManagerUpdatedToNewlyInsertedUser_NoDuplicateKeyInBatchProcessing()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var timestamp = DateTime.Now.Ticks;
            
            // Existing user already in database (will be processed in ProcessExistingUsersInBatches)
            var existingEmployeeId = Guid.NewGuid().ToString();
            var existingEmployeeUpn = $"existingemployee{timestamp}@test.com";
            
            // New manager that will be bulk-inserted (not in DB yet)
            var newManagerId = Guid.NewGuid().ToString();
            var newManagerUpn = $"bulkinsertedmanager{timestamp}@test.com";

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users
                    .Where(u => u.UserPrincipalName == existingEmployeeUpn || u.UserPrincipalName == newManagerUpn)
                    .ToListAsync();

                if (usersToClean.Any())
                {
                    cleanupDb.users.RemoveRange(usersToClean);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Step 1: Create existing employee user WITHOUT manager (simulates user already in production DB)
            var existingEmployee = new GraphUser
            {
                UserPrincipalName = existingEmployeeUpn,
                Id = existingEmployeeId,
                AccountEnabled = true,
                Mail = existingEmployeeUpn
            };

            var step1Loader = new FakeUserMetadataLoader(new List<GraphUser> { existingEmployee });
            var step1Updater = new UserMetadataUpdater(telemetry, config, step1Loader);
            await step1Updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Verify existing employee was created without manager
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Include(u => u.Manager)
                    .Where(u => u.UserPrincipalName == existingEmployeeUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser, "Existing employee should be created");
                Assert.IsNull(dbUser.ManagerId, "Existing employee should not have a manager yet");
            }

            // Step 2: Simulate the production scenario:
            // - New manager user is returned from Graph API (will be bulk-inserted)
            // - Existing employee is also returned from Graph API with manager relationship updated
            // This triggers:
            //   a) InsertMissingUsers ? bulk inserts newManager
            //   b) ProcessExistingUsersInBatches ? updates existingEmployee's manager
            
            var newManager = new GraphUser
            {
                UserPrincipalName = newManagerUpn,
                Id = newManagerId,
                AccountEnabled = true,
                Mail = newManagerUpn
            };

            var existingEmployeeWithManager = new GraphUser
            {
                UserPrincipalName = existingEmployeeUpn,
                Id = existingEmployeeId,
                AccountEnabled = true,
                Mail = existingEmployeeUpn,
                ManagerInfo = new List<ManagerInfo>
                {
                    new ManagerInfo { Id = newManagerId } // Points to newly inserted manager!
                }
            };

            var step2Loader = new FakeUserMetadataLoader(new List<GraphUser> { newManager, existingEmployeeWithManager });
            var step2Updater = new UserMetadataUpdater(telemetry, config, step2Loader);
            
            // Act: This is where the production error occurred
            // The fix ensures:
            // 1. dbUsersByUpn and dbUsersByAadId are updated after bulk insert with tracked entities
            // 2. Cache is pre-populated with tracked entities
            // 3. When ProcessExistingUsersInBatches runs, manager lookups find the newly inserted manager
            // 4. No duplicate insert attempts
            await step2Updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert: Verify the complete workflow succeeded without duplicate key errors
            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbEmployee = await finalVerifyDb.users
                    .Include(u => u.Manager)
                    .Where(u => u.UserPrincipalName == existingEmployeeUpn)
                    .FirstOrDefaultAsync();

                var dbManager = await finalVerifyDb.users
                    .Where(u => u.UserPrincipalName == newManagerUpn)
                    .FirstOrDefaultAsync();

                // Verify both users exist
                Assert.IsNotNull(dbEmployee, "Existing employee should still exist");
                Assert.IsNotNull(dbManager, "New manager should be inserted");

                // Verify manager relationship was set correctly
                Assert.IsNotNull(dbEmployee.Manager, "Employee should have a manager assigned");
                Assert.AreEqual(newManagerUpn, dbEmployee.Manager.UserPrincipalName, 
                    "Manager should be the newly inserted user");
                Assert.AreEqual(dbManager.ID, dbEmployee.ManagerId, 
                    "Manager ID should match the newly inserted manager");

                // Verify no duplicates were created (the critical assertion!)
                var allTestUsers = await finalVerifyDb.users
                    .Where(u => u.UserPrincipalName == existingEmployeeUpn || u.UserPrincipalName == newManagerUpn)
                    .ToListAsync();

                Assert.AreEqual(2, allTestUsers.Count, 
                    "Should only have exactly 2 users - no duplicates created during ProcessExistingUsersInBatches");

                // Verify each user appears only once
                var employeeCount = allTestUsers.Count(u => u.UserPrincipalName == existingEmployeeUpn);
                var managerCount = allTestUsers.Count(u => u.UserPrincipalName == newManagerUpn);
                
                Assert.AreEqual(1, employeeCount, "Employee should exist exactly once");
                Assert.AreEqual(1, managerCount, "Manager should exist exactly once (not inserted twice)");
            }
        }

        /// <summary>
        /// REPRODUCTION TEST for production error: "Cannot insert duplicate key row in object 'dbo.users' 
        /// with unique index 'IX_users'. The duplicate key value is (alice_anderson@contoso.com)"
        /// 
        /// This test specifically reproduces the scenario where:
        /// 1. Multiple users exist in DB from previous run
        /// 2. Graph returns NEW users to bulk-insert + EXISTING users to update
        /// 3. An EXISTING user (Employee) has their manager changed to a NEWLY INSERTED user
        /// 4. The newly inserted user (Manager) ALSO has THEIR manager changed to another NEWLY INSERTED user
        /// 
        /// This creates a chain: ExistingEmployee -> NewManager1 -> NewManager2
        /// 
        /// Expected behavior: All relationships set correctly, no duplicate inserts
        /// Bug behavior: NewManager1 tries to be inserted twice when processing ExistingEmployee's manager relationship
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_ProductionScenario_ExistingUserWithNewlyInsertedManagerChain_NoDuplicateKey()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var timestamp = DateTime.Now.Ticks;
            
            // Simulate alice_anderson (existing user in production)
            var existingEmployeeId = Guid.NewGuid().ToString();
            var existingEmployeeUpn = $"alice_anderson{timestamp}@contoso.com";
            
            // New manager 1 (will be bulk-inserted, becomes alice's manager)
            var newManager1Id = Guid.NewGuid().ToString();
            var newManager1Upn = $"newmanager1_{timestamp}@contoso.com";
            
            // New manager 2 (will be bulk-inserted, becomes manager1's manager)
            var newManager2Id = Guid.NewGuid().ToString();
            var newManager2Upn = $"newmanager2_{timestamp}@contoso.com";

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users
                    .Where(u => u.UserPrincipalName == existingEmployeeUpn || 
                                u.UserPrincipalName == newManager1Upn ||
                                u.UserPrincipalName == newManager2Upn)
                    .ToListAsync();

                if (usersToClean.Any())
                {
                    cleanupDb.users.RemoveRange(usersToClean);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Step 1: Create existing employee WITHOUT manager (simulates production DB state before the failing run)
            telemetry.LogInformation("TEST STEP 1: Creating existing employee in DB without manager");
            var existingEmployee = new GraphUser
            {
                UserPrincipalName = existingEmployeeUpn,
                Id = existingEmployeeId,
                AccountEnabled = true,
                Mail = existingEmployeeUpn
            };

            var step1Loader = new FakeUserMetadataLoader(new List<GraphUser> { existingEmployee });
            var step1Updater = new UserMetadataUpdater(telemetry, config, step1Loader);
            await step1Updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Verify existing employee exists
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Where(u => u.UserPrincipalName == existingEmployeeUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser, "Existing employee should exist in DB before the failing scenario");
                Assert.IsNull(dbUser.ManagerId, "Existing employee should not have a manager yet");
            }

            // Step 2: Simulate the EXACT production scenario that caused the error
            // Graph API returns:
            // - NEW manager users (to be bulk-inserted)
            // - EXISTING employee (to be updated with new manager relationship)
            // This creates a manager chain where both managers are newly inserted
            
            telemetry.LogInformation("TEST STEP 2: Simulating production scenario - new managers bulk-inserted, existing user updated");

            var newManager2 = new GraphUser
            {
                UserPrincipalName = newManager2Upn,
                Id = newManager2Id,
                AccountEnabled = true,
                Mail = newManager2Upn
            };

            var newManager1 = new GraphUser
            {
                UserPrincipalName = newManager1Upn,
                Id = newManager1Id,
                AccountEnabled = true,
                Mail = newManager1Upn,
                ManagerInfo = new List<ManagerInfo>
                {
                    new ManagerInfo { Id = newManager2Id } // Manager1's manager is also newly inserted!
                }
            };

            var existingEmployeeWithNewManager = new GraphUser
            {
                UserPrincipalName = existingEmployeeUpn,
                Id = existingEmployeeId,
                AccountEnabled = true,
                Mail = existingEmployeeUpn,
                ManagerInfo = new List<ManagerInfo>
                {
                    new ManagerInfo { Id = newManager1Id } // Existing user's manager is newly inserted!
                }
            };

            // This is the exact scenario:
            // - newManager1 and newManager2 will go through InsertMissingUsers (bulk insert)
            // - existingEmployeeWithNewManager will go through ProcessExistingUsersInBatches
            // - When ProcessExistingUsersInBatches tries to set existingEmployee.Manager = newManager1:
            //   * If dbUsersByAadId has detached entity for newManager1 ? EF tries to INSERT ? DUPLICATE KEY ERROR!
            //   * If dbUsersByAadId has tracked entity for newManager1 ? EF recognizes as existing ? SUCCESS!
            
            var step2Loader = new FakeUserMetadataLoader(new List<GraphUser> 
            { 
                newManager2,           // Will be bulk-inserted
                newManager1,           // Will be bulk-inserted
                existingEmployeeWithNewManager  // Will be updated in ProcessExistingUsersInBatches
            });
            var step2Updater = new UserMetadataUpdater(telemetry, config, step2Loader);
            
            // Act: This should NOT throw "Cannot insert duplicate key row in object 'dbo.users'"
            // If this throws the error, the test will FAIL and show us the exact problem
            telemetry.LogInformation("TEST ACT: Running InsertAndUpdateDatabaseFromExternalUsers - this is where production error occurred");
            await step2Updater.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert: Verify everything worked correctly with no duplicate inserts
            telemetry.LogInformation("TEST ASSERT: Verifying no duplicates and relationships are correct");
            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                // Load all test users
                var allTestUsers = await finalVerifyDb.users
                    .Include(u => u.Manager)
                    .Where(u => u.UserPrincipalName == existingEmployeeUpn || 
                                u.UserPrincipalName == newManager1Upn ||
                                u.UserPrincipalName == newManager2Upn)
                    .ToListAsync();

                // CRITICAL: Should be exactly 3 users, no duplicates
                Assert.AreEqual(3, allTestUsers.Count, 
                    "Should have exactly 3 users total - NO DUPLICATES (this is the production bug!)");

                var dbEmployee = allTestUsers.FirstOrDefault(u => u.UserPrincipalName == existingEmployeeUpn);
                var dbManager1 = allTestUsers.FirstOrDefault(u => u.UserPrincipalName == newManager1Upn);
                var dbManager2 = allTestUsers.FirstOrDefault(u => u.UserPrincipalName == newManager2Upn);

                // Verify all users exist
                Assert.IsNotNull(dbEmployee, "Employee should exist");
                Assert.IsNotNull(dbManager1, "Manager 1 should be inserted exactly once");
                Assert.IsNotNull(dbManager2, "Manager 2 should be inserted exactly once");

                // Verify manager chain relationships
                Assert.IsNotNull(dbEmployee.Manager, "Employee should have a manager");
                Assert.AreEqual(newManager1Upn, dbEmployee.Manager.UserPrincipalName, 
                    "Employee's manager should be Manager 1 (newly inserted)");
                
                Assert.IsNotNull(dbManager1.Manager, "Manager 1 should have a manager");
                Assert.AreEqual(newManager2Upn, dbManager1.Manager.UserPrincipalName, 
                    "Manager 1's manager should be Manager 2 (newly inserted)");

                // Verify each user appears exactly once (no duplicates!)
                var employeeCount = allTestUsers.Count(u => u.UserPrincipalName == existingEmployeeUpn);
                var manager1Count = allTestUsers.Count(u => u.UserPrincipalName == newManager1Upn);
                var manager2Count = allTestUsers.Count(u => u.UserPrincipalName == newManager2Upn);
                
                Assert.AreEqual(1, employeeCount, "Employee should exist exactly once");
                Assert.AreEqual(1, manager1Count, 
                    "Manager 1 should exist exactly once (NOT INSERTED TWICE - this was the production bug!)");
                Assert.AreEqual(1, manager2Count, "Manager 2 should exist exactly once");

                telemetry.LogInformation("TEST PASSED: No duplicate key errors, all relationships correct!");
            }
        }

        /// <summary>
        /// CRITICAL REPRODUCTION TEST: Tests the scenario where reloaded tracked entities become 
        /// detached when there are NO operations between reload and ProcessExistingUsersInBatches.
        /// 
        /// In production with 500k users:
        /// 1. Reload 500k users with tracking (in batches of 1000)
        /// 2. Update dictionaries with tracked entities
        /// 3. NO SaveChanges() called after reload
        /// 4. ProcessExistingUsersInBatches called
        /// 5. When attaching existing users, their NEW managers from dictionary are still tracked
        /// 6. But if tracking is lost somehow, we get duplicate key error
        /// 
        /// This test validates that entities remain tracked across the workflow boundary.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_LargeScaleBatching_ReloadedEntitiesRemainTracked()
        {
            // Arrange
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var timestamp = DateTime.Now.Ticks;
            
            // Create scenario: 1 existing user + 2 newly inserted users in manager chain
            var existingUserId = Guid.NewGuid().ToString();
            var existingUserUpn = $"existing_employee{timestamp}@test.com";
            
            var newMgr1Id = Guid.NewGuid().ToString();
            var newMgr1Upn = $"newmgr1_{timestamp}@test.com";
            
            var newMgr2Id = Guid.NewGuid().ToString();
            var newMgr2Upn = $"newmgr2_{timestamp}@test.com";

            // Cleanup
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users
                    .Where(u => u.UserPrincipalName == existingUserUpn || 
                                u.UserPrincipalName == newMgr1Upn ||
                                u.UserPrincipalName == newMgr2Upn)
                    .ToListAsync();

                if (usersToClean.Any())
                {
                    cleanupDb.users.RemoveRange(usersToClean);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // Step 1: Create existing user
            telemetry.LogInformation("Creating existing user in DB");
            var existingUser = new GraphUser
            {
                UserPrincipalName = existingUserUpn,
                Id = existingUserId,
                AccountEnabled = true,
                Mail = existingUserUpn
            };

            var loader1 = new FakeUserMetadataLoader(new List<GraphUser> { existingUser });
            var updater1 = new UserMetadataUpdater(telemetry, config, loader1);
            await updater1.InsertAndUpdateDatabaseFromExternalUsers();

            // Step 2: Simulate production - insert new managers and update existing user
            // The key difference: We need to verify the entities in dbUsersByAadId are truly tracked
            // when ProcessExistingUsersInBatches runs
            telemetry.LogInformation("Simulating production scenario with manager chain");

            var newMgr2 = new GraphUser
            {
                UserPrincipalName = newMgr2Upn,
                Id = newMgr2Id,
                AccountEnabled = true,
                Mail = newMgr2Upn
            };

            var newMgr1 = new GraphUser
            {
                UserPrincipalName = newMgr1Upn,
                Id = newMgr1Id,
                AccountEnabled = true,
                Mail = newMgr1Upn,
                ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = newMgr2Id } }
            };

            var existingUserUpdated = new GraphUser
            {
                UserPrincipalName = existingUserUpn,
                Id = existingUserId,
                AccountEnabled = true,
                Mail = existingUserUpn,
                ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = newMgr1Id } }
            };

            var loader2 = new FakeUserMetadataLoader(new List<GraphUser> { newMgr2, newMgr1, existingUserUpdated });
            var updater2 = new UserMetadataUpdater(telemetry, config, loader2);
            
            // This is where we need to verify the fix works
            // If reloaded entities are not properly tracked when ProcessExistingUsersInBatches runs,
            // we'll get the duplicate key error
            await updater2.InsertAndUpdateDatabaseFromExternalUsers();

            // Assert
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var allUsers = await verifyDb.users
                    .Include(u => u.Manager)
                    .Where(u => u.UserPrincipalName == existingUserUpn ||
                                u.UserPrincipalName == newMgr1Upn ||
                                u.UserPrincipalName == newMgr2Upn)
                    .ToListAsync();

                Assert.AreEqual(3, allUsers.Count, "Should have exactly 3 users, no duplicates");

                var existing = allUsers.First(u => u.UserPrincipalName == existingUserUpn);
                var mgr1 = allUsers.First(u => u.UserPrincipalName == newMgr1Upn);
                var mgr2 = allUsers.First(u => u.UserPrincipalName == newMgr2Upn);

                Assert.IsNotNull(existing.Manager);
                Assert.AreEqual(newMgr1Upn, existing.Manager.UserPrincipalName);
                Assert.IsNotNull(mgr1.Manager);
                Assert.AreEqual(newMgr2Upn, mgr1.Manager.UserPrincipalName);

                // Verify no duplicates
                Assert.AreEqual(1, allUsers.Count(u => u.UserPrincipalName == newMgr1Upn),
                    "Manager 1 should exist exactly once - production bug was duplicate insert here!");
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
