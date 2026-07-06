using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Performance tests for UserMetadataUpdater measuring insert and update throughput
    /// </summary>
    [TestClass]
    public class UserMetadataUpdaterPerformanceTests
    {
        private static readonly string[] Departments = { "IT", "HR", "Finance", "Marketing", "Sales", "Engineering", "Legal", "Operations" };
        private static readonly string[] JobTitles = { "Developer", "Manager", "Analyst", "Director", "VP", "Engineer", "Consultant", "Lead" };
        private static readonly string[] Offices = { "Building A", "Building B", "Building C", "Remote", "HQ" };
        private static readonly string[] Countries = { "US", "UK", "DE", "FR", "JP" };
        private static readonly string[] States = { "WA", "CA", "NY", "TX", "IL" };
        private static readonly string[] Companies = { "Contoso", "Fabrikam", "Northwind" };

        /// <summary>
        /// Inserts 1000 users, then updates them with changed metadata.
        /// Measures throughput with tenant-level SKUs (bulk SQL update path).
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_Update1000ExistingUsers_WithSkus_Performance()
        {
            const int USER_COUNT = 1000;
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var testPrefix = $"perfsku{DateTime.Now.Ticks}";

            var graphUsers = GenerateGraphUsers(USER_COUNT, testPrefix);

            // Use non-null (empty) SKUs so the bulk update path is exercised
            var fakeSkus = new List<SubscribedSku>();

            try
            {
                // --- Phase 1: Insert ---
                var insertLoader = new FakeUserMetadataLoader(graphUsers, fakeSkus);
                var insertUpdater = new UserMetadataUpdater(logger, config, insertLoader);

                var insertSw = Stopwatch.StartNew();
                await insertUpdater.InsertAndUpdateDatabaseFromExternalUsers();
                insertSw.Stop();

                logger.LogInformation($"PERF: Insert {USER_COUNT} users took {insertSw.ElapsedMilliseconds}ms");

                // Verify insert
                using (var db = new AnalyticsEntitiesContext())
                {
                    var count = await db.users.CountAsync(u => u.UserPrincipalName.StartsWith(testPrefix));
                    Assert.AreEqual(USER_COUNT, count, "All users should be inserted");
                }

                // --- Phase 2: Re-generate users with modified metadata ---
                // (the original list was consumed by Phase 1 via allActiveGraphUsers.Clear())
                var updatedGraphUsers = GenerateGraphUsers(USER_COUNT, testPrefix);
                for (int i = 0; i < updatedGraphUsers.Count; i++)
                {
                    updatedGraphUsers[i].Department = Departments[(i + 1) % Departments.Length];
                    updatedGraphUsers[i].JobTitle = JobTitles[(i + 1) % JobTitles.Length];
                    updatedGraphUsers[i].PostalCode = $"{20000 + i}";
                    updatedGraphUsers[i].OfficeLocation = Offices[(i + 1) % Offices.Length];
                }

                var updateLoader = new FakeUserMetadataLoader(updatedGraphUsers, fakeSkus);
                var updateUpdater = new UserMetadataUpdater(logger, config, updateLoader);

                var updateSw = Stopwatch.StartNew();
                await updateUpdater.InsertAndUpdateDatabaseFromExternalUsers();
                updateSw.Stop();

                logger.LogInformation($"PERF: Update {USER_COUNT} existing users took {updateSw.ElapsedMilliseconds}ms");

                // Verify correctness
                using (var db = new AnalyticsEntitiesContext())
                {
                    var updatedUsers = await db.users
                        .Include(u => u.Department)
                        .Include(u => u.JobTitle)
                        .Include(u => u.OfficeLocation)
                        .Where(u => u.UserPrincipalName.StartsWith(testPrefix))
                        .ToListAsync();

                    Assert.AreEqual(USER_COUNT, updatedUsers.Count, "User count should not change");

                    var firstUser = updatedUsers.First(u => u.UserPrincipalName == $"{testPrefix}_user0@test.com");
                    Assert.AreEqual(Departments[1], firstUser.Department?.Name, "Department should be updated");
                    Assert.AreEqual(JobTitles[1], firstUser.JobTitle?.Name, "Job title should be updated");
                    Assert.AreEqual("20000", firstUser.PostalCode, "PostalCode should be updated");
                    Assert.AreEqual(Offices[1], firstUser.OfficeLocation?.Name, "OfficeLocation should be updated");
                }

                // Assert reasonable performance: 1000-user update should complete in under 60 seconds
                Assert.IsTrue(updateSw.ElapsedMilliseconds < 60000,
                    $"Update took {updateSw.ElapsedMilliseconds}ms, expected under 60000ms");

                logger.LogInformation($"=== Results: Insert={insertSw.ElapsedMilliseconds}ms, Update={updateSw.ElapsedMilliseconds}ms ===");
            }
            finally
            {
                await CleanupTestUsers(testPrefix);
            }
        }

        /// <summary>
        /// Inserts 1000 users, then updates them with changed metadata.
        /// Measures throughput without tenant-level SKUs (original EF per-entity path).
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_Update1000ExistingUsers_WithoutSkus_Performance()
        {
            const int USER_COUNT = 1000;
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var testPrefix = $"perfnosku{DateTime.Now.Ticks}";

            var graphUsers = GenerateGraphUsers(USER_COUNT, testPrefix);

            // Null SKUs → per-user processing (original EF path)
            try
            {
                // --- Phase 1: Insert ---
                var insertLoader = new FakeUserMetadataLoader(graphUsers);
                var insertUpdater = new UserMetadataUpdater(logger, config, insertLoader);

                var insertSw = Stopwatch.StartNew();
                await insertUpdater.InsertAndUpdateDatabaseFromExternalUsers();
                insertSw.Stop();

                logger.LogInformation($"PERF (no SKU): Insert {USER_COUNT} users took {insertSw.ElapsedMilliseconds}ms");

                // --- Phase 2: Re-generate users with modified metadata ---
                var updatedGraphUsers = GenerateGraphUsers(USER_COUNT, testPrefix);
                for (int i = 0; i < updatedGraphUsers.Count; i++)
                {
                    updatedGraphUsers[i].Department = Departments[(i + 1) % Departments.Length];
                    updatedGraphUsers[i].JobTitle = JobTitles[(i + 1) % JobTitles.Length];
                    updatedGraphUsers[i].PostalCode = $"{20000 + i}";
                }

                var updateLoader = new FakeUserMetadataLoader(updatedGraphUsers);
                var updateUpdater = new UserMetadataUpdater(logger, config, updateLoader);

                var updateSw = Stopwatch.StartNew();
                await updateUpdater.InsertAndUpdateDatabaseFromExternalUsers();
                updateSw.Stop();

                logger.LogInformation($"PERF (no SKU): Update {USER_COUNT} existing users took {updateSw.ElapsedMilliseconds}ms");

                // Verify correctness
                using (var db = new AnalyticsEntitiesContext())
                {
                    var updatedUsers = await db.users
                        .Include(u => u.Department)
                        .Include(u => u.JobTitle)
                        .Where(u => u.UserPrincipalName.StartsWith(testPrefix))
                        .ToListAsync();

                    Assert.AreEqual(USER_COUNT, updatedUsers.Count);

                    var firstUser = updatedUsers.First(u => u.UserPrincipalName == $"{testPrefix}_user0@test.com");
                    Assert.AreEqual(Departments[1], firstUser.Department?.Name);
                    Assert.AreEqual(JobTitles[1], firstUser.JobTitle?.Name);
                    Assert.AreEqual("20000", firstUser.PostalCode);
                }

                Assert.IsTrue(updateSw.ElapsedMilliseconds < 120000,
                    $"Update took {updateSw.ElapsedMilliseconds}ms, expected under 120000ms");

                logger.LogInformation($"=== Results (no SKU): Insert={insertSw.ElapsedMilliseconds}ms, Update={updateSw.ElapsedMilliseconds}ms ===");
            }
            finally
            {
                await CleanupTestUsers(testPrefix);
            }
        }

        private static List<GraphUser> GenerateGraphUsers(int count, string prefix)
        {
            var users = new List<GraphUser>(count);
            for (int i = 0; i < count; i++)
            {
                users.Add(new GraphUser
                {
                    UserPrincipalName = $"{prefix}_user{i}@test.com",
                    Id = Guid.NewGuid().ToString(),
                    AccountEnabled = true,
                    Mail = $"{prefix}_user{i}@test.com",
                    PostalCode = $"{10000 + i}",
                    Department = Departments[i % Departments.Length],
                    JobTitle = JobTitles[i % JobTitles.Length],
                    OfficeLocation = Offices[i % Offices.Length],
                    Country = Countries[i % Countries.Length],
                    State = States[i % States.Length],
                    CompanyName = Companies[i % Companies.Length],
                    UsageLocation = Countries[i % Countries.Length]
                });
            }
            return users;
        }

        private static async Task CleanupTestUsers(string prefix)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Delete license lookups first (FK constraint)
                await db.Database.ExecuteSqlCommandAsync(
                    $"DELETE FROM dbo.user_license_type_lookups WHERE user_id IN (SELECT id FROM dbo.users WHERE user_name LIKE '{prefix}%')");
                await db.Database.ExecuteSqlCommandAsync(
                    $"DELETE FROM dbo.users WHERE user_name LIKE '{prefix}%'");
            }
        }
    }
}
