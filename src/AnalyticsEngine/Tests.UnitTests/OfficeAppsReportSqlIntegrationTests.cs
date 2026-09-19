extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.Entities.UsageReports;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity.Migrations;
using System.Linq;
using Configuration = Common.Entities.Migrations.Configuration;
using ReportsAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.ReportsAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// Runs every Office apps report query against a real, throwaway SQL Server database seeded with
    /// data whose correct answers are known by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These queries are raw T-SQL built by string concatenation from a catalogue, so the compiler
    /// checks none of it. A mistyped column, a <c>CROSS APPLY</c> alias that does not match its
    /// column list, or a <c>GROUP BY</c> that omits a projected expression are all invisible until
    /// the query runs - and would surface as a per-chart error message on a customer's screen.
    /// </para>
    /// <para>
    /// More importantly, this pins the MEANING. Most of these aggregates would still return plausible
    /// numbers if they were subtly wrong: an inner join that drops people with no department, a
    /// web-only test applied per row instead of per person, a Copilot rate divided by the wrong
    /// denominator. Every expectation below is therefore derived from a handful of seeded people whose
    /// answer can be counted on fingers, so a wrong-but-plausible result fails rather than ships.
    /// </para>
    /// <para>
    /// All seed data is synthetic - Contoso and Fabrikam addresses, invented departments.
    /// </para>
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class OfficeAppsReportSqlIntegrationTests
    {
        private static string _database;
        private static string _connectionString;

        private const string MasterConnection =
            @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=True";

        /// <summary>Monday starting the first seeded week.</summary>
        private static readonly DateTime WeekA = new DateTime(2026, 3, 2);

        /// <summary>Monday starting the second seeded week.</summary>
        private static readonly DateTime WeekB = new DateTime(2026, 3, 9);

        /// <summary>The window every query is given.</summary>
        private static DateTime From => WeekA;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            Assert.AreEqual(DayOfWeek.Monday, WeekA.DayOfWeek, "The seeded weeks must start on Mondays to match the week spine.");
            Assert.AreEqual(DayOfWeek.Monday, WeekB.DayOfWeek);

            _database = "OfficeAppsReport_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _connectionString =
                $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={_database};Integrated Security=true;MultipleActiveResultSets=True;TrustServerCertificate=True";

            Execute(MasterConnection, $"CREATE DATABASE [{_database}];");

            // The full migration chain, so the queries run against the schema customers actually get.
            var migrationConfig = new Configuration
            {
                TargetDatabase = new System.Data.Entity.Infrastructure.DbConnectionInfo(_connectionString, "Microsoft.Data.SqlClient")
            };
            new DbMigrator(migrationConfig).Update();

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

        /// <summary>
        /// Four active people and a deliberate crowd of inactive ones.
        /// </summary>
        /// <remarks>
        /// The inactive people exist only to give the adoption-rate chart a denominator with something
        /// in it. They have directory records and no activity rows at all, which is exactly the
        /// population that distinguishes "share of the department that used an app" from "share of
        /// the people who used an app that used an app" - the second being 100% by construction.
        /// </remarks>
        private static void Seed()
        {
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var finance = new UserDepartment { Name = "Finance" };
                var engineering = new UserDepartment { Name = "Engineering" };
                db.UserDepartments.Add(finance);
                db.UserDepartments.Add(engineering);
                db.SaveChanges();

                var ada = NewUser(db, "ada@contoso.com", finance);
                var grace = NewUser(db, "grace@contoso.com", finance);
                var alan = NewUser(db, "alan@fabrikam.example", engineering);
                var kurt = NewUser(db, "kurt@contoso.com", null);

                // Finance: 5 people, 2 of them active  -> 40%
                for (var i = 1; i <= 3; i++) NewUser(db, $"finance-quiet-{i}@contoso.com", finance);
                // Engineering: 5 people, 1 active       -> 20%
                for (var i = 1; i <= 4; i++) NewUser(db, $"eng-quiet-{i}@fabrikam.example", engineering);
                // No department: 10 people, 1 active    -> 10%
                for (var i = 1; i <= 9; i++) NewUser(db, $"none-quiet-{i}@contoso.com", null);

                db.SaveChanges();

                // Ada: Outlook + Word on Windows, Excel in the browser (week A); Word again (week B).
                db.AppPlatformUserUsageLog.Add(new AppPlatformUserActivityLog
                {
                    User = ada, Date = WeekA.AddDays(1),
                    Outlook = true, Word = true, Excel = true,
                    Windows = true, Web = true,
                    OutlookWindows = true, WordWindows = true, ExcelWeb = true,
                });
                db.AppPlatformUserUsageLog.Add(new AppPlatformUserActivityLog
                {
                    User = ada, Date = WeekB.AddDays(1),
                    Word = true, Windows = true, WordWindows = true,
                });

                // Grace: Excel, browser only - never anything else.
                db.AppPlatformUserUsageLog.Add(new AppPlatformUserActivityLog
                {
                    User = grace, Date = WeekA.AddDays(2),
                    Excel = true, Web = true, ExcelWeb = true,
                });

                // Alan: Outlook + Teams on a Mac.
                db.AppPlatformUserUsageLog.Add(new AppPlatformUserActivityLog
                {
                    User = alan, Date = WeekA.AddDays(3),
                    Outlook = true, Teams = true, Mac = true,
                    OutlookMac = true, TeamsMac = true,
                });

                // Kurt: OneNote on a phone, in week B, and has no department.
                db.AppPlatformUserUsageLog.Add(new AppPlatformUserActivityLog
                {
                    User = kurt, Date = WeekB.AddDays(2),
                    OneNote = true, Mobile = true, OneNoteMobile = true,
                });

                // Ada used Copilot in Word. Grace has a Copilot report row but never used it, which is
                // what keeps her in the Excel denominator while contributing nothing to the numerator.
                db.CopilotUsageUserActivityLogs.Add(new CopilotUsageUserActivityLog
                {
                    User = ada, Date = WeekB.AddDays(1), ReportPeriodDays = 1,
                    WordLastActivityDate = WeekB,
                });
                db.CopilotUsageUserActivityLogs.Add(new CopilotUsageUserActivityLog
                {
                    User = grace, Date = WeekB.AddDays(1), ReportPeriodDays = 1,
                });

                db.SaveChanges();
            }
        }

        private static User NewUser(AnalyticsEntitiesContext db, string upn, UserDepartment department)
        {
            var user = new User { UserPrincipalName = upn, Department = department };
            db.users.Add(user);
            return user;
        }

        #region Charts that answer "which app is most popular"

        [TestMethod]
        public void AppPopularityCountsEachPersonOncePerAppTheyUsed()
        {
            var rows = RunCategory(ReportsAPIController.AppPopularityQuery());

            Assert.AreEqual(2d, rows["Outlook"], "Ada and Alan used Outlook.");
            Assert.AreEqual(2d, rows["Excel"], "Ada and Grace used Excel.");
            Assert.AreEqual(1d, rows["Word"], "Only Ada used Word, on two separate days - still one person.");
            Assert.AreEqual(1d, rows["Teams"]);
            Assert.AreEqual(1d, rows["OneNote"]);
            Assert.IsFalse(rows.ContainsKey("PowerPoint"), "Nobody used PowerPoint, so it has no bar.");
        }

        [TestMethod]
        public void AppTrendPlacesEachPersonInTheWeekTheyWereActive()
        {
            var rows = RunNamedWeeks(ReportsAPIController.AppWeeklyQuery());

            Assert.AreEqual(2d, Week(rows, "Outlook", WeekA));
            Assert.AreEqual(2d, Week(rows, "Excel", WeekA));
            Assert.AreEqual(1d, Week(rows, "Word", WeekA));
            Assert.AreEqual(1d, Week(rows, "Teams", WeekA));

            Assert.AreEqual(1d, Week(rows, "Word", WeekB), "Ada used Word again the following week.");
            Assert.AreEqual(1d, Week(rows, "OneNote", WeekB));
            Assert.AreEqual(0d, Week(rows, "Excel", WeekB), "Nobody used Excel in week B.");
        }

        [TestMethod]
        public void PlatformChartsCountPeoplePerPlatform()
        {
            var totals = RunCategory(ReportsAPIController.PlatformPopularityQuery());

            Assert.AreEqual(2d, totals["Web"], "Ada and Grace both worked in the browser.");
            Assert.AreEqual(1d, totals["Windows"]);
            Assert.AreEqual(1d, totals["Mac"]);
            Assert.AreEqual(1d, totals["Mobile"]);

            var weekly = RunNamedWeeks(ReportsAPIController.PlatformWeeklyQuery());
            Assert.AreEqual(2d, Week(weekly, "Web", WeekA));
            Assert.AreEqual(1d, Week(weekly, "Windows", WeekB));
            Assert.AreEqual(1d, Week(weekly, "Mobile", WeekB));
            Assert.AreEqual(0d, Week(weekly, "Mac", WeekB));
        }

        /// <summary>
        /// Breadth counts a person once no matter how many days they were active.
        /// </summary>
        [TestMethod]
        public void BreadthBucketsPeopleByHowManyAppsTheyUsed()
        {
            var rows = RunCategory(ReportsAPIController.AppBreadthQuery());

            Assert.AreEqual(2d, rows["1 app"], "Grace (Excel) and Kurt (OneNote).");
            Assert.AreEqual(1d, rows["2 apps"], "Alan used Outlook and Teams.");
            Assert.AreEqual(1d, rows["3 apps"], "Ada used Outlook, Word and Excel across two weeks.");
            Assert.IsFalse(rows.Keys.Any(k => k.StartsWith("0")), "People with no app activity are excluded, not shown as zero.");
        }

        #endregion

        #region The two-dimensional cuts

        [TestMethod]
        public void AppPlatformMatrixReportsWhereEachAppWasActuallyUsed()
        {
            var cells = RunMatrix(ReportsAPIController.AppPlatformMatrixQuery());

            Assert.AreEqual(1d, Cell(cells, "Word", "Windows"));
            Assert.AreEqual(2d, Cell(cells, "Excel", "Web"), "Both Excel users were in the browser.");
            Assert.AreEqual(1d, Cell(cells, "Outlook", "Windows"));
            Assert.AreEqual(1d, Cell(cells, "Outlook", "Mac"));
            Assert.AreEqual(1d, Cell(cells, "Teams", "Mac"));
            Assert.AreEqual(1d, Cell(cells, "OneNote", "Mobile"));

            // Ada used Word on Windows and Excel on the web. Treating "used Word" AND "used the web"
            // as "used Word on the web" would wrongly fill this cell - the cross columns are what stop
            // that, and this is the assertion that proves they are the ones being read.
            Assert.AreEqual(0d, Cell(cells, "Word", "Web"));
        }

        [TestMethod]
        public void DepartmentMatrixGroupsByDirectoryDepartmentAndKeepsPeopleWithNone()
        {
            var cells = RunMatrix(ReportsAPIController.AppByDepartmentQuery());

            Assert.AreEqual(2d, Cell(cells, "Excel", "Finance"), "Ada and Grace are both in Finance.");
            Assert.AreEqual(1d, Cell(cells, "Word", "Finance"));
            Assert.AreEqual(1d, Cell(cells, "Outlook", "Finance"));
            Assert.AreEqual(1d, Cell(cells, "Teams", "Engineering"));
            Assert.AreEqual(1d, Cell(cells, "Outlook", "Engineering"));

            Assert.AreEqual(1d, Cell(cells, "OneNote", "(No department)"),
                "Kurt has no department; an inner join would have dropped him entirely.");
        }

        [TestMethod]
        public void DomainMatrixSplitsPeopleByTheDomainOfTheirSignInAddress()
        {
            var cells = RunMatrix(ReportsAPIController.AppByDomainQuery());

            Assert.AreEqual(2d, Cell(cells, "Excel", "contoso.com"));
            Assert.AreEqual(1d, Cell(cells, "OneNote", "contoso.com"));
            Assert.AreEqual(1d, Cell(cells, "Teams", "fabrikam.example"));
            Assert.AreEqual(1d, Cell(cells, "Outlook", "fabrikam.example"));
            Assert.AreEqual(1d, Cell(cells, "Outlook", "contoso.com"));

            Assert.AreEqual(0d, Cell(cells, "Teams", "contoso.com"),
                "Alan is the only Teams user and he is on the other domain.");
        }

        #endregion

        #region Rates - the ones that are wrong-but-plausible if the denominator slips

        /// <summary>
        /// The rate is over the whole department, so the quiet majority counts.
        /// </summary>
        [TestMethod]
        public void DepartmentAdoptionRateDividesByEveryoneInTheDepartment()
        {
            var rows = RunCategory(ReportsAPIController.DepartmentAdoptionRateQuery());

            Assert.AreEqual(40d, rows["Finance"], "2 of 5 Finance people used an app.");
            Assert.AreEqual(20d, rows["Engineering"], "1 of 5.");
            Assert.AreEqual(10d, rows["(No department)"], "1 of 10.");
        }

        /// <summary>
        /// Lowest adoption first, because the chart exists to point at where to intervene.
        /// </summary>
        [TestMethod]
        public void DepartmentAdoptionRateRanksTheWorstFirst()
        {
            var ordered = RunCategoryOrdered(ReportsAPIController.DepartmentAdoptionRateQuery())
                .Select(r => r.Key).ToList();

            CollectionAssert.AreEqual(
                new[] { "(No department)", "Engineering", "Finance" }, ordered.ToArray(),
                "The chart must lead with the department least likely to be using the apps.");
        }

        /// <summary>
        /// Copilot take-up is measured against everyone using the app, including people with no
        /// Copilot record at all.
        /// </summary>
        [TestMethod]
        public void CopilotAttachRateMeasuresAgainstEveryAppUserNotJustCopilotUsers()
        {
            var rows = RunCategory(ReportsAPIController.CopilotAttachRateQuery());

            Assert.AreEqual(100d, rows["Word"], "Ada is the only Word user and she used Copilot in Word.");

            // Ada and Grace both use Excel; neither used Copilot in it. Grace has a Copilot report row
            // and Ada has one for Word, so an inner join would have produced a non-zero rate here.
            Assert.AreEqual(0d, rows["Excel"]);

            // Alan uses Outlook and Teams and has NO Copilot row whatsoever. He must still be counted
            // in those denominators, which is what keeps the rate honest for un-licensed people.
            Assert.AreEqual(0d, rows["Outlook"]);
            Assert.AreEqual(0d, rows["Teams"]);

            Assert.IsFalse(rows.ContainsKey("PowerPoint"),
                "PowerPoint has no users at all, so there is no denominator and no bar.");
        }

        #endregion

        #region Browser-only

        [TestMethod]
        public void WebOnlyFindsPeopleWithNoDesktopOrMobileUseOfThatApp()
        {
            var rows = RunCategory(ReportsAPIController.WebOnlyQuery());

            Assert.AreEqual(2d, rows["Excel"], "Ada and Grace both only ever touched Excel in the browser.");

            // Ada used Word on Windows, so Word is not browser-only for her even though she is a
            // browser user for Excel. Deciding this per activity row instead of per person is the
            // mistake this asserts against.
            Assert.AreEqual(0d, rows["Word"]);
            Assert.AreEqual(0d, rows["Outlook"]);
            Assert.AreEqual(0d, rows["Teams"]);
            Assert.AreEqual(0d, rows["OneNote"], "Kurt used OneNote on a phone, not the web.");
        }

        #endregion

        #region Every query at least executes

        /// <summary>
        /// Executes every query in the area, including any added later, purely to prove it is valid
        /// T-SQL against the shipped schema.
        /// </summary>
        /// <remarks>
        /// The assertions above cover the ones with interesting semantics; this is the net that
        /// catches a new chart whose SQL was never run - a mistyped column name in a string-built
        /// query is otherwise a runtime error on a customer's screen and nowhere else.
        /// </remarks>
        [TestMethod]
        public void EveryQueryInTheAreaIsValidAgainstTheShippedSchema()
        {
            var queries = new Dictionary<string, string>
            {
                { "AppPopularity", ReportsAPIController.AppPopularityQuery() },
                { "PlatformPopularity", ReportsAPIController.PlatformPopularityQuery() },
                { "AppWeekly", ReportsAPIController.AppWeeklyQuery() },
                { "PlatformWeekly", ReportsAPIController.PlatformWeeklyQuery() },
                { "AppBreadth", ReportsAPIController.AppBreadthQuery() },
                { "AppPlatformMatrix", ReportsAPIController.AppPlatformMatrixQuery() },
                { "AppByDepartment", ReportsAPIController.AppByDepartmentQuery() },
                { "DepartmentAdoptionRate", ReportsAPIController.DepartmentAdoptionRateQuery() },
                { "AppByDomain", ReportsAPIController.AppByDomainQuery() },
                { "WebOnly", ReportsAPIController.WebOnlyQuery() },
                { "CopilotAttachRate", ReportsAPIController.CopilotAttachRateQuery() },
                { "CopilotDataPresence", ReportsAPIController.CopilotDataPresenceQuery() },
            };

            foreach (var query in queries)
            {
                try
                {
                    var rows = 0;
                    Read(query.Value, reader => rows++);
                    Assert.IsTrue(rows >= 0);
                }
                catch (SqlException ex)
                {
                    Assert.Fail($"{query.Key} is not valid SQL against the migrated schema: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// An empty window returns no rows rather than failing.
        /// </summary>
        /// <remarks>
        /// A fresh deployment, or one whose usage-report import has only just been switched on, hits
        /// this on the first page load. A divide-by-zero in one of the rate queries would surface as
        /// a broken chart at exactly the moment an admin is checking the product works.
        /// </remarks>
        [TestMethod]
        public void AWindowWithNoActivityReturnsNothingRatherThanFailing()
        {
            var future = new DateTime(2099, 1, 5);

            foreach (var sql in new[]
            {
                ReportsAPIController.AppPopularityQuery(),
                ReportsAPIController.AppBreadthQuery(),
                ReportsAPIController.WebOnlyQuery(),
                ReportsAPIController.CopilotAttachRateQuery(),
                ReportsAPIController.AppByDepartmentQuery(),
                ReportsAPIController.AppByDomainQuery(),
            })
            {
                var rows = 0;
                Read(sql, _ => rows++, future);
                Assert.AreEqual(0, rows, "An empty window must simply produce an empty chart.");
            }

            // The adoption rate still returns a row per department - everybody is at 0%, which is the
            // truthful answer and not the same as "no data".
            var rates = RunCategory(ReportsAPIController.DepartmentAdoptionRateQuery(), future);
            Assert.AreEqual(0d, rates["Finance"]);
        }

        #endregion

        #region Plumbing

        private static Dictionary<string, double> RunCategory(string sql, DateTime? from = null)
        {
            return RunCategoryOrdered(sql, from).ToDictionary(r => r.Key, r => r.Value);
        }

        private static List<KeyValuePair<string, double>> RunCategoryOrdered(string sql, DateTime? from = null)
        {
            var rows = new List<KeyValuePair<string, double>>();
            Read(sql, reader => rows.Add(new KeyValuePair<string, double>(
                reader.GetString(reader.GetOrdinal("Label")),
                Convert.ToDouble(reader["Value"]))), from);
            return rows;
        }

        private static List<Tuple<string, DateTime, double>> RunNamedWeeks(string sql, DateTime? from = null)
        {
            var rows = new List<Tuple<string, DateTime, double>>();
            Read(sql, reader => rows.Add(Tuple.Create(
                reader.GetString(reader.GetOrdinal("SeriesName")),
                Convert.ToDateTime(reader["WeekStart"]),
                Convert.ToDouble(reader["Value"]))), from);
            return rows;
        }

        private static Dictionary<string, double> RunMatrix(string sql, DateTime? from = null)
        {
            var cells = new Dictionary<string, double>();
            Read(sql, reader => cells[
                reader.GetString(reader.GetOrdinal("RowLabel")) + "\u0000" +
                reader.GetString(reader.GetOrdinal("ColumnLabel"))] = Convert.ToDouble(reader["Value"]), from);
            return cells;
        }

        /// <summary>A week's value for a series, or zero when the query returned no row for it.</summary>
        private static double Week(List<Tuple<string, DateTime, double>> rows, string series, DateTime weekStart)
        {
            return rows
                .Where(r => r.Item1 == series && r.Item2.Date == weekStart.Date)
                .Select(r => r.Item3)
                .DefaultIfEmpty(0d)
                .Single();
        }

        /// <summary>A matrix cell, or zero where the query returned no row for that intersection.</summary>
        private static double Cell(Dictionary<string, double> cells, string row, string column)
        {
            return cells.TryGetValue(row + "\u0000" + column, out var value) ? value : 0d;
        }

        private static void Read(string sql, Action<SqlDataReader> onRow, DateTime? from = null)
        {
            using (var connection = new SqlConnection(_connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = sql;
                command.CommandTimeout = 120;
                command.Parameters.AddWithValue("@from", from ?? From);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read()) onRow(reader);
                }
            }
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

        #endregion
    }
}
