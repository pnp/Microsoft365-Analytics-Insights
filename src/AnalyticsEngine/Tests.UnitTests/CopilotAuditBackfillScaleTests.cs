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
            var cs = ConfigurationManager.ConnectionStrings["SPOInsightsEntities"].ConnectionString;
            var target = new SqlConnectionStringBuilder(cs);
            Assert.AreEqual(@"(localdb)\MSSQLLocalDB", target.DataSource, true);
            Assert.AreEqual("UnitTestingAnalytics_817b_backfill", target.InitialCatalog);
            var days = int.Parse(Environment.GetEnvironmentVariable("BACKFILL_SYNTHETIC_SQL_DAYS") ?? "10");
            Assert.IsTrue(days >= 1 && days <= 10);
            DatabaseUpgrader.CheckDbUpgraded(new DatabaseUpgradeInfo { ConnectionString = cs }, _ => { });
            using (var db = TestDb())
                Assert.AreEqual(0, db.Database.SqlQuery<int>("SELECT COUNT(*) FROM dbo.audit_events").Single(), "Use a fresh dedicated synthetic catalog; this harness never deletes data.");

            var settings = new TestsAppConfig
            {
                ImportJobSettings = new ImportTaskSettings { Copilot = true },
                ResolveCopilotResourceMetadata = false,
                DaysBeforeNowToDownload = 7
            };
            var persistence = new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(),
                Common.Entities.UserScope.UserImportScope.Unfiltered, NullLogger.Instance, settings);
            var state = new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), true);
            var end = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
            await state.CreateAsync(end.AddDays(-days), end, "admin@contoso.com");
            var source = new SyntheticScaleSource(200000, 1000);
            var livePersistence = new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(),
                Common.Entities.UserScope.UserImportScope.Unfiltered, NullLogger.Instance, settings);
            var liveRows = 0;
            async Task<double> LiveProbeAsync(int probe)
            {
                var records = new WebActivityReportSet();
                for (var i = 0; i < 100; i++)
                    records.Add(CopilotAuditSearchRecordMapper.Map(SyntheticRecord(SyntheticId(90000000 + probe * 100 + i)), NullLogger.Instance).Content);
                var sw = Stopwatch.StartNew();
                await livePersistence.CommitAll(records);
                sw.Stop();
                Interlocked.Add(ref liveRows, records.Count);
                return sw.Elapsed.TotalMilliseconds;
            }
            var baselineLiveMs = await LiveProbeAsync(0);
            var before = SqlUsedBytes();
            var timer = Stopwatch.StartNew();
            CopilotAuditBackfillJob job;
            var cycles = 0;
            var liveTimes = new List<double>();
            using (var stopLive = new CancellationTokenSource())
            {
                var live = Task.Run(async () =>
                {
                    for (var probe = 1; probe <= 10; probe++)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(1), stopLive.Token); }
                        catch (OperationCanceledException) { break; }
                        liveTimes.Add(await LiveProbeAsync(probe));
                    }
                });
                try
                {
                    do
                    {
                        job = await new CopilotAuditBackfillImporter(state, source, persistence, settings, NullLogger.Instance,
                            existingEvents: new SqlCopilotAuditBackfillExistingEventFilter(TestDb)).AdvanceLatestAsync();
                        Assert.IsTrue(++cycles <= 100, "Backfill did not converge within the bounded synthetic run.");
                    } while (job.IsActive);
                }
                finally
                {
                    timer.Stop();
                    stopLive.Cancel();
                    await live;
                }
            }
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
            TestContext.WriteLine($"LivePersistenceProbeBatch=100; ColdBaselineMs={baselineLiveMs:F1}; ConcurrentProbeCount={liveTimes.Count}; ConcurrentProbeMs={string.Join(",", liveTimes.Select(t => t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}");
            TestContext.WriteLine("Live probes exercise normal SQL persistence concurrently, NOT a whole live Graph/Activity API import. Cold baseline and warmed concurrent probes are not a controlled performance comparison. Graph permission/response and deployed live-import impact NOT measured.");
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

            private static string ScaleEventId(int value)
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
