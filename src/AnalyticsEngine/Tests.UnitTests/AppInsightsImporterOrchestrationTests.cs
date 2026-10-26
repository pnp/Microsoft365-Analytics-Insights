using Common.Entities.Config;
using DataUtils;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine;

namespace Tests.UnitTests
{
    /// <summary>
    /// The App Insights importer's orchestration - what it reads, the day loop, the windows, the per-day failure
    /// isolation and the ingestion cursor - driven entirely through the ports added by issue #374. No HTTP, no SQL
    /// Server, no wall clock.
    /// </summary>
    [TestClass]
    public class AppInsightsImporterOrchestrationTests
    {
        private static readonly DateTime FixedNowUtc = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc);

        private class Harness
        {
            public readonly FakeAppInsightsSourceLoader Source = new FakeAppInsightsSourceLoader();
            public readonly FakeSiteFilterLoader SiteFilters = new FakeSiteFilterLoader();
            public readonly InMemoryHitWatermarkStore Watermark = new InMemoryHitWatermarkStore();
            public readonly InMemoryAppInsightsDayPersistenceManager Persistence = new InMemoryAppInsightsDayPersistenceManager();
            public FixedClock Clock = new FixedClock(FixedNowUtc);
            public AnalyticsLogger Logger = AnalyticsLogger.ConsoleOnlyTracer();

            // Outlives each run's importer, as the web-job host's does.
            public readonly AppInsightsDayFailureTracker DayFailures = new AppInsightsDayFailureTracker();

            /// <summary>Null runs without a cursor: whole days from the newest stored hit, every cycle.</summary>
            public InMemoryIngestionCursorStore Cursor;
            public int MaxRowsPerWindow = AppInsightsWindowPlanner.DefaultMaxRowsPerWindow;

            public AppInsightsImportCycleResult LastResult;

            public async Task<AppInsightsImportCycleResult> RunAsync(int? daysBeforeOverride = null)
            {
                LastResult = await new AppInsightsImporter(
                    null,
                    Logger,
                    Clock,
                    Source,
                    SiteFilters,
                    Watermark,
                    Persistence,
                    dayFailureTracker: DayFailures,
                    cursorStore: Cursor,
                    maxRowsPerWindow: MaxRowsPerWindow).ImportAndSave(saveRestResponses: false, daysBeforeOverride: daysBeforeOverride);
                return LastResult;
            }
        }

        private static string EventPattern(AnalyticsLogger.AnalyticsEvent e) => "New event '" + e + "'";

        private static int CountOf(string log, AnalyticsLogger.AnalyticsEvent e) => Regex.Matches(log, Regex.Escape(EventPattern(e))).Count;

        private static async Task<string> CaptureConsole(Func<Task> action)
        {
            // Error and Critical lines go to stderr, so the capture is of both streams.
            var console = new StringWriter();
            var original = Console.Out;
            var originalError = Console.Error;
            Console.SetOut(console);
            Console.SetError(console);
            try
            {
                await action();
            }
            finally
            {
                Console.SetOut(original);
                Console.SetError(originalError);
            }
            return console.ToString();
        }

        #region What a cycle without a cursor reads

        [TestMethod]
        public async Task AppInsightsImporter_RunsWithFakeSourceAndFakePersistence_WithoutHttpOrSql()
        {
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = new DateTime(2026, 5, 8, 14, 0, 0, DateTimeKind.Utc);
            h.SiteFilters.Filters.Add(new FilterUrlConfig { Url = "https://contoso.sharepoint.com" });
            h.Source.PageViewCountByDay[new DateTime(2026, 5, 9)] = 3;
            h.Source.CustomEventCountByDay[new DateTime(2026, 5, 9)] = 2;

            var result = await h.RunAsync();

            Assert.AreEqual(1, h.Watermark.ReadCount);
            Assert.AreEqual(AppInsightsReadMode.Bootstrap, result.Mode);
            Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, result.Outcome);

            // From the watermark day (rewound a minute); only the day with data is fetched.
            Assert.AreEqual(new DateTime(2026, 5, 8), h.Source.CountRangesRequested.Single().FromUtc);
            CollectionAssert.AreEqual(new[] { new DateTime(2026, 5, 9) }, h.Source.PageViewDaysRequested.ToArray());
            CollectionAssert.AreEqual(h.Source.PageViewDaysRequested.ToArray(), h.Source.CustomEventDaysRequested.ToArray());

            Assert.AreEqual(3, h.Persistence.SavedPageViews.Single().Rows.Count);
            Assert.AreEqual(2, h.Persistence.SavedCustomEvents.Single().Rows.Count);

            // The org-URL filter loaded at startup is the one handed to the save, not a fresh empty list.
            Assert.AreSame(h.SiteFilters.Filters, h.Persistence.FiltersSeen);
        }

        [TestMethod]
        public async Task AppInsightsImporter_WindowIsDrivenByTheInjectedClock_NotTheWallClock()
        {
            // A clock fixed years in the past: no wall-clock-derived range could produce these bounds, so this fails if
            // ImportAndSave ever goes back to reading DateTime.UtcNow / DateTime.Now.
            var clock = new DateTime(2019, 2, 14, 6, 0, 0, DateTimeKind.Utc);
            var h = new Harness { Clock = new FixedClock(clock) };
            h.Watermark.NewestHitTimestampUtc = new DateTime(2019, 2, 12, 23, 30, 0, DateTimeKind.Utc);

            await h.RunAsync();

            var range = h.Source.CountRangesRequested.Single();
            Assert.AreEqual(new DateTime(2019, 2, 12), range.FromUtc);
            Assert.AreEqual(clock, range.IngestedBeforeUtc);
        }

        [TestMethod]
        public async Task AppInsightsImporter_DaysBeforeOverride_IsAlsoResolvedFromTheInjectedClock()
        {
            var h = new Harness { Clock = new FixedClock(new DateTime(2019, 2, 14, 6, 0, 0, DateTimeKind.Utc)) };
            h.Watermark.NewestHitTimestampUtc = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var result = await h.RunAsync(daysBeforeOverride: 2);

            // The override wins over the (much older) watermark: 2 days back from the fixed clock, rewound a minute,
            // through "today" inclusive - whole days, with no ingestion bounds.
            Assert.AreEqual(AppInsightsReadMode.Backfill, result.Mode);
            var range = h.Source.CountRangesRequested.Single();
            Assert.AreEqual(new DateTime(2019, 2, 12), range.FromUtc);
            Assert.AreEqual(new DateTime(2019, 2, 15), range.ToUtc);
            Assert.IsNull(range.IngestedFromUtc);
            Assert.IsNull(range.IngestedBeforeUtc);
        }

        [TestMethod]
        public async Task AppInsightsImporter_FutureDatedWatermark_DoesNotWalkBackwardsAndLogsTheTimestamp()
        {
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = FixedNowUtc.AddDays(2).AddHours(3);

            var log = await CaptureConsole(() => h.RunAsync());

            Assert.AreEqual(FixedNowUtc.Date, h.Source.CountRangesRequested.Single().FromUtc,
                "A future hit_timestamp must not make the importer start in the future.");
            StringAssert.Contains(log, "App Insights hit watermark is in the future");
            StringAssert.Contains(log, h.Watermark.NewestHitTimestampUtc.Value.ToString("O"));
            StringAssert.Contains(log, FixedNowUtc.ToString("O"));
        }

        [TestMethod]
        public async Task AppInsightsImporter_NoHitsYet_ScansTheFallbackWindow()
        {
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = null;

            await h.RunAsync();

            var range = h.Source.CountRangesRequested.Single();
            Assert.AreEqual(FixedNowUtc.AddDays(-AppInsightsImportWindow.NoHitsFallbackDays).AddMinutes(-1).Date, range.FromUtc);
            Assert.AreEqual(FixedNowUtc + AppInsightsIngestionCursorPolicy.FutureBound, range.ToUtc);
        }

        [TestMethod]
        public async Task AppInsightsImporter_DayThatSavesNothing_StillCountsAsProcessed()
        {
            // No data at all: no window is fetched and nothing is saved (which would run the merge SQL for nothing).
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = FixedNowUtc.AddHours(-2);

            var result = await h.RunAsync();

            Assert.AreEqual(0, h.Source.PageViewWindowsRequested.Count);
            Assert.AreEqual(0, h.Persistence.SavedPageViews.Count);
            Assert.AreEqual(0, h.Persistence.SavedCustomEvents.Count);
            Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, result.Outcome);
        }

        #endregion

        #region Windows (G-2)

        [TestMethod]
        public async Task AppInsightsImporter_ABusyDay_IsFetchedAndSavedOneBoundedWindowAtATime_PageViewsBeforeEvents()
        {
            // G-2: every window of a day used to be held, and joined, before anything was saved - with the day's page
            // views and events fetched in parallel. Now each window is saved before the next is fetched.
            var h = new Harness { MaxRowsPerWindow = 2 };
            h.Watermark.NewestHitTimestampUtc = new DateTime(2026, 5, 9, 0, 30, 0, DateTimeKind.Utc);
            var day = new DateTime(2026, 5, 9, 0, 0, 0, DateTimeKind.Utc);
            foreach (var hour in new[] { 1, 2, 3, 4 })
            {
                h.Source.ExtraPageViews.Add((day.AddHours(hour), day.AddHours(hour)));
            }
            h.Source.ExtraCustomEvents.Add((day.AddHours(2).AddMinutes(5), day.AddHours(2).AddMinutes(5)));
            h.Source.ExtraCustomEvents.Add((day.AddHours(3).AddMinutes(5), day.AddHours(3).AddMinutes(5)));
            var log = new List<string>();
            h.Source.CallLog = log;
            h.Persistence.CallLog = log;

            var result = await h.RunAsync();

            CollectionAssert.AreEqual(new[]
            {
                "fetch pageviews 2026-05-09 01:00", "save pageviews 2",
                "fetch pageviews 2026-05-09 03:00", "save pageviews 2",
                "fetch events 2026-05-09 02:00", "save events 2",
            }, log, string.Join(" | ", log));
            Assert.IsTrue(h.Persistence.SavedPageViews.All(p => p.Rows.Count <= 2), "No window holds more than the planned rows.");
            Assert.AreEqual(3, result.Days.Single().Windows);
            Assert.AreEqual(4, result.Days.Single().PageViews);
            Assert.AreEqual(2, result.Days.Single().Events);
        }

        #endregion

        #region Failed days

        private static readonly DateTime May8 = new DateTime(2026, 5, 8), May9 = new DateTime(2026, 5, 9), May10 = new DateTime(2026, 5, 10);

        /// <summary>The 8th, 9th and 10th each have a page view; the newest stored hit is on the 8th.</summary>
        private static Harness ThreeDaysOfPageViews()
        {
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = new DateTime(2026, 5, 8, 14, 0, 0, DateTimeKind.Utc);
            foreach (var day in new[] { May8, May9, May10 })
            {
                h.Source.PageViewCountByDay[day] = 1;
            }
            return h;
        }

        private static DateTime[] SavedDays(Harness h) => h.Persistence.SavedPageViews.Select(p => p.Rows[0].Timestamp.Date).ToArray();
        private static DateTime[] SavedEventDays(Harness h) => h.Persistence.SavedCustomEvents.Select(e => e.Rows[0].Timestamp.Date).ToArray();

        [TestMethod]
        public async Task AppInsightsImporter_FatalDownloadFailure_DoesNotReportTheSectionAsFinished_AndIsCountedAgainstTheDay()
        {
            // The download failure used to stop the whole day loop with no limit - and, being outside the per-day retry
            // policy, a day whose download always failed stalled the import for ever (G-2). DEBUG also rethrew it.
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = FixedNowUtc.AddHours(-2);
            h.Source.PageViewCountByDay[FixedNowUtc.Date] = 1;
            h.Source.DaysThatFailToDownload.Add(FixedNowUtc.Date);

            var log = await CaptureConsole(() => h.RunAsync());

            Assert.AreEqual(AppInsightsCycleOutcome.Failed, h.LastResult.Outcome);
            Assert.AreEqual("Download", h.LastResult.Days.Single().FailedStage);
            Assert.AreEqual(FixedNowUtc.Date, h.DayFailures.EarliestDayAwaitingRetryUtc, "The download failure counts against the day.");
            StringAssert.Contains(log, "Failed downloading data for day 2026-05-10 (attempt 1 of 3)");
            Assert.AreEqual(0, CountOf(log, AnalyticsLogger.AnalyticsEvent.FinishedSectionImport), "A failed download must not report the section as finished.");
        }

        [TestMethod]
        public async Task AppInsightsImporter_ADayWhoseDownloadKeepsFailing_StopsBeingRetriedOnTheThirdAttempt_SoTheImportCannotStallOnIt()
        {
            var h = ThreeDaysOfPageViews();
            h.Source.DaysThatFailToDownload.Add(May9);

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                h.Source.PageViewDaysRequested.Clear();
                await h.RunAsync();
                CollectionAssert.AreEqual(new[] { May8, May9 }, h.Source.PageViewDaysRequested.ToArray(), $"Attempt {attempt} stops at the failed day.");
                Assert.AreEqual(AppInsightsCycleOutcome.Failed, h.LastResult.Outcome);
            }

            h.Source.PageViewDaysRequested.Clear();
            h.Persistence.SavedPageViews.Clear();
            var log = await CaptureConsole(() => h.RunAsync());

            CollectionAssert.AreEqual(new[] { May8, May9, May10 }, h.Source.PageViewDaysRequested.ToArray(), "The third failure releases the day.");
            CollectionAssert.AreEqual(new[] { May8, May10 }, SavedDays(h));
            Assert.AreEqual(AppInsightsCycleOutcome.Degraded, h.LastResult.Outcome);
            Assert.AreEqual(1, CountOf(log, AnalyticsLogger.AnalyticsEvent.AppInsightsDayGivenUp));
        }

        [TestMethod]
        public async Task AppInsightsImporter_ADayThatFailsToSave_StopsTheRunBeforeAnyLaterDay()
        {
            var h = ThreeDaysOfPageViews();
            h.Persistence.DaysThatFailToSave.Add(May9);

            var log = await CaptureConsole(() => h.RunAsync());

            CollectionAssert.AreEqual(new[] { May8, May9 }, h.Source.PageViewDaysRequested.ToArray(), "Nothing after the failed day may be read.");
            CollectionAssert.AreEqual(new[] { May8 }, SavedDays(h));
            StringAssert.Contains(log, "Failed saving data for day 2026-05-09");
            Assert.AreEqual(AppInsightsCycleOutcome.Failed, h.LastResult.Outcome);
            Assert.AreEqual(0, CountOf(log, AnalyticsLogger.AnalyticsEvent.FinishedSectionImport),
                "A run that stopped at a failed day must not report the section as finished.");
        }

        [TestMethod]
        public async Task AppInsightsImporter_ADayThatFailedToSave_IsRetriedNextCycle_EvenIfTheStoredWatermarkHasMovedPastIt()
        {
            var h = ThreeDaysOfPageViews();
            h.Persistence.DaysThatFailToSave.Add(May9);
            await h.RunAsync();

            // The fault was transient. Meanwhile the newest stored hit has moved past the failed day.
            h.Persistence.DaysThatFailToSave.Clear();
            h.Persistence.SavedPageViews.Clear();
            h.Source.PageViewDaysRequested.Clear();
            h.Watermark.NewestHitTimestampUtc = new DateTime(2026, 5, 10, 6, 0, 0, DateTimeKind.Utc);
            await h.RunAsync();

            CollectionAssert.AreEqual(new[] { May9, May10 }, h.Source.PageViewDaysRequested.ToArray(), "The failed day is retried before any later day.");
            CollectionAssert.AreEqual(new[] { May9, May10 }, SavedDays(h));

            // Once it has saved, it is not read again for its own sake.
            h.Source.PageViewDaysRequested.Clear();
            await h.RunAsync();
            CollectionAssert.AreEqual(new[] { May10 }, h.Source.PageViewDaysRequested.ToArray());
        }

        [TestMethod]
        public async Task AppInsightsImporter_ADayThatKeepsFailing_StopsForcedRetriesOnTheThirdAttempt_ReportedOncePerTransition()
        {
            // G-7: "permanent gap" was logged on every failure after the third, even for a day that would still be read.
            var h = ThreeDaysOfPageViews();
            h.Persistence.DaysThatFailToSave.Add(May9);

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                h.Source.PageViewDaysRequested.Clear();
                await h.RunAsync();
                CollectionAssert.AreEqual(new[] { May8, May9 }, h.Source.PageViewDaysRequested.ToArray(), $"Attempt {attempt} stops at the failed day.");
            }

            h.Source.PageViewDaysRequested.Clear();
            h.Persistence.SavedPageViews.Clear();
            var log = await CaptureConsole(() => h.RunAsync());

            CollectionAssert.AreEqual(new[] { May8, May9, May10 }, h.Source.PageViewDaysRequested.ToArray(), "The third failure releases the day.");
            CollectionAssert.AreEqual(new[] { May8, May10 }, SavedDays(h));
            StringAssert.Contains(log, "Stopped forced retries of this day");
            Assert.IsFalse(log.Contains("permanent gap"), "Whether the day is read again depends on what arrives later; it is not claimed to be permanent.");
            Assert.AreEqual(1, CountOf(log, AnalyticsLogger.AnalyticsEvent.AppInsightsDayGivenUp));
            StringAssert.Contains(log, "Day=2026-05-09");
            Assert.AreEqual(1, CountOf(log, AnalyticsLogger.AnalyticsEvent.FinishedSectionImport));

            // A day whose retries have stopped never stalls the import again, and is not reported as a new transition.
            h.Source.PageViewDaysRequested.Clear();
            log = await CaptureConsole(() => h.RunAsync());
            CollectionAssert.AreEqual(new[] { May8, May9, May10 }, h.Source.PageViewDaysRequested.ToArray());
            Assert.AreEqual(0, CountOf(log, AnalyticsLogger.AnalyticsEvent.AppInsightsDayGivenUp));
            StringAssert.Contains(log, "its forced retries had already stopped");
        }

        [TestMethod]
        public async Task AppInsightsImporter_ADayWhoseEventSectionFails_StopsTheRunAfterItsPageViews_AndIsRetriedNextCycle()
        {
            var h = ThreeDaysOfPageViews();
            foreach (var day in new[] { May8, May9, May10 })
            {
                h.Source.CustomEventCountByDay[day] = 1;
            }
            h.Persistence.DaysWhoseEventsFailToSave.Add(May9);

            var log = await CaptureConsole(() => h.RunAsync());

            CollectionAssert.AreEqual(new[] { May8, May9 }, h.Source.PageViewDaysRequested.ToArray(), "Nothing after the failed day may be read.");
            CollectionAssert.AreEqual(new[] { May8, May9 }, SavedDays(h), "The failed day's page views did save.");
            CollectionAssert.AreEqual(new[] { May8 }, SavedEventDays(h));
            StringAssert.Contains(log, "Failed saving data for day 2026-05-09");
            CollectionAssert.AreEqual(new[] { "Clicks" }, h.LastResult.Days.Last().FailedSections.ToArray());
            StringAssert.Contains(log, "FailedSections=Clicks");

            h.Persistence.DaysWhoseEventsFailToSave.Clear();
            h.Persistence.SavedCustomEvents.Clear();
            h.Source.PageViewDaysRequested.Clear();
            h.Watermark.NewestHitTimestampUtc = new DateTime(2026, 5, 9, 14, 0, 0, DateTimeKind.Utc);
            await h.RunAsync();

            CollectionAssert.AreEqual(new[] { May9, May10 }, h.Source.PageViewDaysRequested.ToArray());
            CollectionAssert.AreEqual(new[] { May9, May10 }, SavedEventDays(h), "The failed day's events are saved on the retry.");
        }

        [TestMethod]
        public async Task AppInsightsImporter_ACountQueryFailure_FailsTheCycleWithoutMovingAnything()
        {
            var h = ThreeDaysOfPageViews();
            h.Cursor = new InMemoryIngestionCursorStore(FixedNowUtc.AddMinutes(-10));
            h.Source.CountsFailWith = new InvalidOperationException("Fake count failure");

            var log = await CaptureConsole(() => h.RunAsync());

            Assert.AreEqual(AppInsightsCycleOutcome.Failed, h.LastResult.Outcome);
            CollectionAssert.Contains(h.LastResult.Reasons, "RowCountQueryFailed");
            Assert.AreEqual(FixedNowUtc.AddMinutes(-10), h.Cursor.CursorUtc);
            StringAssert.Contains(log, "AppInsightsRowCounts: Outcome=Failed");
        }

        #endregion

        #region Ingestion cursor (G-1, G-3)

        [TestMethod]
        public async Task AppInsightsImporter_LateTelemetryForAnEarlierDay_IsImportedOnTheNextCycle()
        {
            // G-1: the import resumed from the newest stored hit, so once today had a hit, yesterday was never read
            // again and telemetry for it that reached App Insights late (an offline browser) was never imported.
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = FixedNowUtc.AddMinutes(-20);
            h.Cursor = new InMemoryIngestionCursorStore(FixedNowUtc.AddMinutes(-10));
            var yesterdayMorning = May9.AddHours(10);
            h.Source.ExtraPageViews.Add((yesterdayMorning, FixedNowUtc.AddMinutes(-5)));          // late: ingested just now
            h.Source.ExtraPageViews.Add((May9.AddHours(11), FixedNowUtc.AddHours(-6)));           // imported by an earlier cycle

            var result = await h.RunAsync();

            Assert.AreEqual(AppInsightsReadMode.Incremental, result.Mode);
            Assert.AreEqual(yesterdayMorning, h.Persistence.SavedPageViews.Single().Rows.Single().Timestamp,
                "Only the late row is read, and it is saved even though a later hit is already stored.");
            Assert.AreEqual(FixedNowUtc, h.Cursor.CursorUtc);
        }

        [TestMethod]
        public async Task AppInsightsImporter_ASteadyCycle_ReadsOnlyWhatWasIngestedSinceTheCursor_NotTheWholeDay()
        {
            // G-3: every cycle re-downloaded and re-staged the whole current day.
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = FixedNowUtc.AddMinutes(-1);
            h.Source.PageViewCountByDay[FixedNowUtc.Date] = 9 * 60;                               // a page view a minute since midnight
            h.Cursor = new InMemoryIngestionCursorStore(FixedNowUtc.AddMinutes(-10));

            await h.RunAsync();

            // Ingested from the cursor less the overlap (15 min) up to now: 25 rows, not the day's 540.
            Assert.AreEqual(25, h.Persistence.SavedPageViews.Sum(p => p.Rows.Count));
            var window = h.Source.PageViewWindowsRequested.Single();
            Assert.AreEqual(FixedNowUtc.AddMinutes(-10) - AppInsightsIngestionCursorPolicy.Overlap, window.IngestedFromUtc);
            Assert.AreEqual(FixedNowUtc, window.IngestedBeforeUtc);
        }

        [TestMethod]
        public async Task AppInsightsImporter_TheNextCycle_RereadsTheOverlapBeforeTheCursor()
        {
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = FixedNowUtc.AddMinutes(-1);
            h.Cursor = new InMemoryIngestionCursorStore(FixedNowUtc.AddMinutes(-10));
            // Ingested a minute before this cycle's bound, so it is in this cycle AND in the next one's overlap.
            h.Source.ExtraPageViews.Add((FixedNowUtc.AddMinutes(-2), FixedNowUtc.AddMinutes(-1)));
            await h.RunAsync();
            Assert.AreEqual(FixedNowUtc, h.Cursor.CursorUtc);

            h.Clock = new FixedClock(FixedNowUtc.AddMinutes(10));
            h.Persistence.SavedPageViews.Clear();
            h.Source.CountRangesRequested.Clear();
            await h.RunAsync();

            var range = h.Source.CountRangesRequested.Single();
            Assert.AreEqual(FixedNowUtc - AppInsightsIngestionCursorPolicy.Overlap, range.IngestedFromUtc);
            Assert.AreEqual(FixedNowUtc.AddMinutes(10), range.IngestedBeforeUtc);
            Assert.AreEqual(1, h.Persistence.SavedPageViews.Single().Rows.Count,
                "The overlap is read again; saving it again is harmless because the merges skip stored page-request ids.");
            Assert.AreEqual(FixedNowUtc.AddMinutes(10), h.Cursor.CursorUtc);
        }

        [TestMethod]
        public async Task AppInsightsImporter_TheCursor_DoesNotMoveWhenADayFails_AndMovesOnceItIsRetriedSuccessfully()
        {
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = FixedNowUtc.AddMinutes(-1);
            var cursor = FixedNowUtc.AddMinutes(-10);
            h.Cursor = new InMemoryIngestionCursorStore(cursor);
            h.Source.ExtraPageViews.Add((May9.AddHours(3), FixedNowUtc.AddMinutes(-3)));
            h.Persistence.DaysThatFailToSave.Add(May9);

            await h.RunAsync();
            Assert.AreEqual(AppInsightsCycleOutcome.Failed, h.LastResult.Outcome);
            Assert.AreEqual(cursor, h.Cursor.CursorUtc, "Nothing read from the failed day may be skipped by the next cycle.");
            Assert.AreEqual(0, h.Cursor.Writes);

            h.Persistence.DaysThatFailToSave.Clear();
            h.Clock = new FixedClock(FixedNowUtc.AddMinutes(10));
            await h.RunAsync();
            Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, h.LastResult.Outcome);
            Assert.AreEqual(May9.AddHours(3), h.Persistence.SavedPageViews.Single().Rows.Single().Timestamp);
            Assert.AreEqual(FixedNowUtc.AddMinutes(10), h.Cursor.CursorUtc);
        }

        [TestMethod]
        public async Task AppInsightsImporter_WithNoCursorYet_ReadsWholeDaysFromTheNewestStoredHit_AndThenSetsTheCursor()
        {
            var h = ThreeDaysOfPageViews();
            h.Cursor = new InMemoryIngestionCursorStore();

            var result = await h.RunAsync();

            Assert.AreEqual(AppInsightsReadMode.Bootstrap, result.Mode);
            CollectionAssert.AreEqual(new[] { May8, May9, May10 }, SavedDays(h));
            Assert.AreEqual(FixedNowUtc, h.Cursor.CursorUtc);
            Assert.AreEqual(FixedNowUtc, result.CursorAdvancedToUtc);
        }

        [TestMethod]
        public async Task AppInsightsImporter_ACursorWithNoStoredHits_IsIgnored()
        {
            // A new or restored database: the cursor says data was imported, but none is there.
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = null;
            h.Cursor = new InMemoryIngestionCursorStore(FixedNowUtc.AddMinutes(-10));

            var result = await h.RunAsync();

            Assert.AreEqual(AppInsightsReadMode.Bootstrap, result.Mode);
            Assert.AreEqual(FixedNowUtc.AddDays(-AppInsightsImportWindow.NoHitsFallbackDays).AddMinutes(-1).Date, h.Source.CountRangesRequested.Single().FromUtc);
        }

        [TestMethod]
        public async Task AppInsightsImporter_ABackfill_ReadsWholeDays_AndLeavesTheCursorAlone()
        {
            var h = ThreeDaysOfPageViews();
            var cursor = FixedNowUtc.AddMinutes(-10);
            h.Cursor = new InMemoryIngestionCursorStore(cursor);

            var result = await h.RunAsync(daysBeforeOverride: 2);

            Assert.AreEqual(AppInsightsReadMode.Backfill, result.Mode);
            CollectionAssert.AreEqual(new[] { May8, May9, May10 }, SavedDays(h));
            Assert.AreEqual(cursor, h.Cursor.CursorUtc);
            Assert.AreEqual(0, h.Cursor.Writes);
        }

        [TestMethod]
        public async Task AppInsightsImporter_ACursorThatCannotBeSaved_DegradesTheCycle()
        {
            var h = ThreeDaysOfPageViews();
            h.Cursor = new InMemoryIngestionCursorStore { WriteFailsWith = new InvalidOperationException("Fake storage outage") };

            var log = await CaptureConsole(() => h.RunAsync());

            Assert.AreEqual(AppInsightsCycleOutcome.Degraded, h.LastResult.Outcome);
            CollectionAssert.Contains(h.LastResult.Reasons, "CursorWriteFailed");
            CollectionAssert.AreEqual(new[] { May8, May9, May10 }, SavedDays(h));
            StringAssert.Contains(log, "AppInsightsCursor: Outcome=WriteFailed");
        }

        [TestMethod]
        public async Task AppInsightsImporter_ACursorThatCannotBeRead_FallsBackToWholeDays_AndDoesNotOverwriteIt()
        {
            // Writing "now" after a fallback read would skip whatever was ingested since the stored cursor for days before
            // the newest stored hit, so the stored cursor is left for the next cycle.
            var h = ThreeDaysOfPageViews();
            h.Cursor = new InMemoryIngestionCursorStore(FixedNowUtc.AddHours(-3)) { ReadFailsWith = new InvalidOperationException("Fake storage outage") };

            var result = await h.RunAsync();

            Assert.AreEqual(AppInsightsReadMode.Bootstrap, result.Mode);
            Assert.AreEqual(AppInsightsCycleOutcome.Degraded, result.Outcome);
            CollectionAssert.Contains(result.Reasons, "CursorReadFailed");
            Assert.AreEqual(0, h.Cursor.Writes);
        }

        #endregion

        #region Signals (L-8, L-9)

        [TestMethod]
        public async Task AppInsightsImporter_SuccessfulRun_ReportsTheSectionAsFinished_WithItsRealDuration()
        {
            // L-8: the "Hits import" JobTimer was never started, so its ElapsedSeconds was always exactly 0.
            var channel = new RecordingChannel();
            using (var configuration = new TelemetryConfiguration { TelemetryChannel = channel, ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001" })
            {
                var h = ThreeDaysOfPageViews();
                h.Logger = new AnalyticsLogger(new TelemetryClient(configuration), "AppInsightsImporter");

                await h.RunAsync();

                var finished = channel.Events.Single(e => e.Name == nameof(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport));
                Assert.AreEqual("Hits import", finished.Properties["OperationName"]);
                Assert.IsTrue(finished.Metrics["ElapsedSeconds"] > 0, "The section's duration must be measured.");

                // One AppInsightsDay event per day, with the shape a log export is read by.
                var days = channel.Events.Where(e => e.Name == nameof(AnalyticsLogger.AnalyticsEvent.AppInsightsDay)).ToList();
                CollectionAssert.AreEqual(new[] { "2026-05-08", "2026-05-09", "2026-05-10" }, days.Select(d => d.Properties["Day"]).ToArray());
                Assert.IsTrue(days.All(d => d.Properties["Outcome"] == "Saved" && d.Metrics["PageViews"] == 1 && d.Metrics["Windows"] == 1));
                Assert.IsTrue(days.All(d => d.Metrics.ContainsKey("FetchSeconds") && d.Metrics.ContainsKey("SaveSeconds")));
            }
        }

        [TestMethod]
        public void CycleTelemetry_ReportsEachOutcome_AndAThrownCycleAsFailed()
        {
            // L-9: a cycle that stopped at a failed day was reported exactly like a successful one.
            var channel = new RecordingChannel();
            using (var configuration = new TelemetryConfiguration { TelemetryChannel = channel, ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001" })
            {
                var logger = new AnalyticsLogger(new TelemetryClient(configuration), "AppInsightsImporter");
                var failed = new AppInsightsImportCycleResult { Mode = AppInsightsReadMode.Incremental };
                failed.Days.Add(new AppInsightsDayResult(May9) { Outcome = AppInsightsDayOutcome.Failed, FailedStage = "Save" });
                failed.Fail("DayFailed");

                Assert.AreEqual(AppInsightsCycleOutcome.Failed, AppInsightsImportTelemetry.TrackCycle(logger, failed, TimeSpan.FromSeconds(3)));
                Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, AppInsightsImportTelemetry.TrackCycle(logger, new AppInsightsImportCycleResult(), TimeSpan.FromSeconds(1)));
                Assert.AreEqual(AppInsightsCycleOutcome.Failed, AppInsightsImportTelemetry.TrackCycle(logger, null, TimeSpan.FromSeconds(1), new TimeoutException("Fake")));

                var cycles = channel.Events.Where(e => e.Name == nameof(AnalyticsLogger.AnalyticsEvent.AppInsightsImportCycle)).ToList();
                CollectionAssert.AreEqual(new[] { "Failed", "Succeeded", "Failed" }, cycles.Select(c => c.Properties["Outcome"]).ToArray());
                Assert.AreEqual("2026-05-09", cycles[0].Properties["FailedDays"]);
                Assert.AreEqual("DayFailed", cycles[0].Properties["Reasons"]);
                Assert.AreEqual("TimeoutException", cycles[2].Properties["ExceptionType"]);
                Assert.AreEqual(3, cycles[0].Metrics["ElapsedSeconds"]);
            }
        }

        private sealed class RecordingChannel : ITelemetryChannel
        {
            public ConcurrentQueue<EventTelemetry> Queue { get; } = new ConcurrentQueue<EventTelemetry>();
            public IList<EventTelemetry> Events => Queue.ToList();
            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }
            public void Send(ITelemetry item)
            {
                if (item is EventTelemetry ev) Queue.Enqueue(ev);
            }
            public void Flush() { }
            public void Dispose() { }
        }

        #endregion
    }
}
