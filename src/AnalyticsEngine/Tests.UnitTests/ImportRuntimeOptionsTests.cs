using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using WebJob.Office365ActivityImporter.Engine;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the two pure rules lifted out of the import composition roots by issue #376:
    /// <see cref="ImportRuntimeOptions"/> (the audit-import environment-variable safety valves, previously
    /// parsed inline in <c>ProgramTasks.DownloadActivityData</c>) and
    /// <see cref="ActivityReportsCadenceGate"/> (the once-a-day throttle on the activity/usage-report phase,
    /// previously an inline <c>DateTime.Now</c> subtraction in <c>GraphImporter</c>).
    ///
    /// Zero SQL Server, Graph, Redis and Service Bus, and no environment variables are read or written -
    /// the raw value is passed in, so these tests cannot disturb a parallel test host's environment.
    /// </summary>
    [TestClass]
    public class ImportRuntimeOptionsTests
    {
        [TestMethod]
        public void RuntimeOptions_MaxConcurrentSavesUnset_DefaultsToOne()
        {
            Assert.AreEqual(1, ImportRuntimeOptions.ResolveMaxConcurrentSaves(null));
            Assert.AreEqual(1, ImportRuntimeOptions.ResolveMaxConcurrentSaves(string.Empty));
            Assert.AreEqual(1, ImportRuntimeOptions.ResolveMaxConcurrentSaves("   "));
        }

        [TestMethod]
        public void RuntimeOptions_MaxConcurrentSavesInvalid_DefaultsToOne()
        {
            foreach (var bad in new[] { "yes", "true", "4x", "2.5", string.Empty, "-3", "0", "1" })
            {
                Assert.AreEqual(1, ImportRuntimeOptions.ResolveMaxConcurrentSaves(bad),
                    $"'{bad}' must leave the strictly-serial default: this valve can only ever turn concurrency ON, "
                    + "so a typo must never be able to stop the importer saving.");
            }
        }

        [TestMethod]
        public void RuntimeOptions_MaxConcurrentSavesValid_IsHonoured()
        {
            Assert.AreEqual(2, ImportRuntimeOptions.ResolveMaxConcurrentSaves("2"));
            Assert.AreEqual(8, ImportRuntimeOptions.ResolveMaxConcurrentSaves("8"));
            Assert.AreEqual(4, ImportRuntimeOptions.ResolveMaxConcurrentSaves("  4  "),
                "App Service settings routinely carry stray whitespace. (This pins the accepted input shape rather "
                + "than the Trim() call, which int.TryParse's NumberStyles.Integer already tolerates.)");
        }

        [TestMethod]
        public void RuntimeOptions_PerBatchDedupCache_AcceptsOneAndTrueCaseInsensitively()
        {
            foreach (var on in new[] { "1", "true", "True", "TRUE", "  true  ", " 1 " })
            {
                Assert.IsTrue(ImportRuntimeOptions.ResolveUsePerBatchDedupCache(on), $"'{on}' must enable the per-batch dedup cache.");
            }
        }

        [TestMethod]
        public void RuntimeOptions_PerBatchDedupCache_IsOffForAnythingElse()
        {
            foreach (var off in new[] { null, string.Empty, "   ", "0", "false", "False", "yes", "on", "2", "truthy", "11" })
            {
                Assert.IsFalse(ImportRuntimeOptions.ResolveUsePerBatchDedupCache(off),
                    $"'{off ?? "<null>"}' must leave the per-cycle cache in place.");
            }
        }

        private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

        [TestMethod]
        public void ActivityReportsGate_NeverImported_Runs()
        {
            Assert.IsTrue(ActivityReportsCadenceGate.ShouldRun(null, OneDay, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)),
                "A fresh install, or a database with no activity data, must import immediately.");
        }

        [TestMethod]
        public void ActivityReportsGate_AtExactlyTheWindow_DoesNotRun()
        {
            var last = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsFalse(ActivityReportsCadenceGate.ShouldRun(last, OneDay, last.Add(OneDay)),
                "The original comparison was strictly greater-than; at exactly 24h the phase still waits.");
            Assert.IsFalse(ActivityReportsCadenceGate.ShouldRun(last, OneDay, last.Add(OneDay).AddTicks(-1)));
            Assert.IsTrue(ActivityReportsCadenceGate.ShouldRun(last, OneDay, last.Add(OneDay).AddTicks(1)));
        }

        [TestMethod]
        public void ActivityReportsGate_UsesTheSuppliedNow_NotWallClock()
        {
            // The stored timestamp is 30 days old by wall time - which would always be "due" - but only one
            // hour old by the supplied clock.
            var wallishLast = DateTime.UtcNow.AddDays(-30);
            var suppliedNow = wallishLast.AddHours(1);

            Assert.IsFalse(ActivityReportsCadenceGate.ShouldRun(wallishLast, OneDay, suppliedNow),
                "The gate must compare against the instant it is given, never DateTime.Now/UtcNow.");
        }

        [TestMethod]
        public void ActivityReportsGate_TreatsALocalStoredTimestampAsAnInstant()
        {
            // Both ISingleDateStore implementations stamp DateTime.Now, so the value read back has
            // DateTimeKind.Local. The gate must convert it to UTC before subtracting; the old inline
            // expression instead subtracted a local reading from DateTime.Now, which is the wall-clock
            // difference rather than the elapsed time.
            var lastUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            var offset = TimeZoneInfo.Local.GetUtcOffset(lastUtc);

            if (Math.Abs(offset.TotalHours) < 1)
            {
                // On a host at (or within an hour of) UTC, a local and a UTC representation of the same
                // instant have the same raw value, so no assertion here can tell the two apart. Say so
                // rather than passing vacuously - this discriminates on any host an hour or more off UTC.
                Assert.Inconclusive($"Host UTC offset is {offset}; local-vs-UTC handling is unobservable here.");
            }

            var lastLocal = lastUtc.ToLocalTime();
            Assert.AreEqual(DateTimeKind.Local, lastLocal.Kind);

            // 25h really elapsed. Skipping the conversion would read this as 25h minus the host offset,
            // which is inside the window on any host east of UTC.
            Assert.IsTrue(ActivityReportsCadenceGate.ShouldRun(lastLocal, OneDay, lastUtc.AddHours(25)),
                "25 hours have really passed, whatever the host's timezone.");

            // 23h really elapsed. Skipping the conversion would read this as 23h minus the host offset,
            // which is outside the window on any host west of UTC.
            Assert.IsFalse(ActivityReportsCadenceGate.ShouldRun(lastLocal, OneDay, lastUtc.AddHours(23)),
                "Only 23 hours have really passed, whatever the host's timezone.");
        }

        [TestMethod]
        public void ActivityReportsGate_LeavesAUtcStoredTimestampAlone()
        {
            var lastUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsTrue(ActivityReportsCadenceGate.ShouldRun(lastUtc, OneDay, lastUtc.AddHours(25)));
            Assert.IsFalse(ActivityReportsCadenceGate.ShouldRun(lastUtc, OneDay, lastUtc.AddHours(23)));
        }
    }
}
