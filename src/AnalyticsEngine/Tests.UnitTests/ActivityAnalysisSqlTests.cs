using App.ControlPanel.Engine;
using Common.Entities.ActivityAnalysis;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Activity analysis SQL against a throwaway LocalDB database: the schema probe, the GROUPING SETS read model and the
    /// filtered weekly series.
    /// </summary>
    /// <remarks>
    /// <para>The <c>dbo</c> tables are written by hand with production's column types; <c>profiling.users</c> and
    /// <c>profiling.ActivitiesWeeklyColumns</c> are created by the shipped <c>Profiling-03-CreateSchema.sql</c>'s own
    /// batches - but without the batch that later added the twelve newer Teams columns, so the database looks like an
    /// install that predates them.</para>
    /// <para>Every name and number is synthetic. The Greek department and licence name cross the <c>nvarchar</c>
    /// columns they would cross in a customer tenant.</para>
    /// </remarks>
    [TestClass]
    public class ActivityAnalysisSqlTests
    {
        private const string GreekDepartment = "Καλημέρα κόσμε";
        private const string GreekLicence = "Copilot για Microsoft 365";
        private const string ProfilingScript = "App.ControlPanel.Engine.SqlExtentions.Profiling-03-CreateSchema.sql";

        private static readonly ActivityAnalysisPeriod Period =
            ActivityAnalysisPeriod.Create(new DateTime(2026, 1, 5), new DateTime(2026, 1, 26));

        internal const string DboSchema = @"
CREATE TABLE dbo.user_departments (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_job_titles (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_company_name (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_office_locations (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_country_or_region (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_state_or_province (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_usage_locations (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.users (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_users PRIMARY KEY CLUSTERED,
    user_name varchar(250) NOT NULL,
    mail nvarchar(max) NULL,
    account_enabled bit NULL,
    manager_id int NULL,
    department_id int NULL,
    job_title_id int NULL,
    company_name_id int NULL,
    office_location_id int NULL,
    country_or_region_id int NULL,
    state_or_province_id int NULL,
    usage_location_id int NULL,
    azure_ad_id nvarchar(max) NULL,
    postalcode nvarchar(50) NULL
);
CREATE TABLE dbo.license_types (
    id int IDENTITY NOT NULL CONSTRAINT PK_license_types PRIMARY KEY,
    sku_id nvarchar(max) NULL,
    name nvarchar(100) NULL
);
CREATE TABLE dbo.user_license_type_lookups (
    id int IDENTITY NOT NULL CONSTRAINT PK_user_license_type_lookups PRIMARY KEY,
    user_id int NOT NULL,
    license_type_id int NOT NULL
);
CREATE UNIQUE INDEX IX_license_type_id_user_id ON dbo.user_license_type_lookups(license_type_id, user_id);";

        private const string Data = @"
INSERT INTO dbo.user_departments (name) VALUES (N'Sales'), (N'" + GreekDepartment + @"');
INSERT INTO dbo.user_company_name (name) VALUES (N'Contoso'), (N'Fabrikam');

SET IDENTITY_INSERT dbo.users ON;
INSERT INTO dbo.users (id, user_name, account_enabled, azure_ad_id, department_id, company_name_id, postalcode) VALUES
    (101, 'user101@contoso.com', 1, N'00000000-0000-0000-0000-000000000101', 1, 1, N''),
    (102, 'user102@contoso.com', 1, N'00000000-0000-0000-0000-000000000102', 2, 1, N''),
    (103, 'user103@contoso.com', 1, N'00000000-0000-0000-0000-000000000103', 1, 2, N''),
    -- Not in profiling.users: disabled, no Entra id, no licence.
    (104, 'user104@contoso.com', 0, N'00000000-0000-0000-0000-000000000104', 1, 1, N''),
    (105, 'user105@contoso.com', 1, NULL, 1, 1, N''),
    (106, 'user106@contoso.com', 1, N'00000000-0000-0000-0000-000000000106', 1, 1, N''),
    -- In profiling.users, but with a week only before the period.
    (107, 'user107@contoso.com', 1, N'00000000-0000-0000-0000-000000000107', 1, 1, N'');
SET IDENTITY_INSERT dbo.users OFF;

SET IDENTITY_INSERT dbo.license_types ON;
INSERT INTO dbo.license_types (id, name, sku_id) VALUES
    (1, N'Microsoft 365 E3', N'SPE_E3'), (2, N'Microsoft 365 E5', N'SPE_E5'), (3, N'" + GreekLicence + @"', N'Microsoft_365_Copilot');
SET IDENTITY_INSERT dbo.license_types OFF;

INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id) VALUES
    (101, 1), (102, 1), (102, 3), (103, 2), (104, 1), (105, 1), (107, 1);

INSERT INTO profiling.ActivitiesWeeklyColumns ([user_id], [date], [Teams Calls], [Emails Sent]) VALUES (101, '2026-01-05', 3, 10);
INSERT INTO profiling.ActivitiesWeeklyColumns ([user_id], [date], [Teams Calls]) VALUES (101, '2026-01-12', 2);
INSERT INTO profiling.ActivitiesWeeklyColumns ([user_id], [date], [Emails Received]) VALUES (101, '2026-01-19', 3000000000);
INSERT INTO profiling.ActivitiesWeeklyColumns ([user_id], [date], [Teams Calls], [Copilot Chats]) VALUES (102, '2026-01-05', 1, 4);
INSERT INTO profiling.ActivitiesWeeklyColumns ([user_id], [date], [Teams Meetings]) VALUES (102, '2026-01-19', 5);
INSERT INTO profiling.ActivitiesWeeklyColumns ([user_id], [date]) VALUES (103, '2026-01-26');
INSERT INTO profiling.ActivitiesWeeklyColumns ([user_id], [date], [Teams Calls]) VALUES
    (104, '2026-01-05', 50), (105, '2026-01-12', 60), (106, '2026-01-12', 70), (107, '2025-12-29', 80);";

        private static ScratchDatabase _db;

        [ClassInitialize]
        public static void ClassInit(TestContext context)
        {
            _db = ScratchDatabase.Create("activityanalysis");
            _db.Execute(DboSchema);
            InstallProfilingTables(_db, withNewerTeamsColumns: false);
            _db.Execute(Data);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _db?.Dispose();
        }

        [TestMethod]
        public async Task Schema_ListsTheColumnsThisDatabaseHas_AndTheCompiledWeeks()
        {
            var schema = await Source().ReadSchemaAsync(CancellationToken.None);

            Assert.IsTrue(schema.Installed);
            Assert.AreEqual(new DateTime(2025, 12, 29), schema.EarliestWeek);
            Assert.AreEqual(new DateTime(2026, 1, 26), schema.LatestWeek);
            Assert.AreEqual(46, schema.AvailableMetrics.Count, "Everything but the twelve Teams columns a later script version added.");
            Assert.IsTrue(schema.IsAvailable(Metric("teams.calls")));
            Assert.IsTrue(schema.IsAvailable(Metric("copilot.app.word")));
            Assert.IsFalse(schema.IsAvailable(Metric("teams.audioDuration")));
            Assert.IsFalse(schema.IsAvailable(Metric("teams.urgentMessages")));
        }

        [TestMethod]
        public async Task ReadModel_TotalsEachPersonOverThePeriod_InOneStatement()
        {
            var model = await Source().LoadReadModelAsync(Period, CancellationToken.None);

            Assert.AreEqual(3, model.PeopleCount,
                "Only people in profiling.users with a week in the period: not the disabled account, the one with no Entra id, "
                + "the one with no licence, nor the one whose only week is before the period.");
            var people = Enumerable.Range(0, model.PeopleCount).ToDictionary(model.UserIdAt);
            CollectionAssert.AreEquivalent(new[] { 101, 102, 103 }, people.Keys.ToList());

            Assert.AreEqual(5, model.TotalOf(people[101], Metric("teams.calls").Index));
            Assert.AreEqual(10, model.TotalOf(people[101], Metric("outlook.emailsSent").Index));
            Assert.AreEqual(int.MaxValue, model.TotalOf(people[101], Metric("outlook.emailsReceived").Index),
                "Three billion in one week saturates the 32-bit per-person total.");
            Assert.AreEqual(1, model.TotalOf(people[102], Metric("teams.calls").Index));
            Assert.AreEqual(5, model.TotalOf(people[102], Metric("teams.meetings").Index));
            Assert.AreEqual(4, model.TotalOf(people[102], Metric("copilot.chats").Index));
            Assert.IsTrue(ActivityAnalysisMetricCatalogue.All.All(m => model.TotalOf(people[103], m.Index) == 0));
            Assert.IsFalse(model.IsAvailable(Metric("teams.audioDuration")));

            var weeks = model.PopulationWeeks;
            var calls = Metric("teams.calls").Index;
            CollectionAssert.AreEqual(new long[] { 4, 2, 0, 0 }, Enumerable.Range(0, 4).Select(w => weeks.SumOf(w, calls)).ToList());
            CollectionAssert.AreEqual(new[] { 2, 1, 0, 0 }, Enumerable.Range(0, 4).Select(w => weeks.ActivePeopleOf(w, calls)).ToList());
            Assert.AreEqual(3000000000L, weeks.SumOf(2, Metric("outlook.emailsReceived").Index), "Weekly sums are 64-bit.");
            Assert.AreEqual(5, weeks.SumOf(2, Metric("teams.meetings").Index));
            Assert.AreEqual(4, weeks.SumOf(0, Metric("copilot.chats").Index));

            CollectionAssert.AreEquivalent(new[] { "Microsoft 365 E3", "Microsoft 365 E5", GreekLicence }, model.Licences.Select(l => l.Name).ToList());
            var held = model.LicencesOf(people[102]).Select(i => model.Licences[i].Id).OrderBy(id => id).ToList();
            CollectionAssert.AreEqual(new[] { 1, 3 }, held);
        }

        [TestMethod]
        public async Task WeeklyTotals_AreReadForExactlyTheRequestedPeople()
        {
            var source = Source();
            var model = await source.LoadReadModelAsync(Period, CancellationToken.None);

            var weeks = await source.LoadWeeklyTotalsAsync(model, new[] { 101, 103 }, CancellationToken.None);

            var calls = Metric("teams.calls").Index;
            CollectionAssert.AreEqual(new long[] { 3, 2, 0, 0 }, Enumerable.Range(0, 4).Select(w => weeks.SumOf(w, calls)).ToList());
            CollectionAssert.AreEqual(new[] { 1, 1, 0, 0 }, Enumerable.Range(0, 4).Select(w => weeks.ActivePeopleOf(w, calls)).ToList());
            Assert.AreEqual(10, weeks.SumOf(0, Metric("outlook.emailsSent").Index));
            Assert.AreEqual(3000000000L, weeks.SumOf(2, Metric("outlook.emailsReceived").Index));
            Assert.AreEqual(0, weeks.SumOf(0, Metric("copilot.chats").Index), "user102 is not one of them.");

            var nobody = await source.LoadWeeklyTotalsAsync(model, new int[0], CancellationToken.None);
            Assert.AreEqual(0, nobody.SumOf(0, calls));
        }

        [TestMethod]
        public async Task Report_EndToEnd_WithTheDirectoryLoader_KeepsGreekNamesIntact()
        {
            var service = new ActivityAnalysisService(Source(), "synthetic-sql", new ActivityAnalysisCaches());
            var directory = await UserFilterStores.CreateDirectoryLoader(_db.ConnectionString).LoadAsync();
            var schema = await service.GetSchemaAsync(CancellationToken.None);
            var query = ActivityAnalysisQuery.Parse("2026-01-05", "2026-01-26", "teams.calls,copilot.chats", null, null)
                .Resolve(schema, new DateTime(2026, 2, 2));
            var audience = new ActivityAnalysisAudience { Directory = directory, SeesIndividuals = true, MinimumPeopleWithoutSeePii = 5 };

            var report = await service.GetReportAsync(query, audience, null, CancellationToken.None);

            Assert.AreEqual(3, report.PopulationPeople);
            Assert.AreEqual(2, report.ActivePeople);
            CollectionAssert.AreEqual(new[] { "Sales", GreekDepartment }, report.Departments.Select(d => d.Name).ToList());
            Assert.AreEqual(5, report.Departments[0].Values.Single(v => v.Metric == "teams.calls").Sum);
            Assert.AreEqual(2, report.Departments[0].People);
            CollectionAssert.AreEqual(new[] { GreekLicence, "Microsoft 365 E3", "Microsoft 365 E5" }, report.Licences.Select(l => l.Name).ToList());
            CollectionAssert.AreEqual(new long[] { 4, 2, 0, 0 }, report.Series.Single(s => s.Metric == "teams.calls").Sum);

            var greek = await service.GetReportAsync(query,
                new ActivityAnalysisAudience
                {
                    Directory = directory,
                    UserFilter = ActivityAnalysisTestDirectory.Filter(directory, "[{\"d\":\"department\",\"v\":[\"" + GreekDepartment + "\"]}]"),
                    SeesIndividuals = true,
                    MinimumPeopleWithoutSeePii = 5,
                },
                null, CancellationToken.None);

            Assert.AreEqual(1, greek.MatchingPeople);
            CollectionAssert.AreEqual(new long[] { 1, 0, 0, 0 }, greek.Series.Single(s => s.Metric == "teams.calls").Sum,
                "The filtered series, read from SQL for the one matching person.");
            CollectionAssert.AreEqual(new long[] { 4, 0, 0, 0 }, greek.Series.Single(s => s.Metric == "copilot.chats").Sum);

            var people = await service.GetPeopleAsync(query, audience, GreekDepartment, false, Metric("teams.calls"), 10, CancellationToken.None);
            Assert.AreEqual("user102@contoso.com", people.People.Single().UserPrincipalName);
            Assert.AreEqual(GreekDepartment, people.People.Single().Department);
        }

        [TestMethod]
        public async Task ADatabaseWithoutTheProfilingTables_IsNotInstalled_AndNeverQueried()
        {
            using (var empty = ScratchDatabase.Create("activityanalysisnone"))
            {
                empty.Execute(DboSchema);
                var source = new SqlActivityAnalysisSource(empty.ConnectionString);

                var schema = await source.ReadSchemaAsync(CancellationToken.None);
                Assert.IsFalse(schema.Installed);
                Assert.AreEqual(ActivityAnalysisReasons.NotInstalled, schema.Reason);

                await Assert.ThrowsExceptionAsync<ActivityAnalysisNotInstalledException>(() => source.LoadReadModelAsync(Period, CancellationToken.None));
            }
        }

        [TestMethod]
        public async Task AnUpgradedDatabaseWithNoWeeksYet_HasEveryMetric_AndNoData()
        {
            using (var upgraded = ScratchDatabase.Create("activityanalysisnew"))
            {
                upgraded.Execute(DboSchema);
                InstallProfilingTables(upgraded, withNewerTeamsColumns: true);
                var source = new SqlActivityAnalysisSource(upgraded.ConnectionString);

                var schema = await source.ReadSchemaAsync(CancellationToken.None);
                Assert.IsTrue(schema.Installed);
                Assert.AreEqual(ActivityAnalysisReasons.NoData, schema.Reason);
                Assert.AreEqual(ActivityAnalysisMetricCatalogue.Count, schema.AvailableMetrics.Count,
                    "Every catalogue column exists once the shipped script has run in full.");

                var model = await source.LoadReadModelAsync(Period, CancellationToken.None);
                Assert.AreEqual(0, model.PeopleCount);
                Assert.AreEqual(ActivityAnalysisMetricCatalogue.Count, model.AvailableMetrics.Count);
            }
        }

        private static SqlActivityAnalysisSource Source() => new SqlActivityAnalysisSource(_db.ConnectionString);

        private static ActivityAnalysisMetric Metric(string key) => ActivityAnalysisMetricCatalogue.All.Single(m => m.Key == key);

        /// <summary>
        /// Runs the shipped script's own batches for the profiling schema, <c>profiling.users</c> and
        /// <c>profiling.ActivitiesWeeklyColumns</c> (with its <c>IX_date</c>) - and, when asked, the later batch that
        /// added the newer Teams columns.
        /// </summary>
        internal static void InstallProfilingTables(ScratchDatabase database, bool withNewerTeamsColumns)
        {
            var batches = ProfilingScriptBatches();
            var wanted = new List<string>
            {
                Single(batches, b => b.Contains("CREATE SCHEMA profiling")),
                Single(batches, b => b.TrimStart().StartsWith("CREATE VIEW profiling.users", StringComparison.Ordinal)),
                Single(batches, b => b.Contains("CREATE TABLE profiling.ActivitiesWeeklyColumns")),
                Single(batches, b => b.Contains("CREATE INDEX IX_date ON profiling.ActivitiesWeeklyColumns")),
            };

            if (withNewerTeamsColumns)
            {
                wanted.Add(Single(batches, b => b.Contains("ALTER TABLE profiling.ActivitiesWeeklyColumns") && b.Contains("\"Teams Urgent Messages\" BIGINT")));
            }

            foreach (var batch in wanted) database.Execute(batch);
        }

        internal static IReadOnlyList<string> ProfilingScriptBatches()
        {
            using (var stream = typeof(DatabaseUpgrader).Assembly.GetManifestResourceStream(ProfilingScript))
            {
                Assert.IsNotNull(stream, "The profiling script is embedded in the installer engine.");
                using (var reader = new StreamReader(stream))
                {
                    return Regex.Split(reader.ReadToEnd(), @"^[ \t]*GO[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                        .Where(b => !string.IsNullOrWhiteSpace(b))
                        .ToList();
                }
            }
        }

        private static string Single(IReadOnlyList<string> batches, Func<string, bool> predicate)
        {
            var found = batches.Where(predicate).ToList();
            Assert.AreEqual(1, found.Count, "Expected exactly one matching batch in the shipped profiling script.");
            return found[0];
        }
    }
}
