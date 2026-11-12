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
    /// over the directory population, every one of them leaves it out, and the two agree. A week before the 28-day window, in
    /// which one person was active on two days, checks the per-person shape the trends take over a wider window; and a week
    /// long before both, in which three analysts and the disabled account were active every day, the People tab's power users
    /// per department. All data is synthetic.
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
        private static TeamsExplorerQuery _powerWeek;
        private static DateTime _earlierWeek;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _window = TeamsExplorerQuery.Create(28, DateTime.UtcNow);
            _powerWeek = TeamsExplorerQuery.Create(7, DateTime.UtcNow.AddDays(-200));
            // The Monday (TeamsExplorerSql.WeekBucket) of a week inside a 90-day window and wholly before the 28-day one.
            var earlier = _window.UsageToExclusiveUtc.AddDays(-45);
            _earlierWeek = earlier.AddDays(-((earlier - new DateTime(1900, 1, 1)).Days % 7));
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

                // A week well before the 28-day window, for the wider one: the first person was active on two of its days,
                // the second has a row with no activity in it, and the disabled account was active too.
                var monday = _earlierWeek;
                db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog { UserID = enabled[0].ID, Date = monday, LastActivityDate = monday, TeamChatMessageCount = 5, PrivateChatMessageCount = 1, MeetingsAttendedCount = 1 });
                db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog { UserID = enabled[0].ID, Date = monday.AddDays(1), LastActivityDate = monday.AddDays(1), TeamChatMessageCount = 3 });
                db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog { UserID = enabled[1].ID, Date = monday.AddDays(2), LastActivityDate = monday.AddDays(2) });
                db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog { UserID = disabled.ID, Date = monday, LastActivityDate = monday, TeamChatMessageCount = 100 });

                // A week long before either window, for the People tab's departments: three enabled analysts and the disabled
                // account active on every day of it - power users, read on a 7-day window that ends with it.
                for (var date = _powerWeek.UsageFromUtc; date < _powerWeek.UsageToExclusiveUtc; date = date.AddDays(1))
                {
                    foreach (var person in enabled.Take(3).Append(disabled))
                    {
                        db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog { UserID = person.ID, Date = date, LastActivityDate = date, TeamChatMessageCount = 1 });
                    }
                }
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

        [TestMethod]
        public async Task OverAWiderWindow_TheDirectoryTrends_AddEachPersonsWeekUp_AndLeaveTheDisabledAccountOut()
        {
            var wide = TeamsExplorerQuery.Create(90, DateTime.UtcNow);
            var store = NewStore();
            var asWritten = await store.GetOverviewAsync(wide, new TeamsExplorerSources());
            var directory = await store.GetOverviewAsync(wide.WithDirectoryPopulation(), new TeamsExplorerSources());
            var adoptionAsWritten = await store.GetAdoptionAsync(wide);
            var adoptionDirectory = await store.GetAdoptionAsync(wide.WithDirectoryPopulation());
            AssertNoQueryFailed(asWritten, directory, adoptionAsWritten, adoptionDirectory);

            // A reader with See PII: the first person and the disabled account were active that week; a row with no
            // activity in it is nobody's.
            var seePiiWeek = asWritten.Trend.Single(p => p.WeekStart == _earlierWeek);
            Assert.AreEqual(2, seePiiWeek.ActiveUsers);
            Assert.AreEqual(108L, seePiiWeek.ChannelMessages);
            Assert.AreEqual(2, SegmentsOf(adoptionAsWritten.SegmentTrend.Single(p => p.WeekStart == _earlierWeek)));

            // Over the directory: the first person once, though active on two days, with both days' messages.
            var directoryWeek = directory.Trend.Single(p => p.WeekStart == _earlierWeek);
            Assert.AreEqual(1, directoryWeek.ActiveUsers);
            Assert.AreEqual(8L, directoryWeek.ChannelMessages);
            Assert.AreEqual(1L, directoryWeek.PrivateMessages);
            Assert.AreEqual(1L, directoryWeek.MeetingsAttended);
            Assert.AreEqual(1, SegmentsOf(adoptionDirectory.SegmentTrend.Single(p => p.WeekStart == _earlierWeek)));

            // Every week adds up to the period's totals for each reader, and the two differ by the disabled account alone.
            Assert.AreEqual(asWritten.Kpis.ChannelMessages, asWritten.Trend.Sum(p => p.ChannelMessages));
            Assert.AreEqual(directory.Kpis.ChannelMessages, directory.Trend.Sum(p => p.ChannelMessages));
            Assert.AreEqual(DisabledChannelMessages + 100, asWritten.Kpis.ChannelMessages.Value - directory.Kpis.ChannelMessages.Value);
            CollectionAssert.AreEqual(
                asWritten.Trend.Select(p => p.WeekStart).ToList(),
                directory.Trend.Select(p => p.WeekStart).ToList(),
                "The same weeks: each also had an enabled person's row.");
            StringAssert.Contains(directory.Queries.Single(q => q.Key == "overview-trend").Sql, "pop.account_enabled");
            Assert.IsFalse(asWritten.Queries.Single(q => q.Key == "overview-trend").Sql.Contains("pop."));
        }

        private static int SegmentsOf(TeamsSegmentTrendPoint week) => (week.Power ?? 0) + (week.Regular ?? 0) + (week.Light ?? 0);

        [TestMethod]
        public async Task People_OverTheDirectoryPopulation_EveryDepartmentsPowerUsersAddUpToThePowerSegment_AndLeaveTheDisabledAccountOut()
        {
            var store = NewStore();
            var asWritten = await store.GetPeopleAsync(_powerWeek);
            var directory = await store.GetPeopleAsync(_powerWeek.WithEveryGroup().WithDirectoryPopulation());
            var overview = await store.GetOverviewAsync(_powerWeek.WithDirectoryPopulation(), new TeamsExplorerSources());
            AssertNoQueryFailed(asWritten, directory, overview);

            // A reader with See PII, as before: the department counts the disabled account, whom the Power segment - read
            // over the directory for every reader - never did, so Power less the departments was that one account.
            Assert.AreEqual(4, asWritten.ChampionsByDepartment.Single(d => d.Name == "Contoso Analysts").Count);
            Assert.IsFalse(asWritten.Queries.Single(q => q.Key == "people-departments").Sql.Contains("account_enabled"),
                "A reader with See PII runs the statement as it was written.");

            // Over the directory population, every department read whole adds up to the Power segment the overview publishes.
            var power = overview.SegmentMix.Single(s => s.Segment == TeamsUserSegment.Power.ToString()).Users;
            Assert.AreEqual(3, power);
            Assert.AreEqual(power, (int)directory.ChampionsByDepartment.Sum(d => d.Count));
            Assert.AreEqual(int.MaxValue, directory.ReadTop, "Every department, whatever the top the people lists are read with.");
            StringAssert.Contains(directory.Queries.Single(q => q.Key == "people-departments").Sql, "u.account_enabled");

            // ...and the section reads the same engagement mix, so it never gives back a Power segment that tab withholds.
            Assert.AreEqual(power, directory.SegmentMix.Single(s => s.Segment == TeamsUserSegment.Power.ToString()).Users);
            Assert.IsNull(asWritten.SegmentMix, "A reader with See PII is not read the mix: nothing of theirs is withheld.");
            Assert.IsFalse(asWritten.Queries.Any(q => q.Key == "people-segments"));
            Assert.AreEqual(0, directory.Dormancy.UnmeasuredUsers, "The People tab never counts the unmeasured people.");
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
