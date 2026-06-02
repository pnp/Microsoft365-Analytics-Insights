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
            var initialSkus = new List<SubscribedSku> { initialSku };
            var usersWithInitialSku = new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { UserPrincipalName = userUpn, Id = userId } };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.Models.User>> { { initialSkuId, usersWithInitialSku } };

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
            var updatedSkus = new List<SubscribedSku> { newSku };
            var usersWithNewSku = new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { UserPrincipalName = userUpn, Id = userId } };
            var updatedFakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.Models.User>> { { newSkuId, usersWithNewSku } };

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
            var skus = new List<SubscribedSku> { sku };
            var usersWithSku = new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { UserPrincipalName = userUpn, Id = userId } };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.Models.User>> { { skuId, usersWithSku } };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, skus, fakeUsersBySku);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Include(u => u.LicenseLookups.Select(l => l.License)).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.AreEqual(1, dbUser.LicenseLookups.Count);
            }

            var emptySkus = new List<SubscribedSku>();
            var updatedFakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, emptySkus, new Dictionary<Guid, List<Microsoft.Graph.Models.User>>());
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
            var skus = new List<SubscribedSku> { new SubscribedSku { SkuId = sku1Id, SkuPartNumber = sku1PartNumber }, new SubscribedSku { SkuId = sku2Id, SkuPartNumber = sku2PartNumber } };
            var graphUserObject = new Microsoft.Graph.Models.User { UserPrincipalName = userUpn, Id = userId };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.Models.User>> { { sku1Id, new List<Microsoft.Graph.Models.User> { graphUserObject } }, { sku2Id, new List<Microsoft.Graph.Models.User> { graphUserObject } } };

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

            var skus = new List<SubscribedSku> { new SubscribedSku { SkuId = sku1Id, SkuPartNumber = sku1PartNumber }, new SubscribedSku { SkuId = sku2Id, SkuPartNumber = sku2PartNumber } };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.Models.User>>
            {
                { sku1Id, new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { UserPrincipalName = user1Upn, Id = user1Id }, new Microsoft.Graph.Models.User { UserPrincipalName = user3Upn, Id = user3Id } } },
                { sku2Id, new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { UserPrincipalName = user2Upn, Id = user2Id }, new Microsoft.Graph.Models.User { UserPrincipalName = user3Upn, Id = user3Id } } }
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
            var skus = new List<SubscribedSku> { new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPartNumber } };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.Models.User>> { { skuId, new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { UserPrincipalName = userUpn, Id = userId } } } };
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, skus, fakeUsersBySku);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            int licenseTypeId;
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var licenseType = await verifyDb.LicenseTypes.Where(l => l.Name == licenseName).FirstOrDefaultAsync();
                Assert.IsNotNull(licenseType); licenseTypeId = licenseType.ID;
            }

            var emptySkus = new List<SubscribedSku>();
            var updatedFakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, emptySkus, new Dictionary<Guid, List<Microsoft.Graph.Models.User>>());
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

        /// <summary>
        /// Reproduces the production duplicate-key error:
        ///   "Cannot insert duplicate key row in object 'dbo.user_license_type_lookups'
        ///    with unique index 'IX_license_type_id_user_id'."
        ///
        /// Two different SKU part numbers ("RIGHTSMANAGEMENT" and "RIGHTSMANAGEMENT_CE")
        /// both resolve to the same product display name ("Azure Information Protection
        /// Plan 1") via OfficeLicenseNameResolver. The license-type cache is keyed by
        /// display-name, so both SKUs return the same LicenseType. When a single user
        /// is assigned both SKUs the importer tries to insert two
        /// UserLicenseTypeLookup rows with the same (license_type_id, user_id), which
        /// the IX_license_type_id_user_id unique index rejects.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_TwoSkusSameDisplayName_DoesNotThrowDuplicateKey()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var userId = Guid.NewGuid().ToString();
            var userUpn = $"dupkeyuser{DateTime.Now.Ticks}@test.com";

            // These two SKU part numbers both resolve to the SAME display name
            // ("Azure Information Protection Plan 1") in the Microsoft licensing CSV,
            // which is why this scenario hits the unique index.
            var sku1Id = Guid.NewGuid(); var sku1PartNumber = "RIGHTSMANAGEMENT";
            var sku2Id = Guid.NewGuid(); var sku2PartNumber = "RIGHTSMANAGEMENT_CE";
            var sharedLicenseName = "Azure Information Protection Plan 1";

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
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };
            var skus = new List<SubscribedSku>
            {
                new SubscribedSku { SkuId = sku1Id, SkuPartNumber = sku1PartNumber },
                new SubscribedSku { SkuId = sku2Id, SkuPartNumber = sku2PartNumber }
            };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.Models.User>>
            {
                { sku1Id, new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { UserPrincipalName = userUpn, Id = userId } } },
                { sku2Id, new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { UserPrincipalName = userUpn, Id = userId } } }
            };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, skus, fakeUsersBySku);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            // Should NOT throw. Before the fix this throws a DbUpdateException with the
            // SQL "Cannot insert duplicate key row ... IX_license_type_id_user_id".
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser, "User should have been saved");
                // Two SKUs collapse to a single LicenseType (same display-name) so we
                // expect exactly one lookup row, not two.
                Assert.AreEqual(1, dbUser.LicenseLookups.Count,
                    "User should have one license lookup when two SKUs share a display name");
                Assert.AreEqual(sharedLicenseName, dbUser.LicenseLookups[0].License.Name);
            }
        }

        /// <summary>
        /// Second possible root cause of the production duplicate-key error:
        /// Microsoft Graph is documented (in GraphUserLoader.LoadAllActiveUsers,
        /// "Graph for some reason gives duplicates; filter that out") to
        /// occasionally return the same user twice in delta-style queries.
        /// LoadUsersBySku does NOT currently dedupe its response, so if Graph
        /// returns the same UPN twice for one SKU the import would try to
        /// insert two UserLicenseTypeLookup rows with identical
        /// (license_type_id, user_id) values.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_GraphReturnsDuplicateUserForSingleSku_DoesNotThrowDuplicateKey()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var userId = Guid.NewGuid().ToString();
            var userUpn = $"graphdupuser{DateTime.Now.Ticks}@test.com";
            var skuId = Guid.NewGuid();
            var skuPartNumber = "ENTERPRISEPACK";
            var licenseName = "Office 365 E3";

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
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };
            var skus = new List<SubscribedSku> { new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPartNumber } };

            // Simulate the Graph quirk: same user returned twice for one SKU.
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.Models.User>>
            {
                {
                    skuId,
                    new List<Microsoft.Graph.Models.User>
                    {
                        new Microsoft.Graph.Models.User { UserPrincipalName = userUpn, Id = userId },
                        new Microsoft.Graph.Models.User { UserPrincipalName = userUpn, Id = userId }
                    }
                }
            };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, skus, fakeUsersBySku);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser, "User should have been saved");
                Assert.AreEqual(1, dbUser.LicenseLookups.Count,
                    "Duplicate users from Graph for the same SKU must collapse to one lookup");
                Assert.AreEqual(licenseName, dbUser.LicenseLookups[0].License.Name);
            }
        }

        /// <summary>
        /// Third possible root cause: SubscribedSkus.GetAsync returning the same
        /// SkuId twice (Graph occasionally returns duplicates here too). The
        /// outer foreach would process it twice, hitting GetLicenseType -> same
        /// cached LicenseType, and try to insert another lookup for the same user.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_DuplicateSubscribedSku_DoesNotThrowDuplicateKey()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var userId = Guid.NewGuid().ToString();
            var userUpn = $"dupskuuser{DateTime.Now.Ticks}@test.com";
            var skuId = Guid.NewGuid();
            var skuPartNumber = "ENTERPRISEPACK";
            var licenseName = "Office 365 E3";

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
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };

            // Same SKU listed twice in the subscribed-SKUs response.
            var skus = new List<SubscribedSku>
            {
                new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPartNumber },
                new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPartNumber }
            };
            var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.Models.User>>
            {
                { skuId, new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { UserPrincipalName = userUpn, Id = userId } } }
            };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, skus, fakeUsersBySku);
            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser);
                Assert.AreEqual(1, dbUser.LicenseLookups.Count,
                    "Same SKU appearing twice in SubscribedSkus must not produce duplicate lookups");
                Assert.AreEqual(licenseName, dbUser.LicenseLookups[0].License.Name);
            }
        }

        /// <summary>
        /// Same root cause as <see cref="UserMetadataUpdater_TwoSkusSameDisplayName_DoesNotThrowDuplicateKey"/>
        /// but via the per-user-licenses code path (used when the importer cannot
        /// read tenant-level SKUs due to missing Organization.Read.All). Two SKU
        /// part numbers in the user's license-details list resolve to the same
        /// LicenseType.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_PerUserLicenses_TwoSkusSameDisplayName_DoesNotThrowDuplicateKey()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var userId = Guid.NewGuid().ToString();
            var userUpn = $"peruserdupuser{DateTime.Now.Ticks}@test.com";

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
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };

            // Build a fake List<LicenseDetails> with two SKU part
            // numbers that both resolve to the same display name.
            var licenseDetailsPage = new List<LicenseDetails>
            {
                new LicenseDetails { SkuId = Guid.NewGuid(), SkuPartNumber = "RIGHTSMANAGEMENT" },
                new LicenseDetails { SkuId = Guid.NewGuid(), SkuPartNumber = "RIGHTSMANAGEMENT_CE" }
            };
            var fakeLicenseDetails = new Dictionary<string, List<LicenseDetails>>
            {
                { userId, licenseDetailsPage }
            };

            // fakeSkus = null forces the per-user license path.
            var fakeLoader = new FakeUserMetadataLoader(
                new List<GraphUser> { graphUser },
                fakeSkus: null,
                fakeUsersBySku: null,
                fakeLicenseDetails: fakeLicenseDetails);

            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser);
                Assert.AreEqual(1, dbUser.LicenseLookups.Count,
                    "Per-user license path must dedupe two SKUs that share a display name");
                Assert.AreEqual("Azure Information Protection Plan 1", dbUser.LicenseLookups[0].License.Name);
            }
        }

        /// <summary>
        /// Step 3: if anything in the user-license / metadata import throws, the
        /// Graph delta token must NOT be advanced. Otherwise the failing users
        /// would be skipped on subsequent cycles (Graph would consider them
        /// "already delivered") and we'd permanently lose them - which matches
        /// the production symptom of "we seem to have not many users imported".
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_LicenseProcessingThrows_DeltaTokenNotAdvanced()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var userId = Guid.NewGuid().ToString();
            var userUpn = $"deltarollbackuser{DateTime.Now.Ticks}@test.com";
            var skuId = Guid.NewGuid();

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
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };
            var skus = new List<SubscribedSku> { new SubscribedSku { SkuId = skuId, SkuPartNumber = "ENTERPRISEPACK" } };
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, skus, new Dictionary<Guid, List<Microsoft.Graph.Models.User>>());

            // Seed the delta provider with an existing token, then arrange for the
            // import to throw before CommitDeltaTokenAsync would be called.
            const string preExistingDelta = "delta-from-previous-successful-cycle";
            await fakeLoader.DeltaValueProvider.SetDeltaToken(preExistingDelta);
            fakeLoader.SimulatedNewDeltaToken = "delta-that-must-NOT-be-saved";
            fakeLoader.OnLoadUsersBySku = _ => throw new InvalidOperationException("simulated Graph failure during license import");

            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await updater.InsertAndUpdateDatabaseFromExternalUsers(),
                "Import should propagate the license-loading failure.");

            var persistedDelta = await fakeLoader.DeltaValueProvider.GetDeltaToken();
            Assert.AreEqual(preExistingDelta, persistedDelta,
                "Delta token must NOT be advanced when the user import fails - " +
                "otherwise failed users get skipped on the next cycle.");
        }

        /// <summary>
        /// Sanity check that confirms the OPPOSITE side of Step 3: on a clean,
        /// successful import the new delta token IS committed.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_SuccessfulImport_DeltaTokenCommitted()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var userId = Guid.NewGuid().ToString();
            var userUpn = $"deltacommituser{DateTime.Now.Ticks}@test.com";

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
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser });

            const string newDelta = "delta-after-successful-import";
            fakeLoader.SimulatedNewDeltaToken = newDelta;

            var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            var persistedDelta = await fakeLoader.DeltaValueProvider.GetDeltaToken();
            Assert.AreEqual(newDelta, persistedDelta,
                "Delta token should be persisted after a successful import.");
        }

        [TestMethod]
        public async Task UserMetadataUpdater_LicenceRefreshSpansEntireDb_NotJustDeltaUsers()
        {
            // Regression test for the licence-count drift bug seen against tenants
            // that persist the Graph users/delta token (e.g. Redis-backed deployments).
            //
            // Scenario reproduced:
            //   Run 1: two users (A and B) exist in Graph, neither has a licence.
            //          Both are inserted into the DB, delta token gets persisted.
            //   Run 2: in Graph both users have just been assigned a licence, but
            //          only user A also has a non-licence metadata change. The
            //          Graph /users/delta response therefore returns ONLY user A.
            //          LoadUsersBySku for the new SKU returns BOTH users.
            //
            // Bug (pre-fix): UserLicenseProcessor.ProcessSKUsForAllUsers was called
            // with the delta-matched subset, so only user A got a licence row. User B
            // - despite having the licence in Graph - was never written, exactly the
            // pattern that produced the customer's ~1/4 licence counts.
            //
            // Fix: the licence refresh now spans the entire DB user population (all
            // existing DB users plus newly inserted ones), so both A and B end up
            // with the correct licence row.

            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var tick = DateTime.Now.Ticks;
            var userAId = Guid.NewGuid().ToString();
            var userBId = Guid.NewGuid().ToString();
            var userAUpn = $"licencedriftA{tick}@test.com";
            var userBUpn = $"licencedriftB{tick}@test.com";

            var skuId = Guid.NewGuid();
            var skuPartNumber = "ENTERPRISEPACK";
            var licenseName = "Office 365 E3";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existing = await cleanupDb.users
                    .Include(u => u.LicenseLookups)
                    .Where(u => u.UserPrincipalName == userAUpn || u.UserPrincipalName == userBUpn)
                    .ToListAsync();
                foreach (var u in existing)
                {
                    cleanupDb.UserLicenseTypeLookups.RemoveRange(u.LicenseLookups);
                }
                cleanupDb.users.RemoveRange(existing);
                await cleanupDb.SaveChangesAsync();

                var staleLicenseTypes = await cleanupDb.LicenseTypes
                    .Where(l => l.Name == licenseName)
                    .ToListAsync();
                if (staleLicenseTypes.Any())
                {
                    cleanupDb.LicenseTypes.RemoveRange(staleLicenseTypes);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            // -------- Run 1: insert both users, no licence assigned to either --------

            var userAGraph = new GraphUser { UserPrincipalName = userAUpn, Id = userAId, AccountEnabled = true, Mail = userAUpn, JobTitle = "Engineer" };
            var userBGraph = new GraphUser { UserPrincipalName = userBUpn, Id = userBId, AccountEnabled = true, Mail = userBUpn, JobTitle = "Engineer" };

            var emptySkuPage = new List<SubscribedSku>();
            var emptyUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.Models.User>>();

            // IMPORTANT: re-use the SAME loader instance across both runs so the
            // FakeDeltaValueProvider keeps the token persisted by run 1 - this
            // mirrors a Redis-backed deployment.
            var fakeLoader = new FakeUserMetadataLoader(
                new List<GraphUser> { userAGraph, userBGraph },
                emptySkuPage,
                emptyUsersBySku);
            fakeLoader.SimulatedNewDeltaToken = $"delta-after-run-1-{tick}";

            await new UserMetadataUpdater(telemetry, config, fakeLoader)
                .InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var a = await verifyDb.users.Include(u => u.LicenseLookups)
                    .FirstOrDefaultAsync(u => u.UserPrincipalName == userAUpn);
                var b = await verifyDb.users.Include(u => u.LicenseLookups)
                    .FirstOrDefaultAsync(u => u.UserPrincipalName == userBUpn);
                Assert.IsNotNull(a, "User A should be inserted in run 1.");
                Assert.IsNotNull(b, "User B should be inserted in run 1.");
                Assert.AreEqual(0, a.LicenseLookups.Count, "User A should have no licences after run 1.");
                Assert.AreEqual(0, b.LicenseLookups.Count, "User B should have no licences after run 1.");
            }

            var persistedDeltaAfterRun1 = await fakeLoader.DeltaValueProvider.GetDeltaToken();
            Assert.IsFalse(string.IsNullOrEmpty(persistedDeltaAfterRun1),
                "Delta token must be persisted after run 1 so run 2 simulates the bugged scenario.");

            // -------- Run 2: both users now have the SKU in Graph, but only user A
            //                 surfaces in the delta response. --------

            var sku = new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPartNumber };
            var skuPage = new List<SubscribedSku> { sku };
            var usersWithSku = new List<Microsoft.Graph.Models.User>
            {
                new Microsoft.Graph.Models.User { UserPrincipalName = userAUpn, Id = userAId },
                new Microsoft.Graph.Models.User { UserPrincipalName = userBUpn, Id = userBId }
            };
            var usersBySku = new Dictionary<Guid, List<Microsoft.Graph.Models.User>> { { skuId, usersWithSku } };

            // Mutate fake state in place so the same loader/delta provider is reused.
            fakeLoader.SetFakeState(
                new List<GraphUser> { userAGraph, userBGraph },
                skuPage,
                usersBySku);

            // Only user A has a non-licence metadata change, so the simulated
            // /users/delta response returns ONLY user A.
            userAGraph.JobTitle = "Senior Engineer";
            fakeLoader.DeltaUsersOverride = new List<GraphUser> { userAGraph };
            fakeLoader.SimulatedNewDeltaToken = $"delta-after-run-2-{tick}";

            await new UserMetadataUpdater(telemetry, config, fakeLoader)
                .InsertAndUpdateDatabaseFromExternalUsers();

            // -------- Assert: BOTH users have the licence row, not just user A. --------

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var a = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .FirstOrDefaultAsync(u => u.UserPrincipalName == userAUpn);
                var b = await verifyDb.users
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .FirstOrDefaultAsync(u => u.UserPrincipalName == userBUpn);

                Assert.IsNotNull(a, "User A should still exist after run 2.");
                Assert.IsNotNull(b, "User B should still exist after run 2.");

                Assert.AreEqual(1, a.LicenseLookups.Count,
                    "User A (in delta) should have the new licence row.");
                Assert.AreEqual(licenseName, a.LicenseLookups[0].License.Name);

                Assert.AreEqual(1, b.LicenseLookups.Count,
                    "REGRESSION: User B is not in the current Graph delta response but is reported by LoadUsersBySku. " +
                    "Pre-fix, ProcessSKUsForAllUsers was scoped to delta users so user B never got a licence row even " +
                    "though Graph said they had the SKU - this caused the customer's tenant-wide licence counts to " +
                    "drift downward run after run. The fix scopes the licence refresh to the entire DB user population.");
                Assert.AreEqual(licenseName, b.LicenseLookups[0].License.Name);
            }
        }
    }
}
