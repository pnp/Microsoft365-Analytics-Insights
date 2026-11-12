using Common.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.TeamsExplorer;
using Common.Entities.UserFilters;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Threading.Tasks;
using Configuration = Common.Entities.Migrations.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// Pins how the Teams Explorer decides who is dormant, and who counts towards reach, against a real throwaway
    /// database seeded the way Graph's usage reports actually arrive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The importer reads Graph's Teams user-activity report one date at a time, and Microsoft documents that form
    /// as returning the users who performed activity on that date. So the report is POSITIVE-ONLY, and this seed
    /// never writes a row for somebody who did nothing: dormancy has to come from the directory and the licences,
    /// and can only be claimed for a window whose every day was imported. The page used to look for all-zero
    /// rows, which a positive-only report does not produce, so its Dormant list was empty and its engagement mix
    /// was measured against active people only.
    /// </para>
    /// <para>
    /// The 7-day usage window has a report on every day, because one person was active on all of them. All seed
    /// data is synthetic.
    /// </para>
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class TeamsExplorerDormancySqlIntegrationTests
    {
        private static string _database;
        private static string _connectionString;
        private static int _alan;
        private static int _katherine;
        private static int _joan;

        private const string MasterConnection =
            @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=True";

        private static DateTime Today => DateTime.UtcNow.Date;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _database = "TeamsExplorerDormancy_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _connectionString =
                $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={_database};Integrated Security=true;MultipleActiveResultSets=True;TrustServerCertificate=True";

            Execute(MasterConnection, ScratchDatabase.CreateDatabaseSql(_database));
            new DbMigrator(new Configuration
            {
                TargetDatabase = new System.Data.Entity.Infrastructure.DbConnectionInfo(_connectionString, "Microsoft.Data.SqlClient"),
            }).Update();

            Seed();
        }

        [ClassCleanup]
        public static void DropDatabase()
        {
            if (_database == null) return;
            SqlConnection.ClearAllPools();
            Execute(MasterConnection,
                $"IF DB_ID('{_database}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END");
        }

        private static void Seed()
        {
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var e3 = new LicenseType { Name = "Office 365 E3", SKUID = "ENTERPRISEPACK" };
                var noTeams = new LicenseType { Name = "Microsoft 365 E3 (no Teams)", SKUID = "Microsoft_365_E3_(no_Teams)" };
                var rooms = new LicenseType { Name = "Microsoft Teams Rooms Pro", SKUID = "Microsoft_Teams_Rooms_Pro" };
                var powerBi = new LicenseType { Name = "Power BI Pro", SKUID = "POWER_BI_PRO" };
                db.LicenseTypes.AddRange(new[] { e3, noTeams, rooms, powerBi });

                var ada = new User { UserPrincipalName = "ada@contoso.com", AccountEnabled = true };
                var alan = new User { UserPrincipalName = "alan@contoso.com", AccountEnabled = true };
                var katherine = new User { UserPrincipalName = "katherine@contoso.com", AccountEnabled = true };
                var grace = new User { UserPrincipalName = "grace@contoso.com", AccountEnabled = true };
                var boardroom = new User { UserPrincipalName = "boardroom@contoso.com", AccountEnabled = true };
                var joan = new User { UserPrincipalName = "joan@contoso.com", AccountEnabled = false };
                db.users.AddRange(new[] { ada, alan, katherine, grace, boardroom, joan });
                db.SaveChanges();
                _alan = alan.ID;
                _katherine = katherine.ID;
                _joan = joan.ID;

                Holds(db, ada, e3);
                Holds(db, alan, e3);          // licensed for Teams, did nothing: dormant
                Holds(db, katherine, powerBi); // no Teams licence: not a Teams user at all
                Holds(db, grace, noTeams);    // an EEA-style suite sold without Teams
                Holds(db, boardroom, rooms);  // a meeting-room resource account
                Holds(db, joan, e3);          // active this week, disabled since

                // Ada was active on every day of the 7-day usage window, so every day has a report.
                var window = TeamsExplorerQuery.Create(7, DateTime.UtcNow);
                for (var day = window.UsageFromUtc; day < window.UsageToExclusiveUtc; day = day.AddDays(1))
                {
                    db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
                    {
                        UserID = ada.ID,
                        Date = day,
                        LastActivityDate = day,
                        PrivateChatMessageCount = 2,
                        MeetingsAttendedCount = 1,
                    });
                }

                db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
                {
                    UserID = joan.ID,
                    Date = window.UsageFromUtc.AddDays(2),
                    LastActivityDate = window.UsageFromUtc.AddDays(2),
                    PrivateChatMessageCount = 3,
                    MeetingsAttendedCount = 5,
                });

                db.SaveChanges();
            }
        }

        private static void Holds(AnalyticsEntitiesContext db, User user, LicenseType licence)
        {
            db.UserLicenseTypeLookups.Add(new UserLicenseTypeLookup { UserId = user.ID, LicenseTypeId = licence.ID });
        }

        private static SqlTeamsExplorerStore NewStore() =>
            new SqlTeamsExplorerStore(new ConnectionStringAnalyticsDbContextFactory(_connectionString));

        private static TeamsExplorerQuery FullyImportedWeek() => TeamsExplorerQuery.Create(7, DateTime.UtcNow);

        [TestMethod]
        public async Task ALicensedPersonWithNoActivityInAFullyImportedWindowIsDormant()
        {
            var people = await NewStore().GetPeopleAsync(FullyImportedWeek());

            Assert.IsNull(people.Queries.FirstOrDefault(q => q.Error != null)?.Error);
            Assert.IsTrue(people.Dormancy.Known, people.Dormancy.Reason);
            Assert.AreEqual(7, people.Dormancy.ImportedDays);
            Assert.AreEqual(7, people.Dormancy.ExpectedDays);
            Assert.AreEqual(2, people.Dormancy.TeamsLicensedUsers,
                "Ada and Alan: the room account's licence, the suite without Teams and the disabled account do not count.");

            CollectionAssert.AreEqual(
                new[] { "alan@contoso.com" },
                people.Dormant.Select(p => p.UserPrincipalName).ToArray(),
                "Only the enabled person licensed for Teams who did nothing is dormant - not the unlicensed, the room "
                + "account, the suite without Teams, or the disabled leaver.");
            Assert.IsNull(people.Dormant.Single().LastActivity);
        }

        [TestMethod]
        public async Task TheEngagementMixIsMeasuredAgainstTheLicensedPopulation()
        {
            var overview = await NewStore().GetOverviewAsync(FullyImportedWeek(), new TeamsExplorerSources());

            Assert.IsNull(overview.Queries.FirstOrDefault(q => q.Error != null)?.Error);
            var mix = overview.SegmentMix.ToDictionary(s => s.Segment);
            Assert.AreEqual(1, mix["Power"].Users, "Ada used Teams every day.");
            Assert.AreEqual(1, mix["Dormant"].Users, "Alan holds a Teams licence and did nothing.");
            Assert.AreEqual(50, mix["Dormant"].SharePct.Value, 0.001,
                "The share is of the people licensed for or using Teams, not just of the people who were active.");
            Assert.AreEqual(0, mix["Light"].Users, "The disabled leaver is not in the population.");
            Assert.AreEqual(0, overview.Dormancy.UnmeasuredUsers);
        }

        [TestMethod]
        public async Task AWindowWithADayNotImportedCannotCallAnyoneDormant()
        {
            // 28 days, of which only the last 7 have a report.
            var query = TeamsExplorerQuery.Create(28, DateTime.UtcNow);
            var store = NewStore();

            var people = await store.GetPeopleAsync(query);
            Assert.IsFalse(people.Dormancy.Known);
            Assert.AreEqual(TeamsDormancy.IncompleteImport, people.Dormancy.Reason);
            Assert.AreEqual(7, people.Dormancy.ImportedDays);
            Assert.AreEqual(28, people.Dormancy.ExpectedDays);
            Assert.AreEqual(0, people.Dormant.Count, "Alan could have been active on a day that was never imported.");

            var overview = await store.GetOverviewAsync(query, new TeamsExplorerSources());
            var mix = overview.SegmentMix.ToDictionary(s => s.Segment);
            Assert.AreEqual(0, mix["Dormant"].Users, "Unproven inactivity is not put in the Dormant segment...");
            Assert.AreEqual(1, overview.Dormancy.UnmeasuredUsers, "...it is counted as unmeasured, so the page can say so.");
            Assert.AreEqual(100, mix.Values.Sum(s => s.SharePct.Value), 0.001);
        }

        [TestMethod]
        public async Task AGroupFilteredImportCannotCallAnyoneDormant()
        {
            var people = await NewStore().GetPeopleAsync(FullyImportedWeek().WithUsageReportsGroupFiltered(true));

            Assert.IsFalse(people.Dormancy.Known);
            Assert.AreEqual(TeamsDormancy.GroupFiltered, people.Dormancy.Reason,
                "Outside the group, nobody was ever looked at, so having no usage row proves nothing.");
            Assert.AreEqual(0, people.Dormant.Count);
            Assert.IsTrue(people.Queries.Single(q => q.Key == "people-dormant").Sql.Contains("DECLARE @groupFiltered bit = 1;"),
                "The SQL shown beside the list must declare the value it ran with.");
        }

        [TestMethod]
        public async Task NobodyLicensedForTeamsInScopeLeavesDormancyUnknown()
        {
            var query = FullyImportedWeek().WithUserScope(ReportUserScope.ForUsers(new[] { _katherine }));

            var people = await NewStore().GetPeopleAsync(query);

            Assert.IsFalse(people.Dormancy.Known);
            Assert.AreEqual(TeamsDormancy.NoTeamsLicences, people.Dormancy.Reason);
            Assert.AreEqual(0, people.Dormant.Count);
        }

        [TestMethod]
        public async Task ReachCountsTheSamePeopleAboveAndBelowTheLine()
        {
            var store = NewStore();

            var overview = await store.GetOverviewAsync(FullyImportedWeek(), new TeamsExplorerSources());
            Assert.AreEqual(5, overview.Kpis.KnownUsers, "Five enabled directory users.");
            Assert.AreEqual(1, overview.Kpis.ActiveUsers,
                "Joan was active but is disabled now: she is not in the denominator, so she cannot be in the numerator.");
            Assert.AreEqual(20, overview.Kpis.ReachPct.Value, 0.001);
            Assert.AreEqual(7, overview.Kpis.MeetingsPerActiveUser.Value, 0.001,
                "Ada attended a meeting on each of 7 days. Joan's 5 are left out with her, or one active user would "
                + "be credited with 12.");

            // Narrowed to the leaver and one enabled person who did nothing: reach used to read 100%.
            var scoped = await store.GetOverviewAsync(
                FullyImportedWeek().WithUserScope(ReportUserScope.ForUsers(new[] { _joan, _alan })),
                new TeamsExplorerSources());
            Assert.AreEqual(1, scoped.Kpis.KnownUsers);
            Assert.AreEqual(0, scoped.Kpis.ActiveUsers);
            Assert.AreEqual(0, scoped.Kpis.ReachPct.Value, 0.001);
        }

        private static void Execute(string connectionString, string sql)
        {
            using (var connection = new SqlConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = sql;
                command.CommandTimeout = 0;
                command.ExecuteNonQuery();
            }
        }
    }
}
