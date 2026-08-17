using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using DataUtils.Sql;
using DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tests.FakeDataGen.Seeding;
using Tests.FakeDataGen.StressTests.FakeLoaders;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;

namespace Tests.FakeDataGen.StressTests.LoadTest
{
    /// <summary>
    /// Non-interactive load-test harness for issue #161 / PR #162. Imports ~100k items in each
    /// import area under each aggressiveness setting (High/Balanced/Gentle == current stable/dev
    /// for per-import cost) and records, per run, BOTH the importer (App Service) CPU and the
    /// SQL Server (LocalDB sqlservr) CPU - the latter being the SQL CPU/DTU concern PR #162 does
    /// not yet address (InsertBatch commit fan-out is still hardcoded 20).
    ///
    /// Levers swept:
    ///  - audit area: AppConfig.MaxAuditReportLoadConcurrency (what PR #162 throttles).
    ///  - SQL-commit areas: InsertBatchConcurrency.MaxConcurrentThreads (the lever PR #162 misses).
    /// </summary>
    public class LoadTestSuite
    {
        private readonly string _connectionString;
        private readonly string _csvPath;
        private readonly int _targetItems;
        private readonly int _seedUsers;
        private readonly int _reps;
        private readonly string[] _areaFilter;

        private Process _importerProc;
        private Process _sqlProc;
        private readonly List<RunResult> _results = new List<RunResult>();

        /// <summary>(label, audit-load cap, SQL-commit cap). High==current stable/dev.</summary>
        private static readonly (string Label, int LoadCap, int CommitCap)[] Settings =
        {
            ("Current/High", 20, 20),
            ("Balanced", 8, 8),
            ("Gentle", 3, 3),
        };

        public LoadTestSuite(string connectionString, string csvPath, int targetItems = 100000, int reps = 3, string areas = null, int seedUsers = 2000)
        {
            _connectionString = connectionString;
            _csvPath = csvPath;
            _targetItems = targetItems;
            _reps = Math.Max(1, reps);
            _seedUsers = seedUsers;
            _areaFilter = string.IsNullOrWhiteSpace(areas)
                ? null
                : areas.Split(',');
        }

        /// <summary>True when this area should run (no filter = all). Matches on a substring.</summary>
        private bool WantArea(string area)
        {
            if (_areaFilter == null) return true;
            foreach (var f in _areaFilter)
            {
                if (!string.IsNullOrWhiteSpace(f) && area.IndexOf(f.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public void Run()
        {
            Console.WriteLine();
            Console.WriteLine("==================================================================");
            Console.WriteLine("  IMPORTER LOAD TEST - aggressiveness settings (issue #161/PR #162)");
            Console.WriteLine("==================================================================");
            Console.WriteLine($"  Target items/area : {_targetItems:N0}");
            Console.WriteLine($"  Reps per setting  : {_reps} (summary reports the median)");
            Console.WriteLine($"  Logical cores     : {Environment.ProcessorCount}");
            Console.WriteLine($"  CSV output        : {_csvPath}");

            _importerProc = Process.GetCurrentProcess();
            _sqlProc = ResolveSqlServerProcess();
            Console.WriteLine($"  Importer PID      : {_importerProc.Id}");
            Console.WriteLine($"  sqlservr PID      : {(_sqlProc != null ? _sqlProc.Id.ToString() : "(not found - SQL CPU unavailable)")}");
            Console.WriteLine();

            WriteCsvHeader();

            // Cheap, high-signal areas first so we always get the headline numbers.
            if (WantArea("audit load (all cores)")) SafeArea("audit load (all cores)", () => RunAuditArea("audit load (all cores)", false));
            if (WantArea("audit load (1-vCPU emu)")) SafeArea("audit load (1-vCPU emu)", () => RunAuditArea("audit load (1-vCPU emu)", true));
            if (WantArea("SQL commit (InsertBatch 100k rows)")) SafeArea("SQL commit (InsertBatch 100k rows)", RunInsertBatchArea);
            if (WantArea("copilot events (real manager)")) SafeArea("copilot events (real manager)", RunCopilotArea);
            if (WantArea("hits (App Insights, real)")) SafeArea("hits (App Insights, real)", RunHitsArea);
            if (WantArea("power platform (real manager)")) SafeArea("power platform (real manager)", RunPowerPlatformArea);
            if (WantArea("sent email (real, EF)")) SafeArea("sent email (real, EF)", RunSentEmailArea);
            if (WantArea("usage activity (SqlBulkCopy)")) SafeArea("usage activity (SqlBulkCopy)", RunUsageArea);

            PrintSummaryTable();
        }

        // ---------------------------------------------------------------- areas

        /// <summary>
        /// Audit full-load path (real ActivityImporter), in-memory loaders. Sweeps the audit LOAD
        /// concurrency - the lever PR #162 actually throttles. SQL is idle here, so this isolates
        /// the App-Service-side CPU win. When <paramref name="pinOneCore"/> is set the importer
        /// process is pinned to a single logical core to emulate the production 1-vCPU B1 plan that
        /// the "100% CPU spike" complaint is about.
        /// </summary>
        private void RunAuditArea(string areaName, bool pinOneCore)
        {
            // The real ActivityImporter derives its time chunks from config (~14), so total reports
            // ~= activeTypes * chunks * reportsPerTimeSlot. Size reportsPerTimeSlot to land near the
            // target and to yield >=100 summary chunks (of 1000) so the load cap actually binds.
            int reportsPerTimeSlot = Math.Max(1000, _targetItems / 14);
            const int reportsPerLoad = 1;
            const int maxSavesPerBatch = 1000;

            IntPtr originalAffinity = _importerProc.ProcessorAffinity;
            if (pinOneCore)
            {
                try { _importerProc.ProcessorAffinity = (IntPtr)1; }
                catch (Exception ex) { Console.WriteLine($"  (could not pin to 1 core: {ex.Message})"); }
            }
            try
            {
                foreach (var s in Settings)
                {
                    for (int rep = 1; rep <= _reps; rep++)
                    {
                        var config = FakeAppConfigFactory.Create();
                        config.MaxAuditReportLoadConcurrency = s.LoadCap;

                        var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
                        var saveManager = new FakeActivityReportPersistenceManager();
                        var importer = new LoadTestActivityImporter(config, telemetry, maxSavesPerBatch,
                            reportsPerLoad, reportsPerTimeSlot, 20);

                        GcReset();
                        var sampler = new CpuSampler(100, _importerProc, _sqlProc);
                        var sw = Stopwatch.StartNew();
                        sampler.Start();
                        var stats = Task.Run(async () => await importer.LoadReportsAndSave(saveManager)).GetAwaiter().GetResult();
                        sampler.Stop();
                        sw.Stop();

                        Record(areaName, s, rep, s.LoadCap, 0 /* no SQL commit */, stats.Total, sw, sampler,
                            "ActivityImporter load fan-out @ MaxAuditReportLoadConcurrency=" + s.LoadCap);
                    }
                }
            }
            finally
            {
                if (pinOneCore)
                {
                    try { _importerProc.ProcessorAffinity = originalAffinity; } catch { /* best effort */ }
                }
            }
        }

        /// <summary>
        /// Controlled benchmark of the single SQL-commit choke point used by every InsertBatch
        /// importer: stage 100k rows in parallel (exact production row-by-row insert path), sweeping
        /// InsertBatchConcurrency.MaxConcurrentThreads. This is the cleanest measurement of the SQL
        /// CPU/DTU lever PR #162 leaves hardcoded at 20.
        /// </summary>
        private void RunInsertBatchArea()
        {
            var rows = BuildBenchmarkRows(_targetItems);

            foreach (var s in Settings)
            {
                InsertBatchConcurrency.MaxConcurrentThreads = s.CommitCap;
                for (int rep = 1; rep <= _reps; rep++)
                {
                    var batch = new InsertBatch<BenchmarkRow>(_connectionString, AnalyticsLogger.ConsoleOnlyTracer());
                    batch.Rows = rows;

                    GcReset();
                    var sampler = new CpuSampler(100, _importerProc, _sqlProc);
                    var sw = Stopwatch.StartNew();
                    sampler.Start();
                    // Empty merge => measure the parallel staging insert itself (the lever); each row is a
                    // separate INSERT spread across CommitCap connections, exactly as the importers do.
                    Task.Run(async () => await batch.SaveToStagingTable(1000, string.Empty)).GetAwaiter().GetResult();
                    sampler.Stop();
                    sw.Stop();

                    Record("SQL commit (InsertBatch 100k rows)", s, rep, 0, s.CommitCap, rows.Count, sw, sampler,
                        "InsertBatch parallel insert @ InsertBatchConcurrency=" + s.CommitCap);
                }
            }

            InsertBatchConcurrency.MaxConcurrentThreads = 20; // restore default
        }

        /// <summary>
        /// Real Copilot import pipeline (CopilotAuditEventManager) committing 100k events to LocalDB,
        /// sweeping the SQL-commit lever. Staging + prerequisite audit_events are done OUTSIDE the
        /// measured window so the recorded CPU isolates CommitAllChanges (the InsertBatch fan-out).
        ///
        /// Unlike the InsertBatch benchmark (a ##temp table dropped each run), Copilot merges into
        /// permanent tables that grow run-over-run. To stop that monotonic growth from unfairly
        /// penalising whichever setting runs last, reps are the OUTER loop and the settings order is
        /// ROTATED each rep, so every setting lands in every position once and the per-setting median
        /// compares like-for-like table sizes.
        /// </summary>
        private void RunCopilotArea()
        {
            SeedUserPool();

            var userUpns = new List<string>(_seedUsers);
            for (int i = 0; i < _seedUsers; i++) userUpns.Add($"stressuser{i}@contoso.com");
            var random = new Random(42);

            for (int rep = 1; rep <= _reps; rep++)
            {
                for (int j = 0; j < Settings.Length; j++)
                {
                    var s = Settings[(j + rep - 1) % Settings.Length]; // rotate order each rep
                    InsertBatchConcurrency.MaxConcurrentThreads = s.CommitCap;

                    var manager = new CopilotAuditEventManager(_connectionString, new FakeCopilotMetadataLoader(),
                        new LoggerFactory().CreateLogger("CopilotLoadTest"));

                    // --- setup (NOT measured): stage events + insert prerequisite audit_events rows ---
                    var eventIds = new List<(Guid Id, string Upn)>(_targetItems);
                    for (int i = 0; i < _targetItems; i++)
                    {
                        var id = Guid.NewGuid();
                        var upn = userUpns[random.Next(userUpns.Count)];
                        eventIds.Add((id, upn));
                        var common = new CommonAuditEvent
                        {
                            Id = id,
                            TimeStamp = DateTime.UtcNow,
                            Operation = new EventOperation { Name = "CopilotInteraction" },
                            User = new User { AzureAdId = Guid.NewGuid().ToString(), UserPrincipalName = upn }
                        };
                        manager.SaveSingleCopilotEventToSqlStaging(BuildCopilotContent(random), common).GetAwaiter().GetResult();
                    }
                    InsertPrerequisiteAuditEvents(eventIds);

                    // --- measured: the SQL commit (InsertBatch staging insert + merge SQL) ---
                    GcReset();
                    var sampler = new CpuSampler(100, _importerProc, _sqlProc);
                    var sw = Stopwatch.StartNew();
                    sampler.Start();
                    Task.Run(async () => await manager.CommitAllChanges()).GetAwaiter().GetResult();
                    sampler.Stop();
                    sw.Stop();

                    Record("copilot events (real manager)", s, rep, 0, s.CommitCap, _targetItems, sw, sampler,
                        "CopilotAuditEventManager.CommitAllChanges @ InsertBatchConcurrency=" + s.CommitCap);
                }
            }

            InsertBatchConcurrency.MaxConcurrentThreads = 20; // restore default
        }

        /// <summary>
        /// Real App Insights "hits" import pipeline (<c>PageViewCollection.SaveToSQL</c>) committing
        /// 100k page-views to LocalDB, sweeping the SQL-commit lever. Like Copilot this is the same
        /// InsertBatch staging path BUT followed by a heavy multi-lookup MERGE (13 lookup upserts +
        /// a 13-join INSERT into hits), so it is expected to be merge-bound. Staging input is built
        /// OUTSIDE the measured window; the merge self-populates its lookups (no FK prerequisites).
        /// Reps are the outer loop with rotated setting order to neutralise table growth.
        /// </summary>
        private void RunHitsArea()
        {
            var random = new Random(99);
            var quiet = new LoggerFactory().CreateLogger("HitsLoadTest");

            for (int rep = 1; rep <= _reps; rep++)
            {
                for (int j = 0; j < Settings.Length; j++)
                {
                    var s = Settings[(j + rep - 1) % Settings.Length]; // rotate order each rep
                    InsertBatchConcurrency.MaxConcurrentThreads = s.CommitCap;

                    // --- setup (NOT measured): build the raw page-view collection ---
                    var pageViews = BuildPageViews(_targetItems, random);

                    // --- measured: stage hits + run the merge (the full SQL commit) ---
                    GcReset();
                    var sampler = new CpuSampler(100, _importerProc, _sqlProc);
                    var sw = Stopwatch.StartNew();
                    sampler.Start();
                    using (var db = new AnalyticsEntitiesContext(_connectionString, true, true))
                    {
                        Task.Run(async () => await pageViews.SaveToSQL(db, quiet)).GetAwaiter().GetResult();
                    }
                    sampler.Stop();
                    sw.Stop();

                    Record("hits (App Insights, real)", s, rep, 0, s.CommitCap, pageViews.Rows.Count, sw, sampler,
                        "PageViewCollection.SaveToSQL (stage + merge) @ InsertBatchConcurrency=" + s.CommitCap);
                }
            }

            InsertBatchConcurrency.MaxConcurrentThreads = 20; // restore default
        }

        /// <summary>
        /// Real Power Platform import (PowerPlatformAuditEventManager: 6 InsertBatch staging tables +
        /// 6 merge scripts). On the same InsertBatch lever as Copilot/hits, so it sweeps the commit
        /// cap with rotation. Minimal Power Apps events (no share permissions) to avoid extra FKs.
        /// </summary>
        private void RunPowerPlatformArea()
        {
            SeedUserPool();
            var userUpns = new List<string>(_seedUsers);
            for (int i = 0; i < _seedUsers; i++) userUpns.Add($"stressuser{i}@contoso.com");
            var random = new Random(55);
            var quiet = new LoggerFactory().CreateLogger("PPLoadTest");

            for (int rep = 1; rep <= _reps; rep++)
            {
                for (int j = 0; j < Settings.Length; j++)
                {
                    var s = Settings[(j + rep - 1) % Settings.Length]; // rotate order each rep
                    InsertBatchConcurrency.MaxConcurrentThreads = s.CommitCap;

                    var manager = new PowerPlatformAuditEventManager(_connectionString, quiet);

                    // --- setup (NOT measured): stage events + prerequisite audit_events ---
                    var eventIds = new List<(Guid Id, string Upn)>(_targetItems);
                    for (int i = 0; i < _targetItems; i++)
                    {
                        var id = Guid.NewGuid();
                        var upn = userUpns[random.Next(userUpns.Count)];
                        eventIds.Add((id, upn));
                        var common = new CommonAuditEvent
                        {
                            Id = id,
                            TimeStamp = DateTime.UtcNow,
                            Operation = new EventOperation { Name = "LaunchPowerApp" },
                            User = new User { AzureAdId = Guid.NewGuid().ToString(), UserPrincipalName = upn }
                        };
                        var content = new PowerAppsAuditLogContent
                        {
                            AppName = $"app-{random.Next(1, 500)}",
                            AppDisplayName = $"App {random.Next(1, 500)}",
                            EnvironmentName = $"env-{random.Next(1, 20)}",
                            AppSessionId = Guid.NewGuid().ToString("N"),
                            ClientType = "Web",
                            UserAgent = "Mozilla/5.0 loadtest"
                        };
                        manager.SaveSinglePowerAppEventToSqlStaging(content, common).GetAwaiter().GetResult();
                    }
                    InsertPrerequisiteAuditEvents(eventIds);

                    // --- measured: the SQL commit ---
                    GcReset();
                    var sampler = new CpuSampler(100, _importerProc, _sqlProc);
                    var sw = Stopwatch.StartNew();
                    sampler.Start();
                    Task.Run(async () => await manager.CommitAllChanges()).GetAwaiter().GetResult();
                    sampler.Stop();
                    sw.Stop();

                    Record("power platform (real manager)", s, rep, 0, s.CommitCap, _targetItems, sw, sampler,
                        "PowerPlatformAuditEventManager.CommitAllChanges @ InsertBatchConcurrency=" + s.CommitCap);
                }
            }

            InsertBatchConcurrency.MaxConcurrentThreads = 20; // restore default
        }

        /// <summary>
        /// Real Sent Email import (SentEmailImporter, fake source loader, no sentiment scoring).
        /// Persists via EF SaveChanges - NOT on the InsertBatch ParallelListProcessor lever - so the
        /// aggressiveness cap cannot change it. Run at a single setting as a per-area 100k baseline.
        /// </summary>
        private void RunSentEmailArea()
        {
            SeedUserPool(); // 2000 users with mail set
            int messagesPerUser = Math.Max(1, _targetItems / _seedUsers);
            var s = Settings[0]; // off-lever: one setting is enough

            for (int rep = 1; rep <= _reps; rep++)
            {
                var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
                var appConfig = FakeAppConfigFactory.Create();
                var loader = new FakeSentEmailSourceLoader(messagesPerUser, 3, 500, 70);
                Func<AnalyticsEntitiesContext> dbFactory = () => new AnalyticsEntitiesContext(_connectionString, true, true);
                var importer = new SentEmailImporter(telemetry, appConfig, loader,
                    NullSentEmailSentimentScorer.Instance, dbFactory);

                GcReset();
                var sampler = new CpuSampler(100, _importerProc, _sqlProc);
                var sw = Stopwatch.StartNew();
                sampler.Start();
                Task.Run(async () => await importer.ImportSentEmails()).GetAwaiter().GetResult();
                sampler.Stop();
                sw.Stop();

                Record("sent email (real, EF)", s, rep, 0, 0 /* off-lever */, (long)messagesPerUser * _seedUsers, sw, sampler,
                    "SentEmailImporter.ImportSentEmails (EF SaveChanges; not on InsertBatch lever)");
            }
        }

        /// <summary>
        /// Usage-report style bulk load: SqlBulkCopy 100k rows into sharepoint_user_activity_log.
        /// The production usage importer upserts via TVP + stored proc and the seed path uses
        /// SqlBulkCopy - neither is on the InsertBatch ParallelListProcessor lever - so this is an
        /// off-lever per-area baseline at a single setting.
        /// </summary>
        private void RunUsageArea()
        {
            SeedUserPool();
            var userIds = LoadSeededUserIds();
            if (userIds.Count == 0) { Console.WriteLine("  [usage] no seeded users; skipping."); return; }
            var random = new Random(77);
            var s = Settings[0]; // off-lever

            for (int rep = 1; rep <= _reps; rep++)
            {
                var dt = new DataTable();
                dt.Columns.Add("date", typeof(DateTime));
                dt.Columns.Add("last_activity_date", typeof(DateTime));
                dt.Columns.Add("user_id", typeof(int));
                dt.Columns.Add("viewed_or_edited", typeof(long));
                dt.Columns.Add("synced", typeof(long));
                dt.Columns.Add("shared_internally", typeof(long));
                dt.Columns.Add("shared_externally", typeof(long));
                var baseDate = DateTime.UtcNow.Date;
                for (int i = 0; i < _targetItems; i++)
                {
                    dt.Rows.Add(baseDate.AddDays(-(i % 365)), baseDate, userIds[random.Next(userIds.Count)],
                        (long)random.Next(0, 50), (long)random.Next(0, 10), (long)random.Next(0, 5), (long)random.Next(0, 3));
                }

                GcReset();
                var sampler = new CpuSampler(100, _importerProc, _sqlProc);
                var sw = Stopwatch.StartNew();
                sampler.Start();
                using (var conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    using (var bulk = new SqlBulkCopy(conn) { DestinationTableName = "sharepoint_user_activity_log", BatchSize = 10000, BulkCopyTimeout = 0 })
                    {
                        foreach (DataColumn col in dt.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                        bulk.WriteToServer(dt);
                    }
                }
                sampler.Stop();
                sw.Stop();

                Record("usage activity (SqlBulkCopy)", s, rep, 0, 0 /* off-lever */, dt.Rows.Count, sw, sampler,
                    "SqlBulkCopy into sharepoint_user_activity_log (not on InsertBatch lever)");
            }
        }

        private List<int> LoadSeededUserIds()
        {
            var ids = new List<int>();
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id FROM users WHERE user_name LIKE 'stressuser%@contoso.com';";
                    using (var r = cmd.ExecuteReader()) { while (r.Read()) ids.Add(r.GetInt32(0)); }
                }
            }
            return ids;
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Builds a realistic-ish raw page-view collection: moderate-cardinality URLs / users /
        /// sessions (so the merge's distinct lookup-upserts do real work) and small-cardinality
        /// device/browser/geo dimensions, with a unique pageRequestId per hit (the dedup key).
        /// </summary>
        private static PageViewCollection BuildPageViews(int count, Random random)
        {
            string[] browsers = { "Edge", "Chrome", "Firefox", "Safari" };
            string[] oses = { "Windows 10", "Windows 11", "macOS", "iOS", "Android" };
            string[] devices = { "PC", "iPhone", "Surface", "Pixel" };
            string[] countries = { "United Kingdom", "United States", "Spain", "Greece", "Germany" };
            string[] cities = { "London", "Seattle", "Madrid", "Athens", "Berlin" };
            string[] provinces = { "England", "Washington", "Madrid", "Attica", "Bavaria" };

            var coll = new PageViewCollection();
            for (int i = 0; i < count; i++)
            {
                int site = random.Next(1, 500);
                int page = random.Next(1, 200);
                string siteUrl = $"https://contoso.sharepoint.com/sites/site{site}";
                string webUrl = siteUrl;
                // ~1 in 6 a non-ASCII page so Unicode round-trips through staging + merge (repo rule).
                string url = (i % 6 == 0)
                    ? $"{siteUrl}/Καλημέρα/σελίδα{page}.aspx"
                    : $"{siteUrl}/pages/page{page}.aspx";

                coll.Rows.Add(new PageViewAppInsightsQueryResult
                {
                    Url = url,
                    Username = $"user{random.Next(0, _seedUsersForHits)}@contoso.com",
                    Browser = browsers[random.Next(browsers.Length)],
                    ClientOS = oses[random.Next(oses.Length)],
                    DeviceModel = devices[random.Next(devices.Length)],
                    CountryOrRegion = countries[random.Next(countries.Length)],
                    City = cities[random.Next(cities.Length)],
                    StateOrProvince = provinces[random.Next(provinces.Length)],
                    DurationMS = random.Next(50, 5000),
                    CustomProperties = new PageViewCustomProps
                    {
                        PageRequestId = Guid.NewGuid(),               // unique dedup key
                        SessionId = $"sess-{random.Next(0, count / 8 + 1)}", // ~8 hits per session
                        SiteUrl = siteUrl,
                        WebUrl = webUrl,
                        PageTitle = (i % 6 == 0) ? "Καλημέρα κόσμε" : $"Page {page}",
                        WebTitle = $"Site {site}",
                        SPRequestDuration = random.Next(10, 800),
                        PageLoad = random.Next(1, 9).ToString(),
                        EventTimestamp = DateTime.UtcNow
                    }
                });
            }
            return coll;
        }

        private const int _seedUsersForHits = 2000;

        private static CopilotAuditLogContent BuildCopilotContent(Random random)
        {
            var resources = new List<AccessedResource>();
            int count = random.Next(0, 4);
            for (int r = 0; r < count; r++)
            {
                resources.Add(new AccessedResource
                {
                    Id = $"resource-{Guid.NewGuid():N}",
                    // Real non-ASCII sample so Unicode round-trips through the staging insert (repo rule).
                    Name = random.Next(0, 5) == 0 ? "Καλημέρα κόσμε.pdf" : $"StressDoc{random.Next(1, 9999)}.docx",
                    Type = "Document",
                    SiteUrl = "https://contoso.sharepoint.com/sites/engineering"
                });
            }
            return new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData { AppHost = "Word", AccessedResources = resources }
            };
        }

        private void InsertPrerequisiteAuditEvents(List<(Guid Id, string Upn)> eventIds)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "INSERT INTO audit_events (id, time_stamp, user_id) " +
                        "SELECT @id, @ts, u.id FROM users u WHERE u.user_name = @upn;";
                    var pId = cmd.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier);
                    var pTs = cmd.Parameters.Add("@ts", System.Data.SqlDbType.DateTime);
                    var pUpn = cmd.Parameters.Add("@upn", System.Data.SqlDbType.NVarChar, 400);
                    foreach (var ev in eventIds)
                    {
                        pId.Value = ev.Id;
                        pTs.Value = DateTime.UtcNow;
                        pUpn.Value = ev.Upn;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
        }

        private void SeedUserPool()
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                UserMetadataSeeder.EnsureMetadataLookups(conn);
                UserMetadataSeeder.EnsureLicenseTypes(conn);
                var seeded = UserMetadataSeeder.SeedUsers(conn, _seedUsers, new Random(123));
                Console.WriteLine($"  [copilot] user pool ready ({seeded.Count:N0} new of {_seedUsers:N0}).");
            }
        }

        private static List<BenchmarkRow> BuildBenchmarkRows(int count)
        {
            var rows = new List<BenchmarkRow>(count);
            var random = new Random(7);
            for (int i = 0; i < count; i++)
            {
                bool greek = (i % 5) == 0;
                rows.Add(new BenchmarkRow
                {
                    Id = Guid.NewGuid(),
                    TimeStamp = DateTime.UtcNow,
                    UserId = random.Next(1, 200000),
                    // Realistic widths incl. a non-ASCII sample so Unicode is exercised (repo rule).
                    Url = greek
                        ? "https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε.pdf"
                        : $"https://contoso.sharepoint.com/sites/team{random.Next(1, 500)}/Shared Documents/file{i}.docx",
                    Title = greek ? "Καλημέρα κόσμε" : $"Document {i}",
                    Operation = "FileAccessed"
                });
            }
            return rows;
        }

        private static void GcReset()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private Process ResolveSqlServerProcess()
        {
            try
            {
                using (var conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT SERVERPROPERTY('ProcessID');";
                        var pid = Convert.ToInt32(cmd.ExecuteScalar());
                        return Process.GetProcessById(pid);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WARNING: could not resolve sqlservr process: {ex.Message}");
                return null;
            }
        }

        private void SafeArea(string area, Action run)
        {
            Console.WriteLine($"---- {area} ----");
            try { run(); }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  AREA FAILED ({area}): {ex.GetBaseException().Message}");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        private void Record(string area, (string Label, int LoadCap, int CommitCap) s, int rep, int loadCap, int commitCap,
            long items, Stopwatch sw, CpuSampler sampler, string notes)
        {
            var r = new RunResult
            {
                Area = area,
                Setting = s.Label,
                Rep = rep,
                LoadCap = loadCap,
                CommitCap = commitCap,
                Items = items,
                WallSeconds = sw.Elapsed.TotalSeconds,
                ImporterCpuSeconds = sampler.CpuSeconds(0),
                ImporterPeakCpuPct = sampler.PeakCpuPercent(0),
                SqlCpuSeconds = sampler.CpuSeconds(1),
                SqlPeakCpuPct = sampler.PeakCpuPercent(1),
                PeakMemMb = sampler.PeakWorkingSetMb,
                Notes = notes
            };
            _results.Add(r);
            AppendCsv(r);

            Console.WriteLine(
                $"  {s.Label,-13} rep{rep} items={items,8:N0}  wall={r.WallSeconds,7:F2}s  " +
                $"thru={r.ItemsPerSecond,9:N0}/s  impCPU={r.ImporterCpuSeconds,6:F1}s(peak {r.ImporterPeakCpuPct,5:F0}%)  " +
                $"sqlCPU={r.SqlCpuSeconds,6:F1}s(peak {r.SqlPeakCpuPct,5:F0}%)");
        }

        // ---------------------------------------------------------------- output

        private void WriteCsvHeader()
        {
            var dir = Path.GetDirectoryName(_csvPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_csvPath,
                "area,setting,rep,load_cap,commit_cap,items,wall_s,items_per_s,importer_cpu_s,importer_peak_cpu_pct,sql_cpu_s,sql_peak_cpu_pct,peak_mem_mb,notes\n");
        }

        private void AppendCsv(RunResult r)
        {
            var ci = CultureInfo.InvariantCulture;
            File.AppendAllText(_csvPath, string.Join(",",
                Csv(r.Area), Csv(r.Setting), r.Rep, r.LoadCap, r.CommitCap, r.Items,
                r.WallSeconds.ToString("F3", ci), r.ItemsPerSecond.ToString("F1", ci),
                r.ImporterCpuSeconds.ToString("F3", ci), r.ImporterPeakCpuPct.ToString("F1", ci),
                r.SqlCpuSeconds.ToString("F3", ci), r.SqlPeakCpuPct.ToString("F1", ci),
                r.PeakMemMb.ToString("F1", ci), Csv(r.Notes)) + "\n");
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        /// <summary>
        /// Prints the per-(area,setting) MEDIAN across reps - medians shrug off the LocalDB/background
        /// jitter that a single run suffers, so the cross-setting comparison is trustworthy.
        /// </summary>
        private void PrintSummaryTable()
        {
            Console.WriteLine();
            Console.WriteLine($"=============================== SUMMARY (median of {_reps} rep(s)) ===============================");
            Console.WriteLine($"{"Area",-34}{"Setting",-13}{"items",9}{"wall_s",9}{"impCPUs",9}{"impPk%",8}{"sqlCPUs",9}{"sqlPk%",8}");

            var keys = new List<(string Area, string Setting)>();
            foreach (var r in _results)
            {
                var k = (r.Area, r.Setting);
                if (!keys.Contains(k)) keys.Add(k);
            }

            string lastArea = null;
            foreach (var k in keys)
            {
                var grp = _results.FindAll(r => r.Area == k.Area && r.Setting == k.Setting);
                if (k.Area != lastArea) { Console.WriteLine(new string('-', 99)); lastArea = k.Area; }
                Console.WriteLine(
                    $"{Trunc(k.Area, 33),-34}{k.Setting,-13}{Median(grp, x => x.Items),9:N0}{Median(grp, x => x.WallSeconds),9:F2}" +
                    $"{Median(grp, x => x.ImporterCpuSeconds),9:F1}{Median(grp, x => x.ImporterPeakCpuPct),8:F0}" +
                    $"{Median(grp, x => x.SqlCpuSeconds),9:F1}{Median(grp, x => x.SqlPeakCpuPct),8:F0}");
            }
            Console.WriteLine(new string('=', 99));
            Console.WriteLine($"Full per-rep CSV: {_csvPath}");
        }

        private static double Median(List<RunResult> rows, Func<RunResult, double> sel)
        {
            var vals = new List<double>();
            foreach (var r in rows) vals.Add(sel(r));
            vals.Sort();
            int n = vals.Count;
            if (n == 0) return 0;
            return (n % 2 == 1) ? vals[n / 2] : (vals[n / 2 - 1] + vals[n / 2]) / 2.0;
        }

        private static string Trunc(string s, int n) { return string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n); }

        // ---------------------------------------------------------------- types

        /// <summary>Representative staging row for the controlled InsertBatch benchmark.</summary>
        [TempTableName("##loadtest_staging")]
        public class BenchmarkRow
        {
            // NB: must NOT be named "id" - InsertBatch auto-prepends an [id] int IDENTITY PK column.
            [Column("event_id")] public Guid Id { get; set; }
            [Column("time_stamp")] public DateTime TimeStamp { get; set; }
            [Column("user_id")] public int UserId { get; set; }
            [Column("full_url", true, SqlTypeOverride = "nvarchar(850)")] public string Url { get; set; }
            [Column("title", true, SqlTypeOverride = "nvarchar(450)")] public string Title { get; set; }
            [Column("operation", true, SqlTypeOverride = "nvarchar(100)")] public string Operation { get; set; }
        }

        private class RunResult
        {
            public string Area { get; set; }
            public string Setting { get; set; }
            public int Rep { get; set; }
            public int LoadCap { get; set; }
            public int CommitCap { get; set; }
            public long Items { get; set; }
            public double WallSeconds { get; set; }
            public double ImporterCpuSeconds { get; set; }
            public double ImporterPeakCpuPct { get; set; }
            public double SqlCpuSeconds { get; set; }
            public double SqlPeakCpuPct { get; set; }
            public double PeakMemMb { get; set; }
            public string Notes { get; set; }
            public double ItemsPerSecond { get { return WallSeconds > 0 ? Items / WallSeconds : 0; } }
        }
    }
}
