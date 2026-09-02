using Common.Entities.Config;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql;
using WebJob.AppInsightsImporter.Engine.Sql.Rules;

namespace Tests.UnitTests
{
    /// <summary>
    /// The App Insights save side driven entirely through the write ports added by issue #369: the
    /// custom-event section orchestration, the page-view save result, and the day manager's forwarding.
    /// Runs with zero SQL Server, Graph, Redis or Service Bus dependency.
    /// </summary>
    [TestClass]
    public class AppInsightsSavePortTests
    {
        private class SectionHarness
        {
            public readonly List<string> CallLog = new List<string>();
            public readonly InMemoryHitUpdatePersistenceManager HitUpdates;
            public readonly InMemorySearchesPersistenceManager Searches;
            public readonly InMemoryPageUpdatePersistenceManager PageUpdates;
            public readonly InMemoryClicksPersistenceManager Clicks;

            public SectionHarness()
            {
                HitUpdates = new InMemoryHitUpdatePersistenceManager(CallLog);
                Searches = new InMemorySearchesPersistenceManager(CallLog);
                PageUpdates = new InMemoryPageUpdatePersistenceManager(CallLog);
                Clicks = new InMemoryClicksPersistenceManager(CallLog);
                Ports = new InMemoryEventSectionPort[] { HitUpdates, Searches, PageUpdates, Clicks };
            }

            /// <summary>The four section ports in the order the saver runs them.</summary>
            public readonly InMemoryEventSectionPort[] Ports;

            public Task RunAsync(CustomEventsResultCollection events)
            {
                return new CustomEventSectionSaver(AnalyticsLogger.ConsoleOnlyTracer(),
                    HitUpdates, Searches, PageUpdates, Clicks).SaveAllSectionsAsync(events);
            }
        }

        /// <summary>
        /// AnalyticsLogger writes traces and TrackEvent lines to the console, so that is how the summary
        /// line and the FinishedSectionImport events are observable without a real App Insights key.
        /// </summary>
        private static async Task<string> CaptureConsole(Func<Task> action)
        {
            var captured = new StringWriter();
            var original = Console.Out;
            Console.SetOut(captured);
            try
            {
                await action();
            }
            finally
            {
                Console.SetOut(original);
            }
            return captured.ToString();
        }

        private static CustomEventsResultCollection SomeEvents()
        {
            var events = new CustomEventsResultCollection();
            events.Rows.Add(new PageExitEventAppInsightsQueryResult
            {
                AppInsightsTimestamp = new DateTime(2026, 3, 4, 11, 0, 0, DateTimeKind.Utc)
            });
            return events;
        }

        #region Section orchestration

        [TestMethod]
        public async Task EventSections_RunInTheOrderTheImporterHasAlwaysUsed()
        {
            // Order is operator-visible behaviour: the four section timings are printed in this sequence
            // and read in that order in the logs.
            var h = new SectionHarness();

            await h.RunAsync(SomeEvents());

            CollectionAssert.AreEqual(
                new[] { "Hit updates", "Searches", "Page updates", "Clicks" },
                h.CallLog.ToArray());
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public async Task EventSections_EachSectionCompletesBeforeTheNextOneStarts(int gatedSection)
        {
            // The order test above only pins the order calls are MADE in. It would still pass if the
            // orchestration started all four sections and then awaited Task.WhenAll - which would be a
            // real behavioural change:
            //   * the clicks merge INSERTs into dbo.urls while the page-updates section UPDATEs dbo.urls
            //     rows through EF, so concurrent sections interleave writes to the same table;
            //   * the hit-update and click sections, and the page-update section's comment sub-path, each
            //     drive their own EFInsertBatch fan-out of up to InsertBatchConcurrency.MaxConcurrentThreads
            //     threads (default 20 - the lever that exists precisely to cap the SQL Server CPU/DTU burst
            //     on commit). Running them together stacks independent fan-outs, so the cap no longer
            //     bounds the whole save. (Searches is not one of them: it stages serially on a single
            //     SqlCommand, so it adds separate serial SQL work rather than more threads.)
            //   * the per-section JobTimer timings operators read would interleave.
            //
            // Every boundary is gated in turn, because gating only one would leave the others free to
            // overlap: gating Searches alone still passes if hit-updates and searches are started
            // together, or if page-updates and clicks are.
            //
            // No polling and no timing race: everything up to the open gate runs synchronously on this
            // thread (the ungated fakes return already-completed tasks, which continue inline), so when
            // RunAsync returns exactly the sections up to and including the gated one can have run.
            var sectionNames = new[] { "Hit updates", "Searches", "Page updates", "Clicks" };
            var h = new SectionHarness();
            h.Ports[gatedSection].Gate = new TaskCompletionSource<int>();

            var run = h.RunAsync(SomeEvents());

            Assert.IsFalse(run.IsCompleted, "The saver must still be waiting on the open section.");
            CollectionAssert.AreEqual(
                sectionNames.Take(gatedSection + 1).ToArray(),
                h.CallLog.ToArray(),
                "Nothing after the section being awaited may have started yet.");

            h.Ports[gatedSection].Gate.SetResult(0);
            await run;

            CollectionAssert.AreEqual(sectionNames, h.CallLog.ToArray());
        }

        [TestMethod]
        public async Task EventSections_EachReceiveTheSameBatchInstance()
        {
            // Guards against a refactor handing a section a copy, or an empty collection, of the batch.
            var events = SomeEvents();
            var h = new SectionHarness();

            await h.RunAsync(events);

            Assert.AreSame(events, h.HitUpdates.Saved.Single());
            Assert.AreSame(events, h.Searches.Saved.Single());
            Assert.AreSame(events, h.PageUpdates.Saved.Single());
            Assert.AreSame(events, h.Clicks.Saved.Single());
        }

        [TestMethod]
        public async Task EventSections_OneFailing_DoesNotAbortTheSiblingSections()
        {
            // The reason SaveSectionSafe exists: a page-update tripping a DbUpdateException used to be
            // able to take the rest of the event save down with it.
            var h = new SectionHarness();
            h.PageUpdates.FailWith = new InvalidOperationException("boom");

            await h.RunAsync(SomeEvents());

            CollectionAssert.AreEqual(
                new[] { "Hit updates", "Searches", "Page updates", "Clicks" },
                h.CallLog.ToArray(),
                "A failing section must be attempted, and must not stop the ones after it.");
        }

        [TestMethod]
        public async Task EventSections_AFailingSection_IsLoggedByNameAndDoesNotEscape()
        {
            var h = new SectionHarness();
            h.Searches.FailWith = new InvalidOperationException("search merge exploded");

            var console = await CaptureConsole(() => h.RunAsync(SomeEvents()));

            StringAssert.Contains(console, "Failed importing 'Searches' section",
                "Operators identify the broken section from this line.");
            StringAssert.Contains(console, "search merge exploded");
        }

        [TestMethod]
        public async Task EventSections_SummaryReportsEachSectionsOwnCount()
        {
            // Four same-typed ints in one interpolated string is exactly the shape that gets transposed,
            // so every section gets a distinct count and one is >= 1000 to pin the ':n0' formatting too.
            var h = new SectionHarness();
            h.HitUpdates.ReturnCount = 1234;
            h.Searches.ReturnCount = 22;
            h.PageUpdates.ReturnCount = 333;
            h.Clicks.ReturnCount = 45;

            var console = await CaptureConsole(() => h.RunAsync(SomeEvents()));

            StringAssert.Contains(console,
                "Event save summary: 1,234 hit-updates, 22 searches, 333 page-updates, 45 clicks");
        }

        [TestMethod]
        public async Task EventSections_AFailedSectionReportsZeroInTheSummary()
        {
            // The other half of the isolation contract: a section that blew up contributes 0 rather than
            // leaving the summary unprinted or carrying a stale count.
            var h = new SectionHarness();
            h.HitUpdates.ReturnCount = 1234;
            h.Searches.ReturnCount = 22;
            h.PageUpdates.ReturnCount = 333;
            h.PageUpdates.FailWith = new InvalidOperationException("boom");
            h.Clicks.ReturnCount = 45;

            var console = await CaptureConsole(() => h.RunAsync(SomeEvents()));

            StringAssert.Contains(console,
                "Event save summary: 1,234 hit-updates, 22 searches, 0 page-updates, 45 clicks");
        }

        [TestMethod]
        public async Task EventSections_OnlySectionsThatSavedSomething_ReportFinishedSectionImport()
        {
            // FinishedSectionImport feeds liveness monitoring, so emitting it for a section that saved
            // nothing would report activity on a dead import. Two of the four sections have rows here.
            var h = new SectionHarness();
            h.HitUpdates.ReturnCount = 3;
            h.Searches.ReturnCount = 0;
            h.PageUpdates.ReturnCount = 0;
            h.Clicks.ReturnCount = 7;

            var console = await CaptureConsole(() => h.RunAsync(SomeEvents()));

            var finishedEvents = Regex.Matches(console,
                "New event '" + nameof(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport) + "'").Count;
            Assert.AreEqual(2, finishedEvents,
                "Exactly the two sections that saved rows should report the section as finished.");
        }

        [TestMethod]
        public async Task EventSections_AFailedSection_DoesNotReportItselfFinished()
        {
            // A section that threw must not look like a completed section to liveness monitoring, even
            // though the orchestration swallows the exception.
            var h = new SectionHarness();
            h.HitUpdates.ReturnCount = 3;
            h.Searches.ReturnCount = 9;
            h.Searches.FailWith = new InvalidOperationException("boom");

            var console = await CaptureConsole(() => h.RunAsync(SomeEvents()));

            var finishedEvents = Regex.Matches(console,
                "New event '" + nameof(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport) + "'").Count;
            Assert.AreEqual(1, finishedEvents, "Only the hit-updates section completed.");
        }

        #endregion

        #region Page-view save result

        private const string InScopeSite = "https://contoso.sharepoint.com/sites/example";

        private static PageViewAppInsightsQueryResult PageView(Guid? pageRequestId, string url, string siteUrl = InScopeSite)
        {
            return new PageViewAppInsightsQueryResult
            {
                Url = url,
                CustomProperties = new PageViewCustomProps
                {
                    PageRequestId = pageRequestId,
                    SiteUrl = siteUrl,
                    SessionId = "session-1",
                    EventTimestamp = new DateTime(2026, 1, 5, 9, 30, 0, DateTimeKind.Utc)
                }
            };
        }

        [TestMethod]
        public void PageViewSaveResult_ReportsEveryPlanCountWithoutTransposingThem()
        {
            // Five ints of the same type reach PageViewSaveResult, so the fixture makes all five values
            // distinct: any two swapped in the mapping fails this.
            var sharedId = Guid.NewGuid();
            var batch = new PageViewCollection();
            batch.Rows.AddRange(new[]
            {
                PageView(Guid.NewGuid(), InScopeSite + "/pages/a.aspx"),          // staged
                PageView(sharedId, InScopeSite + "/pages/b.aspx"),                // staged
                PageView(sharedId, InScopeSite + "/pages/b.aspx"),                // duplicate 1
                PageView(sharedId, InScopeSite + "/pages/b.aspx"),                // duplicate 2
                PageView(sharedId, InScopeSite + "/pages/b.aspx"),                // duplicate 3
                PageView(Guid.NewGuid(), "https://fabrikam.sharepoint.com/sites/other/c.aspx",
                    "https://fabrikam.sharepoint.com/sites/other"),               // out of scope
            });

            var plan = PageViewStagingRules.Plan(batch, new List<FilterUrlConfig>
            {
                new FilterUrlConfig { Url = "https://contoso.sharepoint.com" }
            });

            var result = PageViewSaveResult.FromPlan(plan, mergeRowsAffected: 99);

            Assert.AreEqual(6, result.RawPageViews);
            Assert.AreEqual(2, result.Staged);
            Assert.AreEqual(3, result.DuplicatePageRequestIds);
            Assert.AreEqual(1, result.OutOfScopeUrls);
            Assert.AreEqual(99, result.MergeRowsAffected);
        }

        #endregion

        #region Day persistence manager wiring

        [TestMethod]
        public async Task DayPersistenceManager_ForwardsPageViewSaves_WithoutOpeningADatabaseContext()
        {
            // Page-views go through IPageViewsPersistenceManager, which borrows the importer's existing
            // context - so saving a day of page-views must not open a context of its own. The throwing
            // factory is what makes that assertable: it fails loudly if one is ever created here.
            var port = new InMemoryPageViewsPersistenceManager();
            var contextFactory = new ThrowingAnalyticsDbContextFactory();
            var batch = new PageViewCollection();
            var filters = new List<FilterUrlConfig> { new FilterUrlConfig { Url = "https://contoso.sharepoint.com" } };

            var manager = new SqlAppInsightsDayPersistenceManager(port,
                AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), contextFactory);

            await manager.SavePageViewsAsync(batch, filters);

            Assert.AreSame(batch, port.SavedPageViews.Single());
            Assert.AreSame(filters, port.FiltersSeen, "The loaded org-URL filters must reach the port unchanged.");
            Assert.AreEqual(0, contextFactory.CreateAttempts);
        }

        #endregion
    }
}
