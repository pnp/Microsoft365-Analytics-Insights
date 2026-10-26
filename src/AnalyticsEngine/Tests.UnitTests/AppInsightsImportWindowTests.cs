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

        #region Window planner and ingestion cursor

        private static readonly DateTime PlanDay = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);

        private static AppInsightsHourlyRowCount Hour(int hour, long count) =>
            new AppInsightsHourlyRowCount(AppInsightsTelemetryKind.PageViews, PlanDay.AddHours(hour), count);

        [TestMethod]
        public void WindowPlanner_MergesHoursUpToTheLimit_AndKeepsTheWindowsContiguous()
        {
            var windows = AppInsightsWindowPlanner.PlanDay(PlanDay, new[] { Hour(1, 600), Hour(2, 300), Hour(5, 200), Hour(9, 900) }, maxRowsPerWindow: 1000);

            // 600 + 300 fit; adding 200 would not, so the next window starts where that one ended - no gap, so a row that
            // arrived in an hour the counts saw as empty is still read.
            CollectionAssert.AreEqual(new[]
            {
                (PlanDay.AddHours(1), PlanDay.AddHours(3)),
                (PlanDay.AddHours(3), PlanDay.AddHours(6)),
                (PlanDay.AddHours(6), PlanDay.AddHours(10)),
            }, windows.ToArray());
        }

        [TestMethod]
        public void WindowPlanner_SlicesAnHourOverTheLimit_AndIgnoresOtherDays()
        {
            var windows = AppInsightsWindowPlanner.PlanDay(PlanDay,
                new[] { Hour(3, 2500), new AppInsightsHourlyRowCount(AppInsightsTelemetryKind.PageViews, PlanDay.AddDays(1), 5) }, maxRowsPerWindow: 1000);

            CollectionAssert.AreEqual(new[]
            {
                (PlanDay.AddHours(3), PlanDay.AddHours(3).AddMinutes(20)),
                (PlanDay.AddHours(3).AddMinutes(20), PlanDay.AddHours(3).AddMinutes(40)),
                (PlanDay.AddHours(3).AddMinutes(40), PlanDay.AddHours(4)),
            }, windows.ToArray());
            Assert.AreEqual(0, AppInsightsWindowPlanner.PlanDay(PlanDay, new AppInsightsHourlyRowCount[0]).Count);
        }

        [TestMethod]
        public void IngestionCursor_IncrementalRange_ReadsTheOverlapBeforeTheCursor_WithWideTimestampBounds()
        {
            var cursor = Now.AddMinutes(-10);
            var range = AppInsightsIngestionCursorPolicy.IncrementalRange(cursor, Now);

            Assert.AreEqual(cursor - AppInsightsIngestionCursorPolicy.Overlap, range.IngestedFromUtc);
            Assert.AreEqual(Now, range.IngestedBeforeUtc);
            // Azure Monitor: a timestamp can't be more than two days before receipt or a day in the future.
            Assert.IsTrue(range.IngestedFromUtc.Value - range.FromUtc >= TimeSpan.FromDays(2));
            Assert.IsTrue(range.ToUtc - Now >= TimeSpan.FromDays(1));
        }

        [TestMethod]
        public void IngestionCursor_IncrementalRange_IsBoundedByRetention_AndByTheClock()
        {
            var ancient = AppInsightsIngestionCursorPolicy.IncrementalRange(Now.AddYears(-1), Now);
            Assert.AreEqual(Now - AppInsightsIngestionCursorPolicy.MaxLookback, ancient.IngestedFromUtc);

            var future = AppInsightsIngestionCursorPolicy.IncrementalRange(Now.AddHours(2), Now);
            Assert.AreEqual(Now - AppInsightsIngestionCursorPolicy.Overlap, future.IngestedFromUtc, "A cursor ahead of the clock still reads the overlap.");
            Assert.AreEqual(Now, future.IngestedBeforeUtc);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task IngestionCursor_KeyValueStore_RoundTripsInUtc_PerApplicationId()
        {
            var state = new Common.Entities.State.InMemoryKeyValueStore();
            var store = new KeyValueIngestionCursorStore(state, "00000000-0000-0000-0000-000000000001");
            var other = new KeyValueIngestionCursorStore(state, "00000000-0000-0000-0000-000000000002");

            Assert.IsNull(await store.ReadAsync());
            await store.WriteAsync(Now);

            var read = await store.ReadAsync();
            Assert.AreEqual(Now, read);
            Assert.AreEqual(DateTimeKind.Utc, read.Value.Kind);
            Assert.IsNull(await other.ReadAsync(), "Another Application Insights resource has its own cursor.");
            Assert.AreEqual(Now.ToString("o"), await state.GetStringAsync("IngestionCursor:00000000-0000-0000-0000-000000000001"));
        }

        #endregion
    }
}
