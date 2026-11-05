using Common.Entities;
using Common.Entities.Entities.UsageReports;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;
using Configuration = Common.Entities.Migrations.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// <see cref="SqlCopilotUsagePersistenceManager"/> against a throwaway database with every migration applied, because
    /// the behaviour under test is EF6's: what it does with a second row for a key whose first row is still queued in the
    /// same save batch. Synthetic contoso.com users only.
    /// </summary>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class CopilotUsagePersistenceSqlTests
    {
        private static readonly DateTime RefreshDate = new DateTime(2031, 3, 19);
        private static ScratchDatabase _database;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _database = ScratchDatabase.Create("CopilotUsage");
            new DbMigrator(new Configuration
            {
                TargetDatabase = new DbConnectionInfo(_database.ConnectionString, "Microsoft.Data.SqlClient"),
            }).Update();
        }

        [ClassCleanup]
        public static void DropDatabase()
        {
            SqlConnection.ClearAllPools();
            _database?.Dispose();
        }

        private static AnalyticsEntitiesContext NewContext() => new AnalyticsEntitiesContext(_database.ConnectionString, true, false);

        private static async Task<IReadOnlyDictionary<string, int>> SeedUserAsync(string upn)
        {
            using (var db = NewContext())
            {
                var user = new User { UserPrincipalName = upn };
                db.users.Add(user);
                await db.SaveChangesAsync();
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [upn] = user.ID };
            }
        }

        private static CopilotUsageUserDetailRow DetailRow(string upn, int periodDays, int prompts) => new CopilotUsageUserDetailRow
        {
            ReportRefreshDate = RefreshDate,
            UserPrincipalName = upn,
            ReportPeriodDays = periodDays,
            LastActivityDate = RefreshDate.AddDays(-1),
            PromptsAllApps = prompts,
            ActiveUsageDays = 3,
            ReportVersion = CopilotReportVersions.V2,
        };

        private static async Task<CopilotUsageUpsertResult> UpsertDetailAsync(IReadOnlyDictionary<string, int> ids, params CopilotUsageUserDetailRow[] rows)
        {
            using (var db = NewContext())
            {
                return await new SqlCopilotUsagePersistenceManager(db, NullLogger.Instance).UpsertUserDetailAsync(rows, ids, hasVersion2Data: true);
            }
        }

        private static async Task<List<CopilotUsageUserActivityLog>> StoredDetailAsync(int userId)
        {
            using (var db = NewContext())
            {
                return await db.CopilotUsageUserActivityLogs.AsNoTracking().Where(l => l.UserID == userId).OrderBy(l => l.ReportPeriodDays).ToListAsync();
            }
        }

        /// <summary>
        /// R9-2: two rows for one new (user, date, period) in one batch used to set the queued INSERT to Modified, which EF6
        /// sends as an UPDATE of a row that doesn't exist - a DbUpdateConcurrencyException that failed the whole report.
        /// </summary>
        [TestMethod]
        public async Task UserDetail_TwoRowsForOneNewUserDateAndPeriod_KeepTheLastRowInsteadOfFailingTheReport()
        {
            var upn = $"copilot-usage-{Guid.NewGuid():N}@contoso.com";
            var ids = await SeedUserAsync(upn);

            var result = await UpsertDetailAsync(ids, DetailRow(upn, 28, prompts: 5), DetailRow(upn, 28, prompts: 9));

            Assert.AreEqual(1, result.Inserted);
            Assert.AreEqual(0, result.Updated);
            Assert.AreEqual(1, result.DuplicateRows);
            var stored = await StoredDetailAsync(ids[upn]);
            Assert.AreEqual(1, stored.Count, "One row per user, date and period.");
            Assert.AreEqual(9, stored[0].PromptsAllApps, "The last row wins.");
        }

        [TestMethod]
        public async Task UserDetail_TwoRowsChangingAStoredRowInOneBatch_UpdateItOnceWithTheLastRow()
        {
            var upn = $"copilot-usage-{Guid.NewGuid():N}@contoso.com";
            var ids = await SeedUserAsync(upn);
            await UpsertDetailAsync(ids, DetailRow(upn, 28, prompts: 1));

            var result = await UpsertDetailAsync(ids, DetailRow(upn, 28, prompts: 5), DetailRow(upn, 28, prompts: 9));

            Assert.AreEqual(0, result.Inserted);
            Assert.AreEqual(1, result.Updated);
            Assert.AreEqual(1, result.DuplicateRows);
            Assert.AreEqual(9, (await StoredDetailAsync(ids[upn])).Single().PromptsAllApps);
        }

        /// <summary>The same user and date in two periods are two facts, not a duplicate.</summary>
        [TestMethod]
        public async Task UserDetail_OneUserAndDateInTwoPeriods_AreBothInserted()
        {
            var upn = $"copilot-usage-{Guid.NewGuid():N}@contoso.com";
            var ids = await SeedUserAsync(upn);

            var result = await UpsertDetailAsync(ids, DetailRow(upn, 7, prompts: 2), DetailRow(upn, 28, prompts: 9));

            Assert.AreEqual(2, result.Inserted);
            Assert.AreEqual(0, result.DuplicateRows);
            CollectionAssert.AreEqual(new int?[] { 2, 9 }, (await StoredDetailAsync(ids[upn])).Select(l => l.PromptsAllApps).ToList());
        }

        private static CopilotUserCountLog CountRow(string reportType, string app, int activeUsers) => new CopilotUserCountLog
        {
            ReportRefreshDate = RefreshDate,
            ReportDate = RefreshDate.AddDays(-1),
            ReportType = reportType,
            ReportPeriodDays = 28,
            AppName = app,
            EnabledUsers = 250,
            ActiveUsers = activeUsers,
        };

        private static async Task<CopilotUsageUpsertResult> UpsertCountsAsync(string reportType, int saveBatchSize, params CopilotUserCountLog[] rows)
        {
            using (var db = NewContext())
            {
                var manager = new SqlCopilotUsagePersistenceManager(db, NullLogger.Instance) { UserCountSaveBatchSize = saveBatchSize };
                return await manager.UpsertUserCountsAsync(rows, reportType);
            }
        }

        private static async Task<List<CopilotUserCountLog>> StoredCountsAsync(string reportType)
        {
            using (var db = NewContext())
            {
                return await db.CopilotUserCountLogs.AsNoTracking().Where(r => r.ReportType == reportType).OrderBy(r => r.AppName).ToListAsync();
            }
        }

        /// <summary>R9-2, the aggregate report: the same fault, keyed on report type, period, date and app.</summary>
        [TestMethod]
        public async Task UserCounts_TwoRowsForOneNewAppAndDate_KeepTheLastRowInsteadOfFailingTheReport()
        {
            var reportType = "test-" + Guid.NewGuid().ToString("N").Substring(0, 12);

            var result = await UpsertCountsAsync(reportType, 500, CountRow(reportType, "Word", 10), CountRow(reportType, "Word", 12));

            Assert.AreEqual(1, result.Inserted);
            Assert.AreEqual(1, result.DuplicateRows);
            Assert.AreEqual(12, (await StoredCountsAsync(reportType)).Single().ActiveUsers, "The last row wins.");
        }

        /// <summary>Once the first row is saved, a further row for its key is an ordinary update of what was written.</summary>
        [TestMethod]
        public async Task UserCounts_ARowForAKeySavedEarlierInTheCall_IsAnUpdate()
        {
            var reportType = "test-" + Guid.NewGuid().ToString("N").Substring(0, 12);

            var result = await UpsertCountsAsync(reportType, 1,
                CountRow(reportType, "Word", 10), CountRow(reportType, "Excel", 4), CountRow(reportType, "Word", 12));

            Assert.AreEqual(2, result.Inserted);
            Assert.AreEqual(1, result.Updated);
            Assert.AreEqual(0, result.DuplicateRows);
            CollectionAssert.AreEqual(new[] { 4, 12 }, (await StoredCountsAsync(reportType)).Select(r => r.ActiveUsers).ToList());
        }
    }
}
