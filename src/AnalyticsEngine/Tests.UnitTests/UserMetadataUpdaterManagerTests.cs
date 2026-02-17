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
    /// Tests for manager relationship processing: assignment, changes, removal, cross-batch resolution,
    /// and duplicate-key bug reproductions
    /// </summary>
    [TestClass]
    public class UserMetadataUpdaterManagerTests
    {
        [TestMethod]
        public async Task UserMetadataUpdater_ManagerChanged_DatabaseReflectsChange()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var userUpn = $"employeemanager{timestamp}@test.com";
            var manager1Upn = $"manager1{timestamp}@test.com";
            var manager2Upn = $"manager2{timestamp}@test.com";
            var userId = Guid.NewGuid().ToString();
            var manager1Id = Guid.NewGuid().ToString();
            var manager2Id = Guid.NewGuid().ToString();

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUsers = await cleanupDb.users.Where(u => u.UserPrincipalName == userUpn || u.UserPrincipalName == manager1Upn || u.UserPrincipalName == manager2Upn).ToListAsync();
                cleanupDb.users.RemoveRange(existingTestUsers); await cleanupDb.SaveChangesAsync();
            }

            var manager1 = new GraphUser { UserPrincipalName = manager1Upn, Id = manager1Id, AccountEnabled = true, Mail = manager1Upn };
            var manager2 = new GraphUser { UserPrincipalName = manager2Upn, Id = manager2Id, AccountEnabled = true, Mail = manager2Upn };
            var employee = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = manager1Id } } };

            var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { manager1, manager2, employee });
            await new UserMetadataUpdater(telemetry, config, fakeLoader).InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUser.Manager);
                Assert.AreEqual(manager1Upn, dbUser.Manager.UserPrincipalName);
            }

            var employeeWithNewManager = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = manager2Id } } };
            var updatedFakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { manager1, manager2, employeeWithNewManager });
            await new UserMetadataUpdater(telemetry, config, updatedFakeLoader).InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName == userUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUserFinal.Manager);
                Assert.AreEqual(manager2Upn, dbUserFinal.Manager.UserPrincipalName);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_ManagerRemoved_DatabaseReflectsRemoval()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var employeeUpn = $"emp_mgr_removed{timestamp}@test.com";
            var managerUpn = $"mgr_removed{timestamp}@test.com";
            var employeeId = Guid.NewGuid().ToString();
            var managerId = Guid.NewGuid().ToString();

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName == employeeUpn || u.UserPrincipalName == managerUpn).ToListAsync();
                cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync();
            }

            var manager = new GraphUser { UserPrincipalName = managerUpn, Id = managerId, AccountEnabled = true, Mail = managerUpn };
            var employee = new GraphUser { UserPrincipalName = employeeUpn, Id = employeeId, AccountEnabled = true, Mail = employeeUpn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = managerId } } };

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(new List<GraphUser> { manager, employee })).InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var dbUser = await verifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName == employeeUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUser.Manager);
            }

            var employeeNoManager = new GraphUser { UserPrincipalName = employeeUpn, Id = employeeId, AccountEnabled = true, Mail = employeeUpn, ManagerInfo = new List<ManagerInfo>() };
            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(new List<GraphUser> { manager, employeeNoManager })).InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbUserFinal = await finalVerifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName == employeeUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbUserFinal);
                Assert.IsNull(dbUserFinal.ManagerId, "Manager should be removed");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_MultipleManagersInChain_AllRelationshipsCorrect()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var user0Upn = $"chain_emp{timestamp}@test.com"; var user1Upn = $"chain_mgr1{timestamp}@test.com";
            var user2Upn = $"chain_mgr2{timestamp}@test.com"; var user3Upn = $"chain_mgr3{timestamp}@test.com";
            var user0Id = Guid.NewGuid().ToString(); var user1Id = Guid.NewGuid().ToString();
            var user2Id = Guid.NewGuid().ToString(); var user3Id = Guid.NewGuid().ToString();

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName.Contains("chain_") && u.UserPrincipalName.Contains(timestamp.ToString())).ToListAsync();
                cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync();
            }

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = user0Upn, Id = user0Id, AccountEnabled = true, Mail = user0Upn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = user1Id } } },
                new GraphUser { UserPrincipalName = user1Upn, Id = user1Id, AccountEnabled = true, Mail = user1Upn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = user2Id } } },
                new GraphUser { UserPrincipalName = user2Upn, Id = user2Id, AccountEnabled = true, Mail = user2Upn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = user3Id } } },
                new GraphUser { UserPrincipalName = user3Upn, Id = user3Id, AccountEnabled = true, Mail = user3Upn }
            };

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(graphUsers)).InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var allUsers = await verifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName.Contains("chain_") && u.UserPrincipalName.Contains(timestamp.ToString())).ToListAsync();
                Assert.AreEqual(4, allUsers.Count);
                Assert.AreEqual(user1Upn, allUsers.First(u => u.UserPrincipalName == user0Upn).Manager.UserPrincipalName);
                Assert.AreEqual(user2Upn, allUsers.First(u => u.UserPrincipalName == user1Upn).Manager.UserPrincipalName);
                Assert.AreEqual(user3Upn, allUsers.First(u => u.UserPrincipalName == user2Upn).Manager.UserPrincipalName);
                Assert.IsNull(allUsers.First(u => u.UserPrincipalName == user3Upn).ManagerId);
            }
        }

        #region Duplicate Key Bug Reproductions

        [TestMethod]
        public async Task UserMetadataUpdater_NewlyInsertedUserAsManager_NoDuplicateKeyError()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var existingUserUpn = $"existingemployee{timestamp}@test.com";
            var newManagerUpn = $"newmanager{timestamp}@test.com";
            var existingUserId = Guid.NewGuid().ToString();
            var newManagerId = Guid.NewGuid().ToString();

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName == existingUserUpn || u.UserPrincipalName == newManagerUpn).ToListAsync();
                if (usersToClean.Any()) { cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync(); }
            }

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(new List<GraphUser> { new GraphUser { UserPrincipalName = existingUserUpn, Id = existingUserId, AccountEnabled = true, Mail = existingUserUpn } })).InsertAndUpdateDatabaseFromExternalUsers();

            var existingUserWithNewManager = new GraphUser { UserPrincipalName = existingUserUpn, Id = existingUserId, AccountEnabled = true, Mail = existingUserUpn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = newManagerId } } };
            var newManager = new GraphUser { UserPrincipalName = newManagerUpn, Id = newManagerId, AccountEnabled = true, Mail = newManagerUpn };

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(new List<GraphUser> { newManager, existingUserWithNewManager })).InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var dbExistingUser = await finalVerifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName == existingUserUpn).FirstOrDefaultAsync();
                Assert.IsNotNull(dbExistingUser.Manager);
                Assert.AreEqual(newManagerUpn, dbExistingUser.Manager.UserPrincipalName);
                var allTestUsers = await finalVerifyDb.users.Where(u => u.UserPrincipalName == existingUserUpn || u.UserPrincipalName == newManagerUpn).ToListAsync();
                Assert.AreEqual(2, allTestUsers.Count, "Should only have 2 users (no duplicates)");
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_ExistingUserManagerUpdatedToNewlyInsertedUser_NoDuplicateKeyInBatchProcessing()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var existingEmployeeUpn = $"existingemployee{timestamp}@test.com";
            var newManagerUpn = $"bulkinsertedmanager{timestamp}@test.com";
            var existingEmployeeId = Guid.NewGuid().ToString();
            var newManagerId = Guid.NewGuid().ToString();

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName == existingEmployeeUpn || u.UserPrincipalName == newManagerUpn).ToListAsync();
                if (usersToClean.Any()) { cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync(); }
            }

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(new List<GraphUser> { new GraphUser { UserPrincipalName = existingEmployeeUpn, Id = existingEmployeeId, AccountEnabled = true, Mail = existingEmployeeUpn } })).InsertAndUpdateDatabaseFromExternalUsers();

            var newManager = new GraphUser { UserPrincipalName = newManagerUpn, Id = newManagerId, AccountEnabled = true, Mail = newManagerUpn };
            var existingEmployeeWithManager = new GraphUser { UserPrincipalName = existingEmployeeUpn, Id = existingEmployeeId, AccountEnabled = true, Mail = existingEmployeeUpn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = newManagerId } } };

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(new List<GraphUser> { newManager, existingEmployeeWithManager })).InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var allTestUsers = await finalVerifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName == existingEmployeeUpn || u.UserPrincipalName == newManagerUpn).ToListAsync();
                Assert.AreEqual(2, allTestUsers.Count, "No duplicates created during ProcessExistingUsersInBatches");
                Assert.AreEqual(newManagerUpn, allTestUsers.First(u => u.UserPrincipalName == existingEmployeeUpn).Manager.UserPrincipalName);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_ProductionScenario_ExistingUserWithNewlyInsertedManagerChain_NoDuplicateKey()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var existingEmployeeUpn = $"alice_anderson{timestamp}@contoso.com";
            var newManager1Upn = $"newmanager1_{timestamp}@contoso.com";
            var newManager2Upn = $"newmanager2_{timestamp}@contoso.com";
            var existingEmployeeId = Guid.NewGuid().ToString();
            var newManager1Id = Guid.NewGuid().ToString();
            var newManager2Id = Guid.NewGuid().ToString();

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName == existingEmployeeUpn || u.UserPrincipalName == newManager1Upn || u.UserPrincipalName == newManager2Upn).ToListAsync();
                if (usersToClean.Any()) { cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync(); }
            }

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(new List<GraphUser> { new GraphUser { UserPrincipalName = existingEmployeeUpn, Id = existingEmployeeId, AccountEnabled = true, Mail = existingEmployeeUpn } })).InsertAndUpdateDatabaseFromExternalUsers();

            var step2Users = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = newManager2Upn, Id = newManager2Id, AccountEnabled = true, Mail = newManager2Upn },
                new GraphUser { UserPrincipalName = newManager1Upn, Id = newManager1Id, AccountEnabled = true, Mail = newManager1Upn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = newManager2Id } } },
                new GraphUser { UserPrincipalName = existingEmployeeUpn, Id = existingEmployeeId, AccountEnabled = true, Mail = existingEmployeeUpn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = newManager1Id } } }
            };

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(step2Users)).InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var allTestUsers = await finalVerifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName == existingEmployeeUpn || u.UserPrincipalName == newManager1Upn || u.UserPrincipalName == newManager2Upn).ToListAsync();
                Assert.AreEqual(3, allTestUsers.Count, "Should have exactly 3 users total - NO DUPLICATES");
                Assert.AreEqual(newManager1Upn, allTestUsers.First(u => u.UserPrincipalName == existingEmployeeUpn).Manager.UserPrincipalName);
                Assert.AreEqual(newManager2Upn, allTestUsers.First(u => u.UserPrincipalName == newManager1Upn).Manager.UserPrincipalName);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_LargeScaleBatching_ReloadedEntitiesRemainTracked()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var existingUserUpn = $"existing_employee{timestamp}@test.com"; var newMgr1Upn = $"newmgr1_{timestamp}@test.com"; var newMgr2Upn = $"newmgr2_{timestamp}@test.com";
            var existingUserId = Guid.NewGuid().ToString(); var newMgr1Id = Guid.NewGuid().ToString(); var newMgr2Id = Guid.NewGuid().ToString();

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName == existingUserUpn || u.UserPrincipalName == newMgr1Upn || u.UserPrincipalName == newMgr2Upn).ToListAsync();
                if (usersToClean.Any()) { cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync(); }
            }

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(new List<GraphUser> { new GraphUser { UserPrincipalName = existingUserUpn, Id = existingUserId, AccountEnabled = true, Mail = existingUserUpn } })).InsertAndUpdateDatabaseFromExternalUsers();

            var loader2 = new FakeUserMetadataLoader(new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = newMgr2Upn, Id = newMgr2Id, AccountEnabled = true, Mail = newMgr2Upn },
                new GraphUser { UserPrincipalName = newMgr1Upn, Id = newMgr1Id, AccountEnabled = true, Mail = newMgr1Upn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = newMgr2Id } } },
                new GraphUser { UserPrincipalName = existingUserUpn, Id = existingUserId, AccountEnabled = true, Mail = existingUserUpn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = newMgr1Id } } }
            });
            await new UserMetadataUpdater(telemetry, config, loader2).InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var allUsers = await verifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName == existingUserUpn || u.UserPrincipalName == newMgr1Upn || u.UserPrincipalName == newMgr2Upn).ToListAsync();
                Assert.AreEqual(3, allUsers.Count, "Should have exactly 3 users, no duplicates");
                Assert.AreEqual(newMgr1Upn, allUsers.First(u => u.UserPrincipalName == existingUserUpn).Manager.UserPrincipalName);
                Assert.AreEqual(newMgr2Upn, allUsers.First(u => u.UserPrincipalName == newMgr1Upn).Manager.UserPrincipalName);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_NewUserManagerInSameBatch_WorksCorrectly()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var userAUpn = $"usera_employee{timestamp}@contoso.com";
            var managerBUpn = $"beatriz_brown{timestamp}@contoso.com";
            var userAId = Guid.NewGuid().ToString(); var managerBId = Guid.NewGuid().ToString();

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName.Contains(timestamp.ToString())).ToListAsync();
                if (usersToClean.Any()) { cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = userAUpn, Id = userAId, AccountEnabled = true, Mail = userAUpn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = managerBId } } },
                new GraphUser { UserPrincipalName = managerBUpn, Id = managerBId, AccountEnabled = true, Mail = managerBUpn }
            };

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(graphUsers)).InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var allTestUsers = await verifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName == userAUpn || u.UserPrincipalName == managerBUpn).ToListAsync();
                Assert.AreEqual(2, allTestUsers.Count);
                Assert.AreEqual(managerBUpn, allTestUsers.First(u => u.UserPrincipalName == userAUpn).Manager.UserPrincipalName);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_BugRepro_CrossBatchManagerRelationships_NoDuplicateKey()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var manager1Id = Guid.NewGuid().ToString(); var manager1Upn = $"manager1_{timestamp}@test.com";
            var manager2Id = Guid.NewGuid().ToString(); var manager2Upn = $"manager2_{timestamp}@test.com";
            var employee1Id = Guid.NewGuid().ToString(); var employee1Upn = $"employee1_{timestamp}@test.com";
            var employee2Id = Guid.NewGuid().ToString(); var employee2Upn = $"employee2_{timestamp}@test.com";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName.Contains(timestamp.ToString())).ToListAsync();
                if (usersToClean.Any()) { cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = employee1Upn, Id = employee1Id, AccountEnabled = true, Mail = employee1Upn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = manager1Id } } },
                new GraphUser { UserPrincipalName = employee2Upn, Id = employee2Id, AccountEnabled = true, Mail = employee2Upn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = manager2Id } } },
                new GraphUser { UserPrincipalName = manager1Upn, Id = manager1Id, AccountEnabled = true, Mail = manager1Upn },
                new GraphUser { UserPrincipalName = manager2Upn, Id = manager2Id, AccountEnabled = true, Mail = manager2Upn }
            };

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(graphUsers)).InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var allTestUsers = await verifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName.Contains(timestamp.ToString())).ToListAsync();
                Assert.AreEqual(4, allTestUsers.Count);
                Assert.AreEqual(manager1Upn, allTestUsers.First(u => u.UserPrincipalName == employee1Upn).Manager.UserPrincipalName);
                Assert.AreEqual(manager2Upn, allTestUsers.First(u => u.UserPrincipalName == employee2Upn).Manager.UserPrincipalName);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_BugRepro_ManyUsersCrossBatch_ManagersAtEnd()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            const int numEmployees = 10; const int numManagers = 10;
            var graphUsers = new List<GraphUser>();
            var managerIds = new List<string>(); var managerUpns = new List<string>();

            for (int i = 0; i < numManagers; i++) { managerIds.Add(Guid.NewGuid().ToString()); managerUpns.Add($"mgr{i}_{timestamp}@test.com"); }

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName.Contains(timestamp.ToString())).ToListAsync();
                if (usersToClean.Any()) { cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync(); }
            }

            for (int i = 0; i < numEmployees; i++)
            {
                int managerIndex = i % numManagers;
                graphUsers.Add(new GraphUser { UserPrincipalName = $"emp{i}_{timestamp}@test.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true, Mail = $"emp{i}_{timestamp}@test.com", ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = managerIds[managerIndex] } } });
            }
            for (int i = 0; i < numManagers; i++)
            {
                graphUsers.Add(new GraphUser { UserPrincipalName = managerUpns[i], Id = managerIds[i], AccountEnabled = true, Mail = managerUpns[i] });
            }

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(graphUsers)).InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var allTestUsers = await verifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName.Contains(timestamp.ToString())).ToListAsync();
                Assert.AreEqual(numEmployees + numManagers, allTestUsers.Count);
                for (int i = 0; i < numEmployees; i++)
                {
                    var employee = allTestUsers.First(u => u.UserPrincipalName == $"emp{i}_{timestamp}@test.com");
                    Assert.IsNotNull(employee.Manager, $"Employee {i} should have a manager");
                    Assert.AreEqual(managerUpns[i % numManagers], employee.Manager.UserPrincipalName);
                }
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_BugRepro_UntrackedManagerEntity_SameBatch_WorksCorrectly()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var employeeUpn = $"employee_untracked_mgr_test{timestamp}@test.com";
            var managerUpn = $"manager_untracked_test{timestamp}@test.com";
            var employeeId = Guid.NewGuid().ToString(); var managerId = Guid.NewGuid().ToString();

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName.Contains(timestamp.ToString())).ToListAsync();
                if (usersToClean.Any()) { cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync(); }
            }

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = employeeUpn, Id = employeeId, AccountEnabled = true, Mail = employeeUpn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = managerId } } },
                new GraphUser { UserPrincipalName = managerUpn, Id = managerId, AccountEnabled = true, Mail = managerUpn }
            };

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(graphUsers)).InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var allTestUsers = await verifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName.Contains(timestamp.ToString())).ToListAsync();
                Assert.AreEqual(2, allTestUsers.Count);
                Assert.AreEqual(managerUpn, allTestUsers.First(u => u.UserPrincipalName == employeeUpn).Manager.UserPrincipalName);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_BugRepro_MultipleRealBatches_NoDuplicateKey()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            const int numManagers = 100; const int numEmployees = 510;
            var graphUsers = new List<GraphUser>();
            var managerIds = new List<string>(); var managerUpns = new List<string>();

            for (int i = 0; i < numManagers; i++) { managerIds.Add(Guid.NewGuid().ToString()); managerUpns.Add($"mgr{i}_{timestamp}@bigtest.com"); }

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName.Contains(timestamp.ToString())).ToListAsync();
                if (usersToClean.Any()) { cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync(); }
            }

            for (int i = 0; i < numEmployees; i++)
            {
                int managerIndex = i % numManagers;
                graphUsers.Add(new GraphUser { UserPrincipalName = $"emp{i}_{timestamp}@bigtest.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true, Mail = $"emp{i}_{timestamp}@bigtest.com", ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = managerIds[managerIndex] } } });
            }
            for (int i = 0; i < numManagers; i++)
            {
                graphUsers.Add(new GraphUser { UserPrincipalName = managerUpns[i], Id = managerIds[i], AccountEnabled = true, Mail = managerUpns[i] });
            }

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(graphUsers)).InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var totalUsers = await verifyDb.users.Where(u => u.UserPrincipalName.Contains(timestamp.ToString())).CountAsync();
                Assert.AreEqual(numEmployees + numManagers, totalUsers);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_ManagerAadIdMismatch_NoDuplicateKeyError()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var timestamp = DateTime.Now.Ticks;
            var managerUpn = $"carlos_carter{timestamp}@contoso.com";
            var managerOldAadId = Guid.NewGuid().ToString();
            var managerNewAadId = Guid.NewGuid().ToString();
            var employeeId = Guid.NewGuid().ToString();
            var employeeUpn = $"employee_of_carlos{timestamp}@contoso.com";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var usersToClean = await cleanupDb.users.Where(u => u.UserPrincipalName == managerUpn || u.UserPrincipalName == employeeUpn).ToListAsync();
                if (usersToClean.Any()) { cleanupDb.users.RemoveRange(usersToClean); await cleanupDb.SaveChangesAsync(); }
            }

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(new List<GraphUser> { new GraphUser { UserPrincipalName = managerUpn, Id = managerOldAadId, AccountEnabled = true, Mail = managerUpn } })).InsertAndUpdateDatabaseFromExternalUsers();

            var step2Users = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = managerUpn, Id = managerNewAadId, AccountEnabled = true, Mail = managerUpn },
                new GraphUser { UserPrincipalName = employeeUpn, Id = employeeId, AccountEnabled = true, Mail = employeeUpn, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = managerNewAadId } } }
            };

            await new UserMetadataUpdater(telemetry, config, new FakeUserMetadataLoader(step2Users)).InsertAndUpdateDatabaseFromExternalUsers();

            using (var finalVerifyDb = new AnalyticsEntitiesContext())
            {
                var allTestUsers = await finalVerifyDb.users.Include(u => u.Manager).Where(u => u.UserPrincipalName == managerUpn || u.UserPrincipalName == employeeUpn).ToListAsync();
                Assert.AreEqual(2, allTestUsers.Count, "Carlos should NOT be duplicated despite AAD ID mismatch");
                Assert.AreEqual(managerUpn, allTestUsers.First(u => u.UserPrincipalName == employeeUpn).Manager.UserPrincipalName);
            }
        }

        #endregion
    }
}
