using Common.Entities;
using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Integration tests for batch processing scenarios with lookup caches
    /// These tests specifically validate the fix for FK constraint violations during large-scale user imports
    /// </summary>
    [TestClass]
    public class LookupCacheBatchProcessingTests
    {
        [TestMethod]
        public async Task BatchProcessing_WithRepeatedLookups_NoFKViolations()
        {
            // Arrange - Simulate processing users in batches with repeated department names
            var testRunId = DateTime.Now.Ticks;
            string sharedDeptName = $"Engineering_{testRunId}";
            string sharedLocationName = $"Seattle Office_{testRunId}";
            int batchSize = 50;
            int totalUsers = 200; // Simulate 4 batches

            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.AutoDetectChangesEnabled = false;

                var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
                var batchProcessor = new UserBatchProcessor(telemetry);
                var cache = new UserMetadataCache(db);
                
                int usersProcessed = 0;

                // Act - Process users in batches
                for (int batchStart = 0; batchStart < totalUsers; batchStart += batchSize)
                {
                    var batchUsers = new List<User>();

                    // Create users in batch
                    for (int i = 0; i < batchSize && (batchStart + i) < totalUsers; i++)
                    {
                        var user = new User
                        {
                            UserPrincipalName = $"user{batchStart + i}_{testRunId}@test.com",
                            AccountEnabled = true
                        };

                        // Assign same department and location to simulate real scenario
                        user.Department = await cache.DepartmentCache.GetOrCreateNewResource(
                            sharedDeptName,
                            new UserDepartment { Name = sharedDeptName });

                        user.OfficeLocation = await cache.OfficeLocationCache.GetOrCreateNewResource(
                            sharedLocationName,
                            new UserOfficeLocation { Name = sharedLocationName });

                        db.users.Add(user);
                        batchUsers.Add(user);
                    }

                    // Save batch
                    db.ChangeTracker.DetectChanges();
                    await db.SaveChangesAsync();
                    usersProcessed += batchUsers.Count;

                    // Clear change tracker EXCEPT lookups (this was the fix)
                    batchProcessor.DetachAllEntitiesExceptLookups(db);

                    // Verify lookups are still attached
                    var deptEntry = db.ChangeTracker.Entries<UserDepartment>()
                        .FirstOrDefault(e => e.Entity.Name == sharedDeptName);
                    var locationEntry = db.ChangeTracker.Entries<UserOfficeLocation>()
                        .FirstOrDefault(e => e.Entity.Name == sharedLocationName);

                    Assert.IsNotNull(deptEntry, "Department should remain attached after batch");
                    Assert.IsNotNull(locationEntry, "Location should remain attached after batch");
                    Assert.AreNotEqual(EntityState.Detached, deptEntry.State, "Department should not be detached");
                    Assert.AreNotEqual(EntityState.Detached, locationEntry.State, "Location should not be detached");
                }

                // Assert
                Assert.AreEqual(totalUsers, usersProcessed, "All users should be processed");

                // Verify only ONE department was created despite multiple batches
                var deptCount = await db.UserDepartments.CountAsync(d => d.Name == sharedDeptName);
                Assert.AreEqual(1, deptCount, "Should only have one department record");

                // Verify only ONE location was created
                var locationCount = await db.UserOfficeLocations.CountAsync(l => l.Name == sharedLocationName);
                Assert.AreEqual(1, locationCount, "Should only have one location record");

                // Verify all users reference the same lookups
                var userSearchPattern = $"_{testRunId}@test.com";
                var usersWithDept = await db.users
                    .Include(u => u.Department)
                    .Where(u => u.UserPrincipalName.Contains(userSearchPattern))
                    .ToListAsync();

                var uniqueDeptIds = usersWithDept
                    .Where(u => u.Department != null)
                    .Select(u => u.Department.ID)
                    .Distinct()
                    .Count();

                Assert.AreEqual(1, uniqueDeptIds, "All users should reference the same department ID");
            }
        }

        [TestMethod]
        public async Task BatchProcessing_WithOldDetachAllMethod_WouldCauseDuplicates()
        {
            // This test documents the OLD behavior (before fix) to show what we're preventing
            // Note: We can't actually test the old broken behavior without the unique constraint failing,
            // but we can demonstrate the detachment issue

            // Arrange
            string deptName = $"TestDept_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
                var batchProcessor = new UserBatchProcessor(telemetry);
                var cache = new UserMetadataCache(db);

                // Create department in first batch
                var dept1 = await cache.DepartmentCache.GetOrCreateNewResource(
                    deptName,
                    new UserDepartment { Name = deptName });
                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();

                // Verify department is tracked
                var entryBefore = db.ChangeTracker.Entries<UserDepartment>()
                    .FirstOrDefault(e => e.Entity.Name == deptName);
                Assert.IsNotNull(entryBefore);
                Assert.AreNotEqual(EntityState.Detached, entryBefore.State);

                // Act - Use OLD method that detaches everything (including lookups)
                batchProcessor.DetachAllEntities(db);

                // Assert - Department is now detached (this was the problem)
                var entryAfter = db.ChangeTracker.Entries<UserDepartment>()
                    .FirstOrDefault(e => e.Entity.Name == deptName);
                
                // After DetachAllEntities, entry should either be null or detached
                if (entryAfter != null)
                {
                    Assert.AreEqual(EntityState.Detached, entryAfter.State, 
                        "Old DetachAllEntities method detached lookups, causing FK violations");
                }
                
                // The cache still has the reference, but it's detached from context
                // This means next batch would try to insert it again -> FK violation
            }
        }

        [TestMethod]
        public async Task DetachAllEntitiesExceptLookups_PreservesAllLookupTypes()
        {
            // Arrange - Create one of each lookup type
            string baseName = $"Test_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
                var batchProcessor = new UserBatchProcessor(telemetry);
                var cache = new UserMetadataCache(db);

                // Create all lookup types
                var dept = await cache.DepartmentCache.GetOrCreateNewResource(
                    $"Dept_{baseName}", new UserDepartment { Name = $"Dept_{baseName}" });
                
                var jobTitle = await cache.JobTitleCache.GetOrCreateNewResource(
                    $"Title_{baseName}", new UserJobTitle { Name = $"Title_{baseName}" });
                
                var officeLocation = await cache.OfficeLocationCache.GetOrCreateNewResource(
                    $"Office_{baseName}", new UserOfficeLocation { Name = $"Office_{baseName}" });
                
                var usageLocation = await cache.UseageLocationCache.GetOrCreateNewResource(
                    $"Usage_{baseName}", new UserUsageLocation { Name = $"Usage_{baseName}" });
                
                var state = await cache.StateOrProvinceCache.GetOrCreateNewResource(
                    $"State_{baseName}", new StateOrProvince { Name = $"State_{baseName}" });
                
                var country = await cache.CountryOrRegionCache.GetOrCreateNewResource(
                    $"Country_{baseName}", new CountryOrRegion { Name = $"Country_{baseName}" });
                
                var company = await cache.CompanyNameCache.GetOrCreateNewResource(
                    $"Company_{baseName}", new CompanyName { Name = $"Company_{baseName}" });

                // Create a user to verify it gets detached
                var user = new User { UserPrincipalName = $"user_{baseName}@test.com" };
                db.users.Add(user);

                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();

                // Act - Detach all except lookups
                batchProcessor.DetachAllEntitiesExceptLookups(db);

                // Assert - All lookups should remain tracked
                var trackedEntities = db.ChangeTracker.Entries()
                    .Where(e => e.State != EntityState.Detached)
                    .ToList();

                Assert.IsTrue(trackedEntities.Any(e => e.Entity is UserDepartment), "UserDepartment should be tracked");
                Assert.IsTrue(trackedEntities.Any(e => e.Entity is UserJobTitle), "UserJobTitle should be tracked");
                Assert.IsTrue(trackedEntities.Any(e => e.Entity is UserOfficeLocation), "UserOfficeLocation should be tracked");
                Assert.IsTrue(trackedEntities.Any(e => e.Entity is UserUsageLocation), "UserUsageLocation should be tracked");
                Assert.IsTrue(trackedEntities.Any(e => e.Entity is StateOrProvince), "StateOrProvince should be tracked");
                Assert.IsTrue(trackedEntities.Any(e => e.Entity is CountryOrRegion), "CountryOrRegion should be tracked");
                Assert.IsTrue(trackedEntities.Any(e => e.Entity is CompanyName), "CompanyName should be tracked");

                // User should be detached
                Assert.IsFalse(trackedEntities.Any(e => e.Entity is User), "User should be detached");
            }
        }

        [TestMethod]
        public async Task LargeBatchSimulation_200kUsers_NoFKViolations()
        {
            // This test simulates a scaled-down version of the production scenario
            // Production: 200k users, Test: 1000 users (to keep test time reasonable)
            
            // Arrange
            int totalUsers = 1000;
            int batchSize = 100;
            var commonDepts = new[] { "Engineering", "Sales", "Marketing", "HR", "Finance" };
            var commonLocations = new[] { "Seattle", "New York", "London", "Tokyo", "Sydney" };
            string testRun = DateTime.Now.Ticks.ToString();

            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.AutoDetectChangesEnabled = false;

                var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
                var batchProcessor = new UserBatchProcessor(telemetry);
                var cache = new UserMetadataCache(db);
                
                var random = new Random();
                int totalProcessed = 0;

                // Act - Process in batches
                for (int batchStart = 0; batchStart < totalUsers; batchStart += batchSize)
                {
                    for (int i = 0; i < batchSize && (batchStart + i) < totalUsers; i++)
                    {
                        var userNum = batchStart + i;
                        var user = new User
                        {
                            UserPrincipalName = $"largetest_{testRun}_{userNum}@test.com",
                            AccountEnabled = true
                        };

                        // Randomly assign common departments and locations
                        var deptName = commonDepts[random.Next(commonDepts.Length)];
                        var locationName = commonLocations[random.Next(commonLocations.Length)];

                        user.Department = await cache.DepartmentCache.GetOrCreateNewResource(
                            deptName, new UserDepartment { Name = deptName });

                        user.OfficeLocation = await cache.OfficeLocationCache.GetOrCreateNewResource(
                            locationName, new UserOfficeLocation { Name = locationName });

                        db.users.Add(user);
                    }

                    db.ChangeTracker.DetectChanges();
                    await db.SaveChangesAsync();
                    totalProcessed += Math.Min(batchSize, totalUsers - batchStart);

                    // Critical: Use selective detachment
                    batchProcessor.DetachAllEntitiesExceptLookups(db);
                }

                // Assert
                Assert.AreEqual(totalUsers, totalProcessed);

                // Verify only 5 departments were created (not 1000)
                var deptCount = await db.UserDepartments
                    .CountAsync(d => commonDepts.Contains(d.Name));
                Assert.AreEqual(commonDepts.Length, deptCount, 
                    $"Should have exactly {commonDepts.Length} departments, not one per user");

                // Verify only 5 locations were created
                var locationCount = await db.UserOfficeLocations
                    .CountAsync(l => commonLocations.Contains(l.Name));
                Assert.AreEqual(commonLocations.Length, locationCount,
                    $"Should have exactly {commonLocations.Length} locations, not one per user");

                // Cleanup - Remove test users - evaluate search string before LINQ query
                var searchPrefix = $"largetest_{testRun}_";
                var testUsers = await db.users
                    .Where(u => u.UserPrincipalName.StartsWith(searchPrefix))
                    .ToListAsync();
                db.users.RemoveRange(testUsers);
                await db.SaveChangesAsync();
            }
        }

        [TestMethod]
        public async Task CacheConsistency_AcrossBatches_MaintainsReferences()
        {
            // Arrange
            string deptName = $"ConsistencyTest_{DateTime.Now.Ticks}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.AutoDetectChangesEnabled = false;

                var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
                var batchProcessor = new UserBatchProcessor(telemetry);
                var cache = new UserMetadataCache(db);
                
                // Batch 1
                var dept1 = await cache.DepartmentCache.GetOrCreateNewResource(
                    deptName, new UserDepartment { Name = deptName });
                await db.SaveChangesAsync();
                var dept1Id = dept1.ID;

                batchProcessor.DetachAllEntitiesExceptLookups(db);

                // Batch 2 - Should get same department from cache
                var dept2 = await cache.DepartmentCache.GetOrCreateNewResource(
                    deptName, new UserDepartment { Name = deptName });
                
                // Assert - Should be same instance (cached)
                Assert.AreSame(dept1, dept2, "Should return same cached instance across batches");
                Assert.AreEqual(dept1Id, dept2.ID, "IDs should match");

                // Verify only one in database
                var count = await db.UserDepartments.CountAsync(d => d.Name == deptName);
                Assert.AreEqual(1, count, "Should only have one database record");
            }
        }
    }
}
