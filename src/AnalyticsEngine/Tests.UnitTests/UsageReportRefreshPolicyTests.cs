using Common.Entities.Config;
using Common.Entities.Entities.Teams;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Rules;

namespace Tests.UnitTests
{
    /// <summary>
    /// The finalized-date skip rule for the Graph daily usage reports, extracted by issue #375. Zero SQL
    /// Server, zero Graph, zero wall clock.
    ///
    /// This decision governs whether a day's report is re-downloaded or trusted as final, and it is
    /// expensive to get wrong in both directions: too eager re-downloads and rewrites every report on every
    /// cycle, too lax freezes stale numbers permanently.
    /// </summary>
    [TestClass]
    public class UsageReportRefreshPolicyTests
    {
        private static readonly DateTime NowUtc = new DateTime(2026, 6, 20, 11, 15, 0, DateTimeKind.Utc);
        private const int RefreshableRecentDays = 3;

        private static UsageReportSkipWindow Window(int daysBackMax, DateTime? lastSuccessfulImport, int recentDays = RefreshableRecentDays)
            => UsageReportRefreshPolicy.ResolveSkipWindow(daysBackMax, lastSuccessfulImport, recentDays, NowUtc);

        [TestMethod]
        public void UsageReports_UsageReportPhaseNeverCompleted_SkipsNothing()
        {
            // Stored rows could be from an interrupted save, so nothing is proven complete enough to skip.
            var window = Window(28, null);

            Assert.IsFalse(window.CanSkipAnyDate);
            Assert.AreEqual(0, UsageReportRefreshPolicy.EnumerateSkipCandidates(window).Count());
        }

        [TestMethod]
        public void UsageReports_DateOlderThanRefreshWindow_IsASkipCandidate()
        {
            // A completed phase well in the past: the Graph mutability window (3 days) is then the binding
            // cutoff, so everything from the window start up to now-3d is skippable.
            var window = Window(28, NowUtc.AddDays(-1));

            Assert.IsTrue(window.CanSkipAnyDate);
            Assert.AreEqual(NowUtc.Date.AddDays(-28), window.WindowStartUtc);
            Assert.AreEqual(NowUtc.Date.AddDays(-3), window.SafeCutoffUtc);

            var candidates = UsageReportRefreshPolicy.EnumerateSkipCandidates(window).ToList();
            CollectionAssert.Contains(candidates, NowUtc.Date.AddDays(-10));
        }

        [TestMethod]
        public void UsageReports_DateInsideRefreshWindow_IsNeverASkipCandidate()
        {
            // Graph gap-fills the most recent few days, so those must always be re-downloaded even though
            // they are stored and even though a phase has completed since.
            var window = Window(28, NowUtc);
            var candidates = UsageReportRefreshPolicy.EnumerateSkipCandidates(window).ToList();

            foreach (var recent in new[] { NowUtc.Date, NowUtc.Date.AddDays(-1), NowUtc.Date.AddDays(-2) })
            {
                CollectionAssert.DoesNotContain(candidates, recent, $"{recent:yyyy-MM-dd} can still change in Graph.");
            }
            CollectionAssert.Contains(candidates, NowUtc.Date.AddDays(-3).AddDays(-1));
        }

        [TestMethod]
        public void UsageReports_BoundaryDateExactlyOnWindowEdge_IsReloaded()
        {
            // The cutoff is EXCLUSIVE. now-3d is the first mutable day, so it must not be skipped; the day
            // before it is the last skippable one. An off-by-one here either re-downloads a finalized day
            // forever or freezes a still-changing one.
            var window = Window(28, NowUtc);
            var candidates = UsageReportRefreshPolicy.EnumerateSkipCandidates(window).ToList();

            CollectionAssert.DoesNotContain(candidates, window.SafeCutoffUtc);
            CollectionAssert.Contains(candidates, window.SafeCutoffUtc.AddDays(-1));
            Assert.AreEqual(window.SafeCutoffUtc.AddDays(-1), candidates.Last());
        }

        [TestMethod]
        public void UsageReports_CompletedPhaseOlderThanTheMutabilityWindow_BecomesTheBindingCutoff()
        {
            // The earlier of the two cutoffs wins. With the last completed phase 10 days ago, dates between
            // then and the 3-day mutability edge are NOT skippable - they may be from an interrupted save.
            var window = Window(28, NowUtc.AddDays(-10));

            Assert.AreEqual(NowUtc.Date.AddDays(-10), window.SafeCutoffUtc);
            var candidates = UsageReportRefreshPolicy.EnumerateSkipCandidates(window).ToList();
            CollectionAssert.DoesNotContain(candidates, NowUtc.Date.AddDays(-5));
            CollectionAssert.Contains(candidates, NowUtc.Date.AddDays(-11));
        }

        [TestMethod]
        public void UsageReports_LookBackIsClampedToWhatGraphCanAnswer()
        {
            // Graph retains ~28 days of daily detail, and reports lag 2-3 days, so a configured value
            // outside those bounds is corrected rather than rejected - a bad setting must not stop the import.
            Assert.AreEqual(UsageReportRefreshPolicy.MinDaysBack, UsageReportRefreshPolicy.ClampDaysBack(0));
            Assert.AreEqual(UsageReportRefreshPolicy.MinDaysBack, UsageReportRefreshPolicy.ClampDaysBack(-7));
            Assert.AreEqual(UsageReportRefreshPolicy.MaxDaysBack, UsageReportRefreshPolicy.ClampDaysBack(365));
            Assert.AreEqual(14, UsageReportRefreshPolicy.ClampDaysBack(14));
        }

        [TestMethod]
        public void UsageReports_ClampIsAppliedBeforeTheWindowIsComputed()
        {
            // Reporting the clamped DaysBackMax while computing WindowStartUtc from the raw value would be
            // invisible to a test that only checked DaysBackMax - and would make the indexed path issue 362
            // existence queries instead of 25, and the unindexed path scan a year of a 200k-user table.
            var wide = Window(365, NowUtc);
            Assert.AreEqual(UsageReportRefreshPolicy.MaxDaysBack, wide.DaysBackMax);
            Assert.AreEqual(NowUtc.Date.AddDays(-UsageReportRefreshPolicy.MaxDaysBack), wide.WindowStartUtc);
            Assert.AreEqual(UsageReportRefreshPolicy.MaxDaysBack - RefreshableRecentDays,
                UsageReportRefreshPolicy.EnumerateSkipCandidates(wide).Count());

            var narrow = Window(0, NowUtc);
            Assert.AreEqual(UsageReportRefreshPolicy.MinDaysBack, narrow.DaysBackMax);
            Assert.AreEqual(NowUtc.Date.AddDays(-UsageReportRefreshPolicy.MinDaysBack), narrow.WindowStartUtc);
            Assert.AreEqual(0, UsageReportRefreshPolicy.EnumerateSkipCandidates(narrow).Count(),
                "At the minimum look-back the whole window is still mutable, so there is nothing to skip.");
        }

        [TestMethod]
        public void UsageReports_WindowIsDrivenByTheSuppliedInstantNotTheWallClock()
        {
            // Would fail if the rule went back to reading DateTime.UtcNow itself.
            var pastInstant = new DateTime(2004, 8, 3, 0, 0, 0, DateTimeKind.Utc);
            var window = UsageReportRefreshPolicy.ResolveSkipWindow(28, pastInstant, RefreshableRecentDays, pastInstant);

            Assert.AreEqual(pastInstant.Date.AddDays(-28), window.WindowStartUtc);
            Assert.AreEqual(pastInstant.Date.AddDays(-3), window.SafeCutoffUtc);
        }
    }

    /// <summary>
    /// The table-name resolution behind the storage-inspector port, and the loader wiring that uses it.
    /// The two callers react differently to a missing TableAttribute - the index question throws, the
    /// maintenance silently skips - and that asymmetry is existing behaviour worth pinning at the level
    /// that could actually regress.
    /// </summary>
    [TestClass]
    public class UsageReportTableNameTests
    {
        private class NoTableAttribute { }

        [TestMethod]
        public void UsageReports_TableNameIsSchemaQualified()
        {
            // Every report entity in this assembly declares [Table(...)]; pick one to prove the attribute is
            // what is read, and that the default schema is applied when the attribute states none.
            var resolved = UsageReportTableName.TryResolve(typeof(SharePointUserActivityLog));

            Assert.AreEqual("dbo.sharepoint_user_activity_log", resolved);
        }

        [TestMethod]
        public void UsageReports_TableNameHelper_TryResolveReturnsNullWhereResolveThrows()
        {
            Assert.IsNull(UsageReportTableName.TryResolve(typeof(NoTableAttribute)));
            Assert.ThrowsException<InvalidOperationException>(() => UsageReportTableName.Resolve(typeof(NoTableAttribute)));
        }

        [TestMethod]
        public async Task UsageReports_LoaderWithoutATable_ThrowsForTheIndexQuestionButSkipsMaintenance()
        {
            // Asserted through the LOADER, not the helper: swapping which of Resolve/TryResolve each loader
            // method uses would reverse this behaviour, and a helper-only test would not notice.
            //
            // No database is reached. CompactColumnstoreAsync returns before touching the inspector, and
            // HasLeadingDateIndexAsync resolves the inspector (the injected fake) before evaluating the
            // table name, so a null context is safe for both.
            var inspector = new FakeUsageReportStorageInspector();
            var loader = new TablelessDailyActivityLoader(NullLogger.Instance) { StorageInspector = inspector };

            await loader.CompactColumnstoreAsync(null);
            Assert.AreEqual(0, inspector.CompactionsRequested.Count,
                "Maintenance for an entity with no table must be skipped silently, not attempted.");

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => loader.HasLeadingDateIndexAsync(null));
            Assert.AreEqual(0, inspector.IndexQuestionsAsked.Count);
        }

        [TestMethod]
        public async Task UsageReports_LoaderWithATable_AsksTheInspectorForThatTable()
        {
            // The other half: a normal loader must reach the inspector with its own schema-qualified name.
            var inspector = new FakeUsageReportStorageInspector(hasLeadingDateIndex: false);
            var loader = new OutlookUserActivityLoader(null, null, new UserGroupsFilterModel(null), NullLogger.Instance)
            {
                StorageInspector = inspector
            };

            Assert.IsFalse(await loader.HasLeadingDateIndexAsync(null), "The loader must return what the inspector answers.");
            await loader.CompactColumnstoreAsync(null);

            var expected = UsageReportTableName.Resolve(typeof(OutlookUsageActivityLog));
            CollectionAssert.AreEqual(new[] { expected }, inspector.IndexQuestionsAsked.ToArray());
            CollectionAssert.AreEqual(new[] { expected }, inspector.CompactionsRequested.ToArray());
        }
    }
}
