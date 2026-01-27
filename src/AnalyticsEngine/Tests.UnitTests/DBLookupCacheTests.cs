using Common.Entities;
using Common.Entities.LookupCaches;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for DBLookupCache duplicate handling and resilience improvements
    /// </summary>
    [TestClass]
    public class DBLookupCacheTests
    {
        [TestMethod]
        public async Task UserDepartmentCache_NormalOperation_CreatesNewDepartment()
        {
            // Arrange
            string randomDeptName = $"TestDept_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserDepartmentCache(db);

                // Act
                var dept = await cache.GetOrCreateNewResource(
                    randomDeptName,
                    new UserDepartment { Name = randomDeptName },
                    commitChangeOnSaveNew: true);

                // Assert
                Assert.IsNotNull(dept);
                Assert.AreEqual(randomDeptName, dept.Name);
                Assert.IsTrue(dept.ID > 0, "Department should have been saved to database");

                // Verify it exists in database
                var dbDept = await db.UserDepartments.FirstOrDefaultAsync(d => d.Name == randomDeptName);
                Assert.IsNotNull(dbDept);
                Assert.AreEqual(dept.ID, dbDept.ID);
            }
        }

        [TestMethod]
        public async Task UserDepartmentCache_SecondCall_ReturnsCachedDepartment()
        {
            // Arrange
            string randomDeptName = $"TestDept_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserDepartmentCache(db);

                // Act - First call creates
                var dept1 = await cache.GetOrCreateNewResource(
                    randomDeptName,
                    new UserDepartment { Name = randomDeptName },
                    commitChangeOnSaveNew: true);

                // Act - Second call should return cached
                var dept2 = await cache.GetOrCreateNewResource(
                    randomDeptName,
                    new UserDepartment { Name = randomDeptName },
                    commitChangeOnSaveNew: true);

                // Assert
                Assert.AreSame(dept1, dept2, "Should return same cached instance");
                Assert.AreEqual(dept1.ID, dept2.ID);

                // Verify only one record in database
                var count = await db.UserDepartments.CountAsync(d => d.Name == randomDeptName);
                Assert.AreEqual(1, count, "Should only have one department in database");
            }
        }

        [TestMethod]
        public async Task OfficeLocationCache_NormalOperation_CreatesNewLocation()
        {
            // Arrange
            string randomLocationName = $"TestLocation_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new OfficeLocationCache(db);

                // Act
                var location = await cache.GetOrCreateNewResource(
                    randomLocationName,
                    new UserOfficeLocation { Name = randomLocationName },
                    commitChangeOnSaveNew: true);

                // Assert
                Assert.IsNotNull(location);
                Assert.AreEqual(randomLocationName, location.Name);
                Assert.IsTrue(location.ID > 0);
            }
        }

        [TestMethod]
        public async Task UserJobTitleCache_NormalOperation_CreatesNewJobTitle()
        {
            // Arrange
            string randomJobTitle = $"TestJobTitle_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserJobTitleCache(db);

                // Act
                var jobTitle = await cache.GetOrCreateNewResource(
                    randomJobTitle,
                    new UserJobTitle { Name = randomJobTitle },
                    commitChangeOnSaveNew: true);

                // Assert
                Assert.IsNotNull(jobTitle);
                Assert.AreEqual(randomJobTitle, jobTitle.Name);
                Assert.IsTrue(jobTitle.ID > 0);
            }
        }

        [TestMethod]
        public async Task UsageLocationCache_NormalOperation_CreatesNewLocation()
        {
            // Arrange
            string randomLocation = $"TestUsageLocation_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UsageLocationCache(db);

                // Act
                var location = await cache.GetOrCreateNewResource(
                    randomLocation,
                    new UserUsageLocation { Name = randomLocation },
                    commitChangeOnSaveNew: true);

                // Assert
                Assert.IsNotNull(location);
                Assert.AreEqual(randomLocation, location.Name);
                Assert.IsTrue(location.ID > 0);
            }
        }

        [TestMethod]
        public async Task Load_WithExistingDuplicates_ReturnsFirstByID()
        {
            // Arrange - Create duplicates directly in database
            string duplicateName = $"DuplicateDept_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                // Manually insert duplicates (bypassing cache to simulate existing issue)
                var dept1 = new UserDepartment { Name = duplicateName };
                var dept2 = new UserDepartment { Name = duplicateName };
                
                db.UserDepartments.Add(dept1);
                db.SaveChanges();
                
                // For this test, we need to handle the unique constraint
                // In real scenarios, duplicates might exist from before the constraint was enforced
                // or from race conditions. We'll test the Load method's resilience.
                
                var cache = new UserDepartmentCache(db);

                try
                {
                    // Try to add another with same name to test duplicate handling
                    db.UserDepartments.Add(dept2);
                    db.SaveChanges();
                }
                catch (DbUpdateException)
                {
                    // Expected if unique constraint exists - detach the failed entity
                    db.Entry(dept2).State = EntityState.Detached;
                }

                // Act - Load should handle any existing duplicates gracefully
                var loaded = await cache.Load(duplicateName);

                // Assert
                Assert.IsNotNull(loaded);
                Assert.AreEqual(duplicateName, loaded.Name);
                // Should return the one with lowest ID
                Assert.AreEqual(dept1.ID, loaded.ID);
            }
        }

        [TestMethod]
        public async Task Load_WithNonExistentName_ReturnsNull()
        {
            // Arrange
            string nonExistentName = $"NonExistent_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserDepartmentCache(db);

                // Act
                var result = await cache.Load(nonExistentName);

                // Assert
                Assert.IsNull(result);
            }
        }

        [TestMethod]
        public async Task GetOrCreateNewResource_WithWhitespace_TrimsKey()
        {
            // Arrange
            string baseName = $"TestDept_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            string nameWithSpaces = $"  {baseName}  ";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserDepartmentCache(db);

                // Act
                var dept1 = await cache.GetOrCreateNewResource(
                    nameWithSpaces,
                    new UserDepartment { Name = baseName },
                    commitChangeOnSaveNew: true);

                var dept2 = await cache.GetOrCreateNewResource(
                    baseName,
                    new UserDepartment { Name = baseName },
                    commitChangeOnSaveNew: true);

                // Assert
                Assert.AreSame(dept1, dept2, "Trimmed keys should match");
            }
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public async Task GetOrCreateNewResource_WithNullKey_ThrowsException()
        {
            // Arrange
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserDepartmentCache(db);

                // Act - should throw
                await cache.GetOrCreateNewResource(
                    null,
                    new UserDepartment { Name = "test" });
            }
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public async Task GetOrCreateNewResource_WithEmptyKey_ThrowsException()
        {
            // Arrange
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserDepartmentCache(db);

                // Act - should throw
                await cache.GetOrCreateNewResource(
                    string.Empty,
                    new UserDepartment { Name = "test" });
            }
        }

        [TestMethod]
        public async Task MultipleCache_WithSameContext_SharesLookups()
        {
            // Arrange
            string deptName = $"SharedDept_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            string locationName = $"SharedLocation_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var deptCache = new UserDepartmentCache(db);
                var locationCache = new OfficeLocationCache(db);

                // Act - Create entities through different caches
                var dept = await deptCache.GetOrCreateNewResource(
                    deptName,
                    new UserDepartment { Name = deptName },
                    commitChangeOnSaveNew: true);

                var location = await locationCache.GetOrCreateNewResource(
                    locationName,
                    new UserOfficeLocation { Name = locationName },
                    commitChangeOnSaveNew: true);

                // Assert
                Assert.IsNotNull(dept);
                Assert.IsNotNull(location);
                Assert.AreNotEqual(dept.ID, location.ID);
                
                // Verify both saved to database
                var savedDept = await db.UserDepartments.FindAsync(dept.ID);
                var savedLocation = await db.UserOfficeLocations.FindAsync(location.ID);
                
                Assert.IsNotNull(savedDept);
                Assert.IsNotNull(savedLocation);
            }
        }

        [TestMethod]
        public async Task AllLookupCaches_CanCreateAndRetrieve()
        {
            // Arrange
            string baseName = $"Test_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                // Act & Assert - Test all lookup cache types
                var deptCache = new UserDepartmentCache(db);
                var dept = await deptCache.GetOrCreateNewResource(
                    $"Dept_{baseName}",
                    new UserDepartment { Name = $"Dept_{baseName}" },
                    commitChangeOnSaveNew: true);
                Assert.IsNotNull(dept);

                var jobTitleCache = new UserJobTitleCache(db);
                var jobTitle = await jobTitleCache.GetOrCreateNewResource(
                    $"Title_{baseName}",
                    new UserJobTitle { Name = $"Title_{baseName}" },
                    commitChangeOnSaveNew: true);
                Assert.IsNotNull(jobTitle);

                var officeLocationCache = new OfficeLocationCache(db);
                var officeLocation = await officeLocationCache.GetOrCreateNewResource(
                    $"Office_{baseName}",
                    new UserOfficeLocation { Name = $"Office_{baseName}" },
                    commitChangeOnSaveNew: true);
                Assert.IsNotNull(officeLocation);

                var usageLocationCache = new UsageLocationCache(db);
                var usageLocation = await usageLocationCache.GetOrCreateNewResource(
                    $"Usage_{baseName}",
                    new UserUsageLocation { Name = $"Usage_{baseName}" },
                    commitChangeOnSaveNew: true);
                Assert.IsNotNull(usageLocation);

                var stateCache = new StateOrProvinceCache(db);
                var state = await stateCache.GetOrCreateNewResource(
                    $"State_{baseName}",
                    new StateOrProvince { Name = $"State_{baseName}" },
                    commitChangeOnSaveNew: true);
                Assert.IsNotNull(state);

                var countryCache = new CountryOrRegionCache(db);
                var country = await countryCache.GetOrCreateNewResource(
                    $"Country_{baseName}",
                    new CountryOrRegion { Name = $"Country_{baseName}" },
                    commitChangeOnSaveNew: true);
                Assert.IsNotNull(country);

                var companyCache = new CompanyNameCache(db);
                var company = await companyCache.GetOrCreateNewResource(
                    $"Company_{baseName}",
                    new CompanyName { Name = $"Company_{baseName}" },
                    commitChangeOnSaveNew: true);
                Assert.IsNotNull(company);
            }
        }

        [TestMethod]
        public async Task GetOrCreateNewResource_WithoutCommit_DoesNotSaveToDatabase()
        {
            // Arrange
            string deptName = $"UncommittedDept_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserDepartmentCache(db);

                // Act - Create without committing
                var dept = await cache.GetOrCreateNewResource(
                    deptName,
                    new UserDepartment { Name = deptName },
                    commitChangeOnSaveNew: false);

                // Assert
                Assert.IsNotNull(dept);
                Assert.AreEqual(0, dept.ID, "Should not have ID until saved");

                // Save explicitly
                await db.SaveChangesAsync();
                Assert.IsTrue(dept.ID > 0, "Should have ID after explicit save");
            }
        }

        [TestMethod]
        public async Task ConcurrentAccess_SameName_HandlesGracefully()
        {
            // This test simulates concurrent access by creating entries with the same name
            // through the cache mechanism
            string sharedName = $"ConcurrentDept_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache1 = new UserDepartmentCache(db);
                var cache2 = new UserDepartmentCache(db);

                // Act - Both caches try to create same department
                var dept1 = await cache1.GetOrCreateNewResource(
                    sharedName,
                    new UserDepartment { Name = sharedName },
                    commitChangeOnSaveNew: true);

                var dept2 = await cache2.GetOrCreateNewResource(
                    sharedName,
                    new UserDepartment { Name = sharedName },
                    commitChangeOnSaveNew: true);

                // Assert - Second should get from cache or database, not create duplicate
                Assert.IsNotNull(dept1);
                Assert.IsNotNull(dept2);
                
                // Count in database should be exactly 1
                var count = await db.UserDepartments.CountAsync(d => d.Name == sharedName);
                Assert.AreEqual(1, count, "Should not create duplicate entries");
            }
        }

        [TestMethod]
        public async Task Load_OrdersByID_ReturnsConsistentResults()
        {
            // Arrange
            string testName = $"OrderTest_{DateTime.Now.Ticks}_{Guid.NewGuid()}";
            
            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserDepartmentCache(db);
                
                // Create an entry
                var dept = await cache.GetOrCreateNewResource(
                    testName,
                    new UserDepartment { Name = testName },
                    commitChangeOnSaveNew: true);

                // Act - Load multiple times
                var loaded1 = await cache.Load(testName);
                var loaded2 = await cache.Load(testName);
                var loaded3 = await cache.Load(testName);

                // Assert - All loads should return same ID (consistency)
                Assert.AreEqual(dept.ID, loaded1.ID);
                Assert.AreEqual(dept.ID, loaded2.ID);
                Assert.AreEqual(dept.ID, loaded3.ID);
            }
        }
    }
}
