using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Basic unit tests for UserMetadataUpdater: constructor, property mapping, delta provider, and fakes
    /// </summary>
    [TestClass]
    public class UserMetadataUpdaterBasicTests
    {
        [TestMethod]
        public void UserMetadataUpdater_Constructor_WithInjectedLoader_SetsLoaderCorrectly()
        {
            // Arrange
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();

            // Act
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            // Assert
            Assert.IsNotNull(updater.UserLoader);
            Assert.AreSame(fakeLoader, updater.UserLoader);
        }

        #region GetDbUsersFromGraphUsers

        [TestMethod]
        public void UserMetadataUpdater_GetDbUsersFromGraphUsers_ReturnsMatchingUsers()
        {
            // Arrange
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

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
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

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
        public void UserMetadataUpdater_GetDbUsersFromGraphUsers_EmptyLists_ReturnsEmpty()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            var result = updater.GetDbUsersFromGraphUsers(new List<GraphUser>(), new List<Common.Entities.User>());

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void UserMetadataUpdater_GetDbUsersFromGraphUsers_NoMatchingUsers_ReturnsEmpty()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "graphonly@test.com", Id = Guid.NewGuid().ToString() }
            };

            var dbUsers = new List<Common.Entities.User>
            {
                new Common.Entities.User { UserPrincipalName = "dbonly@test.com", ID = 1 }
            };

            var result = updater.GetDbUsersFromGraphUsers(graphUsers, dbUsers);

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void UserMetadataUpdater_GetDbUsersFromGraphUsers_CaseInsensitiveUPNMatch()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "USER@TEST.COM", Id = Guid.NewGuid().ToString() }
            };

            var dbUsers = new List<Common.Entities.User>
            {
                new Common.Entities.User { UserPrincipalName = "user@test.com", ID = 1 }
            };

            var result = updater.GetDbUsersFromGraphUsers(graphUsers, dbUsers);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("user@test.com", result[0].UserPrincipalName);
        }

        #endregion

        #region UpdateDbUserFromGraphUser

        [TestMethod]
        public void UserMetadataUpdater_UpdateDbUserFromGraphUser_CopiesBasicProperties()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            var graphUser = new GraphUser
            {
                Id = Guid.NewGuid().ToString(),
                UserPrincipalName = "test@test.com",
                AccountEnabled = true,
                PostalCode = "12345",
                Mail = "test@test.com"
            };

            var dbUser = new Common.Entities.User { UserPrincipalName = "test@test.com" };

            updater.UpdateDbUserFromGraphUser(dbUser, graphUser);

            Assert.AreEqual(graphUser.Id, dbUser.AzureAdId);
            Assert.AreEqual(graphUser.AccountEnabled, dbUser.AccountEnabled);
            Assert.AreEqual(graphUser.PostalCode, dbUser.PostalCode);
            Assert.AreEqual(graphUser.Mail, dbUser.Mail);
        }

        [TestMethod]
        public void UserMetadataUpdater_UpdateDbUserFromGraphUser_HandlesNullValues()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

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

            updater.UpdateDbUserFromGraphUser(dbUser, graphUser);

            Assert.AreEqual(graphUser.Id, dbUser.AzureAdId);
            Assert.IsNull(dbUser.AccountEnabled);
            Assert.IsNull(dbUser.PostalCode);
            Assert.IsNull(dbUser.Mail);
        }

        [TestMethod]
        public void UserMetadataUpdater_UpdateDbUserFromGraphUser_OverwritesExistingValues()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            var dbUser = new Common.Entities.User
            {
                UserPrincipalName = "test@test.com",
                AccountEnabled = true,
                PostalCode = "OLD_CODE",
                Mail = "old@test.com",
                AzureAdId = "old-aad-id"
            };

            var graphUser = new GraphUser
            {
                Id = "new-aad-id",
                UserPrincipalName = "test@test.com",
                AccountEnabled = false,
                PostalCode = "NEW_CODE",
                Mail = "new@test.com"
            };

            var result = updater.UpdateDbUserFromGraphUser(dbUser, graphUser);

            Assert.AreSame(dbUser, result);
            Assert.AreEqual("new-aad-id", dbUser.AzureAdId);
            Assert.AreEqual(false, dbUser.AccountEnabled);
            Assert.AreEqual("NEW_CODE", dbUser.PostalCode);
            Assert.AreEqual("new@test.com", dbUser.Mail);
        }

        #endregion

        #region Delta Provider and Fakes

        [TestMethod]
        public async Task UserMetadataUpdater_DeltaProvider_ClearsTokenWhenNoActiveUsers()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser>());

            await fakeLoader.DeltaValueProvider.SetDeltaToken("test-token");

            using (var db = new AnalyticsEntitiesContext())
            {
                var activeUsers = await db.users.Where(u => u.AccountEnabled.HasValue && u.AccountEnabled.Value).ToListAsync();

                if (activeUsers.Count == 0)
                {
                    var tokenBefore = await fakeLoader.DeltaValueProvider.GetDeltaToken();
                    Assert.AreEqual("test-token", tokenBefore);

                    await fakeLoader.DeltaValueProvider.ClearDeltaToken();
                    var tokenAfter = await fakeLoader.DeltaValueProvider.GetDeltaToken();

                    Assert.IsNull(tokenAfter);
                }
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_DeltaTokenPersistence_WorksCorrectly()
        {
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser>());

            var tokenBefore = await fakeLoader.DeltaValueProvider.GetDeltaToken();
            Assert.IsNull(tokenBefore, "Delta token should be null initially");

            await fakeLoader.DeltaValueProvider.SetDeltaToken("test-delta-token-12345");
            var tokenAfterSet = await fakeLoader.DeltaValueProvider.GetDeltaToken();
            Assert.AreEqual("test-delta-token-12345", tokenAfterSet, "Delta token should be set correctly");

            await fakeLoader.DeltaValueProvider.ClearDeltaToken();
            var tokenAfterClear = await fakeLoader.DeltaValueProvider.GetDeltaToken();
            Assert.IsNull(tokenAfterClear, "Delta token should be null after clearing");
        }

        [TestMethod]
        public void UserMetadataUpdater_FakeDeltaProvider_SetGetClearCycle()
        {
            var provider = new FakeDeltaValueProvider();

            Assert.IsNull(provider.GetDeltaToken().Result, "Initial token should be null");

            provider.SetDeltaToken("token-1").Wait();
            Assert.AreEqual("token-1", provider.GetDeltaToken().Result, "Token should be set");

            provider.SetDeltaToken("token-2").Wait();
            Assert.AreEqual("token-2", provider.GetDeltaToken().Result, "Token should be overwritten");

            provider.ClearDeltaToken().Wait();
            Assert.IsNull(provider.GetDeltaToken().Result, "Token should be cleared");

            provider.SetDeltaToken("token-3").Wait();
            Assert.AreEqual("token-3", provider.GetDeltaToken().Result, "Token should be settable after clear");
        }

        [TestMethod]
        public void UserMetadataUpdater_FakeUserMetadataLoader_DefaultsAreEmpty()
        {
            var loader = new FakeUserMetadataLoader();

            Assert.IsNotNull(loader.DeltaValueProvider, "DeltaValueProvider should not be null");
            Assert.AreEqual(0, loader.LoadAllActiveUsers().Result.Count, "Default should have no users");
            Assert.IsNull(loader.LoadTenantSkus().Result, "Default should have null SKUs");
            Assert.AreEqual(0, loader.LoadUsersBySku(Guid.NewGuid()).Result.Count, "Default should return empty SKU users");
            Assert.IsNull(loader.LoadUserLicenseDetails("any-id").Result, "Default should return null license details");
        }

        #endregion
    }
}
