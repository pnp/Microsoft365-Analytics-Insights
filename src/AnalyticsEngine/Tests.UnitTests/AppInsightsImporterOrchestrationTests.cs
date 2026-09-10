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
            public readonly FakeImportDbMaintenance Maintenance = new FakeImportDbMaintenance();
            public readonly FakeSiteFilterLoader SiteFilters = new FakeSiteFilterLoader();
            public readonly InMemoryHitWatermarkStore Watermark = new InMemoryHitWatermarkStore();
            public readonly InMemoryAppInsightsDayPersistenceManager Persistence = new InMemoryAppInsightsDayPersistenceManager();
            public FixedClock Clock = new FixedClock(FixedNowUtc);

            public Task RunAsync(int? daysBeforeOverride = null) => new AppInsightsImporter(
                null,
                AnalyticsLogger.ConsoleOnlyTracer(),
                Clock,
                Source,
                Maintenance,
                SiteFilters,
                Watermark,
                Persistence).ImportAndSave(saveRestResponses: false, daysBeforeOverride: daysBeforeOverride);
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

            // Startup maintenance runs exactly once for the whole run, not per day.
            Assert.AreEqual(1, h.Maintenance.RunCount);
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

        [TestMethod]
        public async Task AppInsightsImporter_OneDayFailing_DoesNotAbortRemainingDays()
        {
            // A single bad day - historically a page-update event tripping a DbUpdateException - must not
            // stall the whole multi-day run and freeze the watermark.
            var h = new Harness();
            h.Watermark.NewestHitTimestampUtc = new DateTime(2026, 5, 8, 14, 0, 0, DateTimeKind.Utc);
            foreach (var day in new[] { new DateTime(2026, 5, 8), new DateTime(2026, 5, 9), new DateTime(2026, 5, 10) })
            {
                h.Source.PageViewCountByDay[day] = 1;
            }
            h.Persistence.DaysThatFailToSave.Add(new DateTime(2026, 5, 9));

            await h.RunAsync();

            Assert.AreEqual(3, h.Source.PageViewDaysRequested.Count, "Every day should still be fetched.");
            CollectionAssert.AreEqual(
                new[] { new DateTime(2026, 5, 8), new DateTime(2026, 5, 10) },
                h.Persistence.SavedPageViews.Select(p => p.Rows[0].Timestamp.Date).ToArray());
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
