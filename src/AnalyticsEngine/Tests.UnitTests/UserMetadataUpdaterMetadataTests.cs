using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
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

        /// <summary>
        /// A deleted user arrives in an incremental read as an <c>@removed</c> entry carrying nothing but the object id.
        /// Their row is marked disabled, and nothing else about it changes: running the entry through the normal mapping
        /// would have cleared their department, manager and the rest.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_DeletedUser_RowMarkedDisabled_AndItsMetadataKept()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var ticks = DateTime.Now.Ticks;
            var userUpn = $"deleteduser{ticks}@contoso.com";
            var managerUpn = $"deletedusermgr{ticks}@contoso.com";
            var userId = Guid.NewGuid().ToString();
            var managerId = Guid.NewGuid().ToString();
            await DeleteUsers(userUpn, managerUpn);

            try
            {
                var manager = new GraphUser { UserPrincipalName = managerUpn, Id = managerId, AccountEnabled = true, Mail = managerUpn };
                var user = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn, Department = "Πωλήσεις", PostalCode = "12345", ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = managerId } } };
                var loader = new FakeUserMetadataLoader(new List<GraphUser> { manager, user });
                Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(), "Cycle 1");

                loader.DeltaUsersOverride = new List<GraphUser> { new GraphUser { Id = userId, Removed = JObject.Parse("{\"reason\":\"changed\"}") } };
                Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(), "Cycle 2");

                using (var db = new AnalyticsEntitiesContext())
                {
                    var dbManager = await db.users.SingleAsync(u => u.UserPrincipalName == managerUpn);
                    var dbUser = await db.users.Include(u => u.Department).SingleAsync(u => u.UserPrincipalName == userUpn);
                    Assert.AreEqual(false, dbUser.AccountEnabled, "A deleted user must not stay enabled.");
                    Assert.AreEqual("Πωλήσεις", dbUser.Department?.Name);
                    Assert.AreEqual("12345", dbUser.PostalCode);
                    Assert.AreEqual(userUpn, dbUser.Mail);
                    Assert.AreEqual(dbManager.ID, dbUser.ManagerId);
                    Assert.AreEqual(true, dbManager.AccountEnabled, "Nobody else is touched.");
                }
            }
            finally
            {
                await DeleteUsers(userUpn, managerUpn);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_DisabledUserNotInTheTable_IsNotInserted()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var userUpn = $"neverenabled{DateTime.Now.Ticks}@contoso.com";
            await DeleteUsers(userUpn);

            var disabled = new GraphUser { UserPrincipalName = userUpn, Id = Guid.NewGuid().ToString(), AccountEnabled = false, Mail = userUpn };
            Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), new FakeUserMetadataLoader(new List<GraphUser> { disabled })).InsertAndUpdateDatabaseFromExternalUsers());

            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(0, await db.users.CountAsync(u => u.UserPrincipalName == userUpn), "An account that was already disabled is not added, as before.");
            }
        }

        /// <summary>
        /// A deleted account's sign-in name given to a new account in the same read - a rehire, say. The row is matched by
        /// sign-in name, so this run moves it to the new account; marking it disabled for the deleted one would disable
        /// the person who just started.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_DeletedAccountsSignInNameReused_TheNewAccountStaysEnabled()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var userUpn = $"reusedname{DateTime.Now.Ticks}@contoso.com";
            var oldId = Guid.NewGuid().ToString();
            var newId = Guid.NewGuid().ToString();
            await DeleteUsers(userUpn);

            try
            {
                var loader = new FakeUserMetadataLoader(new List<GraphUser> { new GraphUser { UserPrincipalName = userUpn, Id = oldId, AccountEnabled = true, Mail = userUpn } });
                Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(), "Cycle 1");

                loader.DeltaUsersOverride = new List<GraphUser>
                {
                    new GraphUser { Id = oldId, Removed = JObject.Parse("{\"reason\":\"deleted\"}") },
                    new GraphUser { UserPrincipalName = userUpn, Id = newId, AccountEnabled = true, Mail = userUpn },
                };
                Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(), "Cycle 2");

                using (var db = new AnalyticsEntitiesContext())
                {
                    var dbUser = await db.users.SingleAsync(u => u.UserPrincipalName == userUpn);
                    Assert.AreEqual(newId, dbUser.AzureAdId);
                    Assert.AreEqual(true, dbUser.AccountEnabled);
                }
            }
            finally
            {
                await DeleteUsers(userUpn);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_DeactivationWriteFails_CheckpointIsNotMoved()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var userUpn = $"deactivationfails{DateTime.Now.Ticks}@contoso.com";
            var userId = Guid.NewGuid().ToString();
            await DeleteUsers(userUpn);

            try
            {
                var loader = new FakeUserMetadataLoader(new List<GraphUser> { new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn } });
                Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(), "Cycle 1");
                var checkpoint = await loader.DeltaValueProvider.GetDeltaToken();

                loader.DeltaUsersOverride = new List<GraphUser> { new GraphUser { Id = userId, Removed = JObject.Parse("{\"reason\":\"deleted\"}") } };
                loader.SimulatedNewDeltaToken = "must-not-be-saved";
                var updater = new UserMetadataUpdater(logger, new AppConfig(), loader)
                {
                    AccountStatusWriterFactory = connectionString => new FailingAccountStatusWriter(),
                };

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => updater.InsertAndUpdateDatabaseFromExternalUsers());
                Assert.AreEqual(checkpoint, await loader.DeltaValueProvider.GetDeltaToken(),
                    "Graph reports a deletion once: committing past it would leave the row enabled for ever.");
            }
            finally
            {
                await DeleteUsers(userUpn);
            }
        }

        private sealed class FailingAccountStatusWriter : IUserAccountStatusWriter
        {
            public Task<int> MarkDisabledAsync(IReadOnlyList<UserRowDeactivation> rows, DateTime lastUpdatedUtc)
                => throw new InvalidOperationException("synthetic account status write failure");
        }

        #region Accounts a complete read of the directory leaves out

        // These run against a test database shared with every other test, whose users are not in any one test's fake
        // directory. OwnRowsOnlyWriter writes only this test's rows, so a check that trusts a read cannot disable
        // anyone else's - while still recording everything the import asked it to disable.

        /// <summary>
        /// The first complete read after an upgrade leaves out everyone who left since object ids were first stored -
        /// for a university or a bulk guest clean-up, more people than are in the directory today. Checked against
        /// Graph's own count of the directory, which no backlog can skew, the read is trusted and they are caught up.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_LeaverBacklogLargerThanTheDirectory_ReadMatchingGraphsCount_IsCaughtUp()
        {
            var ticks = DateTime.Now.Ticks;
            var current = NewUsers("backlog_current", 2, ticks);
            var leavers = NewUsers("backlog_leaver", 3, ticks);
            var everyone = current.Concat(leavers).ToList();
            await DeleteUsers(Upns(everyone));

            try
            {
                Assert.IsTrue(await ImportWritingOnly(new FakeUserMetadataLoader(everyone), everyone).Run(), "Imported while all five still worked here.");

                var loader = new FakeUserMetadataLoader(current) { DirectoryUserCount = current.Count };
                var import = ImportWritingOnly(loader, everyone);
                Assert.IsTrue(await import.Run());

                Assert.AreEqual(1, loader.CountDirectoryUsersCalls);
                var rows = await RowsOf(everyone);
                Assert.IsTrue(leavers.All(l => rows[l.Id].AccountEnabled == false), "Three leavers against two current users: still a deletion each.");
                Assert.IsTrue(current.All(c => rows[c.Id].AccountEnabled == true));
            }
            finally
            {
                await DeleteUsers(Upns(everyone));
            }
        }

        /// <summary>
        /// A read that ended in its deltaLink yet listed clearly fewer users than Graph counts may have lost part of the
        /// directory, so nobody is disabled for being absent from it - even when the users table alone would allow it.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_CompleteReadClearlyShortOfGraphsCount_DisablesNobody()
        {
            var ticks = DateTime.Now.Ticks;
            var current = NewUsers("short_current", 3, ticks);
            var leavers = NewUsers("short_leaver", 1, ticks);
            var everyone = current.Concat(leavers).ToList();
            await DeleteUsers(Upns(everyone));
            var everyoneElse = await EveryoneElseInTheUsersTableAsync();

            try
            {
                Assert.IsTrue(await ImportWritingOnly(new FakeUserMetadataLoader(everyoneElse.Concat(everyone).ToList()), everyone).Run());

                // Everyone else is listed, so the users table would vouch for this read; Graph's count does not.
                var directory = everyoneElse.Concat(current).ToList();
                var loader = new FakeUserMetadataLoader(directory) { DirectoryUserCount = directory.Count + 1_000 };
                var import = ImportWritingOnly(loader, everyone);
                Assert.IsTrue(await import.Run());

                Assert.AreEqual(1, loader.CountDirectoryUsersCalls);
                Assert.AreEqual(0, import.Writer.Asked.Count, "Nothing is disabled for being absent from a read that is short of the directory.");
                Assert.IsTrue((await RowsOf(everyone)).Values.All(u => u.AccountEnabled == true));
            }
            finally
            {
                await DeleteUsers(Upns(everyone));
            }
        }

        /// <summary>
        /// When Graph's count cannot be read, the users table is the fallback: a read that lists more of the table's
        /// accounts than it leaves out is trusted...
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_CountUnavailable_ReadListingMoreOfTheTableThanItLeavesOut_IsTrusted()
        {
            var ticks = DateTime.Now.Ticks;
            var current = NewUsers("fallback_current", 3, ticks);
            var leavers = NewUsers("fallback_leaver", 1, ticks);
            var everyone = current.Concat(leavers).ToList();
            await DeleteUsers(Upns(everyone));
            var everyoneElse = await EveryoneElseInTheUsersTableAsync();

            try
            {
                Assert.IsTrue(await ImportWritingOnly(new FakeUserMetadataLoader(everyoneElse.Concat(everyone).ToList()), everyone).Run());

                var loader = new FakeUserMetadataLoader(everyoneElse.Concat(current).ToList()) { DirectoryUserCount = null };
                Assert.IsTrue(await ImportWritingOnly(loader, everyone).Run());

                Assert.AreEqual(1, loader.CountDirectoryUsersCalls);
                var rows = await RowsOf(everyone);
                Assert.AreEqual(false, rows[leavers[0].Id].AccountEnabled);
                Assert.IsTrue(current.All(c => rows[c.Id].AccountEnabled == true));
            }
            finally
            {
                await DeleteUsers(Upns(everyone));
            }
        }

        /// <summary>
        /// ...and one that leaves out more than it lists is not: that is what a read that lost part of the directory
        /// looks like, and disabling everyone it missed could not be undone by an incremental read.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_CountUnavailable_ReadLeavingOutMoreOfTheTableThanItLists_DisablesNobody()
        {
            var ticks = DateTime.Now.Ticks;
            var current = NewUsers("guard_kept", 1, ticks);
            var leavers = NewUsers("guard_left_out", 3, ticks);
            var everyone = current.Concat(leavers).ToList();
            await DeleteUsers(Upns(everyone));

            try
            {
                Assert.IsTrue(await ImportWritingOnly(new FakeUserMetadataLoader(everyone), everyone).Run());

                var import = ImportWritingOnly(new FakeUserMetadataLoader(current), everyone);
                Assert.IsTrue(await import.Run());

                Assert.AreEqual(0, import.Writer.Asked.Count);
                Assert.IsTrue((await RowsOf(everyone)).Values.All(u => u.AccountEnabled == true));
            }
            finally
            {
                await DeleteUsers(Upns(everyone));
            }
        }

        private static List<GraphUser> NewUsers(string prefix, int count, long ticks)
            => Enumerable.Range(1, count).Select(i =>
            {
                var upn = $"{prefix}{i}_{ticks}@contoso.com";
                return new GraphUser { UserPrincipalName = upn, Id = Guid.NewGuid().ToString(), AccountEnabled = true, Mail = upn };
            }).ToList();

        private static string[] Upns(IEnumerable<GraphUser> users) => users.Select(u => u.UserPrincipalName).ToArray();

        private static async Task<Dictionary<string, Common.Entities.User>> RowsOf(IEnumerable<GraphUser> users)
        {
            var upns = Upns(users);
            using (var db = new AnalyticsEntitiesContext())
            {
                return (await db.users.Where(u => upns.Contains(u.UserPrincipalName)).ToListAsync())
                    .ToDictionary(u => u.AzureAdId, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Everyone already in the users table with an object id, as directory entries that change nothing: no sign-in
        /// name and no account status. A fake directory has to include them to be the whole directory.
        /// </summary>
        private static async Task<List<GraphUser>> EveryoneElseInTheUsersTableAsync()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var ids = await db.users.Where(u => u.AzureAdId != null && u.AzureAdId != "").Select(u => u.AzureAdId).ToListAsync();
                return ids.Select(id => new GraphUser { Id = id }).ToList();
            }
        }

        private static OwnRowsImport ImportWritingOnly(FakeUserMetadataLoader loader, IEnumerable<GraphUser> ownUsers)
        {
            string connectionString;
            using (var db = new AnalyticsEntitiesContext())
            {
                connectionString = db.Database.Connection.ConnectionString;
            }
            return new OwnRowsImport(loader, new OwnRowsOnlyWriter(connectionString, ownUsers.Select(u => u.Id)));
        }

        private sealed class OwnRowsImport
        {
            private readonly FakeUserMetadataLoader _loader;

            public OwnRowsImport(FakeUserMetadataLoader loader, OwnRowsOnlyWriter writer)
            {
                _loader = loader;
                Writer = writer;
            }

            public OwnRowsOnlyWriter Writer { get; }

            public Task<bool> Run() => new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), _loader)
            {
                AccountStatusWriterFactory = connectionString => Writer,
            }.InsertAndUpdateDatabaseFromExternalUsers();
        }

        /// <summary>The real SQL writer, for this test's own rows only; records every row it was asked to disable.</summary>
        private sealed class OwnRowsOnlyWriter : IUserAccountStatusWriter
        {
            private readonly SqlUserAccountStatusWriter _sql;
            private readonly HashSet<string> _ownObjectIds;

            public OwnRowsOnlyWriter(string connectionString, IEnumerable<string> ownObjectIds)
            {
                _sql = new SqlUserAccountStatusWriter(connectionString);
                _ownObjectIds = new HashSet<string>(ownObjectIds, StringComparer.OrdinalIgnoreCase);
            }

            public List<UserRowDeactivation> Asked { get; } = new List<UserRowDeactivation>();

            public Task<int> MarkDisabledAsync(IReadOnlyList<UserRowDeactivation> rows, DateTime lastUpdatedUtc)
            {
                Asked.AddRange(rows);
                return _sql.MarkDisabledAsync(rows.Where(r => _ownObjectIds.Contains(r.ObjectId)).ToList(), lastUpdatedUtc);
            }
        }

        #endregion

        private static async Task DeleteUsers(params string[] upns)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var users = await db.users.Include(u => u.LicenseLookups).Where(u => upns.Contains(u.UserPrincipalName)).ToListAsync();
                if (users.Count == 0)
                {
                    return;
                }

                foreach (var user in users)
                {
                    user.ManagerId = null;
                    user.Manager = null;
                    db.UserLicenseTypeLookups.RemoveRange(user.LicenseLookups);
                }
                await db.SaveChangesAsync();
                db.users.RemoveRange(users);
                await db.SaveChangesAsync();
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
