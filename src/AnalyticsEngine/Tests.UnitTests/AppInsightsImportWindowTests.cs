using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using WebJob.AppInsightsImporter.Engine;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the App Insights import-window rule extracted in issue #374 - the decision that
    /// determines whether hits are missed or double-imported at day boundaries. It previously existed
    /// only as inline statements in <c>AppInsightsImporter.ImportAndSave</c>, with no test.
    ///
    /// Scope note: these methods take the instant as a parameter and never read <c>DateTime.Now</c> or
    /// <c>TimeZoneInfo.Local</c>, so nothing here can be shifted by the host's timezone. The bug the
    /// importer's code comment warns about - a caller supplying a LOCAL clock - is a call-site concern
    /// and is <b>not covered by any current test</b>. <c>AppInsightsImporter</c> does read
    /// <c>_clock.UtcNow</c> today, and <c>ClockAndContextFactoryTests</c> covers the adapter
    /// (<c>SystemClock.UtcNow</c> returns a UTC instant) and the constructor wiring (the injected clock
    /// is stored) - but nothing invokes <c>ImportAndSave</c>, so replacing those reads with
    /// <c>DateTime.Now</c> would leave every test green. A caller-level test can follow once the
    /// importer's database and HTTP dependencies are behind seams (the rest of #374).
    ///
    /// Runs with zero SQL Server, zero HTTP and zero wall-clock dependency.
    /// </summary>
    [TestClass]
    public class AppInsightsImportWindowTests
    {
        private static readonly DateTime Now = new DateTime(2026, 5, 20, 14, 30, 0, DateTimeKind.Utc);

        [TestMethod]
        public void ImportWindow_DaysBeforeOverrideSupplied_TakesPrecedenceOverWatermark()
        {
            var overrideStart = AppInsightsImportWindow.ResolveOverrideStartUtc(7, Now);
            Assert.AreEqual(Now.AddDays(-7), overrideStart);

            var newestHit = new DateTime(2026, 5, 19, 8, 0, 0, DateTimeKind.Utc);
            var start = AppInsightsImportWindow.ResolveStartDateUtc(overrideStart, newestHit, Now);

            Assert.AreEqual(Now.AddDays(-7).AddMinutes(-1), start,
                "An explicit override must win over the stored watermark.");
        }

        [TestMethod]
        public void ImportWindow_NoOverride_ReturnsNull()
        {
            Assert.IsNull(AppInsightsImportWindow.ResolveOverrideStartUtc(null, Now));
        }

        [TestMethod]
        public void ImportWindow_NoOverrideWithExistingHits_StartsFromNewestHitTimestamp()
        {
            var newestHit = new DateTime(2026, 5, 19, 8, 0, 0, DateTimeKind.Utc);

            var start = AppInsightsImportWindow.ResolveStartDateUtc(null, newestHit, Now);

            Assert.AreEqual(newestHit.AddMinutes(-1), start);
        }

        [TestMethod]
        public void ImportWindow_NoOverrideAndNoHits_StartsThirtyOneDaysBeforeNow()
        {
            var start = AppInsightsImportWindow.ResolveStartDateUtc(null, null, Now);

            Assert.AreEqual(Now.AddDays(-AppInsightsImportWindow.NoHitsFallbackDays).AddMinutes(-1), start);
            Assert.AreEqual(31, AppInsightsImportWindow.NoHitsFallbackDays,
                "A first run scans a month; changing this changes how much history a new install pulls.");
        }

        [TestMethod]
        public void ImportWindow_StartDate_IsRewoundOneMinuteForEdgeHits()
        {
            // Re-importing a minute of overlap is harmless - the merge de-duplicates on page-request id -
            // but dropping the rewind would silently lose hits written just after the previous watermark.
            var newestHit = new DateTime(2026, 5, 19, 8, 0, 0, DateTimeKind.Utc);

            var start = AppInsightsImportWindow.ResolveStartDateUtc(null, newestHit, Now);

            Assert.AreEqual(TimeSpan.FromMinutes(-1), start - newestHit);
            Assert.AreEqual(1, AppInsightsImportWindow.EdgeHitRewindMinutes);
        }

        [TestMethod]
        public void ImportWindow_RewindAcrossUtcMidnight_ReachesBackIntoThePreviousDay()
        {
            // The rewind's real consequence: a hit stored just after midnight pulls the window back into
            // the PREVIOUS day, so that day gets requested again. Losing the rewind would silently drop
            // hits written in the seconds before the watermark.
            //
            // Note this is NOT a timezone test - see the class remarks. These methods take the instant as
            // a parameter and never read DateTime.Now or TimeZoneInfo.Local, so there is nothing here for
            // a host timezone to shift.
            var newestHit = new DateTime(2026, 5, 20, 0, 0, 30, DateTimeKind.Utc);

            var start = AppInsightsImportWindow.ResolveStartDateUtc(null, newestHit, Now);

            Assert.AreEqual(new DateTime(2026, 5, 19, 23, 59, 30, DateTimeKind.Utc), start,
                "The one-minute rewind must cross midnight into 19 May.");
            Assert.AreEqual(DateTimeKind.Utc, start.Kind, "Arithmetic must not discard the caller's Kind.");

            var days = AppInsightsImportWindow.EnumerateDays(start, new DateTime(2026, 5, 20, 6, 0, 0, DateTimeKind.Utc));
            CollectionAssert.AreEqual(
                new[] { new DateTime(2026, 5, 19), new DateTime(2026, 5, 20) },
                days.ToArray(),
                "Both 19 and 20 May must be requested, or the pre-midnight hits are never fetched.");
        }

        [TestMethod]
        public void ImportWindow_EnumerateDays_IsInclusiveOfStartAndEndDay()
        {
            var days = AppInsightsImportWindow.EnumerateDays(
                new DateTime(2026, 5, 18, 22, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 5, 20, 3, 0, 0, DateTimeKind.Utc));

            CollectionAssert.AreEqual(
                new[] { new DateTime(2026, 5, 18), new DateTime(2026, 5, 19), new DateTime(2026, 5, 20) },
                days.ToArray(),
                "Both partial end days must be requested, or their hits are never fetched.");
        }

        [TestMethod]
        public void ImportWindow_EnumerateDays_SameDay_ReturnsThatOneDay()
        {
            var days = AppInsightsImportWindow.EnumerateDays(
                new DateTime(2026, 5, 20, 1, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 5, 20, 23, 0, 0, DateTimeKind.Utc));

            CollectionAssert.AreEqual(new[] { new DateTime(2026, 5, 20) }, days.ToArray());
        }

        [TestMethod]
        public void ImportWindow_StartAfterEnd_EnumeratesBackwards_WhichIsAKnownHazard()
        {
            // #374 proposed asserting this returns NO days. It does not - DateTimeUtils.EachDay walks
            // backwards when from > thru, and this test pins the behaviour that actually ships rather
            // than the behaviour the issue assumed.
            //
            // It is normally unreachable: the end is "now" and the start is derived from a stored
            // timestamp. But a future-dated hit_timestamp - clock skew on the writing host, or bad data -
            // makes the importer walk days in reverse, re-requesting a run of future dates. Preserved
            // here deliberately (no behavioural change) and raised separately.
            var days = AppInsightsImportWindow.EnumerateDays(
                new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc));

            CollectionAssert.AreEqual(
                new[] { new DateTime(2026, 5, 22), new DateTime(2026, 5, 21), new DateTime(2026, 5, 20) },
                days.ToArray(),
                "Documents the descending enumeration; it is not an endorsement of it.");
        }
    }
}
