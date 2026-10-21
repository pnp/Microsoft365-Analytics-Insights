using Common.Entities.Config;
using Common.Entities.CopilotAuditBackfill;
using Common.Entities.State;
using Common.Entities;
using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using DataUtils;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.AuditBackfill;

namespace Tests.UnitTests
{
    public partial class CopilotAuditBackfillTests
    {
        [TestMethod]
        [TestCategory("SyntheticScale")]
        public async Task Importer_Synthetic200kUserPagingEvidence()
        {
            if (Environment.GetEnvironmentVariable("BACKFILL_SYNTHETIC_SCALE") != "1")
                Assert.Inconclusive("Opt in with Benchmarks\\Invoke-CopilotAuditBackfillEvidence.ps1.");

            const int users = 200000;
            const int days = 10;
            const int pageSize = 1000;
            var inactive = RegressionImporter(new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), true),
                new CallbackSource(_ => throw new AssertFailedException("Inactive backfill must not read Graph records.")), new CountingPersistence());
            var inactiveTimer = Stopwatch.StartNew();
            for (var i = 0; i < 10000; i++) Assert.IsNull(await inactive.AdvanceLatestAsync());
            inactiveTimer.Stop();
            var state = new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), true);
            var end = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
            await state.CreateAsync(end.AddDays(-days), end, "admin@contoso.com");
            var source = new SyntheticScaleSource(users, pageSize);
            var persistence = new SyntheticScalePersistence();
            var beforeHeap = GC.GetTotalMemory(true);
            var timer = Stopwatch.StartNew();
            CopilotAuditBackfillJob job;
            var cycles = 0;
            do
            {
                job = await RegressionImporter(state, source, persistence, existing: persistence).AdvanceLatestAsync();
                Assert.IsTrue(++cycles <= 100, "Synthetic backfill did not converge.");
            } while (job.IsActive);
            timer.Stop();
            Assert.AreEqual(CopilotAuditBackfillStates.Completed, job.State);
            Assert.AreEqual((long)users * days, job.RecordsSeen);
            Assert.AreEqual((long)users * days, job.RecordsImported + job.RecordsAlreadyPresent);
            Assert.AreEqual(days, source.Submits);
            Assert.AreEqual(users * days / pageSize, source.Pages);
            Assert.AreEqual(users * days, persistence.Ids.Count);
            Assert.AreEqual((long)users * days / 10, job.RecordsAlreadyPresent);
            Assert.AreEqual(0, job.Gaps.Count);
            TestContext.WriteLine("SCOPE=IN_MEMORY_ONLY; SQL/Graph latency, SQL storage and real live-import overhead NOT measured.");
            TestContext.WriteLine("Elapsed time includes synthetic fixture generation and JSON round-trip; persistence retains IDs only. Uniform workload: one distinct Unicode file per user, one resource per interaction, distributed synthetic GUID keys.");
            TestContext.WriteLine($"InactiveBackfillCalls=10000; InMemoryInactiveElapsedMs={inactiveTimer.Elapsed.TotalMilliseconds:F1}; InMemoryInactiveMeanMs={inactiveTimer.Elapsed.TotalMilliseconds / 10000:F6}");
            TestContext.WriteLine($"SyntheticUsers={users}; Days={days}; Interactions={job.RecordsSeen}; PageSize={pageSize}; Pages={source.Pages}; Submissions={source.Submits}");
            TestContext.WriteLine($"ElapsedMs={timer.Elapsed.TotalMilliseconds:F1}; RecordsPerSecond={job.RecordsSeen / timer.Elapsed.TotalSeconds:F1}; SerializedPageUtf8Bytes={source.Bytes}; SerializedFinalStateUtf8Bytes={Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(job))}");
            TestContext.WriteLine($"ManagedHeapDeltaBytes={GC.GetTotalMemory(true) - beforeHeap}; ProcessPeakWorkingSetBytes={Process.GetCurrentProcess().PeakWorkingSet64}; PersistenceCommits={persistence.Commits}; MaxBatch={persistence.MaxBatch}; SyntheticOverlap={job.RecordsAlreadyPresent}");
        }

        [TestMethod]
        [TestCategory("SyntheticSqlScale")]
        public async Task Importer_SyntheticSql200kUserEvidence()
        {
            if (Environment.GetEnvironmentVariable("BACKFILL_SYNTHETIC_SQL_SCALE") != "1")
                Assert.Inconclusive("Opt in with the evidence runner -SqlScale.");
            var days = int.Parse(Environment.GetEnvironmentVariable("BACKFILL_SYNTHETIC_SQL_DAYS") ?? "10");
            Assert.IsTrue(days >= 1 && days <= 10);
            CreateFreshSyntheticSqlDatabase();
            using (var db = TestDb())
            {
                Assert.AreEqual(0, db.Database.SqlQuery<int>("SELECT COUNT(*) FROM dbo.audit_events").Single(), "Use a fresh dedicated synthetic catalog; this harness never deletes data.");
                TestContext.WriteLine("SqlProductVersion=" + db.Database.SqlQuery<string>(
                    "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'))").Single());
            }

            var settings = new TestsAppConfig
            {
                ImportJobSettings = new ImportTaskSettings { Copilot = true },
                ResolveCopilotResourceMetadata = false,
                DaysBeforeNowToDownload = 7
            };
            var sqlPersistence = new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(),
                Common.Entities.UserScope.UserImportScope.Unfiltered, NullLogger.Instance, settings);
            var state = new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), true);
            var end = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
            await state.CreateAsync(end.AddDays(-days), end, "admin@contoso.com");
            var source = new SyntheticScaleSource(200000, 1000);
            var livePersistence = new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(),
                Common.Entities.UserScope.UserImportScope.Unfiltered, NullLogger.Instance, settings);
            var liveRows = 0;
            var probe = 0;
            async Task<double> LiveProbeAsync()
            {
                var records = new WebActivityReportSet();
                for (var i = 0; i < 100; i++)
                    records.Add(CopilotAuditSearchRecordMapper.Map(SyntheticRecord(SyntheticId(90000000 + probe * 100 + i)), NullLogger.Instance).Content);
                probe++;
                var sw = Stopwatch.StartNew();
                await livePersistence.CommitAll(records);
                sw.Stop();
                Interlocked.Add(ref liveRows, records.Count);
                return sw.Elapsed.TotalMilliseconds;
            }
            var coldLiveMs = await LiveProbeAsync();
            var before = SqlUsedBytes();
            WriteSqlStorage("Before");
            var commits = 0;
            long persisted = 0;
            var commitTimes = new List<double>();
            var serialLiveTimes = new List<double>();
            var concurrentLiveTimes = new List<double>();
            var persistence = new MeasuredScalePersistence(async records =>
            {
                var sample = commits % 40 < 5;
                if (sample) serialLiveTimes.Add(await LiveProbeAsync());
                var sw = Stopwatch.StartNew();
                var commit = sqlPersistence.CommitAll(records);
                Task<double> live = null;
                if (sample)
                {
                    await Task.Delay(10);
                    Assert.IsFalse(commit.IsCompleted, "A concurrent probe must overlap real backfill SQL work.");
                    live = LiveProbeAsync();
                }
                var stats = await commit;
                sw.Stop();
                commitTimes.Add(sw.Elapsed.TotalMilliseconds);
                if (live != null)
                {
                    var concurrent = await live;
                    concurrentLiveTimes.Add(concurrent);
                    TestContext.WriteLine($"PairedProbe; ExistingBackfillRows={persisted}; SerialLiveMs={serialLiveTimes.Last():F1}; ConcurrentLiveMs={concurrent:F1}; BackfillBatch={records.Count}; BackfillMs={sw.Elapsed.TotalMilliseconds:F1}");
                }
                persisted += records.Count;
                commits++;
                if (commits % 40 == 0) WriteSqlStorage($"AfterBackfillRows{persisted}");
                return stats;
            });
            var timer = Stopwatch.StartNew();
            CopilotAuditBackfillJob job;
            var cycles = 0;
            var cycleTimes = new List<double>();
            do
            {
                var cycleTimer = Stopwatch.StartNew();
                job = await new CopilotAuditBackfillImporter(state, source, persistence, settings, NullLogger.Instance,
                    existingEvents: new SqlCopilotAuditBackfillExistingEventFilter(TestDb)).AdvanceLatestAsync();
                cycleTimer.Stop();
                cycleTimes.Add(cycleTimer.Elapsed.TotalMilliseconds);
                Assert.IsTrue(++cycles <= 1000, "Backfill did not converge within the bounded synthetic run.");
            } while (job.IsActive);
            timer.Stop();
            Assert.AreEqual(CopilotAuditBackfillStates.Completed, job.State);
            using (var db = TestDb())
            {
                Assert.AreEqual(200000L * days + liveRows, db.Database.SqlQuery<long>("SELECT COUNT_BIG(*) FROM dbo.copilot_chats").Single());
                Assert.AreEqual(200000L, db.Database.SqlQuery<long>("SELECT COUNT_BIG(*) FROM dbo.users WHERE user_name LIKE 'user%@contoso.com'").Single());
                Assert.AreEqual("Καλημέρα κόσμε 000000.docx", db.Database.SqlQuery<string>(
                    "SELECT [name] FROM dbo.copilot_event_accessed_resource_names WHERE [name] = @p0", "Καλημέρα κόσμε 000000.docx").Single());
            }
            TestContext.WriteLine($"SCOPE=LOCALDB_PRODUCTION_SCHEMA; SyntheticUsers=200000; Days={days}; Interactions={job.RecordsSeen}; ElapsedMs={timer.Elapsed.TotalMilliseconds:F1}; RecordsPerSecond={job.RecordsSeen / timer.Elapsed.TotalSeconds:F1}; SqlUsedBytesGrowth={SqlUsedBytes() - before}; Cycles={cycles}");
            TestContext.WriteLine("Elapsed time includes synthetic fixture generation, JSON round-trip and SQL persistence. Uniform workload: one distinct Unicode file per user, one resource per interaction, plus one synthetic live-probe user. SQL byte growth covers all table/index used pages, not allocated file/log size.");
            TestContext.WriteLine($"LivePersistenceProbeBatch=100; SyntheticLiveRows={liveRows}; DiscardedColdMs={coldLiveMs:F1}; PairedProbeCount={concurrentLiveTimes.Count}; WarmSerialMedianMs={Median(serialLiveTimes):F1}; ConcurrentMedianMs={Median(concurrentLiveTimes):F1}; BackfillCommits={commits}; BackfillCommitMedianMs={Median(commitTimes):F1}");
            TestContext.WriteLine($"InstrumentedAdvanceMedianMs={Median(cycleTimes):F1}; InstrumentedAdvanceMaxMs={cycleTimes.Max():F1}; CycleSamples={cycleTimes.Count}; Instrumented timings include paired-probe overhead, not an uninstrumented live-cycle SLA.");
            TestContext.WriteLine("Each paired probe uses the same production persistence and 100 unique synthetic events, serial immediately before then concurrent with a real backfill batch at approximately the same cardinality. Paired probes include semaphore contention. Cold first probe is excluded. No threshold is asserted: impact must be evaluated, not hidden.");
            TestContext.WriteLine("This is a synthetic SQL persistence comparison, NOT whole live Graph/Activity API imports, SQL cloud capacity, real Graph throughput or production isolation. Resource metadata Graph enrichment is disabled. The orchestration runs inline after live import and adds bounded work to that cycle; zero delay is NOT guaranteed.");
            WriteSqlStorage("Final");
            await MeasureSqlExistingEventSelectivityAsync();
        }

        private static void CreateFreshSyntheticSqlDatabase()
        {
            var cs = ConfigurationManager.ConnectionStrings["SPOInsightsEntities"].ConnectionString;
            var target = new SqlConnectionStringBuilder(cs);
            var catalog = Environment.GetEnvironmentVariable("BACKFILL_SYNTHETIC_SQL_CATALOG");
            var instance = Environment.GetEnvironmentVariable("BACKFILL_SYNTHETIC_SQL_INSTANCE");
            const string prefix = "UnitTestingAnalytics_Backfill_";
            const string instancePrefix = "Backfill_";
            Assert.IsTrue(instance != null && instance.StartsWith(instancePrefix, StringComparison.Ordinal)
                && Guid.TryParseExact(instance.Substring(instancePrefix.Length), "N", out _), "Isolate instance-global staging tables.");
            Assert.AreEqual(@"(localdb)\" + instance, target.DataSource, true);
            Assert.IsTrue(catalog != null && catalog.StartsWith(prefix, StringComparison.Ordinal)
                && Guid.TryParseExact(catalog.Substring(prefix.Length), "N", out _), "Use a new random dedicated synthetic catalog.");
            Assert.AreEqual(catalog, target.InitialCatalog);
            target.InitialCatalog = "master";
            using (var connection = new SqlConnection(target.ConnectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "IF DB_ID(@catalog) IS NOT NULL THROW 51000, 'Synthetic catalog already exists; refusing to reuse it.', 1; CREATE DATABASE [" + catalog + "]";
                    command.Parameters.AddWithValue("@catalog", catalog);
                    command.ExecuteNonQuery();
                }
            }
            DatabaseUpgrader.CheckDbUpgraded(new DatabaseUpgradeInfo { ConnectionString = cs }, _ => { });
        }

        private void WriteSqlStorage(string stage)
        {
            using (var db = TestDb())
            {
                foreach (var row in db.Database.SqlQuery<SqlStorageEvidence>(@"
SELECT t.name AS TableName, MAX(p.row_count) AS Rows,
    SUM(CASE WHEN p.index_id <= 1 THEN CONVERT(bigint, p.used_page_count) ELSE 0 END) * 8192 AS DataUsedBytes,
    SUM(CASE WHEN p.index_id > 1 THEN CONVERT(bigint, p.used_page_count) ELSE 0 END) * 8192 AS IndexUsedBytes
FROM sys.dm_db_partition_stats p INNER JOIN sys.tables t ON t.object_id = p.object_id
GROUP BY t.name").ToList())
                    TestContext.WriteLine($"StorageStage={stage}; Table={row.TableName}; Rows={row.Rows}; DataUsedBytes={row.DataUsedBytes}; IndexUsedBytes={row.IndexUsedBytes}");
            }
        }

        private async Task MeasureSqlExistingEventSelectivityAsync()
        {
            var filter = new SqlCopilotAuditBackfillExistingEventFilter(TestDb);
            foreach (var batch in new[] { 100, 1000, 5000 })
            foreach (var percentExisting in new[] { 0, 10, 100 })
            {
                var ids = Enumerable.Range(1, batch).Select(i => i <= batch * percentExisting / 100
                    ? Guid.Parse(SyntheticScaleSource.ScaleEventId(i)) : Guid.Parse(SyntheticId(80000000 + i))).ToList();
                var times = new List<double>();
                for (var iteration = 0; iteration < 6; iteration++)
                {
                    var sw = Stopwatch.StartNew();
                    var found = await filter.GetExistingIdsAsync(ids);
                    sw.Stop();
                    Assert.AreEqual(batch * percentExisting / 100, found.Count);
                    if (iteration > 0) times.Add(sw.Elapsed.TotalMilliseconds);
                }
                TestContext.WriteLine($"ExistingEventFilter; Batch={batch}; ExistingPercent={percentExisting}; WarmMedianMs={Median(times):F1}; Samples={times.Count}");
            }
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count % 2 == 0 ? (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2 : sorted[sorted.Count / 2];
        }

        private sealed class SqlStorageEvidence
        {
            public string TableName { get; set; }
            public long Rows { get; set; }
            public long DataUsedBytes { get; set; }
            public long IndexUsedBytes { get; set; }
        }

        private sealed class MeasuredScalePersistence : IActivityReportPersistenceManager
        {
            private readonly Func<ActivityReportSet, Task<ImportStat>> _commit;
            public MeasuredScalePersistence(Func<ActivityReportSet, Task<ImportStat>> commit) { _commit = commit; }
            public Task<ImportStat> CommitAll(ActivityReportSet activities) => _commit(activities);
        }

        private static long SqlUsedBytes()
        {
            using (var db = TestDb())
                return db.Database.SqlQuery<long>("SELECT SUM(CONVERT(bigint, used_page_count)) * 8192 FROM sys.dm_db_partition_stats").Single();
        }

        private sealed class SyntheticScaleSource : ICopilotAuditSearchSource
        {
            private readonly int _users;
            private readonly int _pageSize;
            private readonly List<CopilotAuditBackfillSlice> _slices = new List<CopilotAuditBackfillSlice>();
            public int Submits;
            public int Pages;
            public long Bytes;
            public SyntheticScaleSource(int users, int pageSize) { _users = users; _pageSize = pageSize; }
            public Task<AppTokenPermissionAccess> GetPermissionAccessAsync()
                => Task.FromResult(AppTokenPermissionAccess.Granted);
            public Task<CopilotAuditSearchQuery> SubmitQueryAsync(CopilotAuditBackfillSlice slice)
            {
                _slices.Add(slice);
                return Task.FromResult(new CopilotAuditSearchQuery { Id = (++Submits).ToString(), Status = "running" });
            }
            public Task<CopilotAuditSearchQuery> GetQueryAsync(string queryId)
                => Task.FromResult(new CopilotAuditSearchQuery { Id = queryId, Status = "succeeded", RecordCountLimit = 1000000, ApproximateReturnedRecordCount = _users });
            public Task<CopilotAuditSearchRecordPage> GetRecordsAsync(string queryId, string nextLink = null)
            {
                var offset = nextLink == null ? 0 : int.Parse(nextLink);
                var day = int.Parse(queryId) - 1;
                var values = new JArray();
                for (var i = offset; i < Math.Min(offset + _pageSize, _users); i++)
                {
                    var record = SyntheticRecord(ScaleEventId(day * _users + i + 1),
                        "\"AppHost\":\"Word\",\"AccessedResources\":[{\"Id\":\"https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε.docx\",\"Name\":\"Καλημέρα κόσμε.docx\",\"Type\":\"File\"}],\"Contexts\":[],\"Messages\":[]");
                    record.UserPrincipalName = $"user{i:000000}@contoso.com";
                    record.AuditData["UserId"] = record.UserPrincipalName;
                    record.AuditData["CopilotEventData"]["AccessedResources"][0]["Name"] = $"Καλημέρα κόσμε {i:000000}.docx";
                    record.AuditData["CopilotEventData"]["AccessedResources"][0]["Id"] = $"https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε {i:000000}.docx";
                    record.CreatedDateTime = _slices[day].StartUtc.AddMinutes(1);
                    record.AuditData["CreationTime"] = record.CreatedDateTime.Value.ToString("s") + "Z";
                    values.Add(JObject.FromObject(record));
                }
                var json = new JObject
                {
                    ["value"] = values,
                    ["@odata.nextLink"] = offset + _pageSize < _users ? (offset + _pageSize).ToString() : null
                }.ToString(Formatting.None);
                Bytes += Encoding.UTF8.GetByteCount(json);
                Pages++;
                return Task.FromResult(CopilotAuditSearchRecordPage.FromJson(json));
            }

            public static string ScaleEventId(int value)
            {
                // Unique, reproducible synthetic IDs with a distributed SQL GUID sort key.
                var a = unchecked((uint)value * 2654435761u);
                var b = unchecked(a * 2246822519u);
                return new Guid(value, unchecked((short)(a >> 16)), unchecked((short)a),
                    (byte)b, (byte)(b >> 8), (byte)(b >> 16), (byte)(b >> 24),
                    (byte)a, (byte)(a >> 8), (byte)(a >> 16), (byte)(a >> 24)).ToString();
            }
        }

        private sealed class SyntheticScalePersistence : IActivityReportPersistenceManager, ICopilotAuditBackfillExistingEventFilter
        {
            public HashSet<Guid> Ids = new HashSet<Guid>();
            public int Commits;
            public int MaxBatch;
            public Task<HashSet<Guid>> GetExistingIdsAsync(IEnumerable<Guid> ids)
            {
                // Model live arrivals immediately before each backfill existence check.
                var list = ids.ToList();
                for (var i = 0; i < list.Count; i += 10) Ids.Add(list[i]);
                return Task.FromResult(new HashSet<Guid>(list.Where(Ids.Contains)));
            }
            public Task<ImportStat> CommitAll(ActivityReportSet activities)
            {
                Commits++;
                MaxBatch = Math.Max(MaxBatch, activities.Count);
                foreach (var activity in activities)
                {
                    Assert.IsTrue(Ids.Add(activity.Id), "Overlapping event must not be written twice.");
                    var content = (WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.CopilotAuditLogContent)activity;
                    StringAssert.StartsWith(content.CopilotEventData.AccessedResources.Single().Name, "Καλημέρα κόσμε ");
                }
                return Task.FromResult(new ImportStat { Imported = activities.Count, Total = activities.Count });
            }
        }
    }
}
