using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace Tests.UnitTests.StressHarness
{
    /// <summary>
    /// MSTest entry point for the DB-backed ActivityAPI load test. Runs under <c>vstest.console</c> (the
    /// trusted VS test host loads this DLL in-process), which is the reliable way to execute it on
    /// machines where Defender ASR blocks freshly-built console executables.
    ///
    /// It is GATED behind the <c>STRESS_RUN</c> environment variable so it never runs as part of a normal
    /// unit-test pass (it drops + rebuilds a dedicated LocalDB database and can take minutes at scale).
    /// Configure via environment variables:
    ///   STRESS_RUN=1                 (required to run at all)
    ///   STRESS_CONN=&lt;conn string&gt;     (optional; defaults to a dedicated LocalDB DB, NOT the unit-test DB)
    ///   STRESS_TOTAL_EVENTS          (default 200000)
    ///   STRESS_EVENTS_PER_BLOB       (default 200)
    ///   STRESS_INSCOPE_PERCENT       (default 1)
    ///   STRESS_MAX_SAVES_PER_BATCH   (default 2000)
    ///   STRESS_DISTINCT_USERS        (default 5000)
    ///   STRESS_INSCOPE_SITES         (default 50)
    ///   STRESS_OUTOFSCOPE_SITES      (default 500)
    ///   STRESS_WINDOW_DAYS           (default 6)
    ///   STRESS_BLOB_LATENCY_MS       (default 0)
    ///   STRESS_RUN_WARM              (default true)
    /// </summary>
    [TestClass]
    public class ActivityApiDbStressTests
    {
        [TestMethod]
        [TestCategory("StressTest")]
        public void ActivityApi_ColdWarm_LoadTest()
        {
            if (!IsTruthy(Env("STRESS_RUN")))
            {
                Assert.Inconclusive("Set STRESS_RUN=1 (and optionally STRESS_* env vars) to run the DB-backed ActivityAPI load test.");
                return;
            }

            var connStr = Env("STRESS_CONN");
            if (string.IsNullOrWhiteSpace(connStr))
            {
                connStr = ActivityApiDbStressRunner.DefaultLocalDbConnectionString;
            }

            var data = new StressAuditDataConfig
            {
                TotalEvents = EnvInt("STRESS_TOTAL_EVENTS", 200000),
                EventsPerBlob = EnvInt("STRESS_EVENTS_PER_BLOB", 200),
                InScopePercent = EnvInt("STRESS_INSCOPE_PERCENT", 1),
                DistinctUsers = EnvInt("STRESS_DISTINCT_USERS", 5000),
                DistinctInScopeSites = EnvInt("STRESS_INSCOPE_SITES", 50),
                DistinctOutOfScopeSites = EnvInt("STRESS_OUTOFSCOPE_SITES", 500),
                WindowDays = EnvInt("STRESS_WINDOW_DAYS", 6),
                SimulatedBlobLatencyMs = EnvInt("STRESS_BLOB_LATENCY_MS", 0),
                PreSeedHistoricalAuditEvents = EnvInt("STRESS_PRESEED_AUDIT_EVENTS", 0),
                UseBlobCheckpoint = IsTruthy(Env("STRESS_BLOB_CHECKPOINT") ?? "true"),
                MaxConcurrentSaves = EnvInt("STRESS_MAX_CONCURRENT_SAVES", 1),
                FailedBlobPercent = EnvInt("STRESS_FAILED_BLOB_PERCENT", 0),
                BaseTimeUtc = DateTime.UtcNow
            };
            int maxSavesPerBatch = EnvInt("STRESS_MAX_SAVES_PER_BATCH", 2000);
            bool runWarm = IsTruthy(Env("STRESS_RUN_WARM") ?? "true");

            Console.WriteLine($"DB-backed ActivityAPI load test: {data.TotalEvents:N0} events, {data.BlobCount:N0} blobs, " +
                $"{data.InScopePercent}% in scope, batch {maxSavesPerBatch:N0}");

            // vstest suppresses a passing test's captured stdout, so tee all harness output to STRESS_OUT
            // (if set) as well as the console, so baseline / before-after numbers are always capturable.
            var outFile = Env("STRESS_OUT");
            Action<string> log = Console.WriteLine;
            if (!string.IsNullOrWhiteSpace(outFile))
            {
                var sync = new object();
                log = msg =>
                {
                    Console.WriteLine(msg);
                    lock (sync) { System.IO.File.AppendAllText(outFile, msg + Environment.NewLine); }
                };
            }

            var result = ActivityApiDbStressRunner.RunAll(connStr, data, maxSavesPerBatch, runWarm, log);

            // Sanity assertions - the harness itself is correct only if these hold.
            Assert.IsNotNull(result.Cold, "COLD scenario did not produce a result.");
            Assert.IsTrue(result.Cold.EventsGenerated > 0, "COLD generated no events - the content loader emitted nothing.");
            Assert.IsTrue(result.Cold.MergedStats.Imported > 0, "COLD imported nothing - the whitelist/scope wiring is wrong.");
            Assert.IsTrue(result.Cold.MergedStats.URLsOutOfScope > 0, "COLD ignored nothing - the out-of-scope wiring is wrong.");
            Assert.AreEqual(0, result.Cold.MergedStats.BlobsSkipped, "COLD should start with an empty checkpoint (no blobs skipped).");

            if (runWarm)
            {
                Assert.IsNotNull(result.Warm, "WARM scenario did not produce a result.");
                // Imported events are deduped via the audit_events cache, so nothing is re-imported.
                Assert.AreEqual(0, result.Warm.MergedStats.Imported, "WARM re-imported events - imported-event dedup (audit_events cache) is not working.");

                if (data.UseBlobCheckpoint)
                {
                    if (data.FailedBlobPercent > 0)
                    {
                        // Failed-download blobs must NOT be checkpointed - they must be re-downloaded next
                        // cycle (data-loss regression guard). The successfully-committed blobs are skipped.
                        int failedBlobs = 0;
                        for (int i = 0; i < data.BlobCount; i++)
                        {
                            if (i % 100 < data.FailedBlobPercent) failedBlobs++;
                        }
                        Assert.AreEqual(failedBlobs, result.Warm.BlobsLoaded, "WARM should re-download exactly the failed-download blobs (a failed download must never be checkpointed).");
                        Assert.AreEqual(result.Cold.BlobsLoaded - failedBlobs, result.Warm.MergedStats.BlobsSkipped, "WARM should skip only the successfully-committed blobs.");
                    }
                    else
                    {
                        // Blob checkpoint (opt B): WARM re-runs the same window, so every blob COLD committed is
                        // skipped - nothing is re-downloaded and the cycle is near-instant on the save side.
                        Assert.AreEqual(result.Cold.BlobsLoaded, result.Warm.MergedStats.BlobsSkipped, "WARM should skip exactly the blobs COLD committed.");
                        Assert.AreEqual(0, result.Warm.BlobsLoaded, "WARM should re-download no blobs when the checkpoint is enabled.");
                        Assert.AreEqual(0, result.Warm.EventsGenerated, "WARM should generate no events when all blobs are checkpointed.");
                    }
                }
                else
                {
                    // Checkpoint off: WARM re-generates the same events (out-of-scope re-evaluated every cycle).
                    Assert.AreEqual(result.Cold.EventsGenerated, result.Warm.EventsGenerated, "WARM should regenerate the same events as COLD when the checkpoint is disabled.");
                }
            }
        }

        private static string Env(string key) => Environment.GetEnvironmentVariable(key);

        private static int EnvInt(string key, int def)
        {
            var v = Env(key);
            return !string.IsNullOrWhiteSpace(v) && int.TryParse(v.Trim(), out int r) ? r : def;
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("y", StringComparison.OrdinalIgnoreCase);
        }
    }
}
