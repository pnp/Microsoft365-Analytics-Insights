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
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for license processing: SKU assignment, removal, change, per-user mode
    /// </summary>
    [TestClass]
    public class UserMetadataUpdaterLicenseTests
    {
        [TestMethod]
        public async Task UserMetadataUpdater_UserLicenseChange_DatabaseReflectsChange()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var userId = Guid.NewGuid().ToString();
            var userUpn = $"licensechangeuser{DateTime.Now.Ticks}@test.com";

            var initialSkuId = Guid.NewGuid();
            var initialSkuPartNumber = "ENTERPRISEPACK";
            var initialLicenseName = "Office 365 E3";

            var newSkuId = Guid.NewGuid();
            var newSkuPartNumber = "ENTERPRISEPREMIUM";
            var newLicenseName = "Office 365 E5";

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
                    .Where(l => l.Name == initialLicenseName || l.Name == newLicenseName)
                    .ToListAsync();
                if (testLicenses.Any())
                {
                    cleanupDb.LicenseTypes.RemoveRange(testLicenses);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };
            var initialSku = new SubscribedSku { SkuId = initialSkuId, SkuPartNumber = initialSkuPartNumber };
            var initialSkus = new GraphServiceSubscribedSkusCollectionPage { initialSku };
            var usersWithInitialSku = new List<Microsoft.Graph.User> { new Microsoft.Graph.User { UserPrincipalName = userUpn, Id = userId } };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>> { { initialSkuId, usersWithInitialSku } };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, initialSkus, fakeUsersBySku);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Include(u => u.LicenseLookups.Select(l => l.License)).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUser, "User should be created in database");
                Assert.AreEqual(1, dbUser.LicenseLookups.Count, "User should have exactly one license initially");
                Assert.AreEqual(initialLicenseName, dbUser.LicenseLookups[0].License.Name);
            }

            var newSku = new SubscribedSku { SkuId = newSkuId, SkuPartNumber = newSkuPartNumber };
            var updatedSkus = new GraphServiceSubscribedSkusCollectionPage { newSku };
            var usersWithNewSku = new List<Microsoft.Graph.User> { new Microsoft.Graph.User { UserPrincipalName = userUpn, Id = userId } };
            var updatedFakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>> { { newSkuId, usersWithNewSku } };

            var updatedFakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, updatedSkus, updatedFakeUsersBySku);
            var updaterWithNewLicense = new UserMetadataUpdater(telemetry, config, updatedFakeLoader);
            await updaterWithNewLicense.InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users.Include(u => u.LicenseLookups.Select(l => l.License)).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUserFinal);
                Assert.AreEqual(1, dbUserFinal.LicenseLookups.Count);
                Assert.AreEqual(newLicenseName, dbUserFinal.LicenseLookups[0].License.Name);
                Assert.IsFalse(dbUserFinal.LicenseLookups.Any(l => l.License.Name == initialLicenseName));
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_AllLicensesRemoved_DatabaseReflectsRemoval()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var userId = Guid.NewGuid().ToString();
            var userUpn = $"licenseremoveduser{DateTime.Now.Ticks}@test.com";
            var skuId = Guid.NewGuid();
            var skuPartNumber = "ENTERPRISEPACK";
            var licenseName = "Office 365 E3";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users.Include(u => u.LicenseLookups).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                if (existingTestUser != null)
                {
                    cleanupDb.UserLicenseTypeLookups.RemoveRange(existingTestUser.LicenseLookups);
                    cleanupDb.users.Remove(existingTestUser);
                    await cleanupDb.SaveChangesAsync();
                }
                var testLicenses = await cleanupDb.LicenseTypes.Where(l => l.Name == licenseName).ToListAsync();
                if (testLicenses.Any()) { cleanupDb.LicenseTypes.RemoveRange(testLicenses); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };
            var sku = new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPartNumber };
            var skus = new GraphServiceSubscribedSkusCollectionPage { sku };
            var usersWithSku = new List<Microsoft.Graph.User> { new Microsoft.Graph.User { UserPrincipalName = userUpn, Id = userId } };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>> { { skuId, usersWithSku } };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, skus, fakeUsersBySku);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Include(u => u.LicenseLookups.Select(l => l.License)).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.AreEqual(1, dbUser.LicenseLookups.Count);
            }

            var emptySkus = new GraphServiceSubscribedSkusCollectionPage();
            var updatedFakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, emptySkus, new Dictionary<Guid, List<Microsoft.Graph.User>>());
            var updaterNoLicenses = new UserMetadataUpdater(telemetry, config, updatedFakeLoader);
            await updaterNoLicenses.InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users.Include(u => u.LicenseLookups).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUserFinal);
                Assert.AreEqual(0, dbUserFinal.LicenseLookups.Count);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_MultipleLicensesSimultaneous_AllLicensesSaved()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"multilicenseuser{DateTime.Now.Ticks}@test.com";
            var sku1Id = Guid.NewGuid(); var sku1PartNumber = "ENTERPRISEPACK"; var license1Name = "Office 365 E3";
            var sku2Id = Guid.NewGuid(); var sku2PartNumber = "ENTERPRISEPREMIUM"; var license2Name = "Office 365 E5";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users.Include(u => u.LicenseLookups).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                if (existingTestUser != null) { cleanupDb.UserLicenseTypeLookups.RemoveRange(existingTestUser.LicenseLookups); cleanupDb.users.Remove(existingTestUser); await cleanupDb.SaveChangesAsync(); }
                var testLicenses = await cleanupDb.LicenseTypes.Where(l => l.Name == license1Name || l.Name == license2Name).ToListAsync();
                if (testLicenses.Any()) { cleanupDb.LicenseTypes.RemoveRange(testLicenses); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };
            var skus = new GraphServiceSubscribedSkusCollectionPage { new SubscribedSku { SkuId = sku1Id, SkuPartNumber = sku1PartNumber }, new SubscribedSku { SkuId = sku2Id, SkuPartNumber = sku2PartNumber } };
            var graphUserObject = new Microsoft.Graph.User { UserPrincipalName = userUpn, Id = userId };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>> { { sku1Id, new List<Microsoft.Graph.User> { graphUserObject } }, { sku2Id, new List<Microsoft.Graph.User> { graphUserObject } } };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, skus, fakeUsersBySku);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Include(u => u.LicenseLookups.Select(l => l.License)).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUser);
                Assert.AreEqual(2, dbUser.LicenseLookups.Count);
                Assert.IsTrue(dbUser.LicenseLookups.Any(l => l.License.Name == license1Name));
                Assert.IsTrue(dbUser.LicenseLookups.Any(l => l.License.Name == license2Name));
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_MultipleUsersWithDifferentLicenses_AllProcessedCorrectly()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var user1Upn = $"batchuser1{timestamp}@test.com"; var user2Upn = $"batchuser2{timestamp}@test.com"; var user3Upn = $"batchuser3{timestamp}@test.com";
            var user1Id = Guid.NewGuid().ToString(); var user2Id = Guid.NewGuid().ToString(); var user3Id = Guid.NewGuid().ToString();
            var sku1Id = Guid.NewGuid(); var sku1PartNumber = "ENTERPRISEPACK"; var license1Name = "Office 365 E3";
            var sku2Id = Guid.NewGuid(); var sku2PartNumber = "ENTERPRISEPREMIUM"; var license2Name = "Office 365 E5";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUsers = await cleanupDb.users.Include(u => u.LicenseLookups).Where(u => u.UserPrincipalName == user1Upn || u.UserPrincipalName == user2Upn || u.UserPrincipalName == user3Upn).ToListAsync();
                foreach (var user in existingTestUsers) { cleanupDb.UserLicenseTypeLookups.RemoveRange(user.LicenseLookups); }
                cleanupDb.users.RemoveRange(existingTestUsers); await cleanupDb.SaveChangesAsync();
                var testLicenses = await cleanupDb.LicenseTypes.Where(l => l.Name == license1Name || l.Name == license2Name).ToListAsync();
                if (testLicenses.Any()) { cleanupDb.LicenseTypes.RemoveRange(testLicenses); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = user1Upn, Id = user1Id, AccountEnabled = true, Mail = user1Upn },
                new GraphUser { UserPrincipalName = user2Upn, Id = user2Id, AccountEnabled = true, Mail = user2Upn },
                new GraphUser { UserPrincipalName = user3Upn, Id = user3Id, AccountEnabled = true, Mail = user3Upn }
            };

            var skus = new GraphServiceSubscribedSkusCollectionPage { new SubscribedSku { SkuId = sku1Id, SkuPartNumber = sku1PartNumber }, new SubscribedSku { SkuId = sku2Id, SkuPartNumber = sku2PartNumber } };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>>
            {
                { sku1Id, new List<Microsoft.Graph.User> { new Microsoft.Graph.User { UserPrincipalName = user1Upn, Id = user1Id }, new Microsoft.Graph.User { UserPrincipalName = user3Upn, Id = user3Id } } },
                { sku2Id, new List<Microsoft.Graph.User> { new Microsoft.Graph.User { UserPrincipalName = user2Upn, Id = user2Id }, new Microsoft.Graph.User { UserPrincipalName = user3Upn, Id = user3Id } } }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers, skus, fakeUsersBySku);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser1 = await verifyDb.users.Include(u => u.LicenseLookups.Select(l => l.License)).Where(u => u.UserPrincipalName == user1Upn).FirstOrDefaultAsync();
                var dbUser2 = await verifyDb.users.Include(u => u.LicenseLookups.Select(l => l.License)).Where(u => u.UserPrincipalName == user2Upn).FirstOrDefaultAsync();
                var dbUser3 = await verifyDb.users.Include(u => u.LicenseLookups.Select(l => l.License)).Where(u => u.UserPrincipalName == user3Upn).FirstOrDefaultAsync();

                Assert.AreEqual(1, dbUser1.LicenseLookups.Count); Assert.AreEqual(license1Name, dbUser1.LicenseLookups[0].License.Name);
                Assert.AreEqual(1, dbUser2.LicenseLookups.Count); Assert.AreEqual(license2Name, dbUser2.LicenseLookups[0].License.Name);
                Assert.AreEqual(2, dbUser3.LicenseLookups.Count);
                Assert.IsTrue(dbUser3.LicenseLookups.Any(l => l.License.Name == license1Name));
                Assert.IsTrue(dbUser3.LicenseLookups.Any(l => l.License.Name == license2Name));
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_NoUsersWithLicense_LicenseTypeRemains()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"orphanlicenseuser{DateTime.Now.Ticks}@test.com";
            var skuId = Guid.NewGuid(); var skuPartNumber = "ENTERPRISEPACK"; var licenseName = "Office 365 E3";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users.Include(u => u.LicenseLookups).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                if (existingTestUser != null) { cleanupDb.UserLicenseTypeLookups.RemoveRange(existingTestUser.LicenseLookups); cleanupDb.users.Remove(existingTestUser); await cleanupDb.SaveChangesAsync(); }
                var testLicenses = await cleanupDb.LicenseTypes.Where(l => l.Name == licenseName).ToListAsync();
                if (testLicenses.Any()) { cleanupDb.LicenseTypes.RemoveRange(testLicenses); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };
            var skus = new GraphServiceSubscribedSkusCollectionPage { new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPartNumber } };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>> { { skuId, new List<Microsoft.Graph.User> { new Microsoft.Graph.User { UserPrincipalName = userUpn, Id = userId } } } };
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, skus, fakeUsersBySku);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            int licenseTypeId;
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var licenseType = await verifyDb.LicenseTypes.Where(l => l.Name == licenseName).FirstOrDefaultAsync();
                Assert.IsNotNull(licenseType); licenseTypeId = licenseType.ID;
            }

            var emptySkus = new GraphServiceSubscribedSkusCollectionPage();
            var updatedFakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, emptySkus, new Dictionary<Guid, List<Microsoft.Graph.User>>());
            var updaterNoLicenses = new UserMetadataUpdater(telemetry, config, updatedFakeLoader);
            await updaterNoLicenses.InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var licenseType = await finalVerifyDb.LicenseTypes.Where(l => l.ID == licenseTypeId).FirstOrDefaultAsync();
                Assert.IsNotNull(licenseType, "License type should still exist even when no users have it");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_UserPerLicenseMode_ReadUserSkusTrue()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"peruser_lic{DateTime.Now.Ticks}@test.com";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users.Include(u => u.LicenseLookups).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                if (existingTestUser != null) { cleanupDb.UserLicenseTypeLookups.RemoveRange(existingTestUser.LicenseLookups); cleanupDb.users.Remove(existingTestUser); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, fakeSkus: null);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUser, "User should be created even without tenant SKU permissions");
            }
        }
    }
}
