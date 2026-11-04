extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.UserFilters;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity.Migrations;
using System.Linq;
using Configuration = Common.Entities.Migrations.Configuration;
using ReportsAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.ReportsAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// Runs the Reports page's weekly active-user query (<c>api/Reports/usage</c>) against a real, throwaway
    /// database seeded the way Graph's per-date usage reports actually arrive.
    /// </summary>
    /// <remarks>
    /// The importer asks for each report one date at a time, and Microsoft documents that form as returning the
    /// users who performed activity on that date. So a date's rows are POSITIVE-ONLY: the seed below never writes
    /// a row for someone who did nothing that day. The week's figure must therefore be everyone with a row on ANY
    /// of its days - the query used to read one date per week (the last one), which counted the people active on
    /// that one day as the whole week's. All seed data is synthetic.
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class UsageWeeklyActiveUsersSqlIntegrationTests
    {
        private static string _database;
        private static string _connectionString;

        private const string MasterConnection =
            @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=True";

        /// <summary>Every day imported; one person active on Monday only, one on Friday only, one on Sunday (and the days between).</summary>
        private static readonly DateTime CompleteWeek = new DateTime(2026, 3, 2);

        /// <summary>Wednesday's report never imported.</summary>
        private static readonly DateTime WeekMissingADay = new DateTime(2026, 3, 9);

        /// <summary>No report imported on any day.</summary>
        private static readonly DateTime WeekNeverImported = new DateTime(2026, 3, 16);

        /// <summary>Every day imported; one person active on all seven.</summary>
        private static readonly DateTime EverydayWeek = new DateTime(2026, 3, 23);

        private static int _mondayOnly;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            Assert.AreEqual(DayOfWeek.Monday, CompleteWeek.DayOfWeek, "Weeks must start on Mondays to match the week spine.");

            _database = "UsageWeeklyActive_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _connectionString =
                $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={_database};Integrated Security=true;MultipleActiveResultSets=True;TrustServerCertificate=True";

            Execute(MasterConnection, ScratchDatabase.CreateDatabaseSql(_database));

            // The full migration chain, so the query runs against the schema - and IX_date - customers actually get.
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
                var monday = new User { UserPrincipalName = "monday.only@contoso.com", AccountEnabled = true };
                var friday = new User { UserPrincipalName = "friday.only@contoso.com", AccountEnabled = true };
                var sunday = new User { UserPrincipalName = "sunday@contoso.com", AccountEnabled = true };
                var everyday = new User { UserPrincipalName = "everyday@contoso.com", AccountEnabled = true };
                db.users.AddRange(new[] { monday, friday, sunday, everyday });
                db.SaveChanges();
                _mondayOnly = monday.ID;

                // The complete week: Monday's and Friday's reports list one person each; the third person is active on
                // every other day, Sunday included, so every day of the week has a report and three people were active.
                Active(db, monday, CompleteWeek);
                Active(db, friday, CompleteWeek.AddDays(4));
                foreach (var offset in new[] { 1, 2, 3, 5, 6 }) Active(db, sunday, CompleteWeek.AddDays(offset));

                // Wednesday's report is missing: there is no row for anyone that day.
                foreach (var offset in new[] { 0, 1, 3, 4, 5, 6 }) Active(db, everyday, WeekMissingADay.AddDays(offset));

                // Nothing at all for WeekNeverImported.

                for (var offset = 0; offset < 7; offset++) Active(db, everyday, EverydayWeek.AddDays(offset));

                db.SaveChanges();
            }
        }

        private static void Active(AnalyticsEntitiesContext db, User user, DateTime day)
        {
            db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
            {
                UserID = user.ID,
                Date = day,
                LastActivityDate = day,
                PrivateChatMessageCount = 1,
            });
        }

        [TestMethod]
        public void PeopleActiveOnDifferentDaysOfAWeekAreAllCounted()
        {
            var weeks = Run("dbo.teams_user_activity_log");

            // Read from its Sunday alone, this week used to count one person.
            Assert.AreEqual(3d, weeks[CompleteWeek].Value,
                "Monday-only, Friday-only and Sunday users are three weekly active users, not one.");
            Assert.AreEqual(7, weeks[CompleteWeek].ImportedDays);
        }

        [TestMethod]
        public void SomeoneActiveEveryDayIsCountedOnce()
        {
            var weeks = Run("dbo.teams_user_activity_log");

            Assert.AreEqual(1d, weeks[EverydayWeek].Value);
            Assert.AreEqual(7, weeks[EverydayWeek].ImportedDays);
        }

        [TestMethod]
        public void AWeekMissingADaysReportIsReportedAsIncomplete()
        {
            var weeks = Run("dbo.teams_user_activity_log");

            var week = weeks[WeekMissingADay];
            Assert.AreEqual(6, week.ImportedDays, "Wednesday has no report, so the week is incomplete.");
            Assert.AreEqual(7, week.ExpectedDays);
            Assert.AreEqual(1d, week.Value, "The week is still counted from the days that were imported.");
        }

        [TestMethod]
        public void AWeekWithNoReportAtAllIsAGapNotAZero()
        {
            var weeks = Run("dbo.teams_user_activity_log");

            Assert.IsFalse(weeks.ContainsKey(WeekNeverImported),
                "A week with no report on any day is unknown, so it must come back as no row (a gap), never as zero.");
        }

        [TestMethod]
        public void ImportCoverageIsNotNarrowedToTheReadersScope()
        {
            // A scope holding only the Monday-only person: the activity narrows, but whether each day was imported
            // is a fact about the import. Narrowing it would turn the six days this person was not active into
            // "not imported" and flag a complete week.
            var weeks = Run("dbo.teams_user_activity_log", ReportUserScope.ForUsers(new[] { _mondayOnly }));

            Assert.AreEqual(1d, weeks[CompleteWeek].Value);
            Assert.AreEqual(7, weeks[CompleteWeek].ImportedDays);
            Assert.AreEqual(0d, weeks[EverydayWeek].Value, "An imported week with nobody in scope active is a real zero.");
            Assert.AreEqual(7, weeks[EverydayWeek].ImportedDays);
        }

        [TestMethod]
        public void TheQueryRunsAgainstEveryWorkloadTable()
        {
            foreach (var table in new[]
            {
                "dbo.outlook_user_activity_log",
                "dbo.onedrive_user_activity_log",
                "dbo.sharepoint_user_activity_log",
                "dbo.yammer_user_activity_log",
            })
            {
                Assert.AreEqual(0, Run(table).Count, $"{table} holds no rows, so no week has a report.");
            }
        }

        private static Dictionary<DateTime, Week> Run(string table, ReportUserScope scope = null)
        {
            scope = scope ?? ReportUserScope.Everyone;
            var sql = ReportScopeSql.Apply(ReportsAPIController.UsageWeeklyActiveUsersQuery(table), scope);
            var weeks = new Dictionary<DateTime, Week>();

            using (var connection = new SqlConnection(_connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = sql;
                command.CommandTimeout = 120;
                command.Parameters.Add(new SqlParameter("@from", SqlDbType.Date) { Value = CompleteWeek });
                command.Parameters.Add(new SqlParameter("@through", SqlDbType.Date) { Value = EverydayWeek });
                command.Parameters.Add(new SqlParameter("@settled", SqlDbType.Date) { Value = EverydayWeek.AddDays(6) });
                var scopeParameter = ReportScopeSql.CreateParameter(scope);
                if (scopeParameter != null) command.Parameters.Add(scopeParameter);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        weeks.Add(Convert.ToDateTime(reader["WeekStart"]).Date, new Week
                        {
                            Value = Convert.ToDouble(reader["Value"]),
                            ImportedDays = Convert.ToInt32(reader["ImportedDays"]),
                            ExpectedDays = Convert.ToInt32(reader["ExpectedDays"]),
                        });
                    }
                }
            }

            return weeks;
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

        private sealed class Week
        {
            public double Value { get; set; }
            public int ImportedDays { get; set; }
            public int ExpectedDays { get; set; }
        }
    }
}
