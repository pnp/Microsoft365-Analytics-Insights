using Common.Entities;
using Common.Entities.Config;
using Common.Entities.LookupCaches;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Infrastructure.Interception;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Synthetic-scale benchmark of <see cref="UserCache.Load"/> - the per-user lookup by UPN - for users who are
    /// in <c>dbo.users</c> (hits) and users who are not yet (misses: the get-or-create path), before and after
    /// it sent its UPN as <c>varchar</c> (#713).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never runs in CI: every test here is Inconclusive unless <c>UPN_LOOKUP_BENCHMARK=1</c>. Build the commit
    /// to measure and run it; "before" is the same test against a build of the old <see cref="UserCache.Load"/>.
    /// </para>
    /// <para>
    /// The benchmark reads databases that already hold 200,000 synthetic users,
    /// <c>lookupbench0000001@contoso.com</c> ... <c>lookupbench0200000@contoso.com</c> (the users
    /// <c>UsageReportLookupBenchmarkTests</c> seeds, #712), ideally one whose <c>user_name</c> has a SQL collation and
    /// one with a Windows collation. It only reads them - no initializer, no writes - so several benchmarks can share
    /// them. <c>UPN_LOOKUP_BENCHMARK_DATABASES</c> (comma-separated catalog names on the configured server; default
    /// the configured test database), <c>UPN_LOOKUP_BENCHMARK_LOOKUPS</c> (default 1000),
    /// <c>UPN_LOOKUP_BENCHMARK_RUNS</c> (default 6; the first is discarded) and <c>UPN_LOOKUP_BENCHMARK_LABEL</c>
    /// (names the results file, <c>upn-lookup-benchmark-{label}.md</c> next to the test assembly).
    /// </para>
    /// <para>
    /// Each run uses one connection, so the session's logical reads and SQL Server CPU time can be read before and
    /// after from <c>sys.dm_exec_sessions</c>. That CPU figure is in milliseconds, too coarse for a single seek, so
    /// each run also reads the statement's own <c>sys.dm_exec_query_stats</c> entry, which times every execution
    /// in microseconds. The actual plan comes from replaying the command EF generated - same text, same parameter
    /// types - with <c>SET STATISTICS XML ON</c>.
    /// </para>
    /// </remarks>
    [TestClass]
    public class UpnLookupBenchmarkTests
    {
        public const string EnableVariable = "UPN_LOOKUP_BENCHMARK";
        private const int TenantUsers = 200000;

        private static string Hit(int i) => "lookupbench" + i.ToString("0000000", CultureInfo.InvariantCulture) + "@contoso.com";

        // Interleaved with the real UPNs, as a new starter's would be.
        private static string Miss(int i) => "lookupbench" + i.ToString("0000000", CultureInfo.InvariantCulture) + ".newstarter@contoso.com";

        [TestMethod]
        [TestCategory("Benchmark")]
        public async Task UserCacheLoad_HitsAndMisses_SyntheticScale()
        {
            if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
            {
                Assert.Inconclusive($"Benchmark only: set {EnableVariable}=1 to run it.");
                return;
            }

            var databases = (Environment.GetEnvironmentVariable(EnableVariable + "_DATABASES") ?? ConfiguredDatabase())
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).ToList();
            var lookups = int.Parse(Environment.GetEnvironmentVariable(EnableVariable + "_LOOKUPS") ?? "1000", CultureInfo.InvariantCulture);
            var runs = int.Parse(Environment.GetEnvironmentVariable(EnableVariable + "_RUNS") ?? "6", CultureInfo.InvariantCulture);
            Assert.IsTrue(runs >= 2, "At least one run after the discarded first run is needed.");
            Assert.IsTrue(lookups > 0 && lookups <= TenantUsers);

            var label = Environment.GetEnvironmentVariable(EnableVariable + "_LABEL") ?? "run";
            var resultsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"upn-lookup-benchmark-{label}.md");
            void Emit(string line)
            {
                File.AppendAllText(resultsPath, line + Environment.NewLine);
                Console.WriteLine(line);
            }

            // Spread across the whole key range; 199 is prime, so the ids are distinct.
            var hits = Enumerable.Range(0, lookups).Select(k => Hit(1 + (int)((long)k * 199 % TenantUsers))).ToList();
            var misses = Enumerable.Range(0, lookups).Select(k => Miss(1 + (int)((long)k * 199 % TenantUsers))).ToList();
            var inList = hits.Take(Math.Min(lookups, SqlUserLookupStore.UpnChunkSize)).ToList();

            Emit($"UPN lookup benchmark ({label}) - {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC - {lookups:N0} lookups per run, {runs} runs per scenario (first discarded)");
            Emit(string.Empty);

            var summary = new List<string>
            {
                string.Empty,
                "Medians (first run discarded):",
                string.Empty,
                "| Database | user_name | Scenario | Elapsed ms | Logical reads | Reads per query | SQL CPU ms | Server CPU us per query | Server elapsed us per query | Parameter sent | Actual plan |",
                "|---|---|---|---|---|---|---|---|---|---|---|",
            };

            Emit("| Database | Scenario | Run | Elapsed ms | Logical reads | SQL CPU ms | SQL commands | Found | Server executions | Server CPU us | Server elapsed us |");
            Emit("|---|---|---|---|---|---|---|---|---|---|---|");

            var recorder = new CommandRecorder();
            DbInterception.Add(recorder);
            try
            {
                foreach (var database in databases)
                {
                    var connectionString = ConnectionStringFor(database);
                    var column = await DescribeUsersAsync(connectionString);

                    var scenarios = new[]
                    {
                        new Scenario("Load, hit", lookups, async db =>
                        {
                            var cache = new UserCache(db);
                            var found = 0;
                            foreach (var upn in hits)
                            {
                                if (await cache.Load(upn) != null) found++;
                            }
                            return found;
                        }),
                        new Scenario("Load, miss", 0, async db =>
                        {
                            var cache = new UserCache(db);
                            var found = 0;
                            foreach (var upn in misses)
                            {
                                if (await cache.Load(upn) != null) found++;
                            }
                            return found;
                        }),
                        // Not changed by #713: the batch prefetch's IN-list, recorded for the follow-up.
                        new Scenario($"IN-list of {inList.Count:N0} (unchanged)", inList.Count, async db =>
                            (await new SqlUserLookupStore(db).GetUsersByUpnAsync(inList)).Count),
                    };

                    foreach (var scenario in scenarios)
                    {
                        var measured = new List<RunResult>();
                        // The first run finds out which statement EF sends; later runs read that statement's own
                        // execution statistics, which time each execution in microseconds.
                        StatementStatsReader stats = null;
                        try
                        {
                            for (var run = 1; run <= runs; run++)
                            {
                                recorder.Clear();
                                var result = await RunAsync(connectionString, scenario, recorder, stats);
                                if (stats == null && recorder.First() != null)
                                {
                                    stats = await StatementStatsReader.OpenAsync(connectionString, recorder.First().Text);
                                }

                                Emit($"| {database} | {scenario.Name} | {run}{(run == 1 ? " (discarded)" : string.Empty)} | {result.ElapsedMs:N0} | {result.LogicalReads:N0} | {result.CpuMs:N0} | {result.Commands:N0} | {result.Found:N0} | {result.ServerExecutions:N0} | {result.ServerWorkerUs:N0} | {result.ServerElapsedUs:N0} |");
                                Assert.AreEqual(scenario.ExpectedFound, result.Found, $"{scenario.Name} on {database} found the wrong number of users.");
                                if (run > 1)
                                {
                                    Assert.AreEqual(result.Commands, result.ServerExecutions,
                                        $"{scenario.Name} on {database}: the statement's execution statistics must cover every command the run sent.");
                                    measured.Add(result);
                                }
                            }
                        }
                        finally
                        {
                            stats?.Dispose();
                        }

                        var captured = recorder.First();
                        var plan = captured == null ? "(no command captured)" : await ActualPlanAsync(connectionString, captured);
                        var reads = Median(measured.Select(r => (double)r.LogicalReads));
                        var queries = Median(measured.Select(r => (double)r.Commands));
                        var serverCpu = Median(measured.Select(r => (double)r.ServerWorkerUs / Math.Max(1, r.ServerExecutions)));
                        var serverElapsed = Median(measured.Select(r => (double)r.ServerElapsedUs / Math.Max(1, r.ServerExecutions)));
                        summary.Add($"| {database} | {column} | {scenario.Name} | {Median(measured.Select(r => r.ElapsedMs)):N0} | {reads:N0} | {reads / Math.Max(1, queries):N1} | {Median(measured.Select(r => (double)r.CpuMs)):N0} | {serverCpu:N1} | {serverElapsed:N1} | {captured?.ParameterSummary ?? "-"} | {plan} |");
                    }
                }
            }
            finally
            {
                DbInterception.Remove(recorder);
            }

            foreach (var line in summary)
            {
                Emit(line);
            }
        }

        /// <summary>
        /// Records, without asserting, what a non-ASCII UPN does on the way into and back out of <c>users.user_name</c>,
        /// which is <c>varchar</c>. Entra issues ASCII UPNs, so this is not a tenant case; it is here so that the
        /// behaviour the PR describes can be reproduced. Writes only inside a transaction that is rolled back, in the
        /// configured test database (not the benchmark databases).
        /// </summary>
        [TestMethod]
        [TestCategory("Benchmark")]
        public async Task UserCacheLoad_NonAsciiInput_RecordWhatComesBack()
        {
            if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
            {
                Assert.Inconclusive($"Experiment only: set {EnableVariable}=1 to run it.");
                return;
            }

            var label = Environment.GetEnvironmentVariable(EnableVariable + "_LABEL") ?? "run";
            var resultsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"upn-lookup-nonascii-{label}.md");
            void Emit(string line)
            {
                File.AppendAllText(resultsPath, line + Environment.NewLine);
                Console.WriteLine(line);
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
                await db.Database.Connection.OpenAsync();
                using (var transaction = db.Database.BeginTransaction())
                {
                    try
                    {
                        Emit($"Non-ASCII UPN behaviour ({label}) - user_name: {await db.Database.SqlQuery<string>(DescribeUserNameSql).SingleAsync()}");
                        Emit(string.Empty);
                        Emit("| Input | Server nvarchar->varchar | Client varchar parameter | 1st import run | Stored | Load(input) | 2nd import run (cold cache) |");
                        Emit("|---|---|---|---|---|---|---|");

                        var token = Guid.NewGuid().ToString("N").Substring(0, 8);
                        // The last two reduce to the same code-page bytes: Κ λ θ η have no equivalent, and α becomes 'a'.
                        var inputs = new[]
                        {
                            $"Καλημέρα.{token}@contoso.com",
                            $"Αβγδεζηθ.{token}@contoso.com",
                            $"Łukasz.{token}@contoso.com",
                            $"José.{token}@contoso.com",
                            $"Καλη.{token}@contoso.com",
                            $"Καθη.{token}@contoso.com",
                        };

                        foreach (var input in inputs)
                        {
                            var server = await db.Database.SqlQuery<byte[]>("SELECT CONVERT(varbinary(600), CONVERT(varchar(250), @p0))",
                                new SqlParameter("@p0", SqlDbType.NVarChar, 4000) { Value = input }).SingleAsync();
                            var client = await db.Database.SqlQuery<byte[]>("SELECT CONVERT(varbinary(600), @p0)",
                                new SqlParameter("@p0", SqlDbType.VarChar, 8000) { Value = input }).SingleAsync();

                            var first = await TryGetOrCreateAsync(db, input);
                            var stored = first.Id.HasValue
                                ? await db.Database.SqlQuery<string>("SELECT user_name FROM dbo.users WHERE id = @p0", first.Id.Value).SingleAsync()
                                : "-";
                            var loaded = await new UserCache(db).Load(input);
                            var second = await TryGetOrCreateAsync(db, input);

                            Emit($"| {Mask(input, token)} | {Hex(server)} | {Hex(client)} | {first} | {Mask(stored, token)} | {(loaded == null ? "null" : $"id {loaded.ID}")} | {second} |");
                        }
                    }
                    finally
                    {
                        transaction.Rollback();
                    }
                }
            }
        }

        private static async Task<CreateOutcome> TryGetOrCreateAsync(AnalyticsEntitiesContext db, string upn)
        {
            try
            {
                var user = await new UserCache(db).GetOrCreateUser(upn, true);
                return new CreateOutcome { Id = user.ID };
            }
            catch (Exception ex)
            {
                return new CreateOutcome { Error = ex.GetType().Name };
            }
        }

        private sealed class CreateOutcome
        {
            public int? Id;
            public string Error;
            public override string ToString() => Id.HasValue ? $"id {Id}" : $"throws {Error}";
        }

        private static string Mask(string value, string token) => value?.Replace(token, "{token}");

        private static string Hex(byte[] bytes) => bytes == null ? "null" : BitConverter.ToString(bytes.Take(12).ToArray()).Replace("-", " ") + (bytes.Length > 12 ? " ..." : string.Empty);

        private static async Task<RunResult> RunAsync(string connectionString, Scenario scenario, CommandRecorder recorder, StatementStatsReader stats)
        {
            using (var db = OpenReadOnly(connectionString))
            {
                await db.Database.Connection.OpenAsync();

                var statsBefore = stats == null ? null : await stats.ReadAsync();
                var before = await SessionCountersAsync(db);

                recorder.Counting = true;
                var watch = Stopwatch.StartNew();
                var found = await scenario.Run(db);
                watch.Stop();
                recorder.Counting = false;

                var after = await SessionCountersAsync(db);
                var statsAfter = stats == null ? null : await stats.ReadAsync();
                return new RunResult
                {
                    ElapsedMs = watch.Elapsed.TotalMilliseconds,
                    LogicalReads = after.LogicalReads - before.LogicalReads,
                    CpuMs = after.CpuMs - before.CpuMs,
                    Commands = recorder.Count,
                    Found = found,
                    ServerExecutions = stats == null ? 0 : statsAfter.Executions - statsBefore.Executions,
                    ServerWorkerUs = stats == null ? 0 : statsAfter.WorkerMicroseconds - statsBefore.WorkerMicroseconds,
                    ServerElapsedUs = stats == null ? 0 : statsAfter.ElapsedMicroseconds - statsBefore.ElapsedMicroseconds,
                };
            }
        }

        private sealed class StatementStats
        {
            public long Executions;
            public long WorkerMicroseconds;
            public long ElapsedMicroseconds;
        }

        /// <summary>
        /// Reads the cumulative <c>sys.dm_exec_query_stats</c> of one statement in one database, which times every
        /// execution in microseconds - the session's millisecond <c>cpu_time</c> is far too coarse for a single seek.
        /// </summary>
        /// <remarks>
        /// Runs on its own connection, so none of its cost lands in the measured session's counters. The statement is
        /// found once by its text (the cached text also carries the parameter declarations, so the old and new forms
        /// are separate entries; <c>CHARINDEX</c> takes at most 8,000 characters, enough to tell them apart), and is
        /// then read by <c>sql_handle</c>, which is a hash of the text and so survives a recompile.
        /// </remarks>
        private sealed class StatementStatsReader : IDisposable
        {
            private readonly SqlConnection _connection;
            private readonly List<byte[]> _handles = new List<byte[]>();

            private StatementStatsReader(SqlConnection connection)
            {
                _connection = connection;
            }

            public static async Task<StatementStatsReader> OpenAsync(string connectionString, string statement)
            {
                var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                var reader = new StatementStatsReader(connection);
                using (var command = new SqlCommand(@"
SELECT DISTINCT qs.sql_handle
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
CROSS APPLY sys.dm_exec_plan_attributes(qs.plan_handle) AS pa
WHERE pa.attribute = 'dbid' AND CAST(pa.value AS int) = DB_ID() AND CHARINDEX(@statement, st.text) > 0;", connection))
                {
                    command.CommandTimeout = 300;
                    command.Parameters.Add("@statement", SqlDbType.NVarChar, 4000).Value = statement.Length > 4000 ? statement.Substring(0, 4000) : statement;
                    using (var rows = await command.ExecuteReaderAsync())
                    {
                        while (await rows.ReadAsync())
                        {
                            reader._handles.Add((byte[])rows[0]);
                        }
                    }
                }

                Assert.IsTrue(reader._handles.Count > 0, "The statement EF sent is not in the plan cache.");
                return reader;
            }

            public async Task<StatementStats> ReadAsync()
            {
                var names = _handles.Select((h, i) => "@h" + i).ToList();
                using (var command = new SqlCommand($@"
SELECT CAST(ISNULL(SUM(qs.execution_count), 0) AS bigint),
       CAST(ISNULL(SUM(qs.total_worker_time), 0) AS bigint),
       CAST(ISNULL(SUM(qs.total_elapsed_time), 0) AS bigint)
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_plan_attributes(qs.plan_handle) AS pa
WHERE qs.sql_handle IN ({string.Join(", ", names)}) AND pa.attribute = 'dbid' AND CAST(pa.value AS int) = DB_ID();", _connection))
                {
                    command.CommandTimeout = 300;
                    for (var i = 0; i < _handles.Count; i++)
                    {
                        command.Parameters.Add(names[i], SqlDbType.VarBinary, 64).Value = _handles[i];
                    }

                    using (var rows = await command.ExecuteReaderAsync())
                    {
                        await rows.ReadAsync();
                        return new StatementStats { Executions = rows.GetInt64(0), WorkerMicroseconds = rows.GetInt64(1), ElapsedMicroseconds = rows.GetInt64(2) };
                    }
                }
            }

            public void Dispose() => _connection.Dispose();
        }

        /// <summary>
        /// A context on a shared benchmark database that never runs an initializer against it - not even the
        /// existence/model check - so nothing in it is created or changed.
        /// </summary>
        private static AnalyticsEntitiesContext OpenReadOnly(string connectionString)
        {
            var db = new AnalyticsEntitiesContext(connectionString, true, false);
            Database.SetInitializer<AnalyticsEntitiesContext>(null);
            return db;
        }

        // net10: through AnalyticsConfig (appsettings.json + environment variables); ConfigurationManager finds nothing here.
        private static string ConfiguredConnectionString() => AnalyticsConfig.ConnectionStrings["SPOInsightsEntities"].ConnectionString;

        private static string ConfiguredDatabase() => new SqlConnectionStringBuilder(ConfiguredConnectionString()).InitialCatalog;

        private static string ConnectionStringFor(string database)
            => new SqlConnectionStringBuilder(ConfiguredConnectionString()) { InitialCatalog = database, ApplicationName = "UpnLookupBenchmark" }.ConnectionString;

        private const string DescribeUserNameSql = @"
SELECT t.name + '(' + CAST(c.max_length AS varchar(10)) + ') ' + c.collation_name
    + ', code page ' + CAST(COLLATIONPROPERTY(c.collation_name, 'CodePage') AS varchar(10))
FROM sys.columns AS c INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.users') AND c.name = 'user_name'";

        private static async Task<string> DescribeUsersAsync(string connectionString)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = DescribeUserNameSql + @";
SELECT COUNT_BIG(*) FROM dbo.users WHERE user_name LIKE 'lookupbench%';";
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        await reader.ReadAsync();
                        var column = reader.GetString(0);
                        await reader.NextResultAsync();
                        await reader.ReadAsync();
                        var synthetic = reader.GetInt64(0);
                        Assert.AreEqual(TenantUsers, synthetic, $"{connection.Database} must hold the {TenantUsers:N0} synthetic users; see UsageReportLookupBenchmarkTests (#712).");
                        return column;
                    }
                }
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

        /// <summary>
        /// Replays the command EF sent - same text, same parameter types and sizes - with <c>SET STATISTICS XML ON</c>,
        /// and summarises the operators that touched <c>dbo.users</c> plus any plan-affecting conversion warning.
        /// </summary>
        private static async Task<string> ActualPlanAsync(string connectionString, CapturedCommand captured)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                using (var on = new SqlCommand("SET STATISTICS XML ON;", connection))
                {
                    await on.ExecuteNonQueryAsync();
                }

                string planXml = null;
                using (var command = new SqlCommand(captured.Text, connection))
                {
                    foreach (var parameter in captured.Parameters)
                    {
                        command.Parameters.Add(new SqlParameter
                        {
                            ParameterName = parameter.Name,
                            DbType = parameter.DbType,
                            Size = parameter.Size,
                            Value = parameter.Value ?? DBNull.Value,
                        });
                    }

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        do
                        {
                            var isPlan = reader.FieldCount == 1 && reader.GetName(0).IndexOf("Showplan", StringComparison.OrdinalIgnoreCase) >= 0;
                            while (await reader.ReadAsync())
                            {
                                if (isPlan)
                                {
                                    planXml = reader.GetString(0);
                                }
                            }
                        } while (await reader.NextResultAsync());
                    }
                }

                return planXml == null ? "(no plan)" : SummarisePlan(planXml);
            }
        }

        private static string SummarisePlan(string planXml)
        {
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            var doc = XDocument.Parse(planXml);
            var operators = doc.Descendants(ns + "RelOp")
                .Select(op =>
                {
                    var target = op.Elements().FirstOrDefault(e => e.Name.LocalName == "IndexScan")?.Element(ns + "Object");
                    if (target == null || (string)target.Attribute("Table") != "[users]")
                    {
                        return null;
                    }

                    var counters = op.Element(ns + "RunTimeInformation")?.Elements(ns + "RunTimeCountersPerThread").ToList() ?? new List<XElement>();
                    long Sum(string attribute) => counters.Sum(c => (long?)c.Attribute(attribute) ?? 0);
                    return $"{(string)op.Attribute("PhysicalOp")} {(string)target.Attribute("Index")} ({Sum("ActualRowsRead"):N0} rows read, {Sum("ActualLogicalReads"):N0} reads)";
                })
                .Where(s => s != null)
                .ToList();

            var converts = doc.Descendants(ns + "PlanAffectingConvert")
                .Select(w => $"PlanAffectingConvert: {(string)w.Attribute("ConvertIssue")}")
                .Distinct()
                .ToList();

            return string.Join("; ", operators.Concat(converts));
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0;
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }

        private sealed class Scenario
        {
            public Scenario(string name, int expectedFound, Func<AnalyticsEntitiesContext, Task<int>> run)
            {
                Name = name;
                ExpectedFound = expectedFound;
                Run = run;
            }

            public string Name { get; }
            public int ExpectedFound { get; }
            public Func<AnalyticsEntitiesContext, Task<int>> Run { get; }
        }

        private sealed class RunResult
        {
            public double ElapsedMs;
            public long LogicalReads;
            public long CpuMs;
            public long Commands;
            public int Found;
            public long ServerExecutions;
            public long ServerWorkerUs;
            public long ServerElapsedUs;
        }

        private sealed class CapturedParameter
        {
            public string Name;
            public DbType DbType;
            public int Size;
            public object Value;
        }

        private sealed class CapturedCommand
        {
            public string Text;
            public List<CapturedParameter> Parameters;

            public string ParameterSummary => Parameters.Count == 0
                ? "none (literals)"
                : string.Join(", ", Parameters.Select(p => $"{p.DbType}({p.Size})").Distinct());
        }

        /// <summary>
        /// Counts the commands that read <c>dbo.users</c> while a run is being measured, and keeps the first one so
        /// its plan can be replayed.
        /// </summary>
        private sealed class CommandRecorder : IDbCommandInterceptor
        {
            private readonly object _lock = new object();
            private CapturedCommand _first;
            private long _count;

            public volatile bool Counting;

            public long Count => Interlocked.Read(ref _count);

            public CapturedCommand First()
            {
                lock (_lock) return _first;
            }

            public void Clear()
            {
                lock (_lock) _first = null;
                Interlocked.Exchange(ref _count, 0);
            }

            public void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext)
            {
                var text = command.CommandText ?? string.Empty;
                if (!Counting || text.IndexOf("[dbo].[users]", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return;
                }

                Interlocked.Increment(ref _count);
                lock (_lock)
                {
                    if (_first == null)
                    {
                        _first = new CapturedCommand
                        {
                            Text = text,
                            Parameters = command.Parameters.Cast<DbParameter>().Select(p => new CapturedParameter
                            {
                                Name = p.ParameterName.StartsWith("@", StringComparison.Ordinal) ? p.ParameterName : "@" + p.ParameterName,
                                DbType = p.DbType,
                                Size = p.Size,
                                Value = p.Value,
                            }).ToList(),
                        };
                    }
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
