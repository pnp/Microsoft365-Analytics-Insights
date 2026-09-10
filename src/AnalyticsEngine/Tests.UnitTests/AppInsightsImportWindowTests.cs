using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using DataUtils;
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
    /// importer's code comment warns about - a caller supplying a LOCAL clock - is a call-site concern,
    /// and it is now covered: the rest of #374 put the importer's database and HTTP dependencies behind
    /// ports, so <c>AppInsightsImporterOrchestrationTests.AppInsightsImporter_WindowIsDrivenByTheInjected
    /// Clock_NotTheWallClock</c> runs <c>ImportAndSave</c> end to end against a clock fixed years in the
    /// past and asserts the days actually requested. Swapping <c>_clock.UtcNow</c> for
    /// <c>DateTime.Now</c>/<c>DateTime.UtcNow</c> inside <c>ImportAndSave</c> now fails a test rather
    /// than leaving every test green.
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
            Assert.AreEqual(DateTimeKind.Utc, start.Kind);
        }

        [TestMethod]
        public void ImportWindow_UnspecifiedSqlWatermark_IsTreatedAsUtc()
        {
            var newestHitFromSql = new DateTime(2026, 5, 19, 8, 0, 0, DateTimeKind.Unspecified);

            var normalized = AppInsightsImportWindow.NormalizeStoredHitTimestampUtc(newestHitFromSql);
            var start = AppInsightsImportWindow.ResolveStartDateUtc(null, newestHitFromSql, Now);

            Assert.AreEqual(new DateTime(2026, 5, 19, 8, 0, 0, DateTimeKind.Utc), normalized.Value);
            Assert.AreEqual(DateTimeKind.Utc, normalized.Value.Kind,
                "SQL datetime strips Kind; the importer must restore UTC rather than leave it unspecified.");
            Assert.AreEqual(new DateTime(2026, 5, 19, 7, 59, 0, DateTimeKind.Utc), start);
            Assert.AreEqual(DateTimeKind.Utc, start.Kind);
        }

        [TestMethod]
        public void ImportWindow_LocalWatermark_IsConvertedToUtcBeforeItIsComparedWithTheUtcClock()
        {
            if (TimeZoneInfo.Local.GetUtcOffset(Now) == TimeSpan.Zero)
            {
                Assert.Inconclusive("This conversion test only proves a tick change on a non-UTC host.");
            }

            var localHit = new DateTime(2026, 5, 19, 8, 0, 0, DateTimeKind.Local);

            var normalized = AppInsightsImportWindow.NormalizeStoredHitTimestampUtc(localHit);

            Assert.AreEqual(localHit.ToUniversalTime(), normalized.Value);
            Assert.AreEqual(DateTimeKind.Utc, normalized.Value.Kind);
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
        public void DateTimeUtils_EachDay_StartAfterEnd_StillEnumeratesBackwards()
        {
            // DateTimeUtils.EachDay is shared code. Its descending mode is not changed here; the
            // App Insights importer guards its own future-watermark call site instead.
            var days = new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc).EachDay(
                new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc));

            CollectionAssert.AreEqual(
                new[] { new DateTime(2026, 5, 22), new DateTime(2026, 5, 21), new DateTime(2026, 5, 20) },
                days.ToArray(),
                "Documents the shared helper's descending enumeration; it is not an endorsement of using it for import windows.");
        }

        [TestMethod]
        public void ImportWindow_StartAfterEnd_StillDelegatesToTheSharedEachDayHelper()
        {
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
