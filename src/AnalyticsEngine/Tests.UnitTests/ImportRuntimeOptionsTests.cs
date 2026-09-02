using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
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
            //
            // Mid-June, deliberately: far from a DST transition in either hemisphere, so ToLocalTime() and
            // ToUniversalTime() round-trip through an unambiguous offset.
            var lastUtc = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
            var lastLocal = lastUtc.ToLocalTime();
            Assert.AreEqual(DateTimeKind.Local, lastLocal.Kind);

            // DateTime subtraction ignores Kind, so this is exactly the skew an implementation that forgot
            // the conversion would introduce: it would test `elapsed > minWait + skew` instead of
            // `elapsed > minWait`.
            var skew = lastLocal - lastUtc;
            if (skew == TimeSpan.Zero)
            {
                // On a host at UTC a local and a UTC representation of the same instant have identical
                // ticks, so no assertion can tell the two apart. Say so rather than passing vacuously.
                Assert.Inconclusive("Host is at UTC; local-vs-UTC handling is unobservable here.");
            }

            // Sweep the window on a 30-minute grid rather than picking two points either side of it. Two
            // points only discriminate when the host offset exceeds the slack chosen, which is how an
            // earlier version of this test passed vacuously at exactly UTC-1.
            for (var halfHours = 0; halfHours <= 96; halfHours++)
            {
                var elapsed = TimeSpan.FromMinutes(30 * halfHours);
                AssertGateAgreesWithRealElapsedTime(lastUtc, lastLocal, elapsed, skew);
            }

            // ...and one point chosen from the skew itself, which discriminates for ANY non-zero offset,
            // in either direction, however small - so this test does not quietly depend on the host's
            // offset happening to land on the grid. The EXPECTED answer is still real elapsed time versus
            // the window; only the sample point is derived from the skew.
            //
            // At minWait + skew/2 the correct answer is "run" when skew is positive and "wait" when it is
            // negative, and an implementation missing the conversion gives the opposite in both cases.
            AssertGateAgreesWithRealElapsedTime(lastUtc, lastLocal, OneDay + TimeSpan.FromTicks(skew.Ticks / 2), skew);
        }

        private static void AssertGateAgreesWithRealElapsedTime(DateTime lastUtc, DateTime lastLocal, TimeSpan elapsed, TimeSpan skew)
        {
            Assert.AreEqual(elapsed > OneDay, ActivityReportsCadenceGate.ShouldRun(lastLocal, OneDay, lastUtc.Add(elapsed)),
                $"After {elapsed} of real elapsed time the gate must give the same answer for a local-kind "
                + $"stored timestamp as for a UTC one (host skew {skew}).");
        }

        [TestMethod]
        public void ActivityReportsGate_NextRunUtc_IsTheLastInstantTheGateIsStillShut()
        {
            // The "will import again after ..." line an operator reads must name the threshold the gate
            // actually uses. Adding minWait to the stored LOCAL value instead - which is what the code did
            // before the gate moved to UTC - reports an hour out across a daylight-saving transition.
            // Asserted against ShouldRun itself, so the two can never drift apart. Note the announced instant
            // is the last one at which the gate is STILL SHUT: the comparison is strictly greater-than, which
            // is what makes the word "after" in that message honest.
            var lastUtc = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

            // The same instant in all three kinds the store can hand back. Utc and Local are what the two
            // ISingleDateStore implementations actually produce; Unspecified is what an offset-less string in
            // Redis parses to - a bare local wall-clock reading, which is why ToUniversalTime() treating
            // Unspecified as local is the right reading of it.
            //
            // Note SpecifyKind(lastUtc, Unspecified).ToLocalTime() would NOT do: ToLocalTime treats
            // Unspecified as UTC, so it just yields the Local value again.
            var representations = new[]
            {
                lastUtc,
                lastUtc.ToLocalTime(),
                DateTime.SpecifyKind(lastUtc.ToLocalTime(), DateTimeKind.Unspecified),
            };

            foreach (var last in representations)
            {
                var nextRun = ActivityReportsCadenceGate.NextRunUtc(last, OneDay);

                Assert.AreEqual(lastUtc.Add(OneDay), nextRun,
                    $"All three representations are the same instant, so they must announce the same threshold ({last.Kind}).");

                Assert.IsFalse(ActivityReportsCadenceGate.ShouldRun(last, OneDay, nextRun.AddTicks(-1)),
                    $"One tick before the announced time the gate must still be shut ({last.Kind}).");
                Assert.IsFalse(ActivityReportsCadenceGate.ShouldRun(last, OneDay, nextRun),
                    $"At exactly the announced time the gate is still shut - the comparison is strictly greater-than ({last.Kind}).");
                Assert.IsTrue(ActivityReportsCadenceGate.ShouldRun(last, OneDay, nextRun.AddTicks(1)),
                    $"One tick after the announced time the gate must be open ({last.Kind}).");
            }

            CollectionAssert.AreEqual(new[] { DateTimeKind.Utc, DateTimeKind.Local, DateTimeKind.Unspecified },
                representations.Select(d => d.Kind).ToArray(),
                "Sanity: the three fixtures must really be three different kinds, or two thirds of this test is a duplicate.");
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
