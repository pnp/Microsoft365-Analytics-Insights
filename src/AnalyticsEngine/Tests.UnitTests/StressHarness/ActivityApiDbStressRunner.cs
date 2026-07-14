using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using Common.Entities;
using DataUtils;
using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace Tests.UnitTests.StressHarness
{
    /// <summary>
    /// Reusable orchestrator for the DB-backed ActivityAPI load test. Drives the REAL
    /// <see cref="ActivityReportSqlPersistenceManager"/> against a real database (LocalDB or a supplied
    /// connection string) at scale with a NARROW org-URL whitelist, and runs two scenarios:
    ///   COLD - empty DB (raw throughput / save serialization).
    ///   WARM - re-run the same window (steady state: everything already imported/ignored).
    /// Reports wall-time, per-phase (save) timing, blob-load counts, import stats and peak memory so
    /// baseline-vs-optimised runs are directly comparable.
    ///
    /// Lives in Tests.UnitTests so it can be run either via <c>vstest.console</c> (an MSTest wrapper) or
    /// via the Tests.FakeDataGen console (<c>--run activityapidb</c>) which references this project.
    /// </summary>
    public static class ActivityApiDbStressRunner
    {
        /// <summary>
        /// Dedicated LocalDB database used when no connection string is supplied. Deliberately NOT the
        /// unit-test DB (UnitTestingSPOInsights) because the COLD scenario DROPS this database.
        /// </summary>
        public const string DefaultLocalDbConnectionString =
            "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=AuditImporterStressTest;" +
            "Integrated Security=true;MultipleActiveResultSets=True;App=EntityFramework";

        // Delete order (children first) for resetting a non-LocalDB target (best effort).
        private static readonly string[] ImportTablesChildFirst =
        {
            "event_meta_sharepoint", "event_meta_exchange", "event_meta_azure_ad", "event_meta_general",
            "audit_events", "ignored_audit_events",
            "urls", "webs", "sites",
            "event_file_names", "event_file_ext", "event_types", "event_operations", "users"
        };

        /// <summary>
        /// Runs the full COLD (+ optional WARM) load test against <paramref name="connectionString"/>.
        /// </summary>
        public static StressRunResult RunAll(string connectionString, StressAuditDataConfig data,
            int maxSavesPerBatch, bool runWarm, Action<string> log = null)
        {
            log = log ?? Console.WriteLine;

            // EF6 logs every SQL statement via Database.Log -> Debug.WriteLine on the parameterless-ctor
            // contexts (the per-batch import cache). That both floods the output and skews the timing we're
            // trying to measure, so silence Debug/Trace output for the duration of the load test.
            System.Diagnostics.Trace.Listeners.Clear();

            // Point the real persistence manager's parameterless AnalyticsEntitiesContext()
            // (name=SPOInsightsEntities) at our target DB.
            ForceRuntimeConnectionString("SPOInsightsEntities", connectionString);

            var prepSw = Stopwatch.StartNew();
            log($"[prep] Resetting target database to an empty state (COLD)...");
            ResetDatabase(connectionString, log);
            log($"[prep] Ensuring schema (DatabaseUpgrader.CheckDbUpgraded)...");
            EnsureSchema(connectionString, log);
            log($"[prep] Seeding org_urls whitelist ({data.InScopePrefix})...");
            SeedWhitelist(connectionString, data.InScopePrefix);
            if (data.PreSeedHistoricalAuditEvents > 0)
            {
                PreSeedHistoricalAuditEvents(connectionString, data.PreSeedHistoricalAuditEvents, data.BaseTimeUtc, log);
            }
            // Start the blob checkpoint empty so COLD is a true cold start (the in-memory store is static,
            // so it would otherwise carry over from a previous run in the same process).
            if (data.UseBlobCheckpoint)
            {
                InMemoryProcessedBlobStore.ResetSharedState();
                log("[prep] Blob checkpoint ENABLED (in-memory; persists COLD -> WARM).");
            }
            else
            {
                log("[prep] Blob checkpoint DISABLED.");
            }
            prepSw.Stop();
            log($"[prep] Done in {prepSw.Elapsed.TotalSeconds:F1}s\n");

            var result = new StressRunResult
            {
                Cold = RunScenario("COLD", connectionString, data, maxSavesPerBatch, log)
            };

            if (runWarm)
            {
                result.Warm = RunScenario("WARM", connectionString, data, maxSavesPerBatch, log);
            }

            PrintComparison(result.Cold, result.Warm, log);
            return result;
        }

        private static StressScenarioResult RunScenario(string name, string connectionString,
            StressAuditDataConfig data, int maxSavesPerBatch, Action<string> log)
        {
            log($"===== {name} scenario =====");

            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var appConfig = StressAppConfigFactory.Create(connectionString);

            SharePointOrgUrlsFilterConfig spFilter;
            using (var db = new AnalyticsEntitiesContext(connectionString, true, false))
            {
                spFilter = SharePointOrgUrlsFilterConfig.Load(db).GetAwaiter().GetResult();
            }
            log($"  Whitelist rules loaded: {spFilter.OrgUrlConfigs.Count}");

            var realManager = new ActivityReportSqlPersistenceManager(
                spFilter, new NoUsersHaveGroupsUserGroupsCache(logger), logger, appConfig, data.MaxConcurrentSaves);
            var countingManager = new CountingActivityReportPersistenceManager(realManager);

            // Production-like: the checkpoint store is selected by the factory from config (empty storage
            // conn -> in-memory). The in-memory store is static, so COLD's marks are visible to WARM.
            var blobStore = data.UseBlobCheckpoint ? ProcessedBlobStoreFactory.Create(appConfig, logger) : null;
            var importer = new DeterministicActivityImporterForStress(appConfig, logger, maxSavesPerBatch, data, blobStore);

            using (var sampler = new PeakMemorySampler())
            {
                long initialMem = sampler.InitialBytes;
                var sw = Stopwatch.StartNew();

                var stats = Task.Run(async () => await importer.LoadReportsAndSave(countingManager)).GetAwaiter().GetResult();

                sw.Stop();
                sampler.Stop();

                var m = new StressScenarioResult
                {
                    Name = name,
                    WallMs = sw.Elapsed.TotalMilliseconds,
                    BlobsLoaded = importer.BlobsLoaded,
                    EventsGenerated = importer.EventsGenerated,
                    CommitAllCalls = countingManager.CommitAllCalls,
                    EventsIntoCommit = countingManager.EventsIntoCommit,
                    TotalCommitMs = countingManager.TotalCommitMs,
                    MergedStats = stats,
                    InitialMemoryBytes = initialMem,
                    PeakMemoryBytes = sampler.PeakBytes,
                    FinalMemoryBytes = GC.GetTotalMemory(false)
                };

                m.Print(log);
                return m;
            }
        }

        #region DB prep

        private static bool IsLocalDb(string connectionString)
        {
            try
            {
                var ds = new SqlConnectionStringBuilder(connectionString).DataSource ?? string.Empty;
                return ds.IndexOf("localdb", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Puts the target DB into an empty state. For LocalDB it drops + lets EF recreate (a true COLD
        /// start). For any other target it deletes rows from the import tables (best effort) - COLD on a
        /// shared/production DB is only partial and should be avoided.
        /// </summary>
        private static void ResetDatabase(string connectionString, Action<string> log)
        {
            if (IsLocalDb(connectionString))
            {
                DropLocalDb(connectionString);
            }
            else
            {
                log("  (non-LocalDB target: deleting import-table rows rather than dropping the DB)");
                DeleteImportTables(connectionString, log);
            }
        }

        private static void DropLocalDb(string connectionString)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var dbName = builder.InitialCatalog;
            if (string.IsNullOrEmpty(dbName)) return;

            var masterBuilder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
            using (var c = new SqlConnection(masterBuilder.ConnectionString))
            {
                c.Open();
                var safe = dbName.Replace("]", "]]");
                var literal = dbName.Replace("'", "''");
                var sql =
                    $"IF DB_ID(N'{literal}') IS NOT NULL\n" +
                    "BEGIN\n" +
                    $"  ALTER DATABASE [{safe}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\n" +
                    $"  DROP DATABASE [{safe}];\n" +
                    "END";
                using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 })
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteImportTables(string connectionString, Action<string> log)
        {
            using (var c = new SqlConnection(connectionString))
            {
                c.Open();
                foreach (var table in ImportTablesChildFirst)
                {
                    try
                    {
                        using (var cmd = new SqlCommand($"IF OBJECT_ID(N'dbo.{table}', N'U') IS NOT NULL DELETE FROM dbo.{table};", c) { CommandTimeout = 0 })
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }
                    catch (Exception ex)
                    {
                        log($"  WARN: could not clear {table}: {ex.Message}");
                    }
                }
            }
        }

        private static void EnsureSchema(string connectionString, Action<string> log)
        {
            var initInfo = new DatabaseUpgradeInfo { ConnectionString = connectionString };
            DatabaseUpgrader.CheckDbUpgraded(initInfo, msg => log($"    [DB] {msg}"));
        }

        /// <summary>
        /// Sets org_urls to EXACTLY the one whitelist prefix, so the in/out-of-scope ratio is exactly what
        /// the data generator produces (nothing else is accidentally in scope).
        /// </summary>
        private static void SeedWhitelist(string connectionString, string prefix)
        {
            using (var c = new SqlConnection(connectionString))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM dbo.org_urls; INSERT INTO dbo.org_urls (url_base, exact_match) VALUES (@p, 0);";
                    cmd.Parameters.Add(new SqlParameter("@p", System.Data.SqlDbType.NVarChar, 4000) { Value = prefix });
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Bulk-inserts <paramref name="count"/> historical rows into audit_events (dummy user, no
        /// operation, timestamps ~30 days before the event window) to model a large pre-existing table.
        /// Uses a set-based tally (sys.all_objects cross join) chunked to keep the transaction log bounded.
        /// </summary>
        private static void PreSeedHistoricalAuditEvents(string connectionString, int count, DateTime baseTimeUtc, Action<string> log)
        {
            log($"[prep] Pre-seeding {count:N0} historical audit_events rows (models a large existing table)...");
            var sw = Stopwatch.StartNew();
            using (var c = new SqlConnection(connectionString))
            {
                c.Open();

                const string dummyUpn = "preseed@contoso.onmicrosoft.com";
                using (var cmd = new SqlCommand("IF NOT EXISTS (SELECT 1 FROM users WHERE user_name=@u) INSERT INTO users(user_name) VALUES(@u);", c))
                {
                    cmd.Parameters.Add(new SqlParameter("@u", System.Data.SqlDbType.NVarChar, 400) { Value = dummyUpn });
                    cmd.ExecuteNonQuery();
                }
                int userId;
                using (var cmd = new SqlCommand("SELECT id FROM users WHERE user_name=@u", c))
                {
                    cmd.Parameters.Add(new SqlParameter("@u", System.Data.SqlDbType.NVarChar, 400) { Value = dummyUpn });
                    userId = (int)cmd.ExecuteScalar();
                }

                // ~30 days before the window so these never fall in a batch's [oldest, newest] cache range.
                var histBase = baseTimeUtc.AddDays(-30);
                const int chunkSize = 250000;
                int done = 0;
                while (done < count)
                {
                    int take = Math.Min(chunkSize, count - done);
                    using (var cmd = new SqlCommand(
                        ";WITH n AS (SELECT TOP (@take) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn " +
                        "FROM sys.all_objects a CROSS JOIN sys.all_objects b) " +
                        "INSERT INTO audit_events (id, user_id, time_stamp) " +
                        "SELECT NEWID(), @user, DATEADD(minute, -(rn + @offset), @histBase) FROM n;", c)
                    { CommandTimeout = 0 })
                    {
                        cmd.Parameters.Add(new SqlParameter("@take", System.Data.SqlDbType.Int) { Value = take });
                        cmd.Parameters.Add(new SqlParameter("@user", System.Data.SqlDbType.Int) { Value = userId });
                        cmd.Parameters.Add(new SqlParameter("@offset", System.Data.SqlDbType.Int) { Value = done });
                        cmd.Parameters.Add(new SqlParameter("@histBase", System.Data.SqlDbType.DateTime) { Value = histBase });
                        cmd.ExecuteNonQuery();
                    }
                    done += take;
                }
            }
            log($"[prep] Pre-seed done ({count:N0} rows) in {sw.Elapsed.TotalSeconds:F1}s");
        }

        /// <summary>
        /// Injects/overrides a runtime connection string in <see cref="ConfigurationManager"/> so
        /// <c>new AnalyticsEntitiesContext()</c> (name=SPOInsightsEntities) resolves to our target DB.
        /// </summary>
        private static void ForceRuntimeConnectionString(string name, string connectionString)
        {
            var settings = ConfigurationManager.ConnectionStrings;

            var collReadOnly = typeof(ConfigurationElementCollection)
                .GetField("bReadOnly", BindingFlags.Instance | BindingFlags.NonPublic);
            collReadOnly?.SetValue(settings, false);

            var existing = settings[name];
            if (existing != null)
            {
                var elemReadOnly = typeof(ConfigurationElement)
                    .GetField("_bReadOnly", BindingFlags.Instance | BindingFlags.NonPublic);
                elemReadOnly?.SetValue(existing, false);
                existing.ConnectionString = connectionString;
                existing.ProviderName = "System.Data.SqlClient";
            }
            else
            {
                settings.Add(new ConnectionStringSettings(name, connectionString, "System.Data.SqlClient"));
            }

            var check = ConfigurationManager.ConnectionStrings[name]?.ConnectionString;
            if (string.IsNullOrEmpty(check))
            {
                throw new InvalidOperationException($"Failed to set runtime connection string '{name}'.");
            }
        }

        #endregion

        private static void PrintComparison(StressScenarioResult cold, StressScenarioResult warm, Action<string> log)
        {
            log("\n================ COLD vs WARM ================");
            log($"{"Metric",-26}{"COLD",16}{"WARM",16}");
            void Row(string label, string c, string w) => log($"{label,-26}{c,16}{w,16}");
            Row("Wall time (s)", (cold.WallMs / 1000.0).ToString("F1"), warm != null ? (warm.WallMs / 1000.0).ToString("F1") : "-");
            Row("Save phase (s)", (cold.TotalCommitMs / 1000.0).ToString("F1"), warm != null ? (warm.TotalCommitMs / 1000.0).ToString("F1") : "-");
            Row("Commit batches", cold.CommitAllCalls.ToString("N0"), warm != null ? warm.CommitAllCalls.ToString("N0") : "-");
            Row("Blobs loaded", cold.BlobsLoaded.ToString("N0"), warm != null ? warm.BlobsLoaded.ToString("N0") : "-");
            Row("Blobs skipped (ckpt)", cold.MergedStats.BlobsSkipped.ToString("N0"), warm != null ? warm.MergedStats.BlobsSkipped.ToString("N0") : "-");
            Row("Events generated", cold.EventsGenerated.ToString("N0"), warm != null ? warm.EventsGenerated.ToString("N0") : "-");
            Row("Imported", cold.MergedStats.Imported.ToString("N0"), warm != null ? warm.MergedStats.Imported.ToString("N0") : "-");
            Row("URLs out of scope", cold.MergedStats.URLsOutOfScope.ToString("N0"), warm != null ? warm.MergedStats.URLsOutOfScope.ToString("N0") : "-");
            Row("Peak memory (MB)", (cold.PeakMemoryBytes / 1048576.0).ToString("F0"), warm != null ? (warm.PeakMemoryBytes / 1048576.0).ToString("F0") : "-");
            log("=============================================\n");
        }
    }

    /// <summary>Metrics captured for one scenario (COLD or WARM).</summary>
    public class StressScenarioResult
    {
        public string Name;
        public double WallMs;
        public long BlobsLoaded;
        public long EventsGenerated;
        public long CommitAllCalls;
        public long EventsIntoCommit;
        public double TotalCommitMs;
        public ImportStat MergedStats;
        public long InitialMemoryBytes;
        public long PeakMemoryBytes;
        public long FinalMemoryBytes;

        public void Print(Action<string> log)
        {
            log($"  {Name} wall time:      {WallMs / 1000.0:F1}s");
            log($"  {Name} save phase:     {TotalCommitMs / 1000.0:F1}s across {CommitAllCalls:N0} commit batch(es)");
            log($"  {Name} blobs loaded:   {BlobsLoaded:N0}");
            log($"  {Name} blobs skipped:  {MergedStats.BlobsSkipped:N0} (checkpoint)");
            log($"  {Name} events:         {EventsGenerated:N0} generated");
            log($"  {Name} stats:          {MergedStats}");
            log($"  {Name} peak memory:    {PeakMemoryBytes / 1048576.0:F0} MB");
            log("");
        }
    }

    public class StressRunResult
    {
        public StressScenarioResult Cold { get; set; }
        public StressScenarioResult Warm { get; set; }
    }

    /// <summary>
    /// Samples managed heap size on a background thread to capture a realistic peak during a scenario
    /// (the simple before/after snapshot in <c>MemoryMonitor</c> misses the mid-run peak).
    /// </summary>
    internal sealed class PeakMemorySampler : IDisposable
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _task;
        private long _peak;

        public PeakMemorySampler()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            InitialBytes = GC.GetTotalMemory(false);
            _peak = InitialBytes;
            _task = Task.Run(() =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    long now = GC.GetTotalMemory(false);
                    if (now > Interlocked.Read(ref _peak)) Interlocked.Exchange(ref _peak, now);
                    try { Task.Delay(200, _cts.Token).Wait(); } catch { /* cancelled */ }
                }
            });
        }

        public long InitialBytes { get; }
        public long PeakBytes => Interlocked.Read(ref _peak);

        public void Stop()
        {
            long now = GC.GetTotalMemory(false);
            if (now > Interlocked.Read(ref _peak)) Interlocked.Exchange(ref _peak, now);
            _cts.Cancel();
            try { _task.Wait(1000); } catch { /* best effort */ }
        }

        public void Dispose()
        {
            if (!_cts.IsCancellationRequested) Stop();
            _cts.Dispose();
        }
    }
}
