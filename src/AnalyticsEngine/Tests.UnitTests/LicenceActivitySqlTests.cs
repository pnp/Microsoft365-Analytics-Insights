using Common.Entities.LicenceActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Globalization;
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
-- Every one of this person's rows inside the period reports a last-activity date OUTSIDE the week
-- it sits in, however large its counters are. Big rolling counters are not activity in that week.
UPDATE dbo.onedrive_user_activity_log
SET last_activity_date = CASE WHEN [date] < '2000-06-19'
                             THEN '2000-06-13' ELSE '2000-06-24' END
WHERE user_id = 2 AND [date] >= '2000-06-14' AND [date] < '2000-06-24';
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
                Assert.AreEqual(2, distribution.Moderate);
                Assert.AreEqual(2, distribution.Zero);
                Assert.AreEqual(0, distribution.Unknown,
                    "Both part-weeks were imported day by day, so nobody is unmeasured.");
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
                Assert.AreEqual(5.6d, active.AverageActions.Value, 0.000001,
                    "Average the per-day maxima across the ten measured days (one 20, nine 4s), "
                    + "never sum or double-count duplicate rows for the same day.");
                Assert.AreEqual(0, users.Users.Single(u => u.UserId == 2)
                    .Workloads.Single(w => w.Workload == "onedrive").ActiveSamples,
                    "Positive counters do not prove activity when every last-activity date "
                    + "falls outside the week the row sits in.");
                var lostOneDay = users.Users.Single(u => u.UserId == 3)
                    .Workloads.Single(w => w.Workload == "onedrive");
                Assert.AreEqual(1, lostOneDay.ActiveSamples);
                Assert.AreEqual(2, lostOneDay.ObservedSamples,
                    "Losing this person's row for one day leaves the week itself measured.");
                Assert.IsTrue(users.MostActive.Any(u => u.UserId == 3));
                Assert.AreEqual(5, users.LeastActive.Count);
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

        [DataTestMethod]
        [DataRow(7, false)]
        [DataRow(28, false)]
        [DataRow(90, false)]
        [DataRow(180, false)]
        [DataRow(7, true)]
        [DataRow(28, true)]
        [DataRow(90, true)]
        [DataRow(180, true)]
        public async Task DistinctWeeklyReadings_PreserveZeroDuplicatesAndExpectedBoundary(
            int days, bool columnstore)
        {
            using (var fixture = LicenceActivitySqlFixture.Create(
                "LicenceDistinctSampleCounts", columnstore
                    ? LicenceActivityUsageIndexMode.Columnstore
                    : LicenceActivityUsageIndexMode.BTreeFallback))
            {
                SeedDirectory(fixture);
                var from = new DateTime(2000, 1, 2); // A Sunday start exercises both partial edge weeks.
                var to = from.AddDays(days - 1);
                var dates = Enumerable.Range(0, days).Select(day => from.AddDays(day))
                    .Where(date => date.DayOfWeek == DayOfWeek.Sunday || date == to)
                    .Select(date => date.ToString("yyyy-MM-dd")).ToArray();
                Assert.IsTrue(dates.Length > 1,
                    "This regression must execute the multi-sample distinct-count branch.");
                if (days == 180) Assert.AreEqual(27, dates.Length);
                var workloads = new[] { "teams", "outlook", "onedrive", "sharepoint" };
                foreach (var workload in workloads)
                {
                    var table = workload + "_user_activity_log";
                    SeedOneUsageTable(fixture, table, dates);
                    SeedOneUsageTable(fixture, table, dates);
                    SeedOneUsageTable(fixture, table, dates);
                    var metric = workload == "teams" ? "private_chat_count"
                        : workload == "outlook" ? "email_send_count" : "viewed_or_edited";
                    fixture.Execute($@"
;WITH DuplicateRows AS
(
    SELECT id,
           ROW_NUMBER() OVER
               (PARTITION BY user_id, CAST([date] AS date) ORDER BY id) AS duplicate_number
    FROM dbo.{table}
)
UPDATE activity
SET [date] = DATEADD(HOUR, (duplicates.duplicate_number - 1) * 6, activity.[date])
FROM dbo.{table} AS activity
JOIN DuplicateRows AS duplicates ON duplicates.id = activity.id;

UPDATE dbo.{table}
SET {metric} = CASE WHEN [date] = '{dates.Last()}T00:00:00' THEN {metric} + 16 ELSE 0 END,
    last_activity_date =
        CASE
            WHEN [date] = '{dates.Last()}T00:00:00' THEN '{dates.Last()}'
            WHEN [date] = '{dates.Last()}T06:00:00' THEN DATEADD(DAY, -7, CAST([date] AS date))
            ELSE NULL
        END
WHERE user_id = 1
  AND CAST([date] AS date) = '{dates.Last()}';

DELETE dbo.{table} WHERE user_id = 5
    OR (user_id = 3 AND CAST([date] AS date) = '{dates.Last()}');");
                }
                var query = LicenceActivityQuery.Create(
                    from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), LicenceActivitySqlFixture.NowUtc);
                var sources = Sources(usageReports: true);
                var overview = await fixture.Store().LoadOverviewAsync(
                    query, sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                foreach (var workload in workloads)
                {
                    var coverage = overview.Coverage.Single(c => c.Workload == workload);
                    Assert.AreEqual("available", coverage.Status);
                    Assert.AreEqual(dates.Length, coverage.ExpectedSamples);
                    var distribution = overview.Licences.Single(l => l.LicenceTypeId == 1)
                        .Workloads.Single(w => w.Workload == workload);
                    Assert.AreEqual(2, distribution.Zero,
                        "The explicit-zero user and the user with no rows at all are both measured zeros.");
                    Assert.AreEqual(0, distribution.Unknown,
                        "Every week was imported in full, so nobody is unmeasured.");
                    Assert.AreEqual(3, distribution.High + distribution.Moderate + distribution.Low,
                        "Three users have positive evidence.");
                    Assert.AreEqual(5,
                        distribution.High + distribution.Moderate + distribution.Low + distribution.Zero,
                        "Everyone holding the licence has complete evidence at the expected-reading boundary.");
                    var users = await fixture.Store().LoadUsersAsync(
                        overview, query.ForUsers(1, workload, null, "activity", "desc", 5, 1, 20,
                            LicenceActivitySqlFixture.NowUtc),
                        sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                    var active = users.Users.Single(u => u.UserId == 1).Workloads.Single(w => w.Workload == workload);
                    Assert.AreEqual(dates.Length, active.ActiveSamples);
                    Assert.AreEqual(dates.Length, active.ObservedSamples);
                    Assert.AreEqual((workload == "teams" || workload == "outlook" ? 5d : 4d)
                        + 16d / days, active.AverageActions.Value, 0.000001,
                        "Average actions stays a per-report-day figure, so duplicate rows for one day "
                        + "collapse to that day's maximum and the extra 16 is spread over the days measured.");
                    var explicitZero = users.Users.Single(u => u.UserId == 4)
                        .Workloads.Single(w => w.Workload == workload);
                    Assert.AreEqual("zero", explicitZero.Band);
                    Assert.AreEqual(dates.Length, explicitZero.ObservedSamples);
                    Assert.AreEqual(0, explicitZero.ActiveSamples);
                    var lostOneDay = users.Users.Single(u => u.UserId == 3)
                        .Workloads.Single(w => w.Workload == workload);
                    Assert.AreEqual("available", lostOneDay.Status,
                        "Losing one of a person's report days does not unmeasure them: the week around "
                        + "it was still imported in full.");
                    Assert.AreEqual(dates.Length, lostOneDay.ObservedSamples);
                    Assert.AreEqual(1, lostOneDay.ActiveSamples);
                    CollectionAssert.Contains(users.LeastActive.Select(u => u.UserId).ToList(), 5);
                    Assert.IsTrue(users.MostActive.Any(u => u.UserId == 3));
                    var absent = users.Users.Single(u => u.UserId == 5)
                        .Workloads.Single(w => w.Workload == workload);
                    Assert.AreEqual("available", absent.Status);
                    Assert.AreEqual(dates.Length, absent.ObservedSamples);
                    Assert.AreEqual(0, absent.ActiveSamples);
                    Assert.AreEqual("zero", absent.Band);
                    Assert.AreEqual(0d, absent.AverageActions.Value);
                }
            }
        }

        [DataTestMethod]
        [DataRow("teams", false)]
        [DataRow("outlook", false)]
        [DataRow("onedrive", false)]
        [DataRow("sharepoint", false)]
        [DataRow("teams", true)]
        [DataRow("outlook", true)]
        [DataRow("onedrive", true)]
        [DataRow("sharepoint", true)]
        public async Task SingleWeek_DuplicateReportDaysAgreeWithDrilldown(
            string workload, bool columnstore)
        {
            using (var fixture = LicenceActivitySqlFixture.Create(
                "LicenceSingleDayDuplicates", columnstore
                    ? LicenceActivityUsageIndexMode.Columnstore
                    : LicenceActivityUsageIndexMode.BTreeFallback))
            {
                SeedDirectory(fixture);
                var table = workload + "_user_activity_log";
                SeedOneUsageTable(fixture, table, new[] { "2000-06-25", "2000-06-25" });
                fixture.Execute($@"
UPDATE dbo.{table}
SET [date] = DATEADD(HOUR, 12, [date])
WHERE id IN (SELECT MAX(id) FROM dbo.{table} GROUP BY user_id);
UPDATE dbo.{table} SET last_activity_date = '2000-06-18' WHERE user_id = 2;
DELETE dbo.{table} WHERE user_id = 3;
DELETE dbo.{table} WHERE user_id = 5 AND [date] = '2000-06-25T12:00:00';");

                var query = LicenceActivityQuery.Create(
                    "2000-06-19", "2000-06-25", LicenceActivitySqlFixture.NowUtc);
                var sources = Sources(usageReports: true);
                var overview = await fixture.Store().LoadOverviewAsync(
                    query, sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var users = await fixture.Store().LoadUsersAsync(
                    overview, query.ForUsers(1, workload, null, "upn", "asc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var distribution = overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == workload);
                var coverage = overview.Coverage.Single(c => c.Workload == workload);
                Assert.AreEqual("available", coverage.Status);
                Assert.AreEqual(1, coverage.ExpectedSamples);
                Assert.AreEqual(1, coverage.ObservedSamples);
                Assert.AreEqual(1, distribution.High,
                    "Duplicate rows on the same report day are still one observed active week.");
                Assert.AreEqual(4, distribution.Zero,
                    "Everyone else was measured across a week imported in full and did nothing in it, "
                    + "including the user with no report rows at all.");
                Assert.AreEqual(0, distribution.Unknown);
                Assert.AreEqual(0, distribution.Moderate + distribution.Low);
                Assert.AreEqual(5, users.TotalUsers);
                foreach (var user in users.Users)
                {
                    var evidence = user.Workloads.Single(w => w.Workload == workload);
                    Assert.AreEqual(user.UserId == 1 ? "high" : "zero", evidence.Band);
                    Assert.AreEqual(1, evidence.ObservedSamples,
                        "Observed readings are a property of the import, not of the person.");
                    Assert.AreEqual(user.UserId == 1 ? 1 : 0, evidence.ActiveSamples);
                }
                var active = users.Users.Single(u => u.UserId == 1)
                    .Workloads.Single(w => w.Workload == workload);
                Assert.AreEqual(workload == "teams" || workload == "outlook" ? 5d : 4d,
                    active.AverageActions.Value,
                    "Duplicate rolling counters are snapshot evidence, not additional actions.");
                CollectionAssert.AreEquivalent(new[] { 1 },
                    users.MostActive.Where(u => u.Workloads.Single(w => w.Workload == workload)
                        .ActiveSamples > 0).Select(u => u.UserId).ToArray());
                CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5 },
                    users.LeastActive.Select(u => u.UserId).ToArray());
                Assert.AreEqual(0d, users.Users.Single(u => u.UserId == 3)
                    .Workloads.Single(w => w.Workload == workload).AverageActions.Value,
                    "A person with no rows across a fully imported week did nothing, measurably.");
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
                Assert.AreEqual(7, teams.ObservedSamples,
                    "The week holding only Monday-to-Thursday reports was not imported in full, and the "
                    + "week ending after the settled date is excluded outright.");
                Assert.AreEqual("2000-06-18", teams.SnapshotDates.Last().ToString("yyyy-MM-dd"));
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
        public async Task PerUserMissingRows_AreMeasuredZeroWhileMissingReportDaysStayUnknown()
        {
            // The rule this pins down: the Graph daily user-detail reports return only the people who
            // did something that day, so across a week that was imported in full an absent person is
            // measured evidence of NO activity. Unknown is reserved for a genuine hole in the
            // measurement - a report DAY nobody has - which is a property of the import, not of a
            // person. Before this, being quiet was indistinguishable from being unmeasured, and a
            // real tenant came back over 97% Unknown for exactly that reason.
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
                    "Other users still prove that every report day of every week was imported.");
                var distribution = overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "teams");
                Assert.AreEqual(1, distribution.High);
                Assert.AreEqual(1, distribution.Moderate);
                Assert.AreEqual(1, distribution.Low);
                Assert.AreEqual(2, distribution.Zero);
                Assert.AreEqual(0, distribution.Unknown,
                    "Nobody is Unknown while every week was imported in full.");
                foreach (var workload in new[] { "outlook", "onedrive", "sharepoint" })
                {
                    var other = overview.Licences.Single(l => l.LicenceTypeId == 1)
                        .Workloads.Single(w => w.Workload == workload);
                    Assert.AreEqual(2, other.Zero, workload);
                    Assert.AreEqual(0, other.Unknown, workload);
                }

                var greekDepartment = overview.Departments.Single(d => d.Id == 2)
                    .Workloads.Single(w => w.Workload == "teams");
                Assert.AreEqual(0, greekDepartment.Unknown,
                    "Losing one day of a fully imported week changes nothing for that person.");
                var unknownDepartment = overview.Departments.Single(d => d.Id == 0)
                    .Workloads.Single(w => w.Workload == "teams");
                Assert.AreEqual(1, unknownDepartment.Zero,
                    "A user with no report rows at all is a measured zero in demographic aggregates.");
                Assert.AreEqual(0, unknownDepartment.Unknown);

                var users = await fixture.Store().LoadUsersAsync(
                    overview,
                    OverviewQuery().ForUsers(
                        1, "teams", null, "activity", "desc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources,
                    NullLicenceActivityDiagnostics.Instance,
                    CancellationToken.None);

                CollectionAssert.Contains(users.LeastActive.Select(u => u.UserId).ToList(), 5,
                    "Someone proven to have done nothing belongs in the least-active list.");
                var stillMeasured = users.Users.Single(u => u.UserId == 3)
                    .Workloads.Single(w => w.Workload == "teams");
                Assert.AreEqual("available", stillMeasured.Status);
                Assert.AreEqual(8, stillMeasured.ObservedSamples);
                Assert.AreEqual("low", stillMeasured.Band);
                var absent = users.Users.Single(u => u.UserId == 5)
                    .Workloads.Single(w => w.Workload == "teams");
                Assert.AreEqual("available", absent.Status);
                Assert.AreEqual(8, absent.ObservedSamples);
                Assert.AreEqual(0, absent.ActiveSamples);
                Assert.AreEqual(0d, absent.AverageActions.Value);
                Assert.IsTrue(users.Users.Single(u => u.UserId == 5).Workloads
                    .Where(w => w.Workload != "copilot")
                    .All(w => w.Status == "available" && w.Band == "zero"));
                Assert.AreEqual("zero", users.Users.Single(u => u.UserId == 4)
                    .Workloads.Single(w => w.Workload == "teams").Band,
                    "A complete set of explicit zero rows remains a measured zero.");
            }
        }

        [TestMethod]
        public async Task AMissingReportDay_MakesItsWholeWeekUnknownForEveryone()
        {
            // The other half of the rule above: absence only means "no activity" once the week behind
            // it was imported in full. Lose one day and the week proves nothing either way, so the
            // whole workload falls back to Unknown rather than quietly reporting the quiet people as
            // inactive.
            using (var fixture = CreateMeasuredFixture())
            {
                fixture.Execute(
                    "DELETE FROM dbo.teams_user_activity_log WHERE [date] = '2000-06-20';");

                var overview = await fixture.Store().LoadOverviewAsync(
                    OverviewQuery(), Sources(usageReports: true),
                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);

                var teams = overview.Coverage.Single(c => c.Workload == "teams");
                Assert.AreEqual("partial", teams.Status);
                Assert.AreEqual(8, teams.ExpectedSamples);
                Assert.AreEqual(7, teams.ObservedSamples,
                    "The week containing the missing Tuesday is no longer a measured reading.");
                Assert.AreEqual(5, overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "teams").Unknown);
                Assert.AreEqual("available",
                    overview.Coverage.Single(c => c.Workload == "outlook").Status,
                    "One workload's import gap must not contaminate the others.");
                Assert.AreEqual(0, overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "outlook").Unknown);
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

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(8)]
        public async Task Copilot_D7CountersTakePrecedenceOverUserLastActivityAcrossQueryPaths(int weeks)
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCopilotCounterAuthority"))
            {
                SeedDirectory(fixture);
                fixture.Execute(@"
INSERT dbo.copilot_usage_report_import_log
    (report_name, report_refresh_date, report_version, report_period, imported_utc,
     rows_read, rows_saved, is_upn_obfuscated, error)
VALUES
    (N'getMicrosoft365CopilotUsageUserDetail', '2000-06-25', N'v2', N'D7',
     '2000-07-01T01:00:00', 4, 4, 0, NULL);");
                foreach (var date in CopilotD7Dates.Skip(CopilotD7Dates.Length - weeks))
                {
                    // Counters belong to the period; lastActivityDate comes from the user envelope.
                    // A v1-shaped row has neither counter and supplies positive evidence, not frequency.
                    fixture.Execute($@"
INSERT dbo.copilot_usage_user_activity_log
    (report_period_days, prompts_all_apps, active_usage_days, is_upn_obfuscated,
     user_id, [date], last_activity_date)
VALUES
    (7, 0, 0, 0, 1, '{date}', '{date}'),
    (7, 12, 0, 0, 2, '{date}', NULL),
    (7, NULL, NULL, 0, 3, '{date}', '{date}'),
    (7, 0, 0, 0, 4, '{date}', NULL);");
                }

                var query = weeks == 8 ? OverviewQuery() : LicenceActivityQuery.Create(
                    "2000-06-19", "2000-06-25", LicenceActivitySqlFixture.NowUtc);
                var sources = Sources(usageReports: false, copilotReports: true);
                var overview = await fixture.Store().LoadOverviewAsync(
                    query, sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var users = await fixture.Store().LoadUsersAsync(
                    overview, query.ForUsers(1, "copilot", null, "upn", "asc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                var distribution = overview.Licences.Single(l => l.LicenceTypeId == 1)
                    .Workloads.Single(w => w.Workload == "copilot");
                Assert.AreEqual("available", overview.Coverage.Single(c => c.Workload == "copilot").Status);
                Assert.AreEqual(1, distribution.High,
                    "An explicit D7 zero must not become active because the user-level date is in range.");
                Assert.AreEqual(2, distribution.Zero);
                Assert.AreEqual(2, distribution.Unknown);
                Assert.AreEqual(0, distribution.Moderate + distribution.Low);
                foreach (var user in users.Users)
                {
                    var evidence = user.Workloads.Single(w => w.Workload == "copilot");
                    var expectedBand = user.UserId == 2 ? "high"
                        : user.UserId == 1 || user.UserId == 4 ? "zero" : "unknown";
                    Assert.AreEqual(expectedBand, evidence.Band,
                        "Overview and drilldown must use the same D7 activity rule for user " + user.UserId);
                    Assert.AreEqual(user.UserId == 2 || user.UserId == 3 ? weeks : 0,
                        evidence.ActiveSamples);
                    Assert.AreEqual(user.UserId == 5 ? 0 : weeks, evidence.ObservedSamples);
                }
                Assert.AreEqual("partial", users.Users.Single(u => u.UserId == 3)
                    .Workloads.Single(w => w.Workload == "copilot").Status);
                Assert.IsNull(users.Users.Single(u => u.UserId == 3)
                    .Workloads.Single(w => w.Workload == "copilot").AverageActions);
                Assert.AreEqual(12d, users.Users.Single(u => u.UserId == 2)
                    .Workloads.Single(w => w.Workload == "copilot").AverageActions.Value);
                CollectionAssert.AreEquivalent(new[] { 2, 3 },
                    users.MostActive.Where(u => u.Workloads.Single(w => w.Workload == "copilot")
                        .ActiveSamples > 0).Select(u => u.UserId).ToArray());
                Assert.IsFalse(users.MostActive.Any(u => u.UserId == 5));
                CollectionAssert.AreEquivalent(new[] { 1, 2, 4 },
                    users.LeastActive.Select(u => u.UserId).ToArray());

                // Selecting another workload takes the bounded #ReturnedUsers/#CopilotReportIds
                // supporting-evidence path rather than Copilot's #EligibleUsers ranking path.
                var nonCopilot = await fixture.Store().LoadUsersAsync(
                    overview, query.ForUsers(1, "teams", null, "upn", "asc", 5, 1, 20,
                        LicenceActivitySqlFixture.NowUtc),
                    sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                foreach (var expectedUser in users.Users)
                {
                    var expected = expectedUser.Workloads.Single(w => w.Workload == "copilot");
                    var supporting = nonCopilot.Users.Single(u => u.UserId == expectedUser.UserId)
                        .Workloads.Single(w => w.Workload == "copilot");
                    Assert.AreEqual(expected.Band, supporting.Band);
                    Assert.AreEqual(expected.ActiveSamples, supporting.ActiveSamples);
                    Assert.AreEqual(expected.ObservedSamples, supporting.ObservedSamples);
                    Assert.AreEqual(expected.AverageActions, supporting.AverageActions);
                }
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

                var exception = await Assert.ThrowsExceptionAsync<Microsoft.Data.SqlClient.SqlException>(() =>
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

        internal static LicenceActivitySqlFixture CreateMeasuredFixture()
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

        internal static void SeedDirectory(LicenceActivitySqlFixture fixture)
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

        /// <summary>
        /// Seeds one usage-report table for the given weekly readings.
        ///
        /// A reading is a WEEK, and licence activity only treats a week as measured when every one of
        /// its days was imported - so each sample date here seeds its whole Monday-to-sample-date week,
        /// which is what a healthy daily import actually leaves behind. Seeding only the week's last
        /// day would model an import that ran once a week, and would (correctly) come back as partial.
        /// </summary>
        internal static void SeedOneUsageTable(
            LicenceActivitySqlFixture fixture,
            string table,
            string[] sampleDates)
        {
            for (var index = 0; index < sampleDates.Length; index++)
            {
                var activeUsers = index == 0
                    ? "1,2,3"
                    : index == 1 ? "1,2" : "1";
                var days = string.Join(",", ReportDaysOfWeekEndingOn(sampleDates[index])
                    .Select(day => "('" + day + "')"));
                var spine = $"CROSS JOIN (VALUES {days}) AS reading(report_date)";

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
       0, 0, id, reading.report_date,
       CASE WHEN id IN ({activeUsers}) THEN reading.report_date ELSE NULL END
FROM dbo.users {spine};");
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
       0, 0, id, reading.report_date,
       CASE WHEN id IN ({activeUsers}) THEN reading.report_date ELSE NULL END
FROM dbo.users {spine};");
                }
                else
                {
                    fixture.Execute($@"
INSERT dbo.{table}
    (viewed_or_edited, synced, shared_internally, shared_externally,
     user_id, [date], last_activity_date)
SELECT CASE WHEN id IN ({activeUsers}) THEN 4 ELSE 0 END,
       CASE WHEN id IN ({activeUsers}) THEN 1 ELSE 0 END,
       0, 0, id, reading.report_date,
       CASE WHEN id IN ({activeUsers}) THEN reading.report_date ELSE NULL END
FROM dbo.users {spine};");
                }
            }
        }

        /// <summary>Monday through the given week-end date, inclusive, as yyyy-MM-dd.</summary>
        internal static IEnumerable<string> ReportDaysOfWeekEndingOn(string weekEnd)
        {
            var end = DateTime.ParseExact(weekEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var start = end.AddDays(-(((int)end.DayOfWeek + 6) % 7));
            for (var day = start; day <= end; day = day.AddDays(1))
                yield return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
