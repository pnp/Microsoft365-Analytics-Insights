using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.TeamsExplorer;
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
    /// The directory population the Teams Explorer reads its usage figures over for a reader without See PII (#844, S4-1),
    /// run against a real, migrated throwaway database, because the narrowing is SQL.
    /// </summary>
    /// <remarks>
    /// Six enabled people in two departments, five of them active, and one account disabled since that was active in the
    /// window. Read as written - as a reader with See PII is served - the overview's totals and trend, the rhythm, the
    /// lifecycle, the engagement trend and the people per platform count the disabled account; the department breakdown,
    /// built from the directory, never did, so the overview less the breakdown's rows was that one account's activity. Read
    /// over the directory population, every one of them leaves it out, and the two agree. All data is synthetic.
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class TeamsExplorerPopulationSqlIntegrationTests
    {
        private const string MasterConnection =
            @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=True";

        /// <summary>The disabled account's channel messages: its activity, whoever can see it.</summary>
        private const long DisabledChannelMessages = 450;

        private static string _database;
        private static string _connectionString;
        private static TeamsExplorerQuery _window;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _window = TeamsExplorerQuery.Create(28, DateTime.UtcNow);
            _database = "TeamsExplorerPopulation_" + Guid.NewGuid().ToString("N").Substring(0, 12);
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
                var analysts = new UserDepartment { Name = "Contoso Analysts" };
                var engineering = new UserDepartment { Name = "Contoso Engineering" };
                db.UserDepartments.Add(analysts);
                db.UserDepartments.Add(engineering);
                db.SaveChanges();

                var enabled = Enumerable.Range(1, 6)
                    .Select(i => new User { UserPrincipalName = $"person{i}@contoso.com", AccountEnabled = true, DepartmentId = i <= 4 ? analysts.ID : engineering.ID })
                    .ToList();
                var disabled = new User { UserPrincipalName = "former@contoso.com", AccountEnabled = false, DepartmentId = analysts.ID };
                db.users.AddRange(enabled);
                db.users.Add(disabled);
                db.SaveChanges();

                // Two days inside the usage window, which ends three days before today. The sixth person did nothing, so,
                // the report being positive-only, has no row at all.
                var day = _window.UsageToExclusiveUtc.AddDays(-2);
                foreach (var person in enabled.Take(5))
                {
                    db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
                    {
                        UserID = person.ID,
                        Date = day,
                        LastActivityDate = day,
                        TeamChatMessageCount = 10,
                        PrivateChatMessageCount = 4,
                        MeetingsAttendedCount = 2,
                    });
                    db.TeamsUserDeviceUsageLog.Add(new GlobalTeamsUserDeviceUsageLog { UserID = person.ID, Date = day, UsedWindows = true });
                }

                db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
                {
                    UserID = disabled.ID,
                    Date = day,
                    LastActivityDate = day,
                    TeamChatMessageCount = DisabledChannelMessages,
                    PrivateChatMessageCount = 30,
                    MeetingsAttendedCount = 7,
                });
                db.TeamsUserDeviceUsageLog.Add(new GlobalTeamsUserDeviceUsageLog { UserID = disabled.ID, Date = day, UsedWindows = true, UsedLinux = true });
                db.SaveChanges();
            }
        }

        [TestMethod]
        public async Task AsWritten_EveryUsageFigureCountsTheDisabledAccount_SoTheOverviewLessTheBreakdownWasItsActivity()
        {
            var store = NewStore();
            var overview = await store.GetOverviewAsync(_window, new TeamsExplorerSources());
            var adoption = await store.GetAdoptionAsync(_window);
            AssertNoQueryFailed(overview, adoption);

            // What a reader with See PII is served, exactly as before: the disabled account's activity is in the totals.
            Assert.AreEqual(50 + DisabledChannelMessages, overview.Kpis.ChannelMessages);
            Assert.AreEqual(50 + DisabledChannelMessages, overview.Trend.Sum(p => p.ChannelMessages));
            Assert.AreEqual(6, adoption.Rhythm.MonthlyActiveUsers);
            Assert.AreEqual(6, adoption.Lifecycle.NewUsers + adoption.Lifecycle.ReturningUsers + adoption.Lifecycle.LapsedUsers);
            Assert.AreEqual(6, adoption.SegmentTrend.Sum(p => p.Power + p.Regular + p.Light));
            Assert.AreEqual(1, adoption.Devices.Single(d => d.Platform == "Linux").Users);
            Assert.IsFalse(overview.Queries.Concat(adoption.Queries).Any(q => q.Sql.Contains("/*population") || q.Sql.Contains("pop.")),
                "A reader with See PII runs the statements as they were written.");

            // The breakdown never counted it: the overview less the breakdown's rows is the one disabled account's messages.
            Assert.AreEqual(5, overview.Kpis.ActiveUsers);
            Assert.AreEqual(DisabledChannelMessages + 30, overview.Kpis.ChannelMessages.Value + overview.Kpis.PrivateMessages.Value - BreakdownMessages(adoption), 0.0001);
        }

        [TestMethod]
        public async Task OverTheDirectoryPopulation_EveryUsageFigureLeavesItOut_AndTheOverviewLessTheBreakdownIsNothing()
        {
            var store = NewStore();
            var query = _window.WithDirectoryPopulation();
            var overview = await store.GetOverviewAsync(query, new TeamsExplorerSources());
            var adoption = await store.GetAdoptionAsync(query);
            AssertNoQueryFailed(overview, adoption);

            Assert.AreEqual(50, overview.Kpis.ChannelMessages);
            Assert.AreEqual(20, overview.Kpis.PrivateMessages);
            Assert.AreEqual(10, overview.Kpis.MeetingsAttended);
            Assert.AreEqual(50, overview.Trend.Sum(p => p.ChannelMessages));
            Assert.AreEqual(5, overview.Trend.Sum(p => p.ActiveUsers));
            Assert.AreEqual(5, adoption.Rhythm.MonthlyActiveUsers);
            Assert.AreEqual(5, adoption.Rhythm.WeeklyActiveUsers);
            Assert.AreEqual(5, adoption.Lifecycle.NewUsers + adoption.Lifecycle.ReturningUsers + adoption.Lifecycle.LapsedUsers);
            Assert.AreEqual(5, adoption.PeriodActiveUsers);
            Assert.AreEqual(5, adoption.SegmentTrend.Sum(p => p.Power + p.Regular + p.Light));
            Assert.IsFalse(adoption.Devices.Any(d => d.Platform == "Linux"), "Only the disabled account used Linux.");
            Assert.AreEqual(5, adoption.Devices.Single(d => d.Platform == "Windows").Users);

            // Reach was already measured over the directory: the same for both readers.
            Assert.AreEqual(5, overview.Kpis.ActiveUsers);
            Assert.AreEqual(6, overview.Kpis.KnownUsers);

            // One population: the overview less the breakdown's rows leaves nothing.
            Assert.AreEqual(0, overview.Kpis.ChannelMessages.Value + overview.Kpis.PrivateMessages.Value - BreakdownMessages(adoption), 0.0001);
            Assert.AreEqual(5, adoption.Breakdown.Sum(r => r.ActiveUsers));

            foreach (var key in new[] { "overview-usage", "overview-trend" })
            {
                StringAssert.Contains(overview.Queries.Single(q => q.Key == key).Sql, "pop.account_enabled", key + " shows the narrowing it ran with.");
            }
            foreach (var key in new[] { "adoption-rhythm", "adoption-segment-trend", "adoption-devices", "adoption-lifecycle" })
            {
                StringAssert.Contains(adoption.Queries.Single(q => q.Key == key).Sql, "pop.account_enabled", key + " shows the narrowing it ran with.");
            }
        }

        private static double BreakdownMessages(TeamsAdoption adoption) =>
            adoption.Breakdown.Sum(r => r.MessagesPerActiveUser * r.ActiveUsers);

        private static void AssertNoQueryFailed(params TeamsExplorerSection[] sections)
        {
            var failed = sections.SelectMany(s => s.Queries).Where(q => q.Error != null).ToList();
            Assert.AreEqual(0, failed.Count, string.Join(" | ", failed.Select(q => $"{q.Key}: {q.Error}")));
        }

        private static SqlTeamsExplorerStore NewStore() =>
            new SqlTeamsExplorerStore(new ConnectionStringAnalyticsDbContextFactory(_connectionString));

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
