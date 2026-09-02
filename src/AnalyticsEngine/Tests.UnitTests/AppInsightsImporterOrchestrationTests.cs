using Common.Entities.Config;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
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

            public Task RunAsync() => new AppInsightsImporter(
                new Common.Entities.Config.AppConfig(),
                AnalyticsLogger.ConsoleOnlyTracer(),
                Clock,
                Source,
                Maintenance,
                SiteFilters,
                Watermark,
                Persistence).ImportAndSave(saveRestResponses: false, daysBeforeOverride: null);
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
