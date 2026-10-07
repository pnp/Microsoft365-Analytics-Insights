using Common.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Infrastructure.Interception;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;

namespace Tests.UnitTests
{
    /// <summary>
    /// Synthetic-scale benchmark of the daily usage-report save path with a COLD lookup cache (#705, #495).
    /// Runs the real <c>OutlookUserActivityLoader.SaveLoadedReportsToSql</c> against SQL Server with 200,000
    /// synthetic users (<c>lookupbench0000001@contoso.com</c> ...) and one synthetic report day per size,
    /// measuring elapsed time, the number of user-lookup SQL commands, the loader's own lookup counters and
    /// the session's logical reads and SQL Server CPU time.
    ///
    /// <para>
    /// Never runs in CI: it is Inconclusive unless <c>USAGE_REPORT_LOOKUP_BENCHMARK=1</c>. Optional:
    /// <c>USAGE_REPORT_LOOKUP_BENCHMARK_SIZES</c> (comma-separated report sizes, default <c>5000,20000</c>),
    /// <c>USAGE_REPORT_LOOKUP_BENCHMARK_RUNS</c> (default 3), <c>USAGE_REPORT_LOOKUP_BENCHMARK_LABEL</c> (names
    /// the results file) and <c>USAGE_REPORT_LOOKUP_BENCHMARK_KEEP_ROWS=1</c> (keep the day's rows from an earlier
    /// run instead of deleting them first). The first run of each size inserts the day's rows and is discarded; the remaining runs
    /// re-save the same, unchanged day - the shape of the daily re-import of the recent window - so the dirty
    /// check skips every write and what is left is lookup resolution plus the existing-row read. Results are
    /// written to the console and, as each run finishes, to <c>usage-report-lookup-benchmark-{label}.md</c> next
    /// to the test assembly.
    /// </para>
    /// <para>
    /// Mind the sizes on the per-user path: where <c>users.user_name</c> is <c>varchar</c> under a SQL collation
    /// (the Azure SQL default, and this database's), EF's <c>nvarchar</c> parameter makes every per-user query scan
    /// the whole <c>IX_users</c> index, so 100,000 rows cost the old path close to half an hour a run.
    /// </para>
    /// </summary>
    [TestClass]
    public class UsageReportLookupBenchmarkTests
    {
        public const string EnableVariable = "USAGE_REPORT_LOOKUP_BENCHMARK";
        private const int TenantUsers = 200000;
        private const string UpnPrefix = "lookupbench";
        private const string UpnSuffix = "@contoso.com";

        private static string Upn(int i) => UpnPrefix + i.ToString("0000000", CultureInfo.InvariantCulture) + UpnSuffix;

        [TestMethod]
        [TestCategory("Benchmark")]
        public async Task UsageReportSave_ColdLookupCache_SyntheticScale()
        {
            if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
            {
                Assert.Inconclusive($"Benchmark only: set {EnableVariable}=1 to run it.");
                return;
            }

            var sizes = (Environment.GetEnvironmentVariable(EnableVariable + "_SIZES") ?? "5000,20000")
                .Split(',').Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToList();
            var runs = int.Parse(Environment.GetEnvironmentVariable(EnableVariable + "_RUNS") ?? "3", CultureInfo.InvariantCulture);
            Assert.IsTrue(runs >= 2, "At least one run after the discarded first run is needed.");
            Assert.IsTrue(sizes.All(s => s > 0 && s <= TenantUsers));

            var tableName = typeof(OutlookUsageActivityLog).GetCustomAttribute<TableAttribute>().Name;
            var label = Environment.GetEnvironmentVariable(EnableVariable + "_LABEL") ?? "run";
            var resultsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"usage-report-lookup-benchmark-{label}.md");
            var report = new StringBuilder();
            // Written as it goes, so a long run that is stopped part-way still leaves its finished rows behind.
            void Emit(string line)
            {
                report.AppendLine(line);
                File.AppendAllText(resultsPath, line + Environment.NewLine);
                Console.WriteLine(line);
            }

            Emit($"Usage-report lookup benchmark ({label}) - {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC - {TenantUsers:N0} synthetic users, runs per size: {runs} (first discarded)");
            Emit(string.Empty);

            using (var setupDb = new AnalyticsEntitiesContext())
            {
                setupDb.Database.Initialize(false);
                var created = await EnsureSyntheticUsersAsync(setupDb);
                Emit($"Synthetic users inserted this run: {created:N0}");
                Emit($"users.user_name column: {await DescribeUserNameColumnAsync(setupDb)}");
                Emit(string.Empty);
            }

            Emit("| Report rows | Run | Elapsed ms | Rows/s | User-lookup SQL commands | LookupDatabaseCallCount | LookupDatabaseCallMs | LookupResolveMs | ExistingRowLoadMs | Logical reads | SQL CPU ms | Added | Unchanged |");
            Emit("|---|---|---|---|---|---|---|---|---|---|---|---|---|");

            var summary = new StringBuilder();
            summary.AppendLine();
            summary.AppendLine("Medians (first run discarded):");
            summary.AppendLine();
            summary.AppendLine("| Report rows | Elapsed ms | Rows/s | User-lookup SQL commands | LookupDatabaseCallCount | LookupDatabaseCallMs | Logical reads | SQL CPU ms |");
            summary.AppendLine("|---|---|---|---|---|---|---|---|");

            var counter = new UserLookupCommandCounter();
            DbInterception.Add(counter);
            try
            {
                for (var sizeIndex = 0; sizeIndex < sizes.Count; sizeIndex++)
                {
                    var size = sizes[sizeIndex];
                    var reportDate = new DateTime(2000, 1, 1).AddDays(sizeIndex);
                    // KEEP_ROWS=1 keeps a previous run's rows for the day, so the first run re-saves rows another
                    // build wrote: Added = 0 then proves this build resolved every user to the same id.
                    if (Environment.GetEnvironmentVariable(EnableVariable + "_KEEP_ROWS") != "1")
                    {
                        using (var cleanDb = new AnalyticsEntitiesContext())
                        {
                            await cleanDb.Database.ExecuteSqlCommandAsync($"DELETE FROM dbo.[{tableName}] WHERE [date] = @p0", reportDate);
                        }
                    }

                    var rows = Enumerable.Range(1, size).Select(i => new OutlookUserActivityUserDetail
                    {
                        UserPrincipalName = Upn(i),
                        LastActivityDateString = reportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ReadCount = i % 50,
                        ReceiveCount = i % 70,
                        SendCount = i % 30,
                    }).ToList();

                    var measured = new List<RunResult>();
                    for (var run = 1; run <= runs; run++)
                    {
                        var result = await RunOnceAsync(rows, reportDate, counter);
                        Emit($"| {size:N0} | {run}{(run == 1 ? " (discarded)" : string.Empty)} | {result.ElapsedMs:N0} | {result.RowsPerSecond:N0} | {result.LookupCommands:N0} | {result.Metric("LookupDatabaseCallCount"):N0} | {result.Metric("LookupDatabaseCallMs"):N0} | {result.Metric("LookupResolveMs"):N0} | {result.Metric("ExistingRowLoadMs"):N0} | {result.LogicalReads:N0} | {result.CpuMs:N0} | {result.Metric("AddedRowCount"):N0} | {result.Metric("UnchangedRowCount"):N0} |");
                        if (run > 1)
                        {
                            measured.Add(result);
                        }
                    }

                    summary.AppendLine($"| {size:N0} | {Median(measured.Select(r => (double)r.ElapsedMs)):N0} | {Median(measured.Select(r => r.RowsPerSecond)):N0} | {Median(measured.Select(r => (double)r.LookupCommands)):N0} | {Median(measured.Select(r => r.Metric("LookupDatabaseCallCount"))):N0} | {Median(measured.Select(r => r.Metric("LookupDatabaseCallMs"))):N0} | {Median(measured.Select(r => (double)r.LogicalReads)):N0} | {Median(measured.Select(r => (double)r.CpuMs)):N0} |");
                }
            }
            finally
            {
                DbInterception.Remove(counter);
            }

            foreach (var line in summary.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.None))
            {
                Emit(line);
            }
        }

        private static async Task<RunResult> RunOnceAsync(List<OutlookUserActivityUserDetail> rows, DateTime reportDate, UserLookupCommandCounter counter)
        {
            var recorder = new RecordingInstrumentation();
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.LazyLoadingEnabled = false;
                db.Database.Initialize(false);

                // One open connection for the whole save, so every command runs on one session whose
                // cumulative logical reads and CPU time can be read before and after.
                await db.Database.Connection.OpenAsync();
                var before = await SessionCountersAsync(db);

                var loader = new OutlookUserActivityLoader(null, Common.Entities.UserScope.UserImportScope.Unfiltered, AnalyticsLogger.ConsoleOnlyTracer())
                {
                    SaveInstrumentation = recorder,
                };
                loader.LoadedReportPages[reportDate] = rows;

                // Cold, as at the start of every daily phase: a new shared id cache and a new lookup cache.
                counter.Reset();
                var watch = Stopwatch.StartNew();
                await loader.SaveLoadedReportsToSql(new ConcurrentLookupDbIdsCache(), new UserCache(db));
                watch.Stop();
                var lookupCommands = counter.Count;

                var after = await SessionCountersAsync(db);
                var completed = recorder.Events.Single(e => e.Stage == UsageReportSaveStageIds.SaveCompleted);
                return new RunResult
                {
                    ElapsedMs = watch.ElapsedMilliseconds,
                    RowsPerSecond = rows.Count * 1000.0 / Math.Max(1, watch.ElapsedMilliseconds),
                    LookupCommands = lookupCommands,
                    LogicalReads = after.LogicalReads - before.LogicalReads,
                    CpuMs = after.CpuMs - before.CpuMs,
                    Metrics = new Dictionary<string, double>(completed.Metrics),
                };
            }
        }

        // sys.dm_exec_sessions is cumulative per session and updated as each request completes: logical_reads in
        // pages, cpu_time in milliseconds of SQL Server CPU.
        private static async Task<SessionCounters> SessionCountersAsync(AnalyticsEntitiesContext db)
            => await db.Database.SqlQuery<SessionCounters>(
                "SELECT logical_reads AS LogicalReads, CAST(cpu_time AS bigint) AS CpuMs FROM sys.dm_exec_sessions WHERE session_id = @@SPID").SingleAsync();

        /// <summary>One row of <see cref="SessionCountersAsync"/>. Public only so EF can materialise it.</summary>
        public sealed class SessionCounters
        {
            public long LogicalReads { get; set; }
            public long CpuMs { get; set; }
        }

        private static async Task<int> EnsureSyntheticUsersAsync(AnalyticsEntitiesContext db)
        {
            // Set-based: one INSERT ... SELECT over a generated number sequence, skipping users already there.
            const string sql = @"
SET NOCOUNT ON;
;WITH n AS (
    SELECT TOP (@p0) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
    FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b
),
candidate AS (
    SELECT n.i, '" + UpnPrefix + @"' + RIGHT('0000000' + CAST(n.i AS varchar(7)), 7) + '" + UpnSuffix + @"' AS upn
    FROM n
)
INSERT INTO dbo.users (user_name)
SELECT c.upn
FROM candidate AS c
WHERE NOT EXISTS (SELECT 1 FROM dbo.users AS u WHERE u.user_name = c.upn)
ORDER BY c.i;
SELECT @@ROWCOUNT;";
            return await db.Database.SqlQuery<int>(sql, TenantUsers).SingleAsync();
        }

        private static async Task<string> DescribeUserNameColumnAsync(AnalyticsEntitiesContext db)
        {
            const string sql = @"
SELECT TOP (1) t.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' WHEN t.name LIKE 'n%' THEN CAST(c.max_length / 2 AS varchar(10)) ELSE CAST(c.max_length AS varchar(10)) END + ') ' + c.collation_name
    + ', indexed: ' + CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns AS ic WHERE ic.object_id = c.object_id AND ic.column_id = c.column_id AND ic.key_ordinal = 1) THEN 'yes' ELSE 'no' END
FROM sys.columns AS c INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.users') AND c.name = 'user_name'";
            return await db.Database.SqlQuery<string>(sql).SingleAsync();
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0;
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }

        private sealed class RunResult
        {
            public long ElapsedMs;
            public double RowsPerSecond;
            public long LookupCommands;
            public long LogicalReads;
            public long CpuMs;
            public Dictionary<string, double> Metrics;
            public double Metric(string name) => Metrics.TryGetValue(name, out var v) ? v : 0;
        }

        private sealed class RecordingInstrumentation : IUsageReportSaveInstrumentation
        {
            private readonly object _lock = new object();
            public bool IsEnabled => true;
            public List<UsageReportSaveTelemetryPoint> Events { get; } = new List<UsageReportSaveTelemetryPoint>();
            public void Track(UsageReportSaveTelemetryPoint point)
            {
                lock (_lock) Events.Add(point);
            }
        }

        /// <summary>
        /// Counts reader commands that read <c>dbo.users</c>: the per-user lookup in the old path and the
        /// batched lookup in the new one. The save path's other reads (the day's existing rows) do not
        /// touch the users table.
        /// </summary>
        private sealed class UserLookupCommandCounter : IDbCommandInterceptor
        {
            private long _count;
            public long Count => Interlocked.Read(ref _count);
            public void Reset() => Interlocked.Exchange(ref _count, 0);

            public void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext)
            {
                var text = command.CommandText ?? string.Empty;
                if (text.IndexOf("[dbo].[users]", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("dbo.users", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Interlocked.Increment(ref _count);
                }
            }

            public void ReaderExecuted(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext) { }
            public void NonQueryExecuting(DbCommand command, DbCommandInterceptionContext<int> interceptionContext) { }
            public void NonQueryExecuted(DbCommand command, DbCommandInterceptionContext<int> interceptionContext) { }
            public void ScalarExecuting(DbCommand command, DbCommandInterceptionContext<object> interceptionContext) { }
            public void ScalarExecuted(DbCommand command, DbCommandInterceptionContext<object> interceptionContext) { }
        }
    }
}
