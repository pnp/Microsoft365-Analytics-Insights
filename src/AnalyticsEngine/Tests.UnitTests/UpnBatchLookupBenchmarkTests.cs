using Common.Entities;
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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Opt-in, isolated 200k-user reproduction of #713. Set UPN_BATCH_BENCHMARK=1.
    /// UPN_BATCH_BENCHMARK_RECOMPILE=1 adds OPTION(RECOMPILE) to measured user queries;
    /// otherwise it measures warm cached plans (real parameter reuse). Six runs, first discarded.
    /// Writes only synthetic fixtures and deletes its databases afterwards.
    /// </summary>
    [TestClass]
    public class UpnBatchLookupBenchmarkTests
    {
        [TestMethod]
        [TestCategory("Benchmark")]
        public async Task BatchResolutionAndTrackedManagerReload_Synthetic200k_BothCollations()
        {
            if (Environment.GetEnvironmentVariable("UPN_BATCH_BENCHMARK") != "1")
                Assert.Inconclusive("Set UPN_BATCH_BENCHMARK=1 to run the synthetic benchmark.");
            var recompile = Environment.GetEnvironmentVariable("UPN_BATCH_BENCHMARK_RECOMPILE") == "1";
            var output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "upn-batch-benchmark-" + (recompile ? "recompile" : "cached") + ".txt");
            File.WriteAllText(output, "200,000 synthetic users; median of five runs after discarded warm-up; " +
                (recompile ? "OPTION(RECOMPILE)" : "warm cached plans") + Environment.NewLine +
                "| Collation | Keys | Distribution | Path | Elapsed ms | Logical reads | User plans |" + Environment.NewLine +
                "|---|---|---|---|---|---|---|" + Environment.NewLine);
            foreach (var collation in new[] { "SQL_Latin1_General_CP1_CI_AS", "Latin1_General_CI_AS" })
            {
                using (var fixture = await UpnBatchLookupTests.Fixture.CreateAsync(collation, benchmark: true))
                {
                    foreach (var count in new[] { 100, 1000 })
                    foreach (var spread in new[] { false, true })
                    {
                        var names = Enumerable.Range(0, count).Select(i =>
                            "lookupbench" + (1 + (spread ? i * 199 : i)).ToString("0000000") + "@contoso.com").ToList();
                        var paths = new Dictionary<string, Func<AnalyticsEntitiesContext, Task<int>>>
                        {
                            ["before manager IN"] = async db => (await db.users.Where(u => names.Contains(u.UserPrincipalName)).Include(u => u.LicenseLookups).ToListAsync()).Count,
                            ["after manager IDs"] = async db => (await new SqlUserLookupStore(db).GetUsersByUpnAsync(names)).Count,
                            ["before cache nvarchar"] = async db => await ForcedTypeAsync(db, names, SqlDbType.NVarChar),
                            ["before recovery varchar"] = async db => await ForcedTypeAsync(db, names, SqlDbType.VarChar),
                            ["after shared adaptive"] = async db => (await ExistingUserIds.FindAsync(db, names)).Count(id => id.HasValue),
                        };
                        foreach (var path in paths)
                        {
                            var times = new List<double>();
                            var reads = new List<double>();
                            var recorder = new Recorder(recompile);
                            DbInterception.Add(recorder);
                            try
                            {
                                for (var run = 0; run < 6; run++)
                                {
                                    using (var db = new UpnBatchLookupTests.FixtureContext(fixture.ConnectionString))
                                    {
                                        await db.Database.Connection.OpenAsync();
                                        // Warm the database-scoped metadata probe outside the timed operation.
                                        await ExistingUserIds.FindAsync(db, new[] { names[0] });
                                        recorder.Commands.Clear();
                                        var before = await ReadsAsync(db);
                                        var watch = Stopwatch.StartNew();
                                        var found = await path.Value(db);
                                        watch.Stop();
                                        var after = await ReadsAsync(db);
                                        Assert.AreEqual(count, found, "Every path must return the same synthetic users.");
                                        if (run > 0) { times.Add(watch.Elapsed.TotalMilliseconds); reads.Add(after - before); }
                                    }
                                }
                                var plans = new List<string>();
                                foreach (var command in recorder.Commands)
                                    plans.Add(await PlanAsync(fixture.ConnectionString, command));
                                var line = $"| {collation} | {count} | {(spread ? "spread" : "adjacent")} | {path.Key} | {Median(times):F2} | {Median(reads):F0} | {string.Join("; ", plans)} |";
                                Console.WriteLine(line);
                                File.AppendAllText(output, line + Environment.NewLine);
                            }
                            finally { DbInterception.Remove(recorder); }
                        }
                    }
                }
            }
        }

        private static async Task<int> ForcedTypeAsync(AnalyticsEntitiesContext db, IReadOnlyList<string> names, SqlDbType type)
        {
            var query = ExistingUserIds.BuildQuery(names, 0, names.Count, type);
            return (await db.Database.SqlQuery<ExistingUserIds.Match>(query.Sql, query.Parameters).ToListAsync()).Count;
        }

        private static Task<long> ReadsAsync(AnalyticsEntitiesContext db) => db.Database.SqlQuery<long>(
            "SELECT logical_reads FROM sys.dm_exec_sessions WHERE session_id = @@SPID").SingleAsync();

        private static double Median(List<double> values) => values.OrderBy(v => v).ElementAt(values.Count / 2);

        private sealed class Captured
        {
            public string Text;
            public List<SqlParameter> Parameters;
        }

        private sealed class Recorder : DbCommandInterceptor
        {
            private readonly bool _recompile;
            public readonly List<Captured> Commands = new List<Captured>();
            public Recorder(bool recompile) { _recompile = recompile; }
            public override void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> context)
            {
                if (!command.CommandText.Contains("[dbo].[users]") && !command.CommandText.Contains("INNER JOIN dbo.users")) return;
                if (_recompile) command.CommandText = command.CommandText.TrimEnd().TrimEnd(';') + " OPTION (RECOMPILE);";
                Commands.Add(new Captured
                {
                    Text = command.CommandText,
                    Parameters = command.Parameters.Cast<SqlParameter>().Select(p => (SqlParameter)((ICloneable)p).Clone()).ToList()
                });
            }
        }

        private static async Task<string> PlanAsync(string connectionString, Captured captured)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand("SET STATISTICS XML ON", connection)) await command.ExecuteNonQueryAsync();
                using (var command = new SqlCommand(captured.Text, connection))
                {
                    command.CommandTimeout = 600;
                    command.Parameters.AddRange(captured.Parameters.ToArray());
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        do
                        {
                            var isPlan = reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan");
                            while (await reader.ReadAsync())
                            {
                                if (!isPlan) continue;
                                XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
                                var xml = XDocument.Parse(reader.GetString(0));
                                var operators = xml.Descendants(ns + "RelOp").Where(op =>
                                    op.Elements().Any(e => e.Element(ns + "Object")?.Attribute("Table")?.Value == "[users]"))
                                    .Select(op => op.Attribute("PhysicalOp").Value).Distinct();
                                var joins = xml.Descendants(ns + "RelOp").Select(op => op.Attribute("PhysicalOp").Value)
                                    .Where(op => op.Contains("Join") || op == "Hash Match" || op == "Nested Loops").Distinct();
                                return string.Join("/", operators) + " (" + string.Join("/", joins) + ")";
                            }
                        } while (await reader.NextResultAsync());
                    }
                }
            }
            Assert.Fail("No actual plan returned.");
            return null;
        }
    }
}
