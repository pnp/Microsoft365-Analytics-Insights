using Common.Entities.LicenceActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class LicenceActivitySqlTests
    {
        private static readonly string[] SampleDates =
        {
            "2000-05-07", "2000-05-14", "2000-05-21", "2000-05-28",
            "2000-06-04", "2000-06-11", "2000-06-18", "2000-06-25"
        };

        private static readonly string[] CopilotD7Dates =
        {
            "2000-05-07", "2000-05-14", "2000-05-21", "2000-05-28",
            "2000-06-04", "2000-06-11", "2000-06-18", "2000-06-25"
        };

        [TestMethod]
        public void SyntheticFixture_MatchesProductionTextAndIndexBoundaries()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceSchema"))
            {
                Assert.AreEqual("varchar:250", Convert.ToString(fixture.Scalar(@"
SELECT TYPE_NAME(c.user_type_id) + ':' + CAST(c.max_length AS varchar(10))
FROM sys.columns AS c
WHERE c.object_id = OBJECT_ID(N'dbo.users') AND c.name = N'user_name';")));
                Assert.AreEqual("nvarchar:200", Convert.ToString(fixture.Scalar(@"
SELECT TYPE_NAME(c.user_type_id) + ':' + CAST(c.max_length AS varchar(10))
FROM sys.columns AS c
WHERE c.object_id = OBJECT_ID(N'dbo.user_departments') AND c.name = N'name';")));
                Assert.AreEqual(1, Convert.ToInt32(fixture.Scalar(@"
SELECT COUNT(*)
FROM sys.indexes AS i
JOIN sys.index_columns AS ic
  ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns AS c
  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(N'dbo.teams_user_activity_log')
  AND i.name = N'IX_date'
  AND c.name = N'last_activity_date'
  AND ic.key_ordinal = 2;")));
                Assert.AreEqual(1, Convert.ToInt32(fixture.Scalar(@"
SELECT COUNT(*)
FROM sys.indexes AS i
JOIN sys.index_columns AS ic
  ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns AS c
  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(N'dbo.teams_user_activity_log')
  AND i.name = N'IX_teams_user_activity_log_metrics'
  AND c.name = N'meetings_organized_count'
  AND ic.is_included_column = 1;")));
                Assert.AreEqual(1, Convert.ToInt32(fixture.Scalar(@"
SELECT COUNT(*)
FROM sys.indexes AS i
JOIN sys.index_columns AS first_key
  ON first_key.object_id = i.object_id AND first_key.index_id = i.index_id
JOIN sys.columns AS first_column
  ON first_column.object_id = first_key.object_id AND first_column.column_id = first_key.column_id
JOIN sys.index_columns AS second_key
  ON second_key.object_id = i.object_id AND second_key.index_id = i.index_id
JOIN sys.columns AS second_column
  ON second_column.object_id = second_key.object_id AND second_column.column_id = second_key.column_id
WHERE i.object_id = OBJECT_ID(N'dbo.user_license_type_lookups')
  AND i.is_unique = 1
  AND first_key.key_ordinal = 1 AND first_column.name = N'license_type_id'
  AND second_key.key_ordinal = 2 AND second_column.name = N'user_id';")));
            }
        }

        [TestMethod]
        public void DisabledSources_AreNotReferencedAndSearchNeverLowersTheUpnColumn()
        {
            var disabledSql = LicenceActivitySql.BuildOverview(Sources(usageReports: false));
            foreach (var table in new[]
            {
                "teams_user_activity_log", "outlook_user_activity_log",
                "onedrive_user_activity_log", "sharepoint_user_activity_log",
                "copilot_usage_user_activity_log", "copilot_chats", "copilot_interactions"
            })
            {
                Assert.IsFalse(disabledSql.Contains(table),
                    "A disabled source must not be queried: " + table);
            }

            var overview = new LicenceActivityOverview { Query = OverviewQuery() };
            foreach (var workload in LicenceActivityQuery.Workloads)
            {
                overview.Coverage.Add(new LicenceActivityCoverage
                {
                    Workload = workload,
                    Status = "disabled",
                    Source = string.Empty,
                    Measure = string.Empty
                });
            }
            var usersSql = LicenceActivitySql.BuildUsers(
                overview,
                OverviewQuery().ForUsers(
                    1, "teams", "ALPHA", "upn", "asc", 10, 1, 20,
                    LicenceActivitySqlFixture.NowUtc));
            Assert.IsFalse(usersSql.Contains("LOWER("));
            StringAssert.Contains(usersSql, "u.user_name LIKE @searchPattern");
            StringAssert.Contains(usersSql, "u.mail LIKE @searchPattern");
        }

        [TestMethod]
        public async Task Overview_DeduplicatesOverlappingMembershipsAndBandsMeasuredZeros()
        {
            using (var fixture = CreateMeasuredFixture())
            {
                var overview = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), Sources(usageReports: true),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);

                Assert.AreEqual(5, overview.DistinctAssignedUsers,
                    "Disabled accounts and guests still hold their current assignments.");
                Assert.AreEqual(3, overview.Licences.Count);
                Assert.AreEqual(5, overview.Licences.Single(l => l.LicenceTypeId == 1).AssignedUsers);
                Assert.AreEqual(2, overview.Licences.Single(l => l.LicenceTypeId == 2).AssignedUsers);
                Assert.AreEqual(0, overview.Licences.Single(l => l.LicenceTypeId == 3).AssignedUsers,
                    "An imported empty SKU must not disappear.");

                var teams = overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "teams");
                Assert.AreEqual(1, teams.High, "Teams high distribution");
                Assert.AreEqual(1, teams.Moderate, "Teams moderate distribution");
                Assert.AreEqual(1, teams.Low, "Teams low distribution");
                Assert.AreEqual(2, teams.Zero, "Teams zero distribution");
                Assert.AreEqual(0, teams.Unknown, "Teams unknown distribution");

                var empty = overview.Licences.Single(l => l.LicenceTypeId == 3);
                Assert.IsTrue(empty.Workloads.All(w =>
                    w.High == 0 && w.Moderate == 0 && w.Low == 0 && w.Zero == 0 && w.Unknown == 0));

                var coverage = overview.Coverage.Single(c => c.Workload == "teams");
                Assert.AreEqual("available", coverage.Status);
                Assert.AreEqual(8, coverage.ExpectedSamples);
                Assert.AreEqual(8, coverage.ObservedSamples);
                Assert.IsNull(coverage.ReportPeriodDays,
                    "The M365 tables do not persist a report-period key.");
                Assert.IsNull(coverage.LatestImportUtc,
                    "The daily report tables store report dates, not an import-completion timestamp.");
                Assert.AreEqual(new DateTime(2000, 5, 1), coverage.EffectiveFromUtc.Value);
                Assert.AreEqual(new DateTime(2000, 6, 25), coverage.EffectiveToUtc.Value);
                CollectionAssert.AreEqual(
                    SampleDates,
                    coverage.SnapshotDates.Select(d => d.ToString("yyyy-MM-dd")).ToArray());

                Assert.AreEqual("Καλημέρα κόσμε",
                    overview.Departments.Single(d => d.Id == 2).Name,
                    "The production nvarchar metadata boundary must preserve non-Latin values.");
                Assert.AreEqual(1, overview.Departments.Single(d => d.Id == 0).AssignedUsers);
            }
        }

        [TestMethod]
        public async Task LegacyDuplicateMemberships_DoNotInflateCountsOrBreakUserStaging()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceDuplicates"))
            {
                SeedDirectory(fixture);
                fixture.Execute(@"
DROP INDEX IX_license_type_id_user_id ON dbo.user_license_type_lookups;
INSERT dbo.user_license_type_lookups (user_id, license_type_id) VALUES (1, 1);");

                var sources = Sources(usageReports: false);
                var overview = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                Assert.AreEqual(5, overview.DistinctAssignedUsers);
                Assert.AreEqual(5,
                    overview.Licences.Single(l => l.LicenceTypeId == 1).AssignedUsers);

                var users = await fixture.Store().LoadUsersAsync(
                    overview,
                    OverviewQuery().ForUsers(
                        1, "teams", null, "upn", "asc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources,
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);
                Assert.AreEqual(5, users.TotalUsers);
                Assert.AreEqual(5, users.Users.Select(u => u.UserId).Distinct().Count());
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task CustomSampleWindows_DeduplicateReportDaysAndPreserveMissingVersusZero(bool columnstore)
        {
            using (var fixture = LicenceActivitySqlFixture.Create(
                "LicenceSampleWindows", columnstore
                    ? LicenceActivityUsageIndexMode.Columnstore
                    : LicenceActivityUsageIndexMode.BTreeFallback))
            {
                SeedDirectory(fixture);
                SeedOneUsageTable(fixture, "onedrive_user_activity_log",
                    new[] { "2000-06-18", "2000-06-23" });
                fixture.Execute(@"
INSERT dbo.onedrive_user_activity_log
    (viewed_or_edited, synced, shared_internally, shared_externally, user_id, [date], last_activity_date)
VALUES
    (20, 0, 0, 0, 1, '2000-06-18T12:00:00', '2000-06-18T23:59:59'),
    (3000, 0, 0, 0, 2, '2000-06-16', '2000-06-16'),
    (3000, 0, 0, 0, 2, '2000-06-24', '2000-06-24');
UPDATE dbo.onedrive_user_activity_log
SET last_activity_date = CASE WHEN [date] = '2000-06-18'
                             THEN '2000-06-13' ELSE '2000-06-24' END
WHERE user_id = 2 AND [date] IN ('2000-06-18', '2000-06-23');
UPDATE dbo.onedrive_user_activity_log SET last_activity_date = '2000-06-19'
WHERE user_id = 4;
DELETE dbo.onedrive_user_activity_log WHERE user_id = 3 AND [date] = '2000-06-23';");

                var query = LicenceActivityQuery.Create(
                    "2000-06-14", "2000-06-23", LicenceActivitySqlFixture.NowUtc);
                var sources = Sources(usageReports: true);
                var overview = await fixture.Store().LoadOverviewAsync(
                    query, sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var distribution = overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "onedrive");
                Assert.AreEqual(1, distribution.High);
                Assert.AreEqual(1, distribution.Moderate);
                Assert.AreEqual(2, distribution.Zero);
                Assert.AreEqual(1, distribution.Unknown);
                CollectionAssert.AreEqual(new[] { "2000-06-18", "2000-06-23" },
                    overview.Coverage.Single(c => c.Workload == "onedrive")
                        .SnapshotDates.Select(d => d.ToString("yyyy-MM-dd")).ToArray());

                var users = await fixture.Store().LoadUsersAsync(overview,
                    query.ForUsers(1, "onedrive", null, "upn", "asc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var active = users.Users.Single(u => u.UserId == 1)
                    .Workloads.Single(w => w.Workload == "onedrive");
                Assert.AreEqual(2, active.ActiveSamples);
                Assert.AreEqual(12d, active.AverageActions.Value,
                    "Average the per-day maxima (20 and 4), never sum or double-count duplicate snapshots.");
                Assert.AreEqual(0, users.Users.Single(u => u.UserId == 2)
                    .Workloads.Single(w => w.Workload == "onedrive").ActiveSamples);
                Assert.IsTrue(users.MostActive.Any(u => u.UserId == 3),
                    "A missing sample must not erase positive partial evidence.");
                Assert.IsFalse(users.LeastActive.Any(u => u.UserId == 3));
                Assert.AreEqual(4, users.LeastActive.Count);
            }
        }

        [TestMethod]
        public async Task ConcurrentConnections_DoNotCollideOnTempTableConstraintNames()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceTempNames"))
            {
                SeedDirectory(fixture);
                using (var blocker = new SqlConnection(fixture.ConnectionString))
                {
                    blocker.Open();
                    using (var command = new SqlCommand(@"
CREATE TABLE #probe
(
    workload tinyint NOT NULL,
    sample_date date NOT NULL,
    CONSTRAINT PK_LicenceActivity_Samples PRIMARY KEY (workload, sample_date)
);", blocker))
                    {
                        command.ExecuteNonQuery();
                    }

                    var overview = await fixture.Store().LoadOverviewAsync(
                        OverviewQuery(), Sources(usageReports: false),
                        NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                    Assert.AreEqual(3, overview.Licences.Count);
                }
            }
        }

        [TestMethod]
        public async Task SqlCommandMeasurements_DrainOnSuccessAndSiblingFailure()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceSqlLifetime"))
            {
                SeedDirectory(fixture);
                var measurement = new LicenceActivitySqlMeasurement();
                var store = fixture.Store(measurement.Instrumentation(includeShowplan: false));
                await store.LoadOverviewAsync(
                    OverviewQuery(), Sources(usageReports: true),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                Assert.AreEqual(0, measurement.ActiveCommands);
                Assert.AreEqual(0, measurement.ActiveConnections);
                Assert.IsTrue(measurement.PeakCommands >= 1);
                Assert.IsTrue(measurement.PeakConnections >= 2);

                fixture.Execute("DROP TABLE dbo.onedrive_user_activity_log;");
                await Assert.ThrowsExceptionAsync<SqlException>(() => store.LoadOverviewAsync(
                    OverviewQuery(), Sources(usageReports: true),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None));
                Assert.AreEqual(0, measurement.ActiveCommands,
                    "All sibling commands must drain before the store reports failure.");
                Assert.AreEqual(0, measurement.ActiveConnections,
                    "The shared eligibility connection must also be disposed after failure.");
            }
        }

        [TestMethod]
        public async Task Coverage_DistinguishesDisabledNotImportedPartialAndUnsettledRows()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCoverage"))
            {
                SeedDirectory(fixture);
                SeedOneUsageTable(fixture, "teams_user_activity_log", SampleDates.Take(7).ToArray());
                SeedOneUsageTable(fixture, "teams_user_activity_log", new[] { "2000-06-22", "2000-07-02" });
                var coverageQuery = LicenceActivityQuery.Create(
                    "2000-05-01", "2000-07-02", LicenceActivitySqlFixture.NowUtc);

                var partial = await fixture.Store().LoadOverviewAsync(
                    coverageQuery, Sources(usageReports: true),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);

                var teams = partial.Coverage.Single(c => c.Workload == "teams");
                Assert.AreEqual("partial", teams.Status);
                Assert.AreEqual(9, teams.ExpectedSamples);
                Assert.AreEqual(8, teams.ObservedSamples,
                    "The Thursday snapshot is valid as-of evidence, while the unsettled July 2 row is excluded.");
                Assert.AreEqual("2000-06-22", teams.SnapshotDates.Last().ToString("yyyy-MM-dd"));
                Assert.AreEqual(5, partial.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "teams").Unknown);
                Assert.AreEqual("notImported",
                    partial.Coverage.Single(c => c.Workload == "outlook").Status);

                var partialUsers = await fixture.Store().LoadUsersAsync(
                    partial,
                    coverageQuery.ForUsers(
                        1, "teams", null, "activity", "desc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    Sources(usageReports: true),
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);
                Assert.IsTrue(partialUsers.MostActive.Count > 0,
                    "Positive as-of evidence remains useful for most-active.");
                Assert.AreEqual(0, partialUsers.LeastActive.Count,
                    "An incomplete week can hide activity, so nobody is ranked least-active.");

                var disabled = await fixture.Store().LoadOverviewAsync(
                    coverageQuery, Sources(usageReports: false),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                Assert.IsTrue(disabled.Coverage
                    .Where(c => c.Workload != "copilot")
                    .All(c => c.Status == "disabled" && c.ObservedSamples == 0));
                Assert.AreEqual(5, disabled.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "teams").Unknown);

                var disabledUsers = await fixture.Store().LoadUsersAsync(
                    disabled,
                    coverageQuery.ForUsers(
                        1, "teams", null, "activity", "asc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    Sources(usageReports: false),
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);
                Assert.AreEqual(0, disabledUsers.MostActive.Count);
                Assert.AreEqual(0, disabledUsers.LeastActive.Count);
                Assert.AreEqual(5, disabledUsers.Users.Count);
                Assert.IsTrue(disabledUsers.Users.All(u =>
                    u.Workloads.Single(w => w.Workload == "teams").Band == "unknown"));
            }
        }

        [TestMethod]
        public async Task PerUserMissingRows_AreUnknownAndNeverLeastActive()
        {
            using (var fixture = CreateMeasuredFixture())
            {
                fixture.Execute(@"
DELETE FROM dbo.teams_user_activity_log WHERE user_id = 5;
DELETE FROM dbo.outlook_user_activity_log WHERE user_id = 5;
DELETE FROM dbo.onedrive_user_activity_log WHERE user_id = 5;
DELETE FROM dbo.sharepoint_user_activity_log WHERE user_id = 5;
DELETE FROM dbo.teams_user_activity_log
WHERE user_id = 3 AND [date] = '2000-05-14';
DELETE FROM dbo.outlook_user_activity_log
WHERE user_id = 3 AND [date] = '2000-05-14';
DELETE FROM dbo.onedrive_user_activity_log
WHERE user_id = 3 AND [date] = '2000-05-14';
DELETE FROM dbo.sharepoint_user_activity_log
WHERE user_id = 3 AND [date] = '2000-05-14';");

                var sources = Sources(usageReports: true);
                var overview = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);

                Assert.AreEqual("available",
                    overview.Coverage.Single(c => c.Workload == "teams").Status,
                    "Other users still prove that every global snapshot date exists.");
                var distribution = overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "teams");
                Assert.AreEqual(1, distribution.High);
                Assert.AreEqual(1, distribution.Moderate);
                Assert.AreEqual(0, distribution.Low);
                Assert.AreEqual(1, distribution.Zero);
                Assert.AreEqual(2, distribution.Unknown);
                foreach (var workload in new[] { "outlook", "onedrive", "sharepoint" })
                {
                    var other = overview.Licences.Single(l => l.LicenceTypeId == 1)
                        .Workloads.Single(w => w.Workload == workload);
                    Assert.AreEqual(1, other.Zero, workload);
                    Assert.AreEqual(2, other.Unknown, workload);
                }

                var greekDepartment = overview.Departments.Single(d => d.Id == 2)
                    .Workloads.Single(w => w.Workload == "teams");
                Assert.AreEqual(1, greekDepartment.Unknown,
                    "A user missing one weekly row is unknown in demographic aggregates.");
                var unknownDepartment = overview.Departments.Single(d => d.Id == 0)
                    .Workloads.Single(w => w.Workload == "teams");
                Assert.AreEqual(1, unknownDepartment.Unknown,
                    "A user missing every weekly row is unknown in demographic aggregates.");

                var users = await fixture.Store().LoadUsersAsync(
                    overview,
                    OverviewQuery().ForUsers(
                        1, "teams", null, "activity", "desc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources,
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);

                CollectionAssert.DoesNotContain(users.LeastActive.Select(u => u.UserId).ToList(), 3);
                CollectionAssert.DoesNotContain(users.LeastActive.Select(u => u.UserId).ToList(), 5);
                Assert.AreEqual("partial", users.Users.Single(u => u.UserId == 3)
                    .Workloads.Single(w => w.Workload == "teams").Status);
                Assert.AreEqual(7, users.Users.Single(u => u.UserId == 3)
                    .Workloads.Single(w => w.Workload == "teams").ObservedSamples);
                Assert.AreEqual("missingCoverage", users.Users.Single(u => u.UserId == 5)
                    .Workloads.Single(w => w.Workload == "teams").Status);
                Assert.AreEqual(0, users.Users.Single(u => u.UserId == 5)
                    .Workloads.Single(w => w.Workload == "teams").ObservedSamples);
                Assert.IsNull(users.Users.Single(u => u.UserId == 5)
                    .Workloads.Single(w => w.Workload == "teams").AverageActions);
                Assert.IsTrue(users.Users.Single(u => u.UserId == 5).Workloads
                    .Where(w => w.Workload != "copilot")
                    .All(w => w.Status == "missingCoverage" && w.Band == "unknown"));
                Assert.AreEqual("zero", users.Users.Single(u => u.UserId == 4)
                    .Workloads.Single(w => w.Workload == "teams").Band,
                    "A complete set of explicit zero rows remains a measured zero.");
            }
        }

        [TestMethod]
        public async Task Copilot_PrefersFullyContainedD7AndNeverCombinesPeriodCollisions()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCopilotD7"))
            {
                SeedDirectory(fixture);
                fixture.Execute(@"
INSERT dbo.copilot_usage_report_import_log
    (report_name, report_refresh_date, report_version, report_period, imported_utc,
     rows_read, rows_saved, is_upn_obfuscated, error)
VALUES
    (N'getMicrosoft365CopilotUsageUserDetail', '2000-06-25', N'v2', N'D7',
     '2000-07-01T01:00:00', 5, 5, 0, NULL);");

                for (var index = 0; index < CopilotD7Dates.Length; index++)
                {
                    var active = index < 2 ? 1 : 0;
                    var prompts = index < 2 ? 12 + index : 0;
                    var lastActivitySql = active == 1
                        ? "'" + CopilotD7Dates[index] + "'"
                        : "NULL";
                    fixture.Execute($@"
INSERT dbo.copilot_usage_user_activity_log
    (report_period_days, prompts_all_apps, active_usage_days, is_upn_obfuscated,
     user_id, [date], last_activity_date)
VALUES
    (7, {prompts}, {active}, 0, 1, '{CopilotD7Dates[index]}',
     {lastActivitySql}),
    (7, 0, 0, 0, 4, '{CopilotD7Dates[index]}', NULL),
    (28, 1000, 28, 0, 1, '{CopilotD7Dates[index]}', '{CopilotD7Dates[index]}');");
                }

                var overview = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), Sources(usageReports: false, copilotReports: true),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);

                var coverage = overview.Coverage.Single(c => c.Workload == "copilot");
                Assert.AreEqual("available", coverage.Status);
                Assert.AreEqual(7, coverage.ReportPeriodDays.Value);
                Assert.AreEqual(8, coverage.ExpectedSamples);
                Assert.AreEqual(8, coverage.SnapshotDates.Count);
                var distribution = overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "copilot");
                Assert.AreEqual(1, distribution.Moderate);
                Assert.AreEqual(1, distribution.Zero);
                Assert.AreEqual(3, distribution.Unknown);

                var users = await fixture.Store().LoadUsersAsync(
                    overview,
                    OverviewQuery().ForUsers(
                        1, "copilot", null, "activity", "desc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    Sources(usageReports: false, copilotReports: true),
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);

                var activeUser = users.Users.Single(u => u.UserId == 1)
                    .Workloads.Single(w => w.Workload == "copilot");
                Assert.AreEqual(2, activeUser.ActiveSamples);
                Assert.AreEqual("moderate", activeUser.Band);
                Assert.AreEqual(25d / 8d, activeUser.AverageActions.Value, 0.001,
                    "D28 prompt totals on the same date must not fan out or enter the D7 average.");
                Assert.AreEqual("zero", users.Users.Single(u => u.UserId == 4)
                    .Workloads.Single(w => w.Workload == "copilot").Band);
                Assert.AreEqual("unknown", users.Users.Single(u => u.UserId == 2)
                    .Workloads.Single(w => w.Workload == "copilot").Band,
                    "A missing official per-user row is unknown, not measured zero.");
            }
        }

        [TestMethod]
        public async Task Copilot_D7LeadingGapMakesCustomRangePartialAndDisablesLeastActive()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCopilotD7Gap"))
            {
                SeedDirectory(fixture);
                fixture.Execute(@"
INSERT dbo.copilot_usage_report_import_log
    (report_name, report_refresh_date, report_version, report_period, imported_utc,
     rows_read, rows_saved, is_upn_obfuscated, error)
VALUES
    (N'getMicrosoft365CopilotUsageUserDetail', '2000-06-25', N'v2', N'D7',
     '2000-07-01T01:00:00', 5, 5, 0, NULL);");
                foreach (var date in CopilotD7Dates)
                {
                    fixture.Execute($@"
INSERT dbo.copilot_usage_user_activity_log
    (report_period_days, prompts_all_apps, active_usage_days, is_upn_obfuscated,
     user_id, [date], last_activity_date)
VALUES (7, 0, 0, 0, 1, '{date}', NULL);");
                }

                var query = LicenceActivityQuery.Create(
                    "2000-05-02", "2000-06-25", LicenceActivitySqlFixture.NowUtc);
                var sources = Sources(usageReports: false, copilotReports: true);
                var overview = await fixture.Store().LoadOverviewAsync(
                    query, sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var coverage = overview.Coverage.Single(c => c.Workload == "copilot");
                Assert.AreEqual("partial", coverage.Status);
                Assert.AreEqual(new DateTime(2000, 5, 8), coverage.EffectiveFromUtc.Value);
                Assert.AreEqual(new DateTime(2000, 6, 25), coverage.EffectiveToUtc.Value);

                var users = await fixture.Store().LoadUsersAsync(
                    overview,
                    query.ForUsers(
                        1, "copilot", null, "activity", "asc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources,
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);
                Assert.AreEqual(0, users.LeastActive.Count);
                Assert.AreEqual("unknown", users.Users.Single(u => u.UserId == 1)
                    .Workloads.Single(w => w.Workload == "copilot").Band);
            }
        }

        [TestMethod]
        public async Task Copilot_LongerRollingWindowReportsItsEffectiveRangeAndMissingCoverage()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCopilotD28"))
            {
                SeedDirectory(fixture);
                fixture.Execute(@"
INSERT dbo.copilot_usage_report_import_log
    (report_name, report_refresh_date, report_version, report_period, imported_utc,
     rows_read, rows_saved, is_upn_obfuscated, error)
VALUES
    (N'getMicrosoft365CopilotUsageUserDetail', '2000-06-25', N'v2', N'D28',
     '2000-07-01T01:00:00', 5, 5, 0, NULL);
INSERT dbo.copilot_usage_user_activity_log
    (report_period_days, prompts_all_apps, active_usage_days, is_upn_obfuscated,
     user_id, [date], last_activity_date)
VALUES (28, 40, 10, 0, 1, '2000-06-25', '2000-06-24');");
                fixture.Execute(@"
INSERT dbo.copilot_usage_user_activity_log
    (report_period_days, prompts_all_apps, active_usage_days, is_upn_obfuscated,
     user_id, [date], last_activity_date)
SELECT 28, 0, 0, 0, id, '2000-06-25', NULL
FROM dbo.users
WHERE id IN (2, 3, 4, 5);
UPDATE dbo.copilot_usage_user_activity_log
SET prompts_all_apps = -2, active_usage_days = -1
WHERE user_id = 5 AND report_period_days = 28;");

                var wide = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), Sources(usageReports: false, copilotReports: true),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var wideCoverage = wide.Coverage.Single(c => c.Workload == "copilot");
                Assert.AreEqual("missingCoverage", wideCoverage.Status);
                Assert.AreEqual(28, wideCoverage.ReportPeriodDays.Value);
                Assert.AreEqual(new DateTime(2000, 5, 29), wideCoverage.EffectiveFromUtc.Value);
                Assert.AreEqual(new DateTime(2000, 6, 25), wideCoverage.EffectiveToUtc.Value);

                var wideUsers = await fixture.Store().LoadUsersAsync(
                    wide,
                    OverviewQuery().ForUsers(
                        1, "copilot", null, "activity", "desc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    Sources(usageReports: false, copilotReports: true),
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);
                Assert.AreEqual("unknown", wideUsers.Users.Single(u => u.UserId == 1)
                    .Workloads.Single(w => w.Workload == "copilot").Band,
                    "A D28 snapshot inside a 56-day request must not be presented as full-range evidence.");

                var exactQuery = LicenceActivityQuery.Create(
                    "2000-05-29", "2000-06-25", LicenceActivitySqlFixture.NowUtc);
                var exact = await fixture.Store().LoadOverviewAsync(
                    exactQuery, Sources(usageReports: false, copilotReports: true),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                Assert.AreEqual("available", exact.Coverage.Single(c => c.Workload == "copilot").Status);
                var exactDistribution = exact.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "copilot");
                Assert.AreEqual(1, exactDistribution.Moderate);
                Assert.AreEqual(3, exactDistribution.Zero);
                Assert.AreEqual(1, exactDistribution.Unknown);

                var exactUsers = await fixture.Store().LoadUsersAsync(
                    exact,
                    exactQuery.ForUsers(
                        1, "copilot", null, "activity", "desc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    Sources(usageReports: false, copilotReports: true),
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);
                var invalid = exactUsers.Users.Single(u => u.UserId == 5)
                    .Workloads.Single(w => w.Workload == "copilot");
                Assert.AreEqual("partial", invalid.Status);
                Assert.IsNull(invalid.AverageActions);
                CollectionAssert.DoesNotContain(exactUsers.LeastActive.Select(u => u.UserId).ToList(), 5);
                Assert.AreEqual("zero", exactUsers.Users.Single(u => u.UserId == 2)
                    .Workloads.Single(w => w.Workload == "copilot").Band);
            }
        }

        [TestMethod]
        public async Task Copilot_LongPeriodChoicePrefersLargestContainedCoverageAfterExactMatch()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCopilotPeriodChoice"))
            {
                SeedDirectory(fixture);
                fixture.Execute(@"
INSERT dbo.copilot_usage_report_import_log
    (report_name, report_refresh_date, report_version, report_period, imported_utc,
     rows_read, rows_saved, is_upn_obfuscated, error)
VALUES
    (N'getMicrosoft365CopilotUsageUserDetail', '2000-06-25', N'v2', N'D90',
     '2000-07-01T01:00:00', 2, 2, 0, NULL);
INSERT dbo.copilot_usage_user_activity_log
    (report_period_days, prompts_all_apps, active_usage_days, is_upn_obfuscated,
     user_id, [date], last_activity_date)
VALUES
    (28, 10, 4, 0, 1, '2000-06-25', '2000-06-24'),
    (90, 30, 12, 0, 1, '2000-06-25', '2000-06-24');");

                var query = LicenceActivityQuery.Create(
                    "1999-12-29", "2000-06-25", LicenceActivitySqlFixture.NowUtc);
                var overview = await fixture.Store().LoadOverviewAsync(
                    query, Sources(usageReports: false, copilotReports: true),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var coverage = overview.Coverage.Single(c => c.Workload == "copilot");
                Assert.AreEqual(90, coverage.ReportPeriodDays.Value);
                Assert.AreEqual(new DateTime(2000, 3, 28), coverage.EffectiveFromUtc.Value);
            }
        }

        [TestMethod]
        public async Task Copilot_V1LastActivityIsPositiveEvidenceButNotFrequencyOrLeastActiveProof()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCopilotV1"))
            {
                SeedDirectory(fixture);
                fixture.Execute(@"
INSERT dbo.copilot_usage_report_import_log
    (report_name, report_refresh_date, report_version, report_period, imported_utc,
     rows_read, rows_saved, is_upn_obfuscated, error)
VALUES
    (N'getMicrosoft365CopilotUsageUserDetail', '2000-06-25', N'v1', N'D7',
     '2000-07-01T01:00:00', 1, 1, 0, NULL);");
                foreach (var date in CopilotD7Dates)
                {
                    fixture.Execute($@"
INSERT dbo.copilot_usage_user_activity_log
    (report_period_days, prompts_all_apps, active_usage_days, is_upn_obfuscated,
     user_id, [date], last_activity_date)
VALUES (7, NULL, NULL, 0, 1, '{date}', '{date}');");
                }

                var sources = Sources(usageReports: false, copilotReports: true);
                var overview = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var distribution = overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "copilot");
                Assert.AreEqual(5, distribution.Unknown);
                Assert.AreEqual(0, distribution.High);

                var users = await fixture.Store().LoadUsersAsync(
                    overview,
                    OverviewQuery().ForUsers(
                        1, "copilot", null, "activity", "desc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources,
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);
                var evidence = users.MostActive.Single().Workloads
                    .Single(w => w.Workload == "copilot");
                Assert.AreEqual("partial", evidence.Status);
                Assert.AreEqual("unknown", evidence.Band);
                Assert.AreEqual(0, users.LeastActive.Count);
            }
        }

        [TestMethod]
        public async Task Copilot_NegativeD7MetricsAreUnknownWhileExplicitZeroRemainsMeasured()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCopilotNegative"))
            {
                SeedDirectory(fixture);
                fixture.Execute(@"
INSERT dbo.copilot_usage_report_import_log
    (report_name, report_refresh_date, report_version, report_period, imported_utc,
     rows_read, rows_saved, is_upn_obfuscated, error)
VALUES
    (N'getMicrosoft365CopilotUsageUserDetail', '2000-06-25', N'v2', N'D7',
     '2000-07-01T01:00:00', 2, 2, 0, NULL);");

                for (var index = 0; index < CopilotD7Dates.Length; index++)
                {
                    var date = CopilotD7Dates[index];
                    var activeDays = index == 0 ? -1 : 1;
                    var prompts = index == 0 ? -2 : 5;
                    var lastActivitySql = index == 0 ? "NULL" : "'" + date + "'";
                    fixture.Execute($@"
INSERT dbo.copilot_usage_user_activity_log
    (report_period_days, prompts_all_apps, active_usage_days, is_upn_obfuscated,
     user_id, [date], last_activity_date)
VALUES
    (7, {prompts}, {activeDays}, 0, 1, '{date}',
     {lastActivitySql}),
    (7, 0, 0, 0, 4, '{date}', NULL);");
                }

                var sources = Sources(usageReports: false, copilotReports: true);
                var overview = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var distribution = overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "copilot");
                Assert.AreEqual(1, distribution.Zero);
                Assert.AreEqual(4, distribution.Unknown);

                var users = await fixture.Store().LoadUsersAsync(
                    overview,
                    OverviewQuery().ForUsers(
                        1, "copilot", null, "activity", "desc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources,
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);
                var invalid = users.Users.Single(u => u.UserId == 1)
                    .Workloads.Single(w => w.Workload == "copilot");
                Assert.AreEqual("partial", invalid.Status);
                Assert.AreEqual("unknown", invalid.Band);
                Assert.IsNull(invalid.AverageActions,
                    "A negative prompt counter must not enter an average.");
                CollectionAssert.DoesNotContain(users.LeastActive.Select(u => u.UserId).ToList(), 1);
                Assert.AreEqual("zero", users.Users.Single(u => u.UserId == 4)
                    .Workloads.Single(w => w.Workload == "copilot").Band);
            }
        }

        [TestMethod]
        public async Task Copilot_ConcealedIdentitiesUsePositiveOnlyFallbackAndNeverCreateFalseZeros()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCopilotFallback"))
            {
                SeedDirectory(fixture);
                fixture.Execute(@"
INSERT dbo.copilot_usage_report_import_log
    (report_name, report_refresh_date, report_version, report_period, imported_utc,
     rows_read, rows_saved, is_upn_obfuscated, error)
VALUES
    (N'getMicrosoft365CopilotUsageUserDetail', '2000-06-30', N'v2', N'D28',
     '2000-07-01T01:00:00', 5, 0, 1, NULL);
INSERT dbo.copilot_chats (event_id, app_host, user_id, time_stamp)
VALUES
    ('00000000-0000-0000-0000-000000000001', N'Teams', 1, '2000-06-20'),
    ('00000000-0000-0000-0000-000000000002', N'Teams', 1, '2000-06-21');");

                var sources = Sources(
                    usageReports: false, copilotReports: true, copilotAudit: true);
                var overview = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);

                var coverage = overview.Coverage.Single(c => c.Workload == "copilot");
                Assert.AreEqual("unmatchableIdentity", coverage.Status);
                Assert.AreEqual("copilotAudit", coverage.Source);
                Assert.AreEqual(5, coverage.UnmatchedUsers);
                Assert.AreEqual(5, overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "copilot").Unknown);

                var users = await fixture.Store().LoadUsersAsync(
                    overview,
                    OverviewQuery().ForUsers(
                        1, "copilot", null, "activity", "desc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources,
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);

                Assert.AreEqual(1, users.MostActive[0].UserId);
                Assert.AreEqual(0, users.LeastActive.Count,
                    "Positive-only event evidence must never turn absence into a least-active zero.");
                Assert.AreEqual("unknown", users.MostActive[0].Workloads
                    .Single(w => w.Workload == "copilot").Band);
            }
        }

        [TestMethod]
        public async Task Copilot_InteractionHistoryIsUsedWhenItIsTheOnlyEnabledPositiveSource()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCopilotInteractions"))
            {
                SeedDirectory(fixture);
                fixture.Execute(@"
INSERT dbo.copilot_interactions
    (graph_interaction_id, session_id, user_id, created_utc,
     body_char_count, body_word_count, attachment_count, link_count,
     mention_count, context_count)
VALUES
    (N'synthetic-interaction-1', 1, 2, '2000-06-20', 20, 4, 0, 0, 0, 0);
INSERT dbo.copilot_interaction_import_log
    (run_started_utc, run_finished_utc, users_in_scope, users_scanned, users_skipped,
     users_failed, interactions_read, interactions_saved, cognitive_docs_scored, error)
VALUES
    ('2000-07-01T00:00:00', '2000-07-01T00:05:00', 5, 5, 0, 0, 1, 1, 0, NULL);");

                var sources = Sources(
                    usageReports: false, copilotInteractions: true);
                var overview = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var coverage = overview.Coverage.Single(c => c.Workload == "copilot");
                Assert.AreEqual("partial", coverage.Status);
                Assert.AreEqual("copilotInteractions", coverage.Source);
                Assert.AreEqual(new DateTime(2000, 7, 1, 0, 5, 0, DateTimeKind.Utc),
                    coverage.LatestImportUtc.Value);

                var users = await fixture.Store().LoadUsersAsync(
                    overview,
                    OverviewQuery().ForUsers(
                        1, "copilot", null, "activity", "desc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources,
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);
                Assert.AreEqual(2, users.MostActive.Single().UserId);
                Assert.AreEqual(0, users.LeastActive.Count);
            }
        }

        [TestMethod]
        public async Task Users_RankPageFilterAndEscapeSearchInSqlWithDeterministicTies()
        {
            using (var fixture = CreateMeasuredFixture())
            {
                var sources = Sources(usageReports: true);
                var overview = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);

                var query = OverviewQuery().ForUsers(
                    1, "teams", null, "activity", "desc", 2, 1, 2,
                    LicenceActivitySqlFixture.NowUtc);
                var users = await fixture.Store().LoadUsersAsync(
                    overview, query, sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);

                CollectionAssert.AreEqual(
                    new[] { 1, 2 },
                    users.MostActive.Select(u => u.UserId).ToArray());
                CollectionAssert.AreEqual(
                    new[] { 4, 5 },
                    users.LeastActive.Select(u => u.UserId).ToArray(),
                    "Measured-zero ties use UPN then numeric id and include disabled accounts.");
                Assert.AreEqual(0, users.LeastActive.Single(u => u.UserId == 5)
                    .Workloads.Single(w => w.Workload == "teams").ActiveSamples,
                    "Positive snapshot counters do not prove weekly activity when last_activity_date is outside that week.");
                Assert.AreEqual(5, users.TotalUsers);
                Assert.AreEqual(5, users.RankedUsers);
                Assert.AreEqual(2, users.Users.Count);

                var secondPage = query.ForUsers(
                    1, "teams", null, "activity", "desc", 2, 2, 2,
                    LicenceActivitySqlFixture.NowUtc);
                var secondPageUsers = await fixture.Store().LoadUsersAsync(
                    overview, secondPage, sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                CollectionAssert.AreEqual(
                    new[] { 3, 5 },
                    secondPageUsers.Users.Select(u => u.UserId).ToArray(),
                    "The browse order is activity descending, then the published-counter average; user 5 has an out-of-week counter but still a zero activity band.");

                var underscore = query.ForUsers(
                    1, "teams", "_", "upn", "asc", 10, 1, 20,
                    LicenceActivitySqlFixture.NowUtc);
                var underscoreUsers = await fixture.Store().LoadUsersAsync(
                    overview, underscore, sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                Assert.AreEqual(1, underscoreUsers.TotalUsers);
                Assert.AreEqual("zero_user@contoso.example", underscoreUsers.Users[0].UserPrincipalName);

                var caseInsensitive = query.ForUsers(
                    1, "teams", "ALPHA", "upn", "asc", 10, 1, 20,
                    LicenceActivitySqlFixture.NowUtc);
                var caseInsensitiveUsers = await fixture.Store().LoadUsersAsync(
                    overview, caseInsensitive, sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                Assert.AreEqual("alpha@contoso.example", caseInsensitiveUsers.Users.Single().UserPrincipalName);

                var mailAlias = query.ForUsers(
                    1, "teams", "alias.search", "upn", "asc", 10, 1, 20,
                    LicenceActivitySqlFixture.NowUtc);
                var mailAliasUsers = await fixture.Store().LoadUsersAsync(
                    overview, mailAlias, sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                Assert.AreEqual("zero_user@contoso.example", mailAliasUsers.Users.Single().UserPrincipalName);

                foreach (var literal in new[] { "%", "[", "Καλημέρα" })
                {
                    var escaped = query.ForUsers(
                        1, "teams", literal, "upn", "asc", 10, 1, 20,
                        LicenceActivitySqlFixture.NowUtc);
                    var escapedUsers = await fixture.Store().LoadUsersAsync(
                        overview, escaped, sources,
                        NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                    Assert.AreEqual(0, escapedUsers.TotalUsers,
                        "LIKE metacharacters and Unicode input must remain data, never SQL syntax.");
                }

                var greekDepartment = LicenceActivityQuery.Create(
                    OverviewQuery().From, OverviewQuery().To, LicenceActivitySqlFixture.NowUtc,
                    departmentId: 2, licenceTypeId: 1, workload: "teams");
                var filteredOverview = await fixture.Store().LoadOverviewAsync(
                    greekDepartment, sources,
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                Assert.AreEqual(1, filteredOverview.DistinctAssignedUsers);
                Assert.AreEqual("Καλημέρα κόσμε", filteredOverview.Departments.Single().Name);
            }
        }

        [TestMethod]
        public async Task Overview_RejectsMoreThanFiveHundredImportedSkusExplicitly()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceLimit"))
            {
                fixture.Execute(@"
;WITH E1(n) AS
(
    SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS n(n)
),
E2(n) AS (SELECT 0 FROM E1 AS a CROSS JOIN E1 AS b),
E4(n) AS (SELECT 0 FROM E2 AS a CROSS JOIN E2 AS b),
Numbers(n) AS
(
    SELECT TOP (501) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
    FROM E4
)
INSERT dbo.license_types (name, sku_id)
SELECT N'Synthetic limit ' + CAST(n AS nvarchar(10)),
       N'LIMIT_' + CAST(n AS nvarchar(10))
FROM Numbers;");

                var exception = await Assert.ThrowsExceptionAsync<System.Data.SqlClient.SqlException>(() =>
                    fixture.Store().LoadOverviewAsync(
                        OverviewQuery(), Sources(usageReports: false),
                        NullLicenceActivityDiagnostics.Instance, CancellationToken.None));
                StringAssert.Contains(exception.Message, "at most 500");
            }
        }

        [TestMethod]
        public async Task Demographics_AreBoundedToFiftyButRetainTheSelectedValue()
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceDemographics"))
            {
                fixture.Execute(@"
;WITH E1(n) AS
(
    SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS n(n)
),
E2(n) AS (SELECT 0 FROM E1 AS a CROSS JOIN E1 AS b),
Numbers(n) AS
(
    SELECT TOP (51) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
    FROM E2
)
INSERT dbo.user_departments (name)
SELECT N'Synthetic department ' + RIGHT(N'00' + CAST(n AS nvarchar(2)), 2)
FROM Numbers;

INSERT dbo.license_types (name, sku_id)
VALUES (N'Synthetic demographic SKU', N'SYNTHETIC_DEMOGRAPHIC');

;WITH E1(n) AS
(
    SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS n(n)
),
E2(n) AS (SELECT 0 FROM E1 AS a CROSS JOIN E1 AS b),
Numbers(n) AS
(
    SELECT TOP (51) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
    FROM E2
)
INSERT dbo.users (user_name, account_enabled, department_id)
SELECT 'demographic' + CAST(n AS varchar(2)) + '@contoso.example', 1, n
FROM Numbers;

INSERT dbo.user_license_type_lookups (user_id, license_type_id)
SELECT id, 1 FROM dbo.users;");

                var unfiltered = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), Sources(usageReports: false),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                Assert.AreEqual(50, unfiltered.Departments.Count);
                Assert.IsTrue(unfiltered.DemographicsTruncated);

                var selectedQuery = LicenceActivityQuery.Create(
                    OverviewQuery().From, OverviewQuery().To,
                    LicenceActivitySqlFixture.NowUtc, departmentId: 51);
                var selected = await fixture.Store().LoadOverviewAsync(
                    selectedQuery, Sources(usageReports: false),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                Assert.AreEqual(1, selected.Departments.Count);
                Assert.AreEqual(51, selected.Departments[0].Id);
                Assert.AreEqual(1, selected.Departments[0].AssignedUsers);
            }
        }

        private static LicenceActivitySqlFixture CreateMeasuredFixture()
        {
            var fixture = LicenceActivitySqlFixture.Create("LicenceMeasured");
            SeedDirectory(fixture);
            SeedAllUsageTables(fixture, SampleDates);
            fixture.Execute(@"
INSERT dbo.teams_user_activity_log
(
    private_chat_count, team_chat_count, calls_count, meetings_count,
    adhoc_meetings_attended_count, adhoc_meetings_organized_count,
    meetings_attended_count, meetings_organized_count,
    scheduled_onetime_meetings_attended_count, scheduled_onetime_meetings_organized_count,
    scheduled_recurring_meetings_attended_count, scheduled_recurring_meetings_organized_count,
    audio_duration_seconds, video_duration_seconds, screenshare_duration_seconds,
    post_messages, reply_messages, urgent_messages, user_id, [date], last_activity_date
)
VALUES
    (99, 99, 0, 0, 0, 0, 99, 99, 0, 0, 0, 0, 0, 0, 0, 99, 99, 0,
     5, '2000-05-07', '2000-04-24');");
            return fixture;
        }

        private static void SeedDirectory(LicenceActivitySqlFixture fixture)
        {
            fixture.Execute(@"
INSERT dbo.user_departments (name)
VALUES (N'Engineering'), (N'Καλημέρα κόσμε');
INSERT dbo.user_country_or_region (name)
VALUES (N'Contoso North'), (N'Contoso South');

INSERT dbo.users
    (user_name, mail, account_enabled, department_id, country_or_region_id)
VALUES
    ('alpha@contoso.example', N'alpha@contoso.example', 1, 1, 1),
    ('beta@contoso.example', N'beta@contoso.example', 1, 1, 1),
    ('guest#EXT#@contoso.example', N'guest#EXT#@contoso.example', 1, 2, 2),
    ('disabled@contoso.example', N'disabled@contoso.example', 0, 1, 1),
    ('zero_user@contoso.example', N'alias.search@contoso.example', NULL, NULL, NULL);

INSERT dbo.license_types (name, sku_id)
VALUES
    (N'Contoso Suite', N'CONTOSO_SUITE'),
    (N'Contoso Add-on', N'CONTOSO_ADDON'),
    (N'Contoso Empty', N'CONTOSO_EMPTY');

INSERT dbo.user_license_type_lookups (user_id, license_type_id)
VALUES
    (1, 1), (2, 1), (3, 1), (4, 1), (5, 1),
    (1, 2), (4, 2);");
        }

        private static void SeedAllUsageTables(
            LicenceActivitySqlFixture fixture,
            string[] sampleDates)
        {
            SeedOneUsageTable(fixture, "teams_user_activity_log", sampleDates);
            SeedOneUsageTable(fixture, "outlook_user_activity_log", sampleDates);
            SeedOneUsageTable(fixture, "onedrive_user_activity_log", sampleDates);
            SeedOneUsageTable(fixture, "sharepoint_user_activity_log", sampleDates);
        }

        private static void SeedOneUsageTable(
            LicenceActivitySqlFixture fixture,
            string table,
            string[] sampleDates)
        {
            for (var index = 0; index < sampleDates.Length; index++)
            {
                var date = sampleDates[index];
                var activeUsers = index == 0
                    ? "1,2,3"
                    : index == 1 ? "1,2" : "1";

                if (table == "teams_user_activity_log")
                {
                    fixture.Execute($@"
INSERT dbo.teams_user_activity_log
(
    private_chat_count, team_chat_count, calls_count, meetings_count,
    adhoc_meetings_attended_count, adhoc_meetings_organized_count,
    meetings_attended_count, meetings_organized_count,
    scheduled_onetime_meetings_attended_count, scheduled_onetime_meetings_organized_count,
    scheduled_recurring_meetings_attended_count, scheduled_recurring_meetings_organized_count,
    audio_duration_seconds, video_duration_seconds, screenshare_duration_seconds,
    post_messages, reply_messages, urgent_messages, user_id, [date], last_activity_date
)
SELECT CASE WHEN id IN ({activeUsers}) THEN 2 ELSE 0 END,
       CASE WHEN id IN ({activeUsers}) THEN 1 ELSE 0 END,
       0, 0, 0, 0,
       CASE WHEN id IN ({activeUsers}) THEN 1 ELSE 0 END,
       0, 0, 0, 0, 0, 0, 0, 0,
       CASE WHEN id IN ({activeUsers}) THEN 1 ELSE 0 END,
       0, 0, id, '{date}',
       CASE WHEN id IN ({activeUsers}) THEN '{date}' ELSE NULL END
FROM dbo.users;");
                }
                else if (table == "outlook_user_activity_log")
                {
                    fixture.Execute($@"
INSERT dbo.outlook_user_activity_log
    (email_send_count, email_receive_count, email_read_count,
     meeting_created_count, meeting_interacted_count, user_id, [date], last_activity_date)
SELECT CASE WHEN id IN ({activeUsers}) THEN 2 ELSE 0 END,
       CASE WHEN id IN ({activeUsers}) THEN 1 ELSE 0 END,
       CASE WHEN id IN ({activeUsers}) THEN 3 ELSE 0 END,
       0, 0, id, '{date}',
       CASE WHEN id IN ({activeUsers}) THEN '{date}' ELSE NULL END
FROM dbo.users;");
                }
                else
                {
                    fixture.Execute($@"
INSERT dbo.{table}
    (viewed_or_edited, synced, shared_internally, shared_externally,
     user_id, [date], last_activity_date)
SELECT CASE WHEN id IN ({activeUsers}) THEN 4 ELSE 0 END,
       CASE WHEN id IN ({activeUsers}) THEN 1 ELSE 0 END,
       0, 0, id, '{date}',
       CASE WHEN id IN ({activeUsers}) THEN '{date}' ELSE NULL END
FROM dbo.users;");
                }
            }
        }

        private static LicenceActivityQuery OverviewQuery()
        {
            return LicenceActivityQuery.Create(
                "2000-05-01", "2000-06-25", LicenceActivitySqlFixture.NowUtc);
        }

        private static LicenceActivitySources Sources(
            bool usageReports,
            bool copilotReports = false,
            bool copilotAudit = false,
            bool copilotInteractions = false)
        {
            return new LicenceActivitySources
            {
                UserMetadata = true,
                UsageReports = usageReports,
                CopilotUsageReports = copilotReports,
                CopilotAudit = copilotAudit,
                CopilotInteractions = copilotInteractions,
                NowUtc = LicenceActivitySqlFixture.NowUtc
            };
        }
    }
}
