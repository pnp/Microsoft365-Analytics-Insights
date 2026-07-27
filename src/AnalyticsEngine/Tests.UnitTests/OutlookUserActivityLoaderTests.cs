using Common.Entities;
using Common.Entities.Config;
using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.Extensions.Logging;
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

        /// <summary>
        /// Optimisation A: the per-date upsert dirty-checks each existing row and only issues an UPDATE when a
        /// mapped value actually changed. Re-saving identical data (what happens for the finalized days re-fetched
        /// every run) must write nothing; changing a value must write and persist. No Graph involved.
        /// </summary>
        [TestMethod]
        public async Task SaveLoadedReportsToSql_DirtyCheck_SkipsUnchangedRows()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.LazyLoadingEnabled = false;

                var groupsCache = new NoUsersHaveGroupsUserGroupsCache(logger);
                var loader = new OutlookUserActivityLoader(null, groupsCache, new UserGroupsFilterModel("FakeGroup1;FakeGroup2"), logger);

                var testDate = DateTime.UtcNow.Date.AddDays(-4);
                var runId = DateTime.UtcNow.Ticks;
                var upns = Enumerable.Range(0, 3).Select(n => $"dirtycheck{n}_{runId}@unit.test".ToLower()).ToList();

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

                // Insert three new rows -> three DB writes.
                loader.LoadedReportPages.Clear();
                loader.LoadedReportPages.Add(testDate, BuildPage(5));
                await loader.SaveLoadedReportsToSql(userIdCache, userCache);
                Assert.AreEqual(3, loader.LastSaveDbWriteCount, "Initial insert should write all three rows");

                // Re-save identical data -> dirty-check should issue ZERO writes (this is the optimisation).
                loader.LoadedReportPages.Clear();
                loader.LoadedReportPages.Add(testDate, BuildPage(5));
                await loader.SaveLoadedReportsToSql(userIdCache, userCache);
                Assert.AreEqual(0, loader.LastSaveDbWriteCount, "Re-saving identical data must issue no UPDATEs");

                // Change a value -> the changed rows are written and the DB reflects the new value.
                loader.LoadedReportPages.Clear();
                loader.LoadedReportPages.Add(testDate, BuildPage(99));
                await loader.SaveLoadedReportsToSql(userIdCache, userCache);
                Assert.AreEqual(3, loader.LastSaveDbWriteCount, "Changed rows must be written");

                var userIds = await db.users.Where(u => upns.Contains(u.UserPrincipalName)).Select(u => u.ID).ToListAsync();
                var logs = await db.OutlookUsageActivityLogs.Where(l => userIds.Contains(l.UserID) && l.Date == testDate).ToListAsync();
                Assert.AreEqual(3, logs.Count, "Upsert must not duplicate rows");
                Assert.IsTrue(logs.All(l => l.ReadCount == 99), "Existing rows should be updated to the new value");
            }
        }

        /// <summary>
        /// Optimisation B (SQL half): a stored date old enough to be finalized in Graph is returned as skippable,
        /// but a recently-stored date (which can still change) is never returned. Pure DB, no Graph.
        /// </summary>
        [TestMethod]
        public async Task GetFinalizedStoredDatesToSkipAsync_IncludesFinalizedStored_ExcludesRecent()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.LazyLoadingEnabled = false;

                var groupsCache = new NoUsersHaveGroupsUserGroupsCache(logger);
                var loader = new OutlookUserActivityLoader(null, groupsCache, new UserGroupsFilterModel("FakeGroup1;FakeGroup2"), logger)
                {
                    RefreshableRecentDays = 3
                };

                var finalizedDate = DateTime.UtcNow.Date.AddDays(-6);   // stored + older than the recent window -> skippable
                var recentDate = DateTime.UtcNow.Date.AddDays(-2);      // stored but within the recent window -> never skipped

                var runId = DateTime.UtcNow.Ticks;
                var upn = $"skipdates_{runId}@unit.test".ToLower();
                var userIdCache = new ConcurrentLookupDbIdsCache();
                var userCache = new UserCache(db);

                loader.LoadedReportPages.Clear();
                loader.LoadedReportPages.Add(finalizedDate, new List<OutlookUserActivityUserDetail>
                {
                    new OutlookUserActivityUserDetail { UserPrincipalName = upn, LastActivityDateString = finalizedDate.ToString("yyyy-MM-dd"), ReadCount = 1 }
                });
                loader.LoadedReportPages.Add(recentDate, new List<OutlookUserActivityUserDetail>
                {
                    new OutlookUserActivityUserDetail { UserPrincipalName = upn, LastActivityDateString = recentDate.ToString("yyyy-MM-dd"), ReadCount = 1 }
                });
                await loader.SaveLoadedReportsToSql(userIdCache, userCache);

                var skip = await loader.GetFinalizedStoredDatesToSkipAsync(db, daysBackMax: 7);

                Assert.IsTrue(skip.Contains(finalizedDate), "A stored, finalized date should be skippable");
                Assert.IsFalse(skip.Contains(recentDate), "A date within the recent window must never be skipped, even if stored");
            }
        }

        /// <summary>
        /// Optimisation B (download half): PopulateLoadedReportPagesFromGraph must NOT fetch a date in the skip
        /// set, but must still fetch every other date in the window. The Graph fetch is overridden so this runs
        /// with no HTTP - proving the loaders are abstract enough to test the skip path without Graph.
        /// </summary>
        [TestMethod]
        public async Task PopulateLoadedReportPagesFromGraph_SkipsFinalizedDates_WithoutCallingGraph()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var groupsCache = new NoUsersHaveGroupsUserGroupsCache(logger);

            var loader = new RecordingOutlookLoader(groupsCache, new UserGroupsFilterModel("FakeGroup1;FakeGroup2"), logger,
                date => new List<OutlookUserActivityUserDetail>
                {
                    new OutlookUserActivityUserDetail { UserPrincipalName = $"u_{date:yyyyMMdd}@unit.test", LastActivityDateString = date.ToString("yyyy-MM-dd") }
                });

            const int daysBackMax = 7;
            var skippedDate = DateTime.UtcNow.Date.AddDays(-5);
            var datesToSkip = new HashSet<DateTime> { skippedDate };

            await loader.PopulateLoadedReportPagesFromGraph(daysBackMax, datesToSkip);

            CollectionAssert.DoesNotContain(loader.RequestedDates, skippedDate, "Skipped finalized date must not be fetched from Graph");
            Assert.IsFalse(loader.LoadedReportPages.Keys.Any(k => k.Date == skippedDate), "Skipped date must not be loaded");
            Assert.AreEqual(daysBackMax - 1, loader.RequestedDates.Count, "Exactly one day should be skipped");

            for (int d = 1; d <= daysBackMax; d++)
            {
                var expected = DateTime.UtcNow.Date.AddDays(-d);
                if (expected == skippedDate) continue;
                CollectionAssert.Contains(loader.RequestedDates, expected, $"Date {expected:yyyy-MM-dd} should have been fetched");
            }
        }

        // Test double: overrides the Graph fetch so the date-skipping logic can be verified with no HTTP.
        private class RecordingOutlookLoader : OutlookUserActivityLoader
        {
            public List<DateTime> RequestedDates { get; } = new List<DateTime>();
            private readonly Func<DateTime, List<OutlookUserActivityUserDetail>> _dataForDate;

            public RecordingOutlookLoader(UserGroupsCache groupsCache, UserGroupsFilterModel filter, ILogger logger,
                Func<DateTime, List<OutlookUserActivityUserDetail>> dataForDate)
                : base(null, groupsCache, filter, logger)
            {
                _dataForDate = dataForDate;
            }

            protected override Task<List<OutlookUserActivityUserDetail>> LoadReportPageForDateFromGraph(DateTime date)
            {
                RequestedDates.Add(date.Date);
                var data = _dataForDate?.Invoke(date.Date) ?? new List<OutlookUserActivityUserDetail>();
                return Task.FromResult(data);
            }
        }
    }
}
