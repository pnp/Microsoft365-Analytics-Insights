extern alias AnalyticsWeb;

using Common.Entities.LicenceActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    // Invoked by the opt-in synthetic SQL performance fixture, using the same seeded database.
    internal static class LicenceActivityLoadTests
    {
        internal const int ColdBudgetMs = 15000;
        internal const int WarmBudgetMs = 500;
        internal const int ConcurrentBudgetMs = 25000;
        internal const long MemoryDeltaBudgetBytes = 128L * 1024 * 1024;

        internal static async Task<LoadResult> RunAsync(
            ILicenceActivityStore sqlStore, LicenceActivitySources sources, DateTime endUtc,
            Func<long> logicalReads, Action<string> progress = null)
        {
            if (logicalReads == null) throw new ArgumentNullException(nameof(logicalReads));
            var result = new LoadResult();
            var store = new MeteredStore(sqlStore);
            var elapsed = Stopwatch.StartNew();
            using (var memory = new MemorySampler())
            {
                foreach (var run in new[] { 7, 180 }.SelectMany(days => Enumerable.Range(1, 3),
                    (days, repeat) => new { Days = days, Repeat = repeat }))
                {
                    var days = run.Days;
                    progress?.Invoke("HTTP load: " + days + "-day window, 50 SKUs, repeat " + run.Repeat + "/3.");
                    var hostStart = elapsed.Elapsed;
                    Func<DateTime> hostNow = () => sources.NowUtc.Add(elapsed.Elapsed - hostStart);
                    using (var host = new LicenceActivityHttpHost(store, sources, hostNow))
                    {
                        var query = "from=" + endUtc.AddDays(1 - days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                            + "&to=" + endUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        var overviewPath = "api/LicenceActivity/overview?" + query;
                        var overview = await Measure(host.Client, overviewPath, "overview-cold", days, 300000,
                            store, logicalReads, result, ColdBudgetMs);
                        Assert.AreEqual(300000, (int)overview["distinctAssignedUsers"], "Load fixture must contain 300,000 distinct licence holders.");
                        var skus = ((JArray)overview["licences"]).OrderBy(s => (int)s["assignedUsers"]).ToArray();
                        Assert.AreEqual(50, skus.Length, "The current acceptance target is 50, not 20, licence types.");
                        Assert.IsTrue(skus.Any(s => (int)s["assignedUsers"] > 0 && (int)s["assignedUsers"] <= 100),
                            "Include a genuinely small non-empty SKU.");
                        Assert.AreEqual(300000, (int)skus.Last()["assignedUsers"], "Include a tenant-wide SKU.");
                        result.Assignments = skus.Sum(s => (long)s["assignedUsers"]);
                        Assert.IsTrue(result.Assignments >= 2000000, "Exercise millions of overlapping memberships.");
                        foreach (var workload in LicenceActivityQuery.Workloads)
                        {
                            var distributions = skus.SelectMany(s => (JArray)s["workloads"])
                                .Where(w => (string)w["workload"] == workload).ToArray();
                            Assert.IsTrue(distributions.Any(w => (int)w["zero"] > 0),
                                workload + ": do not benchmark an all-unknown shortcut; seed explicit measured-zero evidence.");
                            Assert.IsTrue(distributions.Any(w => (int)w["high"] > 0),
                                workload + ": seed positive activity too.");
                        }
                        var overviewId = (string)overview["snapshotId"];
                        await Measure(host.Client, overviewPath, "overview-warm", days, 300000,
                            store, logicalReads, result, WarmBudgetMs, requireCacheHit: true);

                        // A complete 50 x 5 sweep can outlive the real five-minute overview TTL.
                        // Honour that TTL rather than freezing the cache clock or counting a legitimate
                        // expiry as a latency failure. Explicit idle time is not part of request latency.
                        async Task EnsureOverview(int nextRequestBudgetMs)
                        {
                            var delay = DelayBeforeOverviewRefresh(
                                ((DateTime)overview["expiresUtc"]).ToUniversalTime(), hostNow(), nextRequestBudgetMs);
                            if (!delay.HasValue) return;
                            result.CacheExpiryWaitMs += (long)delay.Value.TotalMilliseconds;
                            if (delay.Value > TimeSpan.Zero) await Task.Delay(delay.Value);
                            var previousId = overviewId;
                            overview = await Measure(host.Client, overviewPath, "overview-expiry-renewal", days, 300000,
                                store, logicalReads, result, ColdBudgetMs);
                            overviewId = (string)overview["snapshotId"];
                            Assert.AreNotEqual(previousId, overviewId, "An expired overview must be replaced, not reused.");
                            Assert.AreEqual(300000, (int)overview["distinctAssignedUsers"]);
                            Assert.AreEqual(50, ((JArray)overview["licences"]).Count);
                        }

                        foreach (var cell in WorkloadMatrix(skus))
                        {
                            await EnsureOverview(ColdBudgetMs + WarmBudgetMs);
                            // Most/least are always ranked by the selected workload. The default UPN
                            // sort affects only the browse page; activity-sort paging is checked below.
                            var path = UserPath(overviewId, cell.LicenceTypeId, cell.Workload, 100, 1, 100);
                            var users = await Measure(host.Client, path, "users-cold", days, cell.AssignedPopulation,
                                store, logicalReads, result, ColdBudgetMs,
                                licenceTypeId: cell.LicenceTypeId, workload: cell.Workload, repeat: run.Repeat);
                            CheckUsers(users, 100, 100);
                            Assert.AreEqual(cell.AssignedPopulation, (int)users["totalUsers"]);
                            Assert.AreEqual(cell.LicenceTypeId, (int)users["query"]["licenceTypeId"]);
                            Assert.AreEqual(cell.Workload, (string)users["query"]["workload"]);
                            Assert.AreEqual(Math.Min(100, cell.KnownUsers), ((JArray)users["leastActive"]).Count,
                                "Every known user, including genuine zeros, must be eligible for least-active ranking.");
                            Assert.IsTrue(((JArray)users["mostActive"]).Count >= Math.Min(100, cell.PositiveKnownUsers),
                                "Known positive activity must not be replaced by an empty or all-unknown shortcut.");
                            await Measure(host.Client, path, "users-warm", days, cell.AssignedPopulation,
                                store, logicalReads, result, WarmBudgetMs, requireCacheHit: true,
                                licenceTypeId: cell.LicenceTypeId, workload: cell.Workload, repeat: run.Repeat);
                            result.CompletedMatrixCells++;
                            memory.Sample();
                        }

                        var tenantSku = (int)skus.Last()["licenceTypeId"];
                        foreach (var workload in LicenceActivityQuery.Workloads)
                        {
                            await EnsureOverview(ColdBudgetMs);
                            var path = UserPath(overviewId, tenantSku, workload, 25, 2, 50)
                                + "&sort=activity&direction=desc";
                            var users = await Measure(host.Client, path, "workload-ranking", days, 300000,
                                store, logicalReads, result, ColdBudgetMs,
                                licenceTypeId: tenantSku, workload: workload, repeat: run.Repeat);
                            CheckUsers(users, 25, 50);
                        }

                        await EnsureOverview(ColdBudgetMs);
                        await Measure(host.Client, UserPath(overviewId, tenantSku, "teams", 10, 3000, 100),
                            "deep-page", days, 300000, store, logicalReads, result, ColdBudgetMs);
                        await EnsureOverview(ColdBudgetMs);
                        var missing = await Measure(host.Client,
                            UserPath(overviewId, tenantSku, "teams", 10, 1, 50) + "&search=nonexistent-synthetic-identity",
                            "search-miss", days, 300000, store, logicalReads, result, ColdBudgetMs);
                        Assert.AreEqual(0, (int)missing["totalUsers"]);
                        await EnsureOverview(ColdBudgetMs);
                        await Measure(host.Client,
                            UserPath(overviewId, tenantSku, "teams", 10, 1, 50) + "&search=contoso",
                            "search-common", days, 300000, store, logicalReads, result, ColdBudgetMs);

                        var department = ((JArray)overview["departments"]).FirstOrDefault(d => (int)d["id"] > 0);
                        var country = ((JArray)overview["countries"]).FirstOrDefault(c => (int)c["id"] > 0);
                        Assert.IsNotNull(department, "Seed realistic department metadata.");
                        Assert.IsNotNull(country, "Seed realistic country metadata.");
                        await Measure(host.Client, overviewPath + "&departmentId=" + department["id"],
                            "department", days, 300000, store, logicalReads, result, ColdBudgetMs);
                        await Measure(host.Client, overviewPath + "&countryId=" + country["id"],
                            "country", days, 300000, store, logicalReads, result, ColdBudgetMs);

                        // A new query shape prevents an earlier warm result from concealing a duplicate cold run.
                        await EnsureOverview(ConcurrentBudgetMs);
                        var duplicatePath = UserPath(overviewId, tenantSku, "teams", 97, 3, 91);
                        var before = store.Calls;
                        var readsBefore = logicalReads();
                        var duplicateResponses = await Task.WhenAll(Enumerable.Range(0, 8)
                            .Select(_ => Request(host.Client, duplicatePath)));
                        CheckResponses(duplicateResponses, ConcurrentBudgetMs);
                        Assert.AreEqual(1, store.Calls - before, "Eight identical cold HTTP requests must perform one SQL load.");
                        Assert.AreEqual(1, duplicateResponses.Select(r => (string)r.Json["snapshotId"]).Distinct().Count());
                        result.Observations.Add(Group("duplicate-cold-8", days, duplicateResponses,
                            store.Calls - before, logicalReads() - readsBefore));

                        await EnsureOverview(ConcurrentBudgetMs);
                        before = store.Calls;
                        readsBefore = logicalReads();
                        var distinctResponses = await Task.WhenAll(new[] { skus[0], skus[16], skus[32], skus[49] }
                            .Select(s => Request(host.Client, UserPath(overviewId, (int)s["licenceTypeId"], "outlook", 93, 4, 87))));
                        CheckResponses(distinctResponses, ConcurrentBudgetMs);
                        Assert.AreEqual(4, store.Calls - before);
                        Assert.IsTrue(store.PeakActive <= 4, "Concurrent SQL work must remain bounded across request keys.");
                        result.Observations.Add(Group("distinct-cold-4", days, distinctResponses,
                            store.Calls - before, logicalReads() - readsBefore));

                        await EnsureOverview(ColdBudgetMs * 2);
                        var exportUsers = await Measure(host.Client, UserPath(overviewId, tenantSku, "teams", 100, 1, 100),
                            "export-view", days, 300000, store, logicalReads, result, ColdBudgetMs);
                        before = store.Calls;
                        var export = await Request(host.Client, "api/LicenceActivity/export?overviewId=" + overviewId
                            + "&usersId=" + exportUsers["snapshotId"], workbook: true);
                        Assert.AreEqual(HttpStatusCode.OK, export.Status);
                        Assert.IsTrue(export.ElapsedMs <= ColdBudgetMs, "Excel HTTP latency exceeded the cold-request budget.");
                        Assert.AreEqual(0, store.Calls - before, "Excel must use the actual displayed snapshot, not re-query.");
                        Assert.IsTrue(export.Bytes > 0);
                        result.Observations.Add(Group("excel-snapshot", days, new[] { export }, 0, 0));
                    }
                }
                memory.Sample();
                result.ManagedHeapDeltaBytes = Math.Max(0, memory.PeakManagedBytes - memory.InitialManagedBytes);
                result.PrivateMemoryDeltaBytes = Math.Max(0, memory.PeakPrivateBytes - memory.InitialPrivateBytes);
                Assert.IsTrue(result.ManagedHeapDeltaBytes <= MemoryDeltaBudgetBytes,
                    "The web process plus in-process HTTP client exceeded the managed-memory growth budget.");
                Assert.IsTrue(result.PrivateMemoryDeltaBytes <= MemoryDeltaBudgetBytes,
                    "The web process plus in-process HTTP client exceeded the private-memory growth budget.");
            }
            Assert.AreEqual(result.RequiredMatrixCells, result.CompletedMatrixCells,
                "Acceptance requires all 50 SKUs x 5 workloads x 2 windows x 3 fresh-host repeats, with immediate warm partners.");
            result.ElapsedMs = elapsed.ElapsedMilliseconds;
            result.PeakConcurrentSqlLoads = store.PeakActive;
            var output = Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_LOAD_RESULTS");
            if (!string.IsNullOrWhiteSpace(output))
                File.WriteAllText(Path.GetFullPath(output), JsonConvert.SerializeObject(result, Formatting.Indented),
                    new UTF8Encoding(false));
            else
                progress?.Invoke("HTTP load results: " + JsonConvert.SerializeObject(result));
            return result;
        }

        private static string UserPath(string overviewId, int licence, string workload, int top, int page, int size) =>
            "api/LicenceActivity/users?overviewId=" + overviewId + "&licenceTypeId=" + licence
            + "&workload=" + workload + "&top=" + top + "&page=" + page + "&pageSize=" + size;

        internal static IEnumerable<WorkloadCase> WorkloadMatrix(IEnumerable<JToken> skus)
        {
            foreach (var sku in skus)
                foreach (var workload in LicenceActivityQuery.Workloads)
                {
                    var distribution = ((JArray)sku["workloads"]).Single(w => (string)w["workload"] == workload);
                    var positive = (int)distribution["high"] + (int)distribution["moderate"] + (int)distribution["low"];
                    yield return new WorkloadCase
                    {
                        LicenceTypeId = (int)sku["licenceTypeId"], AssignedPopulation = (int)sku["assignedUsers"],
                        Workload = workload, PositiveKnownUsers = positive, KnownUsers = positive + (int)distribution["zero"]
                    };
                }
        }

        internal static TimeSpan? DelayBeforeOverviewRefresh(DateTime expiresUtc, DateTime nowUtc, int nextRequestBudgetMs)
        {
            var remaining = expiresUtc - nowUtc;
            if (remaining.TotalMilliseconds > nextRequestBudgetMs + 1000) return null;
            return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining.Add(TimeSpan.FromMilliseconds(50));
        }

        internal sealed class WorkloadCase
        {
            internal int LicenceTypeId;
            internal int AssignedPopulation;
            internal string Workload;
            internal int KnownUsers;
            internal int PositiveKnownUsers;
        }

        private static async Task<JObject> Measure(
            HttpClient client, string path, string scenario, int days, int population, MeteredStore store,
            Func<long> reads, LoadResult result, int budgetMs, bool requireCacheHit = false,
            int? licenceTypeId = null, string workload = null, int? repeat = null)
        {
            var before = store.Calls;
            var readsBefore = reads();
            var response = await Request(client, path);
            CheckResponses(new[] { response }, budgetMs);
            var observation = Group(scenario, days, new[] { response }, store.Calls - before, reads() - readsBefore);
            observation.AssignedPopulation = population;
            observation.LicenceTypeId = licenceTypeId;
            observation.Workload = workload;
            observation.Repeat = repeat;
            if (requireCacheHit)
            {
                Assert.AreEqual(0, observation.SqlLoads);
                Assert.AreEqual(0L, observation.LogicalReads);
            }
            result.Observations.Add(observation);
            return response.Json;
        }

        private static async Task<Response> Request(HttpClient client, string path, bool workbook = false)
        {
            var watch = Stopwatch.StartNew();
            using (var response = await client.GetAsync(path))
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                watch.Stop();
                return new Response
                {
                    Status = response.StatusCode, ElapsedMs = watch.ElapsedMilliseconds, Bytes = bytes.Length,
                    Json = workbook ? null : JObject.Parse(Encoding.UTF8.GetString(bytes))
                };
            }
        }

        private static void CheckResponses(IEnumerable<Response> responses, int budgetMs)
        {
            foreach (var response in responses)
            {
                Assert.AreEqual(HttpStatusCode.OK, response.Status, response.Json?.ToString());
                Assert.IsTrue(response.ElapsedMs <= budgetMs, "HTTP latency budget exceeded: " + response.ElapsedMs + "ms > " + budgetMs + "ms.");
                Assert.IsTrue(response.Bytes <= 1024 * 1024, "Response exceeded 1 MiB.");
            }
        }

        private static void CheckUsers(JObject result, int top, int pageSize)
        {
            Assert.IsTrue(((JArray)result["mostActive"]).Count <= top);
            Assert.IsTrue(((JArray)result["leastActive"]).Count <= top);
            Assert.IsTrue(((JArray)result["users"]).Count <= pageSize);
            Assert.IsTrue((int)result["rankedUsers"] <= (int)result["totalUsers"]);
            var workload = (string)result["query"]["workload"];
            foreach (var user in (JArray)result["leastActive"])
            {
                var evidence = ((JArray)user["workloads"]).Single(w => (string)w["workload"] == workload);
                Assert.AreNotEqual("unknown", (string)evidence["band"], "Missing evidence must never rank as least-active.");
            }
        }

        private static Observation Group(string scenario, int days, Response[] responses, int loads, long reads)
        {
            var ordered = responses.Select(r => r.ElapsedMs).OrderBy(t => t).ToArray();
            return new Observation
            {
                Scenario = scenario, WindowDays = days, Requests = responses.Length, SqlLoads = loads,
                LogicalReads = reads, MedianMs = ordered[ordered.Length / 2],
                P95Ms = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1],
                MaximumMs = ordered.Last(), MaximumResponseBytes = responses.Max(r => r.Bytes)
            };
        }

        internal sealed class LoadResult
        {
            public int Users { get; set; } = 300000;
            public int LicenceTypes { get; set; } = 50;
            public long Assignments { get; set; }
            public string Scope { get; set; } = "In-process attributed HTTP API, real SQL adapter, production cache and Excel writer; synthetic database only. Client and server share the measured process. Cold means fresh application caches/HttpServer, not a fresh OS process or cold SQL. Not an IIS/network/Entra deployment test or an Azure capacity guarantee.";
            public string SqlBufferPool { get; set; } = "Retained/uncontrolled SQL buffer and plan caches; no DBCC cache clearing. Not SQL-cold evidence.";
            public string ConcurrencyUnit { get; set; } = "SqlLoads counts shared store/report loads, not individual SQL commands or connections; each load can issue multiple commands.";
            public int MemorySampleIntervalMs { get; set; } = 50;
            public int RequiredMatrixCells { get; set; } = 50 * 5 * 2 * 3;
            public int CompletedMatrixCells { get; set; }
            public long CacheExpiryWaitMs { get; set; }
            public long ElapsedMs { get; set; }
            public long ManagedHeapDeltaBytes { get; set; }
            public long PrivateMemoryDeltaBytes { get; set; }
            public int PeakConcurrentSqlLoads { get; set; }
            public List<Observation> Observations { get; set; } = new List<Observation>();
        }

        internal sealed class Observation
        {
            public string Scenario { get; set; }
            public int WindowDays { get; set; }
            public int AssignedPopulation { get; set; }
            public int? LicenceTypeId { get; set; }
            public string Workload { get; set; }
            public int? Repeat { get; set; }
            public int Requests { get; set; }
            public int SqlLoads { get; set; }
            public long LogicalReads { get; set; }
            public long MedianMs { get; set; }
            public long P95Ms { get; set; }
            public long MaximumMs { get; set; }
            public int MaximumResponseBytes { get; set; }
        }

        private sealed class Response
        {
            internal HttpStatusCode Status;
            internal long ElapsedMs;
            internal int Bytes;
            internal JObject Json;
        }

        private sealed class MeteredStore : ILicenceActivityStore
        {
            private readonly ILicenceActivityStore _inner;
            private int _calls;
            private int _active;
            private int _peakActive;
            internal MeteredStore(ILicenceActivityStore inner) { _inner = inner; }
            internal int Calls => Volatile.Read(ref _calls);
            internal int PeakActive => Volatile.Read(ref _peakActive);
            private async Task<T> Run<T>(Func<Task<T>> action)
            {
                Interlocked.Increment(ref _calls);
                var active = Interlocked.Increment(ref _active);
                int previous;
                do { previous = Volatile.Read(ref _peakActive); }
                while (active > previous && Interlocked.CompareExchange(ref _peakActive, active, previous) != previous);
                try { return await action().ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref _active); }
            }
            public Task<LicenceActivityOverview> LoadOverviewAsync(LicenceActivityQuery query, LicenceActivitySources sources,
                ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken) =>
                Run(() => _inner.LoadOverviewAsync(query, sources, diagnostics, cancellationToken));
            public Task<LicenceActivityUsers> LoadUsersAsync(LicenceActivityOverview overview, LicenceActivityQuery query,
                LicenceActivitySources sources, ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken) =>
                Run(() => _inner.LoadUsersAsync(overview, query, sources, diagnostics, cancellationToken));
        }

        private sealed class MemorySampler : IDisposable
        {
            private readonly object _gate = new object();
            private readonly Timer _timer;
            private readonly Process _process = Process.GetCurrentProcess();
            internal long InitialManagedBytes { get; }
            internal long InitialPrivateBytes { get; }
            internal long PeakManagedBytes { get; private set; }
            internal long PeakPrivateBytes { get; private set; }
            internal MemorySampler()
            {
                InitialManagedBytes = GC.GetTotalMemory(false);
                InitialPrivateBytes = _process.PrivateMemorySize64;
                PeakManagedBytes = InitialManagedBytes;
                PeakPrivateBytes = InitialPrivateBytes;
                _timer = new Timer(_ => Sample(), null, 50, 50);
            }
            internal void Sample()
            {
                lock (_gate)
                {
                    _process.Refresh();
                    PeakManagedBytes = Math.Max(PeakManagedBytes, GC.GetTotalMemory(false));
                    PeakPrivateBytes = Math.Max(PeakPrivateBytes, _process.PrivateMemorySize64);
                }
            }
            public void Dispose()
            {
                using (var drained = new ManualResetEvent(false))
                {
                    _timer.Dispose(drained);
                    drained.WaitOne();
                }
                _process.Dispose();
            }
        }

    }

    [TestClass]
    public class LicenceActivityLoadHarnessTests
    {
        [TestMethod]
        public void WorkloadMatrix_ExercisesEverySkuAndWorkload_AndRetainsKnownZeroPopulation()
        {
            var skus = Enumerable.Range(1, 50).Select(id => new JObject
            {
                ["licenceTypeId"] = id,
                ["assignedUsers"] = id * 1000,
                ["workloads"] = new JArray(LicenceActivityQuery.Workloads.Select(workload => new JObject
                {
                    ["workload"] = workload, ["high"] = 1, ["moderate"] = 1,
                    ["low"] = 0, ["zero"] = 7, ["unknown"] = id * 1000 - 9
                }))
            }).ToArray();

            var matrix = LicenceActivityLoadTests.WorkloadMatrix(skus).ToArray();
            Assert.AreEqual(250, matrix.Length);
            Assert.AreEqual(250, matrix.Select(c => c.LicenceTypeId + ":" + c.Workload).Distinct().Count());
            foreach (var sku in skus)
                Assert.AreEqual(5, matrix.Count(c => c.LicenceTypeId == (int)sku["licenceTypeId"]));
            foreach (var workload in LicenceActivityQuery.Workloads)
                Assert.AreEqual(50, matrix.Count(c => c.Workload == workload));
            Assert.IsTrue(matrix.All(c => c.KnownUsers == 9 && c.PositiveKnownUsers == 2),
                "Measured zeros are known evidence; unknown rows must not inflate least-active eligibility.");
            Assert.AreEqual(42000, matrix.Single(c => c.LicenceTypeId == 42 && c.Workload == "outlook").AssignedPopulation);
        }

        [TestMethod]
        public void OverviewRenewal_HonoursRealExpiryWithoutRenewingHealthySnapshots()
        {
            var now = new DateTime(2000, 7, 4, 0, 0, 0, DateTimeKind.Utc);
            var pairBudget = LicenceActivityLoadTests.ColdBudgetMs + LicenceActivityLoadTests.WarmBudgetMs;
            Assert.IsNull(LicenceActivityLoadTests.DelayBeforeOverviewRefresh(now.AddMinutes(1), now, pairBudget));
            Assert.AreEqual(TimeSpan.FromMilliseconds(10050),
                LicenceActivityLoadTests.DelayBeforeOverviewRefresh(now.AddSeconds(10), now, pairBudget).Value);
            Assert.AreEqual(TimeSpan.Zero,
                LicenceActivityLoadTests.DelayBeforeOverviewRefresh(now, now, pairBudget).Value);
            Assert.AreEqual(TimeSpan.Zero,
                LicenceActivityLoadTests.DelayBeforeOverviewRefresh(now.AddSeconds(-1), now, pairBudget).Value);
            Assert.IsNull(LicenceActivityLoadTests.DelayBeforeOverviewRefresh(now.AddSeconds(17), now, pairBudget));
            Assert.IsNotNull(LicenceActivityLoadTests.DelayBeforeOverviewRefresh(now.AddSeconds(16), now, pairBudget));
        }

        [TestMethod]
        public void EvidenceScope_DeclaresMatrixAndColdnessWithoutWeakeningBudgets()
        {
            var result = new LicenceActivityLoadTests.LoadResult();
            Assert.AreEqual(1500, result.RequiredMatrixCells);
            StringAssert.Contains(result.Scope, "fresh application caches");
            StringAssert.Contains(result.SqlBufferPool, "Not SQL-cold evidence");
            StringAssert.Contains(result.ConcurrencyUnit, "not individual SQL commands");
            Assert.AreEqual(50, result.MemorySampleIntervalMs);
            Assert.AreEqual(15000, LicenceActivityLoadTests.ColdBudgetMs);
            Assert.AreEqual(500, LicenceActivityLoadTests.WarmBudgetMs);
            Assert.AreEqual(25000, LicenceActivityLoadTests.ConcurrentBudgetMs);
            Assert.AreEqual(128L * 1024 * 1024, LicenceActivityLoadTests.MemoryDeltaBudgetBytes);
        }
    }
}
