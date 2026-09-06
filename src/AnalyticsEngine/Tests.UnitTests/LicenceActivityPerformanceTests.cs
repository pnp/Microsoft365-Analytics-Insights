using Common.Entities.LicenceActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Explicitly opt-in release-scale harness. It creates one uniquely named scratch database containing
    /// 300,000 synthetic users, 50 SKUs, just over two million
    /// overlapping assignments and millions of sampled workload rows, then executes the real store batches.
    /// The first invocation is the cold application
    /// call and three repeats produce the warm median. It deliberately does not run DBCC DROPCLEANBUFFERS:
    /// that is server-global and unsafe on the shared developer SQL instance.
    /// Elapsed times from this LocalDB harness are diagnostic and must not be treated as Azure SQL
    /// 200-400 DTU acceptance evidence or converted linearly to DTUs.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class LicenceActivityPerformanceTests
    {
        [TestMethod]
        [TestCategory("Performance")]
        public async Task SqlStore_AtReleaseScale_RecordsElapsedReadsAndPlansAcrossWindowsAndSkuSizes()
        {
            if (!string.Equals(
                Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF"),
                "1",
                StringComparison.Ordinal))
            {
                // Inconclusive, NOT a silent Passed. This test asserts nothing when it is opted out,
                // so reporting Passed would let an untouched release-scale harness masquerade as
                // acceptance evidence in a TRX. vstest reports Inconclusive as Skipped, so CI (which
                // never sets LICENCE_ACTIVITY_PERF) stays green without claiming the scale run happened.
                Assert.Inconclusive(
                    "LICENCE_ACTIVITY_PERF skipped; set LICENCE_ACTIVITY_PERF=1 to seed the LocalDB scale fixture. "
                    + "No scale assertion was executed. This harness is not Azure SQL 200-400 DTU acceptance evidence.");
            }

            var indexMode = ReadIndexMode();
            var seed = Stopwatch.StartNew();
            try
            {
                using (var fixture = LicenceActivitySqlFixture.CreateScale(
                    "LicenceActivityScale", indexMode))
                {
                    seed.Stop();
                    var metricsIndexes = Convert.ToInt32(fixture.Scalar(
                        indexMode == LicenceActivityUsageIndexMode.Columnstore
                            ? "SELECT COUNT(*) FROM sys.indexes WHERE name LIKE N'NCCI[_]%[_]metrics';"
                            : indexMode == LicenceActivityUsageIndexMode.BTreeFallback
                                ? "SELECT COUNT(*) FROM sys.indexes WHERE name LIKE N'IX[_]%[_]metrics';"
                                : "SELECT COUNT(*) FROM sys.indexes WHERE name LIKE N'NCCI[_]%[_]metrics' OR name LIKE N'IX[_]%[_]metrics';"));
                    Assert.AreEqual(
                        indexMode == LicenceActivityUsageIndexMode.DateOnly ? 0 : 4,
                        metricsIndexes);
                    Console.WriteLine(
                        "LICENCE_ACTIVITY_PERF seedMs={0} users=300000 skus=50 indexMode={1}",
                        seed.ElapsedMilliseconds,
                        indexMode);
                    Console.WriteLine(
                        "LICENCE_ACTIVITY_PERF environment=LocalDB azureBaseline=200-400DTU azureLatency=unmeasured");

                    var sources = new LicenceActivitySources
                    {
                        UserMetadata = true,
                        UsageReports = !string.Equals(
                            Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF_DISABLE_M365"),
                            "1",
                            StringComparison.Ordinal),
                        CopilotUsageReports = !string.Equals(
                            Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF_DISABLE_COPILOT"),
                            "1",
                            StringComparison.Ordinal),
                        NowUtc = LicenceActivitySqlFixture.NowUtc
                    };
                    var narrowQuery = LicenceActivityQuery.Create(
                        LicenceActivitySqlFixture.NarrowFromUtc.ToString("yyyy-MM-dd"),
                        LicenceActivitySqlFixture.NarrowToUtc.ToString("yyyy-MM-dd"),
                        LicenceActivitySqlFixture.NowUtc);
                    var wideQuery = LicenceActivityQuery.Create(
                        LicenceActivitySqlFixture.WideFromUtc.ToString("yyyy-MM-dd"),
                        LicenceActivitySqlFixture.WideToUtc.ToString("yyyy-MM-dd"),
                        LicenceActivitySqlFixture.NowUtc);

                    var diagnosticTimeout = LicenceActivitySql.CommandTimeoutSeconds;
                    if (int.TryParse(
                        Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF_DIAGNOSTIC_TIMEOUT"),
                        out var configuredDiagnosticTimeout)
                        && configuredDiagnosticTimeout > diagnosticTimeout)
                        diagnosticTimeout = configuredDiagnosticTimeout;
                    if (string.Equals(Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF_DIAGNOSTIC"),
                            "1", StringComparison.Ordinal)
                        || diagnosticTimeout > LicenceActivitySql.CommandTimeoutSeconds)
                    {
                        var diagnosticWide = string.Equals(
                            Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF_DIAGNOSTIC_WINDOW"),
                            "wide",
                            StringComparison.OrdinalIgnoreCase);
                        var diagnosticQuery = diagnosticWide ? wideQuery : narrowQuery;
                        if (int.TryParse(Environment.GetEnvironmentVariable(
                            "LICENCE_ACTIVITY_PERF_DIAGNOSTIC_DEPARTMENT"), out var diagnosticDepartment))
                        {
                            diagnosticQuery = LicenceActivityQuery.Create(
                                diagnosticQuery.From, diagnosticQuery.To, LicenceActivitySqlFixture.NowUtc,
                                departmentId: diagnosticDepartment);
                        }
                        var diagnosticScenario = diagnosticWide
                            ? "overview-wide-180d"
                            : "overview-narrow-7d";
                        if (string.Equals(Environment.GetEnvironmentVariable(
                            "LICENCE_ACTIVITY_PERF_DIAGNOSTIC_HTTP"), "1", StringComparison.Ordinal))
                        {
                            await DiagnoseHttpOverview(fixture, diagnosticQuery, sources);
                            Assert.Inconclusive(
                                "HTTP overview preflight only, with production SQL/response limits; "
                                + "the complete acceptance matrix has NOT run.");
                        }
                        var diagnostic = new LicenceActivitySqlMeasurement();
                        var diagnosticUsers = string.Equals(
                            Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF_DIAGNOSTIC_USERS"),
                            "1", StringComparison.Ordinal);
                        LicenceActivityOverview diagnosticOverview = null;
                        if (diagnosticUsers)
                        {
                            diagnosticOverview = await fixture.Store(new SqlLicenceActivityStoreInstrumentation
                                { CommandTimeoutSeconds = diagnosticTimeout })
                                .LoadOverviewAsync(diagnosticQuery, sources,
                                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                            diagnosticScenario = "users-" + diagnosticScenario;
                        }
                        var includeShowplan = string.Equals(
                            Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF_DIAGNOSTIC_SHOWPLAN"),
                            "1",
                            StringComparison.Ordinal);
                        var diagnosticWatch = Stopwatch.StartNew();
                        var uninstrumented = string.Equals(Environment.GetEnvironmentVariable(
                            "LICENCE_ACTIVITY_PERF_DIAGNOSTIC_UNINSTRUMENTED"), "1", StringComparison.Ordinal);
                        if (uninstrumented) diagnosticTimeout = LicenceActivitySql.CommandTimeoutSeconds;
                        var diagnosticStore = uninstrumented ? fixture.Store()
                            : fixture.Store(diagnostic.Instrumentation(
                                includeShowplan: includeShowplan, commandTimeoutSeconds: diagnosticTimeout));
                        try
                        {
                        if (diagnosticUsers)
                        {
                            var sku = int.TryParse(Environment.GetEnvironmentVariable(
                                "LICENCE_ACTIVITY_PERF_DIAGNOSTIC_SKU"), out var configuredSku) ? configuredSku : 2;
                            var workload = Environment.GetEnvironmentVariable(
                                "LICENCE_ACTIVITY_PERF_DIAGNOSTIC_WORKLOAD") ?? "teams";
                            var users = await diagnosticStore.LoadUsersAsync(diagnosticOverview,
                                diagnosticQuery.ForUsers(sku, workload, null, "activity", "desc",
                                    100, 1, 100, LicenceActivitySqlFixture.NowUtc),
                                sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                            var licence = diagnosticOverview.Licences.Single(l => l.LicenceTypeId == sku);
                            var distribution = licence.Workloads.Single(w => w.Workload == workload);
                            var positive = distribution.High + distribution.Moderate + distribution.Low;
                            Assert.AreEqual(licence.AssignedUsers, users.TotalUsers);
                            Assert.AreEqual(Math.Min(100, positive + distribution.Zero), users.LeastActive.Count);
                            Assert.IsTrue(users.MostActive.Count >= Math.Min(100, positive));
                            diagnosticScenario += "-sku" + sku + "-" + workload;
                        }
                        else
                        {
                            await diagnosticStore.LoadOverviewAsync(
                                diagnosticQuery, sources,
                                NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                        }
                        }
                        finally
                        {
                        diagnosticWatch.Stop();
                        Console.WriteLine(
                            "LICENCE_ACTIVITY_PERF_DIAGNOSTIC scenario={0} elapsedMs={1} logicalReads={2} readsByOperation={3} operations={4} instrumented={5}",
                            diagnosticScenario,
                            diagnosticWatch.ElapsedMilliseconds,
                            uninstrumented ? "unmeasured"
                                : diagnostic.TotalLogicalReads.ToString(CultureInfo.InvariantCulture),
                            string.Join(",", diagnostic.LogicalReadsByOperation),
                            string.Join(",", diagnostic.Operations),
                            !uninstrumented);
                        Console.WriteLine(
                            "LICENCE_ACTIVITY_PERF_DIAGNOSTIC statementTimings={0}",
                            string.Join(
                                " | ",
                                diagnostic.Messages
                                    .Where(message => message.IndexOf(
                                        "Execution Times", StringComparison.Ordinal) >= 0)
                                    .Select(message => string.Join(
                                        " ",
                                        message.Split(
                                            (char[])null,
                                            StringSplitOptions.RemoveEmptyEntries)))));
                        if (!uninstrumented)
                            Console.WriteLine(
                                "LICENCE_ACTIVITY_PERF_DIAGNOSTIC innerSqlPeakCommands={0} innerSqlPeakConnections={1} activeCommands={2} activeConnections={3}",
                                diagnostic.PeakCommands, diagnostic.PeakConnections,
                                diagnostic.ActiveCommands, diagnostic.ActiveConnections);
                        if (includeShowplan && !uninstrumented)
                        {
                            Console.WriteLine(
                                "LICENCE_ACTIVITY_PERF_DIAGNOSTIC operators={0}",
                                string.Join(",", PlanOperators(diagnostic.Showplans)));
                            var plans = diagnostic.Showplans.Select(XDocument.Parse).ToArray();
                            var aggregateRows = plans.SelectMany(plan => plan.Descendants()
                                .Where(node => node.Name.LocalName == "RelOp"
                                    && (string)node.Attribute("LogicalOp") == "Aggregate"))
                                .Select(node => node.Elements()
                                    .Where(child => child.Name.LocalName == "RunTimeInformation")
                                    .SelectMany(child => child.Elements())
                                    .Sum(counter => (double?)counter.Attribute("ActualRows") ?? 0))
                                .DefaultIfEmpty(0).Max();
                            Console.WriteLine(
                                "LICENCE_ACTIVITY_PERF_DIAGNOSTIC maximumAggregateRows={0} spillWarnings={1} maximumGrantedMemoryKb={2}",
                                aggregateRows,
                                plans.Sum(plan => plan.Descendants()
                                    .Count(node => node.Name.LocalName == "SpillToTempDb")),
                                plans.SelectMany(plan => plan.Descendants()
                                    .Where(node => node.Name.LocalName == "MemoryGrantInfo"))
                                    .Select(node => (long?)node.Attribute("GrantedMemory") ?? 0)
                                    .DefaultIfEmpty(0).Max());
                        }
                        }
                        // Diagnostics can retain the production timeout. They still do not execute
                        // the full acceptance matrix and must never be reported as a passing test.
                        Assert.Inconclusive(
                            "LICENCE_ACTIVITY_PERF_DIAGNOSTIC run only (scenario={0}, commandTimeoutSeconds={1}, production={2}). "
                            + "Diagnostics were captured but NO acceptance assertion or HTTP load was executed, "
                            + "so this run is NOT Azure SQL 200-400 DTU acceptance evidence.",
                            diagnosticScenario,
                            diagnosticTimeout,
                            LicenceActivitySql.CommandTimeoutSeconds);
                    }

                LicenceActivityOverview narrow = null;
                var narrowMetrics = await Measure(
                    fixture,
                    "overview-narrow-7d",
                    async store =>
                    {
                        narrow = await store.LoadOverviewAsync(
                            narrowQuery, sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                    });

                LicenceActivityOverview wide = null;
                var wideMetrics = await Measure(
                    fixture,
                    "overview-wide-180d",
                    async store =>
                    {
                        wide = await store.LoadOverviewAsync(
                            wideQuery, sources, NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                    });

                Assert.AreEqual(50, narrow.Licences.Count);
                Assert.AreEqual(50, wide.Licences.Count);
                Assert.AreEqual(300000, wide.DistinctAssignedUsers);
                Assert.AreEqual(300000, wide.Licences.Single(l => l.LicenceTypeId == 1).AssignedUsers);
                Assert.AreEqual(240000, wide.Licences.Single(l => l.LicenceTypeId == 2).AssignedUsers);
                Assert.AreEqual(50, wide.Licences.Single(l => l.LicenceTypeId == 3).AssignedUsers);
                Assert.AreEqual(1, wide.Licences.Single(l => l.LicenceTypeId == 4).AssignedUsers);
                Assert.AreEqual(100, wide.Licences.Single(l => l.LicenceTypeId == 7).AssignedUsers);
                Assert.AreEqual(15000, wide.Licences.Single(l => l.LicenceTypeId == 10).AssignedUsers);
                Assert.IsTrue(Convert.ToInt64(fixture.Scalar(
                    "SELECT COUNT_BIG(*) FROM dbo.user_license_type_lookups;")) >= 2000000);
                foreach (var table in new[]
                {
                    "teams_user_activity_log", "outlook_user_activity_log",
                    "onedrive_user_activity_log", "sharepoint_user_activity_log",
                    "copilot_usage_user_activity_log"
                })
                {
                    Assert.AreEqual(7800000L, Convert.ToInt64(fixture.Scalar(
                        "SELECT SUM(rows) FROM sys.partitions WHERE object_id = OBJECT_ID(N'dbo."
                        + table + "') AND index_id IN (0, 1);")), table);
                }

                foreach (var window in new[]
                {
                    new { Name = "narrow-7d", Query = narrowQuery, Overview = narrow },
                    new { Name = "wide-180d", Query = wideQuery, Overview = wide }
                })
                {
                    foreach (var sku in new[]
                    {
                        new { Id = 4, Name = "sparse-1", Expected = 1 },
                        new { Id = 3, Name = "small-50", Expected = 50 },
                        new { Id = 2, Name = "common-240k", Expected = 240000 },
                        new { Id = 1, Name = "all-300k", Expected = 300000 }
                    })
                    {
                        var usersQuery = window.Query.ForUsers(
                            sku.Id, "teams", null, "activity", "desc",
                            top: 100, page: 1, pageSize: 100,
                            nowUtc: LicenceActivitySqlFixture.NowUtc);
                        LicenceActivityUsers users = null;
                        var metrics = await Measure(
                            fixture,
                            "users-" + window.Name + "-" + sku.Name,
                            async store =>
                            {
                                users = await store.LoadUsersAsync(
                                    window.Overview, usersQuery, sources,
                                    NullLicenceActivityDiagnostics.Instance, CancellationToken.None);
                            });

                        Assert.AreEqual(sku.Expected, users.TotalUsers);
                        Assert.IsTrue(users.MostActive.Count <= 100);
                        Assert.IsTrue(users.LeastActive.Count <= 100);
                        Assert.IsTrue(users.Users.Count <= 100);
                        Assert.IsTrue(users.TotalUsers >= users.Users.Count);
                        AssertMeasured(metrics);
                    }
                }

                AssertMeasured(narrowMetrics);
                AssertMeasured(wideMetrics);

                var loadMeasurement = new LicenceActivitySqlMeasurement();
                var load = await LicenceActivityLoadTests.RunAsync(
                    fixture.Store(loadMeasurement.Instrumentation(includeShowplan: false)),
                    sources,
                    LicenceActivitySqlFixture.WideToUtc,
                    () => loadMeasurement.TotalLogicalReads,
                    message => Console.WriteLine("LICENCE_ACTIVITY_PERF " + message));
                    Console.WriteLine(
                        "LICENCE_ACTIVITY_PERF httpLoadMs={0} assignments={1} peakSharedReportLoads={2} "
                        + "managedHeapDelta={3} privateMemoryDelta={4} innerSqlPeakCommands={5} innerSqlPeakConnections={6}",
                        load.ElapsedMs,
                        load.Assignments,
                        load.PeakConcurrentSqlLoads,
                        load.ManagedHeapDeltaBytes,
                        load.PrivateMemoryDeltaBytes,
                        loadMeasurement.PeakCommands, loadMeasurement.PeakConnections);
                    Assert.AreEqual(0, loadMeasurement.ActiveCommands);
                    Assert.AreEqual(0, loadMeasurement.ActiveConnections);
                }
            }
            finally
            {
                if (string.Equals(
                    Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF_CLEANUP"),
                    "1",
                    StringComparison.Ordinal))
                {
                    LicenceActivitySqlFixture.CleanupRetainedScale();
                }
            }
        }

        private static async Task<Measurement> Measure(
            LicenceActivitySqlFixture fixture,
            string scenario,
            Func<SqlLicenceActivityStore, Task> execute)
        {
            var coldDiagnostics = new LicenceActivitySqlMeasurement();
            var first = Stopwatch.StartNew();
            try
            {
                await execute(fixture.Store(coldDiagnostics.Instrumentation(includeShowplan: false)));
            }
            catch
            {
                first.Stop();
                Console.WriteLine(
                    "LICENCE_ACTIVITY_PERF_TIMEOUT scenario={0} elapsedMs={1} logicalReadsBeforeFailure={2} messages={3}",
                    scenario,
                    first.ElapsedMilliseconds,
                    coldDiagnostics.TotalLogicalReads,
                    string.Join(" | ", coldDiagnostics.Messages));
                throw;
            }
            first.Stop();

            var repeats = new List<long>();
            for (var run = 0; run < 3; run++)
            {
                var watch = Stopwatch.StartNew();
                await execute(fixture.Store());
                watch.Stop();
                repeats.Add(watch.ElapsedMilliseconds);
            }
            repeats.Sort();

            var diagnostics = new LicenceActivitySqlMeasurement();
            var planWatch = Stopwatch.StartNew();
            await execute(fixture.Store(diagnostics.Instrumentation(includeShowplan: true)));
            planWatch.Stop();

            var operators = PlanOperators(diagnostics.Showplans);
            var measurement = new Measurement
            {
                Scenario = scenario,
                ColdApplicationCallMs = first.ElapsedMilliseconds,
                WarmMedianMs = repeats[repeats.Count / 2],
                InstrumentedMs = planWatch.ElapsedMilliseconds,
                LogicalReads = diagnostics.TotalLogicalReads,
                PlanCount = diagnostics.Showplans.Count,
                Operators = operators
            };

            Console.WriteLine(
                "LICENCE_ACTIVITY_PERF scenario={0} coldAppMs={1} warmMedianMs={2} "
                + "instrumentedMs={3} logicalReads={4} plans={5} operators={6}",
                measurement.Scenario,
                measurement.ColdApplicationCallMs,
                measurement.WarmMedianMs,
                measurement.InstrumentedMs,
                measurement.LogicalReads,
                measurement.PlanCount,
                measurement.Operators);
            return measurement;
        }

        private static async Task DiagnoseHttpOverview(
            LicenceActivitySqlFixture fixture, LicenceActivityQuery query, LicenceActivitySources sources)
        {
            var measurement = new LicenceActivitySqlMeasurement();
            var clock = Stopwatch.StartNew();
            using (var host = new LicenceActivityHttpHost(
                fixture.Store(measurement.Instrumentation(includeShowplan: false)), sources,
                () => sources.NowUtc.Add(clock.Elapsed)))
            {
                var path = "api/LicenceActivity/overview?from=" + query.From + "&to=" + query.To;
                if (query.DepartmentId.HasValue) path += "&departmentId=" + query.DepartmentId.Value;
                if (query.CountryId.HasValue) path += "&countryId=" + query.CountryId.Value;
                foreach (var temperature in new[] { "cold", "warm" })
                {
                    var reads = measurement.TotalLogicalReads;
                    var watch = Stopwatch.StartNew();
                    using (var response = await host.Client.GetAsync(path))
                    {
                        var content = await response.Content.ReadAsByteArrayAsync();
                        watch.Stop();
                        Console.WriteLine(
                            "LICENCE_ACTIVITY_PERF_HTTP_PREFLIGHT state={0} status={1} elapsedMs={2} bytes={3} logicalReads={4} innerSqlPeakCommands={5} innerSqlPeakConnections={6}",
                            temperature, (int)response.StatusCode, watch.ElapsedMilliseconds, content.Length,
                            measurement.TotalLogicalReads - reads, measurement.PeakCommands, measurement.PeakConnections);
                        if (!response.IsSuccessStatusCode) break;
                        if (temperature == "warm")
                            Assert.AreEqual(0L, measurement.TotalLogicalReads - reads,
                                "A warm preflight must not re-read SQL.");
                    }
                }
                var drain = Stopwatch.StartNew();
                while ((measurement.ActiveCommands != 0 || measurement.ActiveConnections != 0)
                    && drain.Elapsed < TimeSpan.FromMinutes(1))
                    await Task.Delay(50);
                Assert.AreEqual(0, measurement.ActiveCommands);
                Assert.AreEqual(0, measurement.ActiveConnections);
            }
        }

        private static void AssertMeasured(Measurement measurement)
        {
            Assert.IsTrue(measurement.ColdApplicationCallMs >= 0);
            Assert.IsTrue(measurement.WarmMedianMs >= 0);
            Assert.IsTrue(measurement.LogicalReads > 0,
                measurement.Scenario + " did not emit STATISTICS IO logical reads.");
            Assert.IsTrue(measurement.PlanCount > 0,
                measurement.Scenario + " did not emit a STATISTICS XML plan.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(measurement.Operators));
        }

        private static string PlanOperators(IEnumerable<string> plans)
        {
            var operators = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var plan in plans)
            {
                if (string.IsNullOrWhiteSpace(plan)) continue;
                var document = XDocument.Parse(plan);
                foreach (var element in document.Descendants()
                    .Where(e => e.Name.LocalName == "RelOp"))
                {
                    var physical = element.Attribute("PhysicalOp");
                    if (physical == null) continue;
                    var accessed = element.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "Object");
                    var index = accessed?.Attribute("Index");
                    var table = accessed?.Attribute("Table");
                    operators.Add(
                        physical.Value
                        + (table == null ? string.Empty : "[" + table.Value + "]")
                        + (index == null ? string.Empty : "[" + index.Value + "]"));
                }
            }
            return string.Join("|", operators);
        }

        private static LicenceActivityUsageIndexMode ReadIndexMode()
        {
            var configured = Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF_INDEX_MODE");
            if (string.Equals(configured, "fallback", StringComparison.OrdinalIgnoreCase))
                return LicenceActivityUsageIndexMode.BTreeFallback;
            if (string.Equals(configured, "date", StringComparison.OrdinalIgnoreCase))
                return LicenceActivityUsageIndexMode.DateOnly;
            if (string.Equals(configured, "columnstore", StringComparison.OrdinalIgnoreCase))
                return LicenceActivityUsageIndexMode.Columnstore;
            return LicenceActivityUsageIndexMode.BTreeFallback;
        }

        private sealed class Measurement
        {
            internal string Scenario;
            internal long ColdApplicationCallMs;
            internal long WarmMedianMs;
            internal long InstrumentedMs;
            internal long LogicalReads;
            internal int PlanCount;
            internal string Operators;
        }
    }
}
