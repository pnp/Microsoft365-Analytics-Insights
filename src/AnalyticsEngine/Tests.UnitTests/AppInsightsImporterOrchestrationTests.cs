using Common.Entities.Config;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
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
    /// The App Insights importer's orchestration - the day loop, the window it derives, and the per-day
    /// failure isolation - driven entirely through the ports added by issue #374. No HTTP, no SQL Server,
    /// no wall clock.
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

            // Outlives each run's importer, as the web-job host's does.
            public readonly AppInsightsDayFailureTracker DayFailures = new AppInsightsDayFailureTracker();
            public readonly AppInsightsQueryWindowCache QueryWindows = new AppInsightsQueryWindowCache();

            public Task RunAsync(int? daysBeforeOverride = null) => new AppInsightsImporter(
                null,
                AnalyticsLogger.ConsoleOnlyTracer(),
                Clock,
                Source,
                SiteFilters,
                Watermark,
                Persistence,
                dayFailureTracker: DayFailures,
                queryWindowCache: QueryWindows).ImportAndSave(saveRestResponses: false, daysBeforeOverride: daysBeforeOverride);
        }

        [TestMethod]
        public async Task AppInsightsImporter_RunsWithFakeSourceAndFakePersistence_WithoutHttpOrSql()
        {
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = new DateTime(2026, 5, 8, 14, 0, 0, DateTimeKind.Utc);
            h.SiteFilters.Filters.Add(new FilterUrlConfig { Url = "https://contoso.sharepoint.com" });
            h.Source.PageViewCountByDay[new DateTime(2026, 5, 9)] = 3;
            h.Source.CustomEventCountByDay[new DateTime(2026, 5, 9)] = 2;

            await h.RunAsync();

            Assert.AreEqual(1, h.Watermark.ReadCount);

            // 8th (the watermark day, rewound a minute), 9th, 10th (today).
            CollectionAssert.AreEqual(
                new[] { new DateTime(2026, 5, 8), new DateTime(2026, 5, 9), new DateTime(2026, 5, 10) },
                h.Source.PageViewDaysRequested.ToArray());
            CollectionAssert.AreEqual(h.Source.PageViewDaysRequested.ToArray(), h.Source.CustomEventDaysRequested.ToArray());

            // Only the day with data is saved; empty days are skipped entirely.
            Assert.AreEqual(1, h.Persistence.SavedPageViews.Count);
            Assert.AreEqual(3, h.Persistence.SavedPageViews.Single().Rows.Count);
            Assert.AreEqual(2, h.Persistence.SavedCustomEvents.Single().Rows.Count);

            // The org-URL filter loaded at startup is the one handed to the save, not a fresh empty list.
            Assert.AreSame(h.SiteFilters.Filters, h.Persistence.FiltersSeen);
        }

        [TestMethod]
        public async Task AppInsightsImporter_WindowIsDrivenByTheInjectedClock_NotTheWallClock()
        {
            // A clock fixed years in the past: no wall-clock-derived window could produce these days, so
            // this fails if ImportAndSave ever goes back to reading DateTime.UtcNow / DateTime.Now.
            var h = new Harness { Clock = new FixedClock(new DateTime(2019, 2, 14, 6, 0, 0, DateTimeKind.Utc)) };
            h.Watermark.NewestHitTimestampUtc = new DateTime(2019, 2, 12, 23, 30, 0, DateTimeKind.Utc);

            await h.RunAsync();

            CollectionAssert.AreEqual(
                new[] { new DateTime(2019, 2, 12), new DateTime(2019, 2, 13), new DateTime(2019, 2, 14) },
                h.Source.PageViewDaysRequested.ToArray());
        }

        [TestMethod]
        public async Task AppInsightsImporter_DaysBeforeOverride_IsAlsoResolvedFromTheInjectedClock()
        {
            // ResolveOverrideStartUtc ignores its clock argument when no override is supplied, so the test
            // above cannot see the FIRST _clock.UtcNow read in ImportAndSave. With an override it can:
            // swapping that read for DateTime.Now would shift the window on any non-UTC host.
            var h = new Harness { Clock = new FixedClock(new DateTime(2019, 2, 14, 6, 0, 0, DateTimeKind.Utc)) };
            h.Watermark.NewestHitTimestampUtc = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            await h.RunAsync(daysBeforeOverride: 2);

            // The override wins over the (much older) watermark: 2 days back from the fixed clock, rewound
            // a minute, through "today" inclusive.
            CollectionAssert.AreEqual(
                new[] { new DateTime(2019, 2, 12), new DateTime(2019, 2, 13), new DateTime(2019, 2, 14) },
                h.Source.PageViewDaysRequested.ToArray());
        }

        [TestMethod]
        public async Task AppInsightsImporter_FutureDatedWatermark_DoesNotWalkBackwardsAndLogsTheTimestamp()
        {
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = FixedNowUtc.AddDays(2).AddHours(3);

            var console = new StringWriter();
            var original = Console.Out;
            Console.SetOut(console);
            try
            {
                await h.RunAsync();
            }
            finally
            {
                Console.SetOut(original);
            }

            CollectionAssert.AreEqual(
                new[] { FixedNowUtc.Date },
                h.Source.PageViewDaysRequested.ToArray(),
                "A future hit_timestamp must not make the importer walk backwards through future days.");
            CollectionAssert.AreEqual(h.Source.PageViewDaysRequested.ToArray(), h.Source.CustomEventDaysRequested.ToArray());

            var log = console.ToString();
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

            // 31 days back, rewound a minute (so the first day is the 32nd back), through today inclusive.
            Assert.AreEqual(FixedNowUtc.AddDays(-AppInsightsImportWindow.NoHitsFallbackDays).AddMinutes(-1).Date,
                h.Source.PageViewDaysRequested.First());
            Assert.AreEqual(FixedNowUtc.Date, h.Source.PageViewDaysRequested.Last());
        }

        [TestMethod]
        public async Task AppInsightsImporter_FatalDownloadFailure_DoesNotReportTheSectionAsFinished()
        {
            // Regression guard. The original code returned straight out of ImportAndSave from the download
            // catch, so JobTimer.TrackFinishedEventAndStopTimer never ran. Extracting the day loop into its
            // own method made that `return` exit only the inner method, which would have emitted
            // FinishedSectionImport for a cycle that imported nothing - a false success to liveness
            // monitoring.
            //
            // The production path is #if DEBUG-forked, so this test is too. Note CI currently builds and
            // tests RELEASE ONLY (the Debug matrix entry is commented out in ci.yml / pr.yml / tests.yml),
            // so it is the #else arm - the one guarding the actual regression - that CI runs. The DEBUG arm
            // only runs in a local Debug build.
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = FixedNowUtc.AddHours(-2);
            h.Source.DaysThatFailToDownload.Add(FixedNowUtc.Date);

            var console = new StringWriter();
            var original = Console.Out;
            Console.SetOut(console);
            try
            {
#if DEBUG
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => h.RunAsync());
#else
                await h.RunAsync();
#endif
            }
            finally
            {
                Console.SetOut(original);
            }

            // AnalyticsLogger.TrackEvent writes "New event '<name>'" to the console, so this is how the
            // event is observable without a real App Insights key. Asserted in BOTH configurations: DEBUG
            // must not track it either, because it never gets that far.
            StringAssert.DoesNotMatch(console.ToString(), new Regex("New event '" + nameof(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport) + "'"),
                "A failed download must not report the section as finished.");
        }

        [TestMethod]
        public async Task AppInsightsImporter_SuccessfulRun_DoesReportTheSectionAsFinished()
        {
            // The other half of the guard above: without this, making the failure path skip the event could
            // be "fixed" by never emitting it at all.
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = FixedNowUtc.AddHours(-2);

            var console = new StringWriter();
            var original = Console.Out;
            Console.SetOut(console);
            try
            {
                await h.RunAsync();
            }
            finally
            {
                Console.SetOut(original);
            }

            StringAssert.Matches(console.ToString(), new Regex("New event '" + nameof(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport) + "'"));
        }

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

        [TestMethod]
        public async Task AppInsightsImporter_ADayThatFailsToSave_StopsTheRunBeforeAnyLaterDay()
        {
            // Replaces AppInsightsImporter_OneDayFailing_DoesNotAbortRemainingDays, which pinned the old behaviour of
            // skipping the failed day and carrying on. The next day's hits then moved the watermark (the newest stored
            // hit_timestamp) past it, so a transient SQL error on any day but the last lost that day for good.
            var h = ThreeDaysOfPageViews();
            h.Persistence.DaysThatFailToSave.Add(May9);

            var log = await CaptureConsole(() => h.RunAsync());

            CollectionAssert.AreEqual(new[] { May8, May9 }, h.Source.PageViewDaysRequested.ToArray(), "Nothing after the failed day may be read.");
            CollectionAssert.AreEqual(new[] { May8 }, SavedDays(h));
            StringAssert.Contains(log, "Failed saving data for day 2026-05-09");
            StringAssert.DoesNotMatch(log, new Regex("New event '" + nameof(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport) + "'"),
                "A run that stopped at a failed day must not report the section as finished.");
        }

        [TestMethod]
        public async Task AppInsightsImporter_ADayThatFailedToSave_IsRetriedNextCycle_EvenIfTheStoredWatermarkHasMovedPastIt()
        {
            var h = ThreeDaysOfPageViews();
            h.Persistence.DaysThatFailToSave.Add(May9);
            await h.RunAsync();

            // The fault was transient. Meanwhile the newest stored hit has moved past the failed day - which is what
            // skipping it used to cause, and what a browser with its clock ahead can still cause.
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
        public async Task AppInsightsImporter_ADayThatKeepsFailing_IsGivenUpOnTheThirdAttempt_SoTheImportCannotStallOnIt()
        {
            // The reason 895ff223 skipped failed days: one deterministic "poison" day used to stall the importer for
            // months. A failed day is now retried, but only a bounded number of times.
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

            CollectionAssert.AreEqual(new[] { May8, May9, May10 }, h.Source.PageViewDaysRequested.ToArray(), "The third failure gives up on the day.");
            CollectionAssert.AreEqual(new[] { May8, May10 }, SavedDays(h));
            StringAssert.Contains(log, "2026-05-09");
            StringAssert.Contains(log, "permanent gap");
            StringAssert.Matches(log, new Regex("New event '" + nameof(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport) + "'"));

            // A given-up day never stalls the import again.
            h.Source.PageViewDaysRequested.Clear();
            await h.RunAsync();
            CollectionAssert.AreEqual(new[] { May8, May9, May10 }, h.Source.PageViewDaysRequested.ToArray());
        }

        [TestMethod]
        public async Task AppInsightsImporter_ADayWhoseEventSectionFails_StopsTheRunAfterItsPageViews_AndIsRetriedNextCycle()
        {
            // A failed custom-event section used to be swallowed, so the day counted as saved and its page views moved
            // the watermark past it. Now the event save throws once every section has been tried, and the day is retried.
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
            StringAssert.DoesNotMatch(log, new Regex("New event '" + nameof(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport) + "'"));

            // The section's fault has cleared, and the failed day's own page views have moved the newest stored hit into it.
            h.Persistence.DaysWhoseEventsFailToSave.Clear();
            h.Persistence.SavedCustomEvents.Clear();
            h.Source.PageViewDaysRequested.Clear();
            h.Watermark.NewestHitTimestampUtc = new DateTime(2026, 5, 9, 14, 0, 0, DateTimeKind.Utc);
            await h.RunAsync();

            CollectionAssert.AreEqual(new[] { May9, May10 }, h.Source.PageViewDaysRequested.ToArray());
            CollectionAssert.AreEqual(new[] { May9, May10 }, SavedEventDays(h), "The failed day's events are saved on the retry.");
        }

        private static DateTime[] SavedEventDays(Harness h) => h.Persistence.SavedCustomEvents.Select(e => e.Rows[0].Timestamp.Date).ToArray();

        [TestMethod]
        public async Task AppInsightsImporter_ForgetsTheQueryWindowsOfDaysItNoLongerReads()
        {
            // The windows remembered for a day over the API's limits are only useful while that day is still read.
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = new DateTime(2026, 5, 9, 14, 0, 0, DateTimeKind.Utc);
            foreach (var day in new[] { May8, May9 })
            {
                h.QueryWindows.Remember("pageview", day, new[] { (day, day.AddHours(12)), (day.AddHours(12), day.AddDays(1)) });
            }

            await h.RunAsync();

            CollectionAssert.AreEqual(new[] { May9, May10 }, h.Source.PageViewDaysRequested.ToArray());
            Assert.IsNull(h.QueryWindows.WindowsFor("pageview", May8), "The 8th is no longer read, so its windows are forgotten.");
            Assert.IsNotNull(h.QueryWindows.WindowsFor("pageview", May9), "The 9th is still read.");
        }

        [TestMethod]
        public async Task AppInsightsImporter_DayThatSavesNothing_StillCountsAsProcessed()
        {
            // No data at all: the loop must complete without touching persistence rather than saving
            // empty collections (which would run the merge SQL for nothing, every cycle).
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = FixedNowUtc.AddHours(-2);

            await h.RunAsync();

            Assert.AreEqual(1, h.Source.PageViewDaysRequested.Count);
            Assert.AreEqual(0, h.Persistence.SavedPageViews.Count);
            Assert.AreEqual(0, h.Persistence.SavedCustomEvents.Count);
        }
    }
}
