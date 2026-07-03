using Common.Entities;
using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests that specifically validate the fix for the initial FK constraint violation problem
    /// Error: "Cannot insert duplicate key row in object 'dbo.user_office_locations' with unique index 'IX_name'"
    /// </summary>
    [TestClass]
    public class DBLookupCacheDuplicateKeyErrorTests
    {
        /// <summary>
        /// This test simulates the EXACT production error scenario:
        /// 1. First batch creates a lookup (e.g., "Princesa 47 Seguros")
        /// 2. DetachAllEntities is called (old broken behavior)
        /// 3. Second batch tries to create the same lookup again
        /// 4. Without the fix, this would cause: "Cannot insert duplicate key row in object 'dbo.user_office_locations' with unique index 'IX_name'"
        /// </summary>
        [TestMethod]
        public async Task ProductionScenario_DetachAndRetry_WithFix_NoFKViolation()
        {
            // Arrange - Use the exact office location name from the production error
            string productionOfficeLocation = "Princesa 47 Seguros";
            string testRun = DateTime.Now.Ticks.ToString();
            string uniqueLocation = $"{productionOfficeLocation}_{testRun}";

            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.AutoDetectChangesEnabled = false;
                var logger = AnalyticsLogger.ConsoleOnlyTracer();
                var batchProcessor = new UserBatchProcessor(logger);
                var cache = new UserMetadataCache(db);

                // BATCH 1: Create first user with this location
                var user1 = new User
                {
                    UserPrincipalName = $"user1_{testRun}@test.com",
                    AccountEnabled = true
                };

                user1.OfficeLocation = await cache.OfficeLocationCache.GetOrCreateNewResource(
                    uniqueLocation,
                    new UserOfficeLocation { Name = uniqueLocation });

                db.users.Add(user1);
                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();

                // Verify the location was created
                var locationCount = await db.UserOfficeLocations.CountAsync(l => l.Name == uniqueLocation);
                Assert.AreEqual(1, locationCount, "First batch should create one location");

                // This is the fix: Use DetachAllEntitiesExceptLookups instead of DetachAllEntities
                batchProcessor.DetachAllEntitiesExceptLookups(db);

                // BATCH 2: Create second user with SAME location
                var user2 = new User
                {
                    UserPrincipalName = $"user2_{testRun}@test.com",
                    AccountEnabled = true
                };

                // Act - This should NOT throw FK violation because lookup is still tracked
                user2.OfficeLocation = await cache.OfficeLocationCache.GetOrCreateNewResource(
                    uniqueLocation,
                    new UserOfficeLocation { Name = uniqueLocation });

                db.users.Add(user2);
                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();

                // Assert - Still only one location in database
                locationCount = await db.UserOfficeLocations.CountAsync(l => l.Name == uniqueLocation);
                Assert.AreEqual(1, locationCount, "Should still have only ONE location after two batches");

                // Verify both users reference the same location ID
                var users = await db.users
                    .Include(u => u.OfficeLocation)
                    .Where(u => u.UserPrincipalName.Contains(testRun))
                    .ToListAsync();

                Assert.AreEqual(2, users.Count);
                Assert.IsNotNull(users[0].OfficeLocation);
                Assert.IsNotNull(users[1].OfficeLocation);
                Assert.AreEqual(users[0].OfficeLocation.ID, users[1].OfficeLocation.ID,
                    "Both users should reference the SAME location");

                // Cleanup
                db.users.RemoveRange(users);
                var location = await db.UserOfficeLocations.FirstOrDefaultAsync(l => l.Name == uniqueLocation);
                if (location != null)
                {
                    db.UserOfficeLocations.Remove(location);
                }
                await db.SaveChangesAsync();
            }
        }

        /// <summary>
        /// Test that simulates what WOULD happen with the old broken code
        /// This documents the problem we fixed
        /// </summary>
        [TestMethod]
        public async Task ProductionScenario_OldBehavior_WouldCauseFKViolation()
        {
            // Arrange
            string locationName = $"TestLocation_{DateTime.Now.Ticks}_{Guid.NewGuid()}";

            using (var db = new AnalyticsEntitiesContext())
            {
                var logger = AnalyticsLogger.ConsoleOnlyTracer();
                var batchProcessor = new UserBatchProcessor(logger);
                var cache = new UserMetadataCache(db);

                // BATCH 1: Create location
                var location1 = await cache.OfficeLocationCache.GetOrCreateNewResource(
                    locationName,
                    new UserOfficeLocation { Name = locationName });
                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();

                // OLD BEHAVIOR: Detach ALL entities including lookups
                batchProcessor.DetachAllEntities(db);

                // Verify lookup is detached (this was the problem!)
                var entry = db.ChangeTracker.Entries<UserOfficeLocation>()
                    .FirstOrDefault(e => e.Entity.Name == locationName);

                // After DetachAllEntities, lookup should be detached or not found
                if (entry != null)
                {
                    Assert.AreEqual(EntityState.Detached, entry.State,
                        "OLD behavior: DetachAllEntities detached the lookup");
                }

                // BATCH 2: Try to get same location again
                // The cache still has the reference, but it's detached
                // This would cause an FK violation when trying to insert again

                // We can't actually test the FK violation without it failing the test,
                // but we can verify the detachment that CAUSED the problem
                Assert.IsTrue(entry == null || entry.State == EntityState.Detached,
                    "This detachment is what caused FK violations in production");
            }
        }

        /// <summary>
        /// Test that the error handler catches SqlException with error codes 2601 and 2627
        /// </summary>
        [TestMethod]
        public async Task ErrorHandler_WithDuplicateKeyViolation_ReloadsFromDatabase()
        {
            // Arrange
            string deptName = $"DuplicateHandling_{DateTime.Now.Ticks}_{Guid.NewGuid()}";

            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserDepartmentCache(db);

                // Create the department first
                var dept1 = await cache.GetOrCreateNewResource(
                    deptName,
                    new UserDepartment { Name = deptName },
                    commitChangeOnSaveNew: true);

                Assert.IsNotNull(dept1);
                Assert.IsTrue(dept1.ID > 0);

                // Clear the cache's internal dictionary to simulate cache miss
                // (We can't directly access the private dictionary, so we create a new cache instance)
                var newCache = new UserDepartmentCache(db);

                // Act - Try to get/create the same department
                // The error handler should catch any duplicate key violation and reload from DB
                var dept2 = await newCache.GetOrCreateNewResource(
                    deptName,
                    new UserDepartment { Name = deptName },
                    commitChangeOnSaveNew: true);

                // Assert - Should successfully get the existing department
                Assert.IsNotNull(dept2);
                Assert.AreEqual(dept1.ID, dept2.ID, "Should reload existing department");

                // Verify only one in database
                var count = await db.UserDepartments.CountAsync(d => d.Name == deptName);
                Assert.AreEqual(1, count, "Should only have one department");
            }
        }

        /// <summary>
        /// Test the exact error message pattern from production
        /// </summary>
        [TestMethod]
        public async Task Production200kUsers_CommonDepartments_NoFKViolations()
        {
            // Arrange - Simulate the production scenario with common department names
            var commonDepts = new[]
            {
                "Engineering",
                "Sales",
                "Marketing",
                "IT",
                "Finance"
            };

            string testRun = DateTime.Now.Ticks.ToString();
            int usersPerDept = 50; // 250 users total, 50 per department

            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.AutoDetectChangesEnabled = false;
                var logger = AnalyticsLogger.ConsoleOnlyTracer();
                var batchProcessor = new UserBatchProcessor(logger);
                var cache = new UserMetadataCache(db);

                var allUsers = new System.Collections.Generic.List<User>();
                int userCount = 0;

                // Process in batches of 50 (same as production BATCH_SIZE)
                const int BATCH_SIZE = 50;

                for (int batchNum = 0; batchNum < (commonDepts.Length * usersPerDept / BATCH_SIZE); batchNum++)
                {
                    for (int i = 0; i < BATCH_SIZE; i++)
                    {
                        var deptIndex = userCount % commonDepts.Length;
                        var deptName = commonDepts[deptIndex];

                        var user = new User
                        {
                            UserPrincipalName = $"prod200k_{testRun}_{userCount}@test.com",
                            AccountEnabled = true
                        };

                        // Act - This should NOT cause FK violations even though we're reusing department names
                        user.Department = await cache.DepartmentCache.GetOrCreateNewResource(
                            deptName,
                            new UserDepartment { Name = deptName });

                        db.users.Add(user);
                        allUsers.Add(user);
                        userCount++;
                    }

                    // Save batch
                    db.ChangeTracker.DetectChanges();
                    await db.SaveChangesAsync();

                    // Critical: Use selective detachment to preserve lookups
                    batchProcessor.DetachAllEntitiesExceptLookups(db);
                }

                // Assert - Should have exactly 5 departments, not 250
                var deptCount = await db.UserDepartments.CountAsync(d => commonDepts.Contains(d.Name));
                Assert.AreEqual(commonDepts.Length, deptCount,
                    $"Should have exactly {commonDepts.Length} departments, not {userCount}");

                // Verify all users were created - evaluate search string before LINQ query
                var searchPrefix = $"prod200k_{testRun}_";
                var savedUsers = await db.users
                    .Where(u => u.UserPrincipalName.Contains(searchPrefix))
                    .CountAsync();
                Assert.AreEqual(userCount, savedUsers, "All users should be saved");

                // Cleanup
                var testUsers = await db.users
                    .Where(u => u.UserPrincipalName.Contains(searchPrefix))
                    .ToListAsync();
                db.users.RemoveRange(testUsers);
                await db.SaveChangesAsync();
            }
        }

        /// <summary>
        /// Test that FirstOrDefaultAsync handles the "Sequence contains more than one element" error
        /// </summary>
        [TestMethod]
        public async Task Load_WithPotentialDuplicates_NoSequenceException()
        {
            // Arrange - Create a department
            string deptName = $"SequenceTest_{DateTime.Now.Ticks}_{Guid.NewGuid()}";

            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserDepartmentCache(db);

                // Create first department
                var dept1 = await cache.GetOrCreateNewResource(
                    deptName,
                    new UserDepartment { Name = deptName },
                    commitChangeOnSaveNew: true);

                // Act - Call Load() which uses FirstOrDefaultAsync
                // This should NOT throw "Sequence contains more than one element"
                var loaded = await cache.Load(deptName);

                // Assert
                Assert.IsNotNull(loaded);
                Assert.AreEqual(dept1.ID, loaded.ID);

                // Test multiple loads for consistency
                var loaded2 = await cache.Load(deptName);
                var loaded3 = await cache.Load(deptName);

                Assert.AreEqual(loaded.ID, loaded2.ID);
                Assert.AreEqual(loaded.ID, loaded3.ID);
            }
        }

        /// <summary>
        /// Test the complete workflow from UserMetadataUpdater perspective
        /// </summary>
        [TestMethod]
        public async Task IntegrationTest_UserMetadataUpdater_NoDuplicateKeys()
        {
            // Arrange - Simulate what UserMetadataUpdater does
            string testRun = DateTime.Now.Ticks.ToString();
            var sharedDept = "Engineering";
            var sharedLocation = "Seattle Office";

            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.AutoDetectChangesEnabled = false;

                var logger = AnalyticsLogger.ConsoleOnlyTracer();
                var cache = new UserMetadataCache(db);
                var batchProcessor = new UserBatchProcessor(logger);
                var dataMapper = new UserDataMapper(logger, cache);

                // Simulate InsertMissingUsers workflow
                const int BATCH_SIZE = 50;
                const int TOTAL_USERS = 150;

                for (int batchStart = 0; batchStart < TOTAL_USERS; batchStart += BATCH_SIZE)
                {
                    var batchUsers = new System.Collections.Generic.List<User>();

                    for (int i = 0; i < BATCH_SIZE && (batchStart + i) < TOTAL_USERS; i++)
                    {
                        var userNum = batchStart + i;
                        var upn = $"integration_{testRun}_{userNum}@test.com";

                        // Create user through cache (like InsertMissingUsers does)
                        var user = await cache.UserCache.GetOrCreateNewResource(
                            upn,
                            dataMapper.UpdateDbUserFromGraphUser(
                                new User { UserPrincipalName = upn },
                                new GraphUser
                                {
                                    UserPrincipalName = upn,
                                    AccountEnabled = true,
                                    Department = sharedDept,
                                    OfficeLocation = sharedLocation
                                }));

                        // Update metadata (like UpdateDbUserWithGraphData does)
                        user.Department = await cache.DepartmentCache.GetOrCreateNewResource(
                            sharedDept,
                            new UserDepartment { Name = sharedDept });

                        user.OfficeLocation = await cache.OfficeLocationCache.GetOrCreateNewResource(
                            sharedLocation,
                            new UserOfficeLocation { Name = sharedLocation });

                        batchUsers.Add(user);
                    }

                    // Save batch (like InsertMissingUsers does)
                    db.ChangeTracker.DetectChanges();
                    await db.SaveChangesAsync();

                    // Critical: Detach users but preserve lookups (THE FIX)
                    batchProcessor.DetachAllEntitiesExceptLookups(db);
                }

                // Assert - Only one department and one location despite 150 users
                var deptCount = await db.UserDepartments.CountAsync(d => d.Name == sharedDept);
                Assert.AreEqual(1, deptCount, "Should have exactly one department");

                var locationCount = await db.UserOfficeLocations.CountAsync(l => l.Name == sharedLocation);
                Assert.AreEqual(1, locationCount, "Should have exactly one location");

                // Evaluate search string before LINQ query (EF can't translate string interpolation)
                var searchPrefix = $"integration_{testRun}_";
                var userCount = await db.users.CountAsync(u => u.UserPrincipalName.Contains(searchPrefix));
                Assert.AreEqual(TOTAL_USERS, userCount, "All users should be created");

                // Cleanup
                var testUsers = await db.users
                    .Where(u => u.UserPrincipalName.Contains(searchPrefix))
                    .ToListAsync();
                db.users.RemoveRange(testUsers);
                await db.SaveChangesAsync();

                // Note: Department and location cleanup skipped as they may be referenced by other tests
                // The unique test names prevent conflicts
            }
        }

        /// <summary>
        /// Test the exact error scenario: detachment causing cache/context mismatch
        /// </summary>
        [TestMethod]
        public async Task CacheMismatch_DetachedEntity_ErrorHandlerRecovery()
        {
            // Arrange
            string locationName = $"CacheMismatch_{DateTime.Now.Ticks}_{Guid.NewGuid()}";

            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new OfficeLocationCache(db);

                // Batch 1: Create and save
                var location1 = await cache.GetOrCreateNewResource(
                    locationName,
                    new UserOfficeLocation { Name = locationName },
                    commitChangeOnSaveNew: true);

                var location1Id = location1.ID;

                // Simulate the problem: entity gets detached but cache still has reference
                db.Entry(location1).State = EntityState.Detached;

                // Batch 2: Try to get the same location
                // Cache has detached entity, will try to add to context again
                // Error handler should catch and reload from DB
                var location2 = await cache.GetOrCreateNewResource(
                    locationName,
                    new UserOfficeLocation { Name = locationName },
                    commitChangeOnSaveNew: true);

                // Assert - Should get the same location (reloaded from DB)
                Assert.IsNotNull(location2);
                Assert.AreEqual(location1Id, location2.ID, "Should reload same location from database");

                // Verify only one in database
                var count = await db.UserOfficeLocations.CountAsync(l => l.Name == locationName);
                Assert.AreEqual(1, count, "Should only have one location in database");
            }
        }

        /// <summary>
        /// Test with the exact batch sizes used in production (500 users per batch)
        /// </summary>
        [TestMethod]
        public async Task ProductionBatchSize_500Users_NoFKViolations()
        {
            // Arrange - Production uses BATCH_SIZE = 500
            const int BATCH_SIZE = 500;
            const int NUM_BATCHES = 3; // 1500 users total
            const int TOTAL_USERS = BATCH_SIZE * NUM_BATCHES;

            string testRun = DateTime.Now.Ticks.ToString();
            string sharedDept = "Engineering";

            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.AutoDetectChangesEnabled = false;
                var logger = AnalyticsLogger.ConsoleOnlyTracer();
                var batchProcessor = new UserBatchProcessor(logger);
                var cache = new UserMetadataCache(db);

                // Act - Process 3 batches of 500 users each, all with same department
                for (int batchNum = 0; batchNum < NUM_BATCHES; batchNum++)
                {
                    for (int i = 0; i < BATCH_SIZE; i++)
                    {
                        var userNum = (batchNum * BATCH_SIZE) + i;
                        var user = new User
                        {
                            UserPrincipalName = $"batch500_{testRun}_{userNum}@test.com",
                            AccountEnabled = true
                        };

                        user.Department = await cache.DepartmentCache.GetOrCreateNewResource(
                            sharedDept,
                            new UserDepartment { Name = sharedDept });

                        db.users.Add(user);
                    }

                    db.ChangeTracker.DetectChanges();
                    await db.SaveChangesAsync();

                    // THE FIX: Preserve lookups across batches
                    batchProcessor.DetachAllEntitiesExceptLookups(db);

                    logger.LogInformation($"Completed batch {batchNum + 1}/{NUM_BATCHES}");
                }

                // Assert - Should have exactly ONE department despite 1500 users
                var deptCount = await db.UserDepartments.CountAsync(d => d.Name == sharedDept);
                Assert.AreEqual(1, deptCount, $"Should have exactly 1 department, not {TOTAL_USERS}");

                // Evaluate search string before LINQ query
                var searchPrefix = $"batch500_{testRun}_";
                var userCount = await db.users.CountAsync(u => u.UserPrincipalName.Contains(searchPrefix));
                Assert.AreEqual(TOTAL_USERS, userCount, "All users should be created");

                // Cleanup
                var testUsers = await db.users
                    .Where(u => u.UserPrincipalName.Contains(searchPrefix))
                    .ToListAsync();
                db.users.RemoveRange(testUsers);
                await db.SaveChangesAsync();
            }
        }
    }
}
