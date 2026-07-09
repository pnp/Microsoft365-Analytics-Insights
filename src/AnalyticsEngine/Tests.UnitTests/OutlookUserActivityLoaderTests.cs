using Common.Entities;
using Common.Entities.Config;
using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace Tests.UnitTests
{
    [TestClass]
    public class OutlookUserActivityLoaderTests
    {
        [TestMethod]
        public async Task OutlookUserActivityLoader_SaveLoadedReportsToSql_BasicInsertTest()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            using (var db = new AnalyticsEntitiesContext())
            {
                // Ensure lazy loading not required for test logic
                db.Configuration.LazyLoadingEnabled = false;

                // Prepare loader (ManualGraphCallClient not needed because we will inject data directly)
                var groupsCache = new NoUsersHaveGroupsUserGroupsCache(logger);
                var loader = new OutlookUserActivityLoader(null, groupsCache, new UserGroupsFilterModel("FakeGroup1;FakeGroup2"), logger);

                // Fake single-day activity page for a unique user
                var testDate = DateTime.UtcNow.Date.AddDays(-2); // Use date unlikely to be current day to avoid partial real data
                var userUpn = $"outlookuser_{DateTime.UtcNow.Ticks}@unit.test".ToLower();

                var detail = new OutlookUserActivityUserDetail
                {
                    UserPrincipalName = userUpn,
                    LastActivityDateString = testDate.ToString("yyyy-MM-dd"),
                    ReadCount = 5,
                    ReceiveCount = 7,
                    SendCount = 3,
                    MeetingCreated = 2,
                    MeetingInteracted = 4
                };

                loader.LoadedReportPages.Clear();
                loader.LoadedReportPages.Add(testDate, new System.Collections.Generic.List<OutlookUserActivityUserDetail> { detail });

                var userIdCache = new ConcurrentLookupDbIdsCache();
                var userCache = new UserCache(db);

                // ACT 1: Save first time (should insert one log and one user)
                await loader.SaveLoadedReportsToSql(userIdCache, userCache);

                var insertedUser = await db.users.Where(u => u.UserPrincipalName == userUpn).SingleOrDefaultAsync();
                Assert.IsNotNull(insertedUser, "User should have been created");

                var logs = await db.OutlookUsageActivityLogs.Where(l => l.UserID == insertedUser.ID && l.Date == testDate).ToListAsync();
                Assert.AreEqual(1, logs.Count, "Exactly one log should be inserted for the day");

                var log = logs.Single();
                Assert.AreEqual(detail.ReadCount, log.ReadCount);
                Assert.AreEqual(detail.ReceiveCount, log.ReceiveCount);
                Assert.AreEqual(detail.SendCount, log.SendCount);
                Assert.AreEqual(detail.MeetingCreated, log.MeetingCreated);
                Assert.AreEqual(detail.MeetingInteracted, log.MeetingInteracted);
                Assert.AreEqual(testDate, log.Date);
                Assert.AreEqual(testDate, log.LastActivityDate, "LastActivityDate should parse correctly");

                // ACT 2: Save same data again (should not create duplicate log)
                await loader.SaveLoadedReportsToSql(userIdCache, userCache);
                var logsAfterSecondSave = await db.OutlookUsageActivityLogs.Where(l => l.UserID == insertedUser.ID && l.Date == testDate).ToListAsync();
                Assert.AreEqual(1, logsAfterSecondSave.Count, "Second save should not create duplicate log");

                // ACT 3: Add new day for same user and save again (should insert second log)
                var secondDate = testDate.AddDays(-1);
                var detail2 = new OutlookUserActivityUserDetail
                {
                    UserPrincipalName = userUpn,
                    LastActivityDateString = secondDate.ToString("yyyy-MM-dd"),
                    ReadCount = 10,
                    ReceiveCount = 11,
                    SendCount = 12,
                    MeetingCreated = 1,
                    MeetingInteracted = 0
                };
                loader.LoadedReportPages.Add(secondDate, new System.Collections.Generic.List<OutlookUserActivityUserDetail> { detail2 });
                await loader.SaveLoadedReportsToSql(userIdCache, userCache);

                var allLogs = await db.OutlookUsageActivityLogs.Where(l => l.UserID == insertedUser.ID && (l.Date == testDate || l.Date == secondDate)).ToListAsync();
                Assert.AreEqual(2, allLogs.Count, "Should have logs for two distinct dates");

                var secondLog = allLogs.Single(l => l.Date == secondDate);
                Assert.AreEqual(detail2.SendCount, secondLog.SendCount);
                Assert.AreEqual(detail2.ReceiveCount, secondLog.ReceiveCount);
                Assert.AreEqual(detail2.ReadCount, secondLog.ReadCount);
            }
        }

        /// <summary>
        /// The batched, per-date upsert in SaveLoadedReportsToSql must still insert every row and update
        /// (not duplicate) on re-save, even when a day's rows span multiple SaveChanges batches. This is the
        /// behaviour that replaced the single all-rows-at-once SaveChangesAsync that OutOfMemoryExceptioned at
        /// ~200k-user scale.
        /// </summary>
        [TestMethod]
        public async Task OutlookUserActivityLoader_SaveLoadedReportsToSql_BatchingUpsertsAcrossBoundary()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.LazyLoadingEnabled = false;

                var groupsCache = new NoUsersHaveGroupsUserGroupsCache(logger);
                var loader = new OutlookUserActivityLoader(null, groupsCache, new UserGroupsFilterModel("FakeGroup1;FakeGroup2"), logger);
                loader.SaveBatchSize = 2;   // force several batches for the five rows below (2 + 2 + 1)

                var testDate = DateTime.UtcNow.Date.AddDays(-3);
                var runId = DateTime.UtcNow.Ticks;
                var upns = Enumerable.Range(0, 5).Select(n => $"batchuser{n}_{runId}@unit.test".ToLower()).ToList();

                List<OutlookUserActivityUserDetail> BuildPage(int readCount) => upns.Select(u => new OutlookUserActivityUserDetail
                {
                    UserPrincipalName = u,
                    LastActivityDateString = testDate.ToString("yyyy-MM-dd"),
                    ReadCount = readCount,
                    ReceiveCount = 7,
                    SendCount = 3,
                }).ToList();

                var userIdCache = new ConcurrentLookupDbIdsCache();
                var userCache = new UserCache(db);

                // ACT 1: insert five rows across three batches
                loader.LoadedReportPages.Clear();
                loader.LoadedReportPages.Add(testDate, BuildPage(5));
                await loader.SaveLoadedReportsToSql(userIdCache, userCache);

                var userIds = await db.users.Where(u => upns.Contains(u.UserPrincipalName)).Select(u => u.ID).ToListAsync();
                Assert.AreEqual(5, userIds.Count, "All five users should be created");

                var logs = await db.OutlookUsageActivityLogs.Where(l => userIds.Contains(l.UserID) && l.Date == testDate).ToListAsync();
                Assert.AreEqual(5, logs.Count, "Five logs should be inserted across batch boundaries");
                Assert.IsTrue(logs.All(l => l.ReadCount == 5), "Inserted values should match");

                // ACT 2: re-save with changed stats -> update (not duplicate) across batches
                loader.LoadedReportPages.Clear();
                loader.LoadedReportPages.Add(testDate, BuildPage(99));
                await loader.SaveLoadedReportsToSql(userIdCache, userCache);

                var logsAfter = await db.OutlookUsageActivityLogs.Where(l => userIds.Contains(l.UserID) && l.Date == testDate).ToListAsync();
                Assert.AreEqual(5, logsAfter.Count, "Re-saving must update, not duplicate, across batches");
                Assert.IsTrue(logsAfter.All(l => l.ReadCount == 99), "Existing rows should be updated to the new values");
            }
        }
    }
}
