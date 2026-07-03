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
    /// Tests for user metadata updates: deactivation, field changes, nullification, whitespace trimming, shared lookups
    /// </summary>
    [TestClass]
    public class UserMetadataUpdaterMetadataTests
    {
        [TestMethod]
        public async Task UserMetadataUpdater_UserDeactivated_AccountEnabledUpdated()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"deactivateduser{DateTime.Now.Ticks}@test.com";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users.Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                if (existingTestUser != null) { cleanupDb.users.Remove(existingTestUser); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUserActive = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUserActive });
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsTrue(dbUser.AccountEnabled.Value, "User should be active initially");
            }

            var graphUserDeactivated = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = false, Mail = userUpn };
            var updatedFakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUserDeactivated });
            var updaterDeactivated = new UserMetadataUpdater(logger, config, updatedFakeLoader);
            await updaterDeactivated.InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users.Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUserFinal);
                Assert.IsFalse(dbUserFinal.AccountEnabled.Value, "User should be deactivated");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_MetadataChanged_DatabaseReflectsChanges()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"metadatachangeuser{DateTime.Now.Ticks}@test.com";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users.Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                if (existingTestUser != null) { cleanupDb.users.Remove(existingTestUser); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUserInitial = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn, Department = "IT", JobTitle = "Developer", OfficeLocation = "Building 1", PostalCode = "12345" };
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUserInitial });
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Include(u => u.Department).Include(u => u.JobTitle).Include(u => u.OfficeLocation).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.AreEqual("IT", dbUser.Department?.Name);
                Assert.AreEqual("Developer", dbUser.JobTitle?.Name);
                Assert.AreEqual("Building 1", dbUser.OfficeLocation?.Name);
                Assert.AreEqual("12345", dbUser.PostalCode);
            }

            var graphUserUpdated = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn, Department = "HR", JobTitle = "Manager", OfficeLocation = "Building 2", PostalCode = "67890" };
            var updatedFakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUserUpdated });
            var updaterUpdated = new UserMetadataUpdater(logger, config, updatedFakeLoader);
            await updaterUpdated.InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users.Include(u => u.Department).Include(u => u.JobTitle).Include(u => u.OfficeLocation).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUserFinal);
                Assert.AreEqual("HR", dbUserFinal.Department?.Name);
                Assert.AreEqual("Manager", dbUserFinal.JobTitle?.Name);
                Assert.AreEqual("Building 2", dbUserFinal.OfficeLocation?.Name);
                Assert.AreEqual("67890", dbUserFinal.PostalCode);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_SameUserReimported_NoChangesOrDuplicates()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"reimportuser{DateTime.Now.Ticks}@test.com";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users.Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                if (existingTestUser != null) { cleanupDb.users.Remove(existingTestUser); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn, Department = "IT", PostalCode = "12345" };
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser });
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            DateTime? firstImportTime;
            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                firstImportTime = dbUser.LastUpdated;
            }

            var fakeLoader2 = new FakeUserMetadataLoader(new List<GraphUser> { graphUser });
            var updater2 = new UserMetadataUpdater(logger, config, fakeLoader2);
            await updater2.InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var allUsers = await finalVerifyDb.users.Where(u => u.UserPrincipalName == userUpn).ToListAsync();
                Assert.AreEqual(1, allUsers.Count, "Should only be one user in database");
                Assert.IsTrue(allUsers[0].LastUpdated > firstImportTime, "LastUpdated should be updated on re-import");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_AllMetadataFields_PopulatedCorrectly()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"fullmetadata{DateTime.Now.Ticks}@test.com";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users.Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                if (existingTestUser != null) { cleanupDb.users.Remove(existingTestUser); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn, Department = "Engineering", JobTitle = "Senior Developer", OfficeLocation = "Building A", PostalCode = "98052", Country = "United States", State = "Washington", CompanyName = "Contoso", UsageLocation = "US" };
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser });
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Include(u => u.Department).Include(u => u.JobTitle).Include(u => u.OfficeLocation).Include(u => u.UsageLocation).Include(u => u.UserCountry).Include(u => u.StateOrProvince).Include(u => u.CompanyName).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUser);
                Assert.AreEqual("Engineering", dbUser.Department?.Name);
                Assert.AreEqual("Senior Developer", dbUser.JobTitle?.Name);
                Assert.AreEqual("Building A", dbUser.OfficeLocation?.Name);
                Assert.AreEqual("98052", dbUser.PostalCode);
                Assert.AreEqual("United States", dbUser.UserCountry?.Name);
                Assert.AreEqual("Washington", dbUser.StateOrProvince?.Name);
                Assert.AreEqual("Contoso", dbUser.CompanyName?.Name);
                Assert.AreEqual("US", dbUser.UsageLocation?.Name);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_MetadataClearedToNull_DatabaseReflectsNullValues()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"clearedmeta{DateTime.Now.Ticks}@test.com";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users.Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                if (existingTestUser != null) { cleanupDb.users.Remove(existingTestUser); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUserWithMeta = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn, Department = "IT", JobTitle = "Dev", OfficeLocation = "HQ", PostalCode = "12345" };
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUserWithMeta });
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Include(u => u.Department).Include(u => u.JobTitle).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUser.Department);
                Assert.IsNotNull(dbUser.JobTitle);
            }

            var graphUserNoMeta = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn, Department = null, JobTitle = null, OfficeLocation = null, PostalCode = null };
            var updatedLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUserNoMeta });
            var updater2 = new UserMetadataUpdater(logger, config, updatedLoader);
            await updater2.InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users.Include(u => u.Department).Include(u => u.JobTitle).Include(u => u.OfficeLocation).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUserFinal);
                Assert.IsNull(dbUserFinal.Department);
                Assert.IsNull(dbUserFinal.JobTitle);
                Assert.IsNull(dbUserFinal.OfficeLocation);
                Assert.IsNull(dbUserFinal.PostalCode);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_WhitespaceInMetadataFields_TrimmedCorrectly()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"whitespacemeta{DateTime.Now.Ticks}@test.com";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users.Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                if (existingTestUser != null) { cleanupDb.users.Remove(existingTestUser); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn, Department = "  Engineering  ", JobTitle = "  Developer  ", OfficeLocation = "  HQ  ", Country = "  USA  ", State = "  WA  ", CompanyName = "  Contoso  ", UsageLocation = "  US  " };
            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser });
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Include(u => u.Department).Include(u => u.JobTitle).Include(u => u.OfficeLocation).Include(u => u.UserCountry).Include(u => u.StateOrProvince).Include(u => u.CompanyName).Include(u => u.UsageLocation).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUser);
                Assert.AreEqual("Engineering", dbUser.Department?.Name);
                Assert.AreEqual("Developer", dbUser.JobTitle?.Name);
                Assert.AreEqual("HQ", dbUser.OfficeLocation?.Name);
                Assert.AreEqual("USA", dbUser.UserCountry?.Name);
                Assert.AreEqual("WA", dbUser.StateOrProvince?.Name);
                Assert.AreEqual("Contoso", dbUser.CompanyName?.Name);
                Assert.AreEqual("US", dbUser.UsageLocation?.Name);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_SharedMetadataValues_ReusesSameLookupEntities()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var user1Upn = $"shared_meta1_{timestamp}@test.com";
            var user2Upn = $"shared_meta2_{timestamp}@test.com";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName == user1Upn || u.UserPrincipalName == user2Upn).ToListAsync();
                cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync();
            }

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = user1Upn, Id = Guid.NewGuid().ToString(), AccountEnabled = true, Mail = user1Upn, Department = $"SharedDept{timestamp}", JobTitle = $"SharedTitle{timestamp}" },
                new GraphUser { UserPrincipalName = user2Upn, Id = Guid.NewGuid().ToString(), AccountEnabled = true, Mail = user2Upn, Department = $"SharedDept{timestamp}", JobTitle = $"SharedTitle{timestamp}" }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);
            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser1 = await verifyDb.users.Include(u => u.Department).Include(u => u.JobTitle).Where(u => u.UserPrincipalName == user1Upn).FirstOrDefaultAsync();
                var dbUser2 = await verifyDb.users.Include(u => u.Department).Include(u => u.JobTitle).Where(u => u.UserPrincipalName == user2Upn).FirstOrDefaultAsync();

                Assert.IsNotNull(dbUser1?.Department); Assert.IsNotNull(dbUser2?.Department);
                Assert.AreEqual(dbUser1.Department.ID, dbUser2.Department.ID, "Both users should share the same department lookup entity");
                Assert.AreEqual(dbUser1.JobTitle.ID, dbUser2.JobTitle.ID, "Both users should share the same job title lookup entity");
            }
        }
    }
}
