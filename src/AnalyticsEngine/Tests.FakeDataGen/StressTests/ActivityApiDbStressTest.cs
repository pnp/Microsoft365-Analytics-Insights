using System;
using DataUtils;
using Tests.UnitTests.StressHarness;

namespace Tests.FakeDataGen.StressTests
{
    /// <summary>
    /// Console (BaseStressTest) front-end for the DB-backed ActivityAPI load test. The heavy lifting lives
    /// in <see cref="ActivityApiDbStressRunner"/> (in Tests.UnitTests) so the exact same code path can be
    /// driven either here - scriptable via <c>Tests.FakeDataGen.exe --run activityapidb</c> + env vars -
    /// or via <c>vstest.console</c> (the MSTest wrapper), which is the reliable way to run it on machines
    /// where Defender ASR blocks freshly-built executables.
    ///
    /// Drives the REAL <c>ActivityReportSqlPersistenceManager</c> against a real DB (LocalDB by default,
    /// or the connection string passed as the first argument) with a narrow org-URL whitelist and ~99%
    /// out-of-scope events, and runs COLD (empty DB) then WARM (re-run the same window) scenarios.
    /// </summary>
    public class ActivityApiDbStressTest : BaseStressTest
    {
        protected override StressTestResult Execute()
        {
            Console.WriteLine("\n=== DB-backed ActivityAPI Import Stress Test Configuration ===\n");

            var data = new StressAuditDataConfig
            {
                TotalEvents = GetIntegerInput("Total audit events per cycle", 200000, 1, 100000000, "STRESS_TOTAL_EVENTS"),
                EventsPerBlob = GetIntegerInput("Events per content blob", 200, 1, 1000000, "STRESS_EVENTS_PER_BLOB"),
                InScopePercent = GetIntegerInput("Percent of events in org_urls whitelist scope", 1, 0, 100, "STRESS_INSCOPE_PERCENT"),
                DistinctUsers = GetIntegerInput("Distinct users", 5000, 1, 1000000, "STRESS_DISTINCT_USERS"),
                DistinctInScopeSites = GetIntegerInput("Distinct in-scope sites", 50, 1, 100000, "STRESS_INSCOPE_SITES"),
                DistinctOutOfScopeSites = GetIntegerInput("Distinct out-of-scope sites", 500, 1, 1000000, "STRESS_OUTOFSCOPE_SITES"),
                WindowDays = GetIntegerInput("Window (days) to spread events over", 6, 1, 30, "STRESS_WINDOW_DAYS"),
                SimulatedBlobLatencyMs = GetIntegerInput("Simulated per-blob download latency (ms)", 0, 0, 10000, "STRESS_BLOB_LATENCY_MS"),
                PreSeedHistoricalAuditEvents = GetIntegerInput("Pre-seed historical audit_events rows (models a large table)", 0, 0, 100000000, "STRESS_PRESEED_AUDIT_EVENTS"),
                UseBlobCheckpoint = GetBooleanInput("Use blob-level checkpoint (opt B)", true, "STRESS_BLOB_CHECKPOINT"),
                MaxConcurrentSaves = GetIntegerInput("Max concurrent SQL saves (opt C; 1 = serial)", 1, 1, 64, "STRESS_MAX_CONCURRENT_SAVES"),
                FailedBlobPercent = GetIntegerInput("Percent of blob downloads to simulate as failed", 0, 0, 100, "STRESS_FAILED_BLOB_PERCENT"),
                BaseTimeUtc = DateTime.UtcNow
            };
            int maxSavesPerBatch = GetIntegerInput("Max events per commit batch", 2000, 1, 100000, "STRESS_MAX_SAVES_PER_BATCH");
            bool runWarm = GetBooleanInput("Run WARM (steady-state) scenario after COLD", true, "STRESS_RUN_WARM");

            string connectionString = string.IsNullOrWhiteSpace(ConnectionString)
                ? ActivityApiDbStressRunner.DefaultLocalDbConnectionString
                : ConnectionString;

            Console.WriteLine("\nCalculated load:");
            Console.WriteLine($"  Total events: {data.TotalEvents:N0} across {data.BlobCount:N0} blobs of {data.EventsPerBlob:N0}");
            Console.WriteLine($"  In scope (imported): ~{data.InScopePercent}% ; out of scope (ignored): ~{100 - data.InScopePercent}%");
            Console.WriteLine($"  Commit batch size: {maxSavesPerBatch:N0}  |  Distinct users: {data.DistinctUsers:N0}");
            Console.WriteLine($"  Whitelist: {data.InScopePrefix}");
            Console.WriteLine($"  Target DB: {StringUtils.RedactSqlConnectionString(connectionString)}");
            Console.WriteLine();
            PauseIfInteractive("Press any key to start test...");
            Console.WriteLine();

            var result = new StressTestResult { Success = true };
            try
            {
                var run = ActivityApiDbStressRunner.RunAll(connectionString, data, maxSavesPerBatch, runWarm, Console.WriteLine);

                var cold = run.Cold;
                var warm = run.Warm;
                result.ItemsProcessed = cold.EventsGenerated;
                result.Duration = TimeSpan.FromMilliseconds(cold.WallMs + (warm?.WallMs ?? 0));
                result.InitialMemoryBytes = cold.InitialMemoryBytes;
                result.PeakMemoryBytes = Math.Max(cold.PeakMemoryBytes, warm?.PeakMemoryBytes ?? 0);
                result.FinalMemoryBytes = (warm ?? cold).FinalMemoryBytes;
                result.Message = $"COLD {cold.WallMs / 1000.0:F1}s"
                    + (warm != null ? $", WARM {warm.WallMs / 1000.0:F1}s" : "")
                    + $" | imported {cold.MergedStats.Imported:N0}, out-of-scope {cold.MergedStats.URLsOutOfScope:N0}";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Exception = ex.GetBaseException();
                result.Message = $"Test failed: {ex.GetBaseException().Message}";
            }

            return result;
        }
    }
}
