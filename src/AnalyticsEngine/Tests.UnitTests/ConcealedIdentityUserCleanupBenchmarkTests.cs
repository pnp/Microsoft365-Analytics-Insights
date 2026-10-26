using Common.Entities;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Maintenance;

namespace Tests.UnitTests
{
    /// <summary>
    /// Synthetic-scale measurement of <see cref="ConcealedIdentityUserCleanup"/>: a users table of
    /// <c>CONCEALED_IDENTITY_CLEANUP_BENCHMARK_REAL_USERS</c> (default 200,000) Contoso users with directory ids, a million
    /// audit events and a million Copilot usage rows referring to them, plus <c>_JUNK_USERS</c> (default 20,000) placeholders
    /// with <c>_DAYS</c> (default 30) days of rows in every user-keyed usage-report table. Measures the whole cleanup, its
    /// batches, and the steady-state first look once nothing is left.
    /// <para>Never runs in CI: Inconclusive unless <c>CONCEALED_IDENTITY_CLEANUP_BENCHMARK=1</c>. Results go to the console and
    /// to <c>concealed-identity-cleanup-benchmark.md</c> next to the test assembly. Everything it adds is removed at the end.</para>
    /// </summary>
    [TestClass]
    public class ConcealedIdentityUserCleanupBenchmarkTests
    {
        public const string EnableVariable = "CONCEALED_IDENTITY_CLEANUP_BENCHMARK";
        private const string RealPrefix = "cleanupbench";

        [TestMethod]
        [TestCategory("Benchmark")]
        public async Task ConcealedIdentityCleanup_SyntheticScale()
        {
            if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
            {
                Assert.Inconclusive($"Benchmark only: set {EnableVariable}=1 to run it.");
                return;
            }

            int Setting(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(EnableVariable + "_" + name), out var v) ? v : fallback;
            var realUsers = Setting("REAL_USERS", 200000);
            var junkUsers = Setting("JUNK_USERS", 20000);
            var days = Setting("DAYS", 30);

            string connectionString;
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
                connectionString = db.Database.Connection.ConnectionString;
            }

            var resultsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "concealed-identity-cleanup-benchmark.md");
            void Emit(string line)
            {
                File.AppendAllText(resultsPath, line + Environment.NewLine);
                Console.WriteLine(line);
            }

            var baseline = Convert.ToInt32(Scalar(connectionString, "SELECT ISNULL(MAX(id), 0) FROM dbo.users;"));
            Emit($"## Concealed-identity cleanup benchmark - {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            Emit($"{realUsers:N0} real users, {junkUsers:N0} placeholders x {days} days x {ConcealedIdentityUserCleanupSql.UsageReportActivityTables.Count} usage-report tables");

            var setup = Stopwatch.StartNew();
            try
            {
                Execute(connectionString, $@"
;WITH n AS (SELECT TOP ({realUsers}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)
INSERT INTO dbo.users (user_name, azure_ad_id, mail)
SELECT '{RealPrefix}' + RIGHT('0000000' + CAST(i AS varchar(7)), 7) + '@contoso.com', CONVERT(nvarchar(36), NEWID()), N'' FROM n;
;WITH n AS (SELECT TOP ({junkUsers}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)
INSERT INTO dbo.users (user_name, azure_ad_id, mail)
SELECT LOWER(CONVERT(varchar(32), HASHBYTES('MD5', '{RealPrefix}-junk-' + CAST(i AS varchar(10))), 2)), N'', N'' FROM n;");

                var junkFilter = $"u.id > {baseline} AND u.user_name NOT LIKE '{RealPrefix}%'";
                var realFilter = $"u.id > {baseline} AND u.user_name LIKE '{RealPrefix}%'";
                foreach (var table in ConcealedIdentityUserCleanupSql.UsageReportActivityTables)
                {
                    var required = Scalar(connectionString, $@"SELECT STRING_AGG(QUOTENAME(c.name), ',') FROM sys.columns AS c
WHERE c.object_id = OBJECT_ID(N'dbo.{table}') AND c.is_nullable = 0 AND c.is_identity = 0 AND c.is_computed = 0 AND c.default_object_id = 0
  AND c.name NOT IN ('user_id', 'date');") as string;
                    var columns = string.IsNullOrEmpty(required) ? "" : "," + required;
                    var zeros = string.IsNullOrEmpty(required) ? "" : "," + string.Join(",", required.Split(',').Select(_ => "0"));
                    Execute(connectionString, $@"
;WITH d AS (SELECT TOP ({days}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n FROM sys.all_objects)
INSERT INTO dbo.[{table}] WITH (TABLOCK) (user_id, [date]{columns})
SELECT u.id, DATEADD(day, -d.n, '2026-09-30'){zeros} FROM dbo.users AS u CROSS JOIN d WHERE {junkFilter};");
                }

                // What keeps users: audit events (indexed user_id) and Copilot usage rows (no index leading with user_id).
                Execute(connectionString, $@"
;WITH n AS (SELECT TOP (5) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
INSERT INTO dbo.audit_events (id, time_stamp, user_id)
SELECT NEWID(), DATEADD(minute, -n.i, '2026-09-30'), u.id FROM dbo.users AS u CROSS JOIN n WHERE {realFilter};
;WITH n AS (SELECT TOP (5) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
INSERT INTO dbo.copilot_usage_user_activity_log (user_id, [date], report_period_days, is_upn_obfuscated)
SELECT u.id, DATEADD(day, -n.i, '2026-09-30'), 7, 0 FROM dbo.users AS u CROSS JOIN n WHERE {realFilter};");
                Emit($"Setup: {setup.Elapsed.TotalSeconds:N0} s");

                var cleanup = new ConcealedIdentityUserCleanup(connectionString, true, new RecordingLogger())
                {
                    MinUserIdExclusive = baseline,
                    TimeBudget = TimeSpan.FromHours(4),
                };
                var watch = Stopwatch.StartNew();
                var result = await cleanup.RunIfDueAsync();
                watch.Stop();
                Assert.IsNull(result.Error, result.Error?.ToString());
                Assert.AreEqual(junkUsers, result.UsersDeleted);

                Emit("| Users deleted | Rows deleted | Batches | Elapsed s | Users/s | Rows/s | Mean batch s | Batch size settled on |");
                Emit("|---|---|---|---|---|---|---|---|");
                Emit($"| {result.UsersDeleted:N0} | {result.ActivityRowsDeleted:N0} | {result.Batches} | {watch.Elapsed.TotalSeconds:N1} | " +
                    $"{result.UsersDeleted / watch.Elapsed.TotalSeconds:N0} | {result.ActivityRowsDeleted / watch.Elapsed.TotalSeconds:N0} | " +
                    $"{watch.Elapsed.TotalSeconds / Math.Max(1, result.Batches):N2} | {cleanup.BatchSize} |");
                var sorted = result.BatchDurations.Select(d => d.TotalSeconds).OrderBy(s => s).ToList();
                Emit($"Batch seconds: min {sorted.First():N2}, median {sorted[sorted.Count / 2]:N2}, max {sorted.Last():N2}; " +
                    $"first five {string.Join(", ", result.BatchDurations.Take(5).Select(d => d.TotalSeconds.ToString("N2", CultureInfo.InvariantCulture)))}");

                // The fixed cost of a batch - the rule, every reference check, the user delete and its foreign-key checks -
                // measured on placeholders with no usage-report rows at all.
                Execute(connectionString, $@"
;WITH n AS (SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
INSERT INTO dbo.users (user_name, azure_ad_id, mail)
SELECT LOWER(CONVERT(varchar(32), HASHBYTES('MD5', '{RealPrefix}-empty-' + CAST(i AS varchar(10))), 2)), N'', N'' FROM n;");
                var fixedCost = new ConcealedIdentityUserCleanup(connectionString, true, new RecordingLogger())
                {
                    MinUserIdExclusive = baseline,
                    BatchSize = 250,
                    TimeBudget = TimeSpan.FromHours(1),
                };
                var empty = await fixedCost.RunIfDueAsync();
                Assert.AreEqual(1000, empty.UsersDeleted);
                Emit($"Fixed cost per batch (no usage-report rows): {string.Join(", ", empty.BatchDurations.Select(d => $"{d.TotalSeconds:N2} s"))} " +
                    $"for batches starting at 250 users");

                // Steady state: the first look over the whole users table, with nothing left to find.
                var looks = new List<(long Reads, double Ms)>();
                for (var i = 0; i < 6; i++)
                {
                    looks.Add(MeasureFirstLook(connectionString));
                }
                var warm = looks.Skip(1).OrderBy(l => l.Ms).ToList();
                Emit($"First look with nothing left (median of 5 warm runs, {Convert.ToInt32(Scalar(connectionString, "SELECT COUNT(*) FROM dbo.users;")):N0} users): " +
                    $"{warm[2].Reads:N0} logical reads, {warm[2].Ms:N1} ms");
                Emit(string.Empty);
            }
            finally
            {
                var ids = $"SELECT id FROM dbo.users WHERE id > {baseline}";
                Execute(connectionString, string.Join(Environment.NewLine,
                    ConcealedIdentityUserCleanupSql.UsageReportActivityTables.Select(t => $"DELETE FROM dbo.[{t}] WHERE user_id IN ({ids});")) + $@"
DELETE FROM dbo.audit_events WHERE user_id IN ({ids});
DELETE FROM dbo.copilot_usage_user_activity_log WHERE user_id IN ({ids});
DELETE FROM dbo.users WHERE id > {baseline};");
            }
        }

        private static (long Reads, double Ms) MeasureFirstLook(string connectionString)
        {
            using (var connection = AzureSqlTokenAuth.CreateConnection(connectionString))
            {
                long reads = 0;
                connection.InfoMessage += (s, e) =>
                {
                    foreach (var part in e.Message.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var marker = part.IndexOf("logical reads ", StringComparison.Ordinal);
                        if (marker < 0) continue;
                        var digits = new string(part.Substring(marker + "logical reads ".Length).TakeWhile(char.IsDigit).ToArray());
                        reads += long.Parse(digits, CultureInfo.InvariantCulture);
                    }
                };
                connection.Open();
                using (var on = new SqlCommand("SET STATISTICS IO ON;", connection)) on.ExecuteNonQuery();
                var watch = Stopwatch.StartNew();
                using (var command = new SqlCommand(ConcealedIdentityUserCleanupSql.BuildAnyCandidateSql(), connection))
                {
                    command.Parameters.AddWithValue("@minUserId", 0);
                    using (var reader = command.ExecuteReader())
                    {
                        do { while (reader.Read()) { } } while (reader.NextResult());
                    }
                }
                watch.Stop();
                return (reads, watch.Elapsed.TotalMilliseconds);
            }
        }

        private static object Scalar(string connectionString, string sql)
        {
            using (var connection = AzureSqlTokenAuth.CreateConnection(connectionString))
            using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
            {
                connection.Open();
                return command.ExecuteScalar();
            }
        }

        private static void Execute(string connectionString, string sql)
        {
            using (var connection = AzureSqlTokenAuth.CreateConnection(connectionString))
            using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
            {
                connection.Open();
                command.ExecuteNonQuery();
            }
        }
    }
}
