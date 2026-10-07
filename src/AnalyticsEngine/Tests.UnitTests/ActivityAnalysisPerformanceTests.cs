using Common.Entities.ActivityAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Opt-in: the Activity analysis SQL at synthetic scale on LocalDB - the read model, the filtered weekly
    /// series for small and large sets of people, and the in-memory projection over the result.
    /// </summary>
    /// <remarks>
    /// <para>Run with <c>ACTIVITY_ANALYSIS_PERF=1</c> (and <c>ACTIVITY_ANALYSIS_PERF_USERS</c>, default 20,000). Every user
    /// gets a year of weeks, nine in ten of them compiled, with about a third of the 58 metrics non-zero. Nothing is
    /// asserted about time - a shared build agent says nothing about a customer's Azure SQL - only that the figures
    /// come back whole; the timings are written to the test output.</para>
    /// <para>Measured on a developer machine's LocalDB (Express: at most four cores, no batch mode) with 50,000 users -
    /// 2.35 million weekly rows, 1.2 GB: the 52-week read model 16-17 s warm (25 s cold) and 11.7 MB in memory; a
    /// 13-week one 5 s; the filtered series for 500, 5,000 and 25,000 people 55 ms, 0.5 s and 5 s; the projection
    /// of every metric over all 50,000 people 28 ms. Read and series time grow with the rows in the period - the
    /// weekly hash aggregate (116 aggregates a row) is most of it - so 200,000 users is roughly four times that, and
    /// is why each read is cached and shared.</para>
    /// </remarks>
    [TestClass]
    public class ActivityAnalysisPerformanceTests
    {
        private const int Weeks = 52;
        private static readonly DateTime FirstWeek = new DateTime(2025, 1, 6);

        [TestMethod]
        [TestCategory("Performance")]
        public async Task ReadModelAndFilteredSeries_AtSyntheticScale()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("ACTIVITY_ANALYSIS_PERF"), "1", StringComparison.Ordinal))
            {
                Assert.Inconclusive("Set ACTIVITY_ANALYSIS_PERF=1 (and optionally ACTIVITY_ANALYSIS_PERF_USERS) to run the synthetic-scale measurement.");
            }

            var users = int.TryParse(Environment.GetEnvironmentVariable("ACTIVITY_ANALYSIS_PERF_USERS"), out var configured) && configured > 0
                ? configured
                : 20000;

            using (var db = ScratchDatabase.Create("activityanalysisperf"))
            {
                db.Execute(ActivityAnalysisSqlTests.DboSchema);
                ActivityAnalysisSqlTests.InstallProfilingTables(db, withNewerTeamsColumns: true);

                var seed = Stopwatch.StartNew();
                db.Execute(SeedSql(users));
                var rows = Convert.ToInt64(db.Scalar("SELECT COUNT_BIG(*) FROM profiling.ActivitiesWeeklyColumns;"), CultureInfo.InvariantCulture);
                var dataPages = Convert.ToInt64(db.Scalar(
                    "SELECT SUM(used_page_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns');"),
                    CultureInfo.InvariantCulture);
                Console.WriteLine("Seeded {0:N0} users, {1:N0} weekly rows ({2:N0} MB) in {3:N0} ms.",
                    users, rows, dataPages * 8 / 1024, seed.ElapsedMilliseconds);

                var source = new SqlActivityAnalysisSource(db.ConnectionString);
                var period = ActivityAnalysisPeriod.Create(FirstWeek, FirstWeek.AddDays(7 * (Weeks - 1)));
                var quarter = ActivityAnalysisPeriod.Create(FirstWeek.AddDays(7 * (Weeks - 13)), FirstWeek.AddDays(7 * (Weeks - 1)));

                var model = await Measure("Read model, 52 weeks", 3, () => source.LoadReadModelAsync(period, CancellationToken.None));
                await Measure("Read model, 13 weeks", 3, () => source.LoadReadModelAsync(quarter, CancellationToken.None));
                Console.WriteLine("Read model: {0:N0} people, {1:N0} bytes ({2:N1} MB).",
                    model.PeopleCount, model.ApproximateBytes, model.ApproximateBytes / 1048576.0);
                Assert.AreEqual(users, model.PeopleCount, "Every synthetic user has a compiled week in the period.");

                var ids = Enumerable.Range(0, model.PeopleCount).Select(model.UserIdAt).OrderBy(id => id).ToArray();
                foreach (var every in new[] { 100, 10, 2 })
                {
                    var subset = ids.Where((id, index) => index % every == 0).ToArray();
                    var weeks = await Measure(
                        "Filtered series, " + subset.Length.ToString("N0", CultureInfo.InvariantCulture) + " people", 3,
                        () => source.LoadWeeklyTotalsAsync(model, subset, CancellationToken.None));
                    Assert.AreEqual(Weeks, weeks.Weeks);
                }

                var everyMetric = string.Join(",", ActivityAnalysisMetricCatalogue.All.Select(m => m.Key));
                var query = ActivityAnalysisQuery.Parse(null, null, everyMetric, "teams.calls:10:", null)
                    .Resolve(ActivityAnalysisSchema.Complete(period.From, period.To), DateTime.UtcNow);
                var audience = new ActivityAnalysisAudience { SeesIndividuals = false, MinimumPeopleWithoutSeePii = 5 };
                var projection = Stopwatch.StartNew();
                var evaluation = model.Evaluate(query, audience);
                var report = evaluation.BuildReport(new ActivityAnalysisWeeklyTotals(Weeks), null);
                Console.WriteLine("Projection, 58 metrics and a range, {0:N0} of {1:N0} matching: {2:N0} ms.",
                    report.MatchingPeople, report.PopulationPeople, projection.ElapsedMilliseconds);
            }
        }

        private static async Task<T> Measure<T>(string label, int runs, Func<Task<T>> run)
        {
            var timings = new List<long>();
            T result = default(T);
            for (var i = 0; i < runs; i++)
            {
                var watch = Stopwatch.StartNew();
                result = await run();
                timings.Add(watch.ElapsedMilliseconds);
            }

            Console.WriteLine("{0}: {1} ms (first run cold).", label, string.Join(" / ", timings));
            return result;
        }

        /// <summary>
        /// Users, licences and a year of weekly rows, set-based. Every value is integer arithmetic on the user id and the
        /// week, so a run is repeatable; about a third of each person's metrics are non-zero in a week.
        /// </summary>
        private static string SeedSql(int users)
        {
            var metrics = ActivityAnalysisMetricCatalogue.All;
            var sql = new StringBuilder();
            sql.AppendFormat(CultureInfo.InvariantCulture, @"
SET NOCOUNT ON;
INSERT INTO dbo.user_departments (name)
SELECT TOP (400) N'Department ' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS nvarchar(10)) FROM sys.all_objects;
INSERT INTO dbo.user_company_name (name)
SELECT TOP (12) N'Company ' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS nvarchar(10)) FROM sys.all_objects;
INSERT INTO dbo.license_types (name, sku_id) VALUES (N'Microsoft 365 E3', N'SPE_E3'), (N'Microsoft 365 E5', N'SPE_E5'), (N'Copilot', N'Microsoft_365_Copilot');

WITH Numbers AS (
    SELECT TOP ({0}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
    FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b CROSS JOIN sys.all_objects AS c)
INSERT INTO dbo.users WITH (TABLOCK) (user_name, account_enabled, azure_ad_id, department_id, company_name_id, postalcode)
SELECT 'user' + CAST(n AS varchar(10)) + '@contoso.com', 1,
       N'00000000-0000-0000-0000-' + RIGHT(N'000000000000' + CAST(n AS nvarchar(12)), 12),
       1 + n % 400, 1 + n % 12, N''
FROM Numbers;

INSERT INTO dbo.user_license_type_lookups WITH (TABLOCK) (user_id, license_type_id) SELECT id, 1 FROM dbo.users;
INSERT INTO dbo.user_license_type_lookups WITH (TABLOCK) (user_id, license_type_id) SELECT id, 2 FROM dbo.users WHERE id % 5 = 0;
INSERT INTO dbo.user_license_type_lookups WITH (TABLOCK) (user_id, license_type_id) SELECT id, 3 FROM dbo.users WHERE id % 10 = 0;

WITH Weeks AS (SELECT TOP ({1}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS w FROM sys.all_objects)
INSERT INTO profiling.ActivitiesWeeklyColumns WITH (TABLOCK) ([user_id], [date]", users, Weeks);

            foreach (var metric in metrics) sql.Append(", ").Append(metric.SqlColumn);
            sql.AppendFormat(CultureInfo.InvariantCulture, @")
SELECT u.id, DATEADD(DAY, 7 * w.w, '{0}')", ActivityAnalysisWeeks.Format(FirstWeek));

            for (var i = 0; i < metrics.Count; i++)
            {
                var scale = metrics[i].Unit == ActivityAnalysisUnits.Seconds ? " * 60" : string.Empty;
                sql.AppendFormat(CultureInfo.InvariantCulture,
                    ",\r\n       CASE WHEN (u.id * 31 + w.w * 17 + {0} * 7) % 3 = 0 THEN ((u.id * 13 + w.w * 7 + {0}) % 40 + 1){1} ELSE 0 END",
                    i, scale);
            }

            sql.Append(@"
FROM dbo.users AS u CROSS JOIN Weeks AS w
WHERE w.w = 0 OR (u.id * 7 + w.w * 3) % 10 <> 0;");
            return sql.ToString();
        }
    }
}
