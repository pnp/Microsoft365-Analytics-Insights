using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Sections;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the <see cref="GraphImporter"/> orchestration loop introduced by issue #376: section
    /// selection, per-section cadence gating, and the rule that the last-run timestamp is written only when
    /// the section actually succeeded.
    ///
    /// None of these touch SQL Server, Graph, Redis or Service Bus - the sections are fakes, which is the
    /// whole point of lifting composition out into <see cref="IGraphImportSectionFactory"/>. Before this,
    /// the only way to find out whether the Teams import had been correctly skipped was to run a real import
    /// against a real tenant.
    /// </summary>
    [TestClass]
    public class GraphImportOrchestrationTests
    {
        private const string FirstSectionCadence = "first-test-section-cadence";
        private const string SecondSectionCadence = "second-test-section-cadence";
        private static readonly DateTime Now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Settings with the force flags explicitly off. Read explicitly rather than trusting the defaults:
        /// AppConfig populates itself from the local App.config, so a machine with
        /// ForceGraphMetadataImport=true would otherwise silently make every gating test pass.
        /// </summary>
        private static AppConfig GatingSettings()
        {
            return new AppConfig
            {
                ForceGraphMetadataImport = false,
                ImportJobSettings = new ImportTaskSettings(),
            };
        }

        private static GraphImporter BuildImporter(AppConfig settings, IImportLastRunStore store, IClock clock, params IGraphImportSection[] sections)
        {
            return new GraphImporter(AnalyticsLogger.ConsoleOnlyTracer(), settings,
                new FakeGraphImportSectionFactory(sections), store, clock);
        }

        [TestMethod]
        public async Task GraphImporter_DisabledSection_IsNotRun()
        {
            var disabled = FakeGraphImportSection.Gated("Disabled section", FirstSectionCadence, 24);
            disabled.Enabled = false;
            var enabled = FakeGraphImportSection.Gated("Enabled section", SecondSectionCadence, 24);

            var store = new RecordingImportLastRunStore();
            await BuildImporter(GatingSettings(), store, new FixedClock(Now), disabled, enabled)
                .GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(0, disabled.RunCount, "A section the tenant has switched off must not run.");
            Assert.IsFalse(store.Reads.Contains(FirstSectionCadence), "A disabled section must not even be looked up in the cadence store.");
            Assert.AreEqual(1, enabled.RunCount, "Disabling one section must not stop the others.");
        }

        [TestMethod]
        public async Task GraphImporter_EnabledSectionWithinCadenceWindow_IsSkipped()
        {
            var section = FakeGraphImportSection.Gated("Gated section", FirstSectionCadence, 24);
            var store = new RecordingImportLastRunStore().Seed(FirstSectionCadence, Now.AddHours(-23));

            await BuildImporter(GatingSettings(), store, new FixedClock(Now), section)
                .GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(0, section.RunCount, "23h into a 24h window the section must be skipped.");
            Assert.AreEqual(0, store.Writes.Count, "A skipped section must not re-stamp its last-run time.");
        }

        [TestMethod]
        public async Task GraphImporter_EnabledSectionOutsideCadenceWindow_IsRun()
        {
            var section = FakeGraphImportSection.Gated("Gated section", FirstSectionCadence, 24);
            var store = new RecordingImportLastRunStore().Seed(FirstSectionCadence, Now.AddHours(-25));

            await BuildImporter(GatingSettings(), store, new FixedClock(Now), section)
                .GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(1, section.RunCount, "25h into a 24h window the section is due.");
        }

        [TestMethod]
        public async Task GraphImporter_ForceGraphMetadataImport_BypassesCadenceGateForEverySection()
        {
            var first = FakeGraphImportSection.Gated("First", FirstSectionCadence, 24);
            var second = FakeGraphImportSection.Gated("Second", SecondSectionCadence, 24);

            // Both ran a minute ago, so without the force flag neither would be due.
            var store = new RecordingImportLastRunStore()
                .Seed(FirstSectionCadence, Now.AddMinutes(-1))
                .Seed(SecondSectionCadence, Now.AddMinutes(-1));

            var settings = GatingSettings();
            settings.ForceGraphMetadataImport = true;

            await BuildImporter(settings, store, new FixedClock(Now), first, second).GetAndSaveAllGraphData(settings);

            Assert.AreEqual(1, first.RunCount, "ForceGraphMetadataImport must bypass the gate.");
            Assert.AreEqual(1, second.RunCount, "ForceGraphMetadataImport must bypass the gate for EVERY section, not just the first.");
            Assert.AreEqual(2, store.Writes.Count, "A forced run still re-stamps the gate, so the next cycle is throttled normally again.");
        }

        [TestMethod]
        public async Task GraphImporter_IntervalHoursZero_RunsEveryCycleAndRecordsNoLastRunTime()
        {
            var section = FakeGraphImportSection.Gated("Ungated by interval", FirstSectionCadence, 0);

            // Seeded as having run this very instant: with gating disabled that must not matter.
            var store = new RecordingImportLastRunStore().Seed(FirstSectionCadence, Now);

            var importer = BuildImporter(GatingSettings(), store, new FixedClock(Now), section);
            await importer.GetAndSaveAllGraphData(GatingSettings());
            await importer.GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(2, section.RunCount, "IntervalHours 0 disables gating, so the section runs every cycle.");
            Assert.AreEqual(0, store.Writes.Count,
                "With gating disabled there is no window to protect, so writing a last-run time is pointless cache traffic on every cycle.");
        }

        [TestMethod]
        public async Task GraphImporter_SectionReturnsFalse_DoesNotRecordLastRunTime()
        {
            var section = FakeGraphImportSection.Gated("Failing section", FirstSectionCadence, 24);
            section.Result = false;

            var store = new RecordingImportLastRunStore();
            await BuildImporter(GatingSettings(), store, new FixedClock(Now), section)
                .GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(1, section.RunCount);
            Assert.AreEqual(0, store.Writes.Count,
                "Stamping the gate after a failure would idle a broken import for a whole interval - issue #285.");
        }

        [TestMethod]
        public async Task GraphImporter_SectionReturnsFalse_StillRunsSubsequentSections()
        {
            var failing = FakeGraphImportSection.Gated("Failing section", FirstSectionCadence, 24);
            failing.Result = false;
            var later = FakeGraphImportSection.Gated("Later section", SecondSectionCadence, 24);

            await BuildImporter(GatingSettings(), new RecordingImportLastRunStore(), new FixedClock(Now), failing, later)
                .GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(1, later.RunCount,
                "A section reporting failure must not unwind the loop - that is why these sections return bool instead of throwing.");
        }

        [TestMethod]
        public async Task GraphImporter_SectionThrows_UnwindsAndSkipsTheSectionsAfterIt()
        {
            // Documents CURRENT behaviour rather than the behaviour #376 proposed. GraphImporter has never
            // isolated a throwing section - ProgramTasks.GetGraphTeamsAndUserData catches only ODataError -
            // and adding isolation here would quietly downgrade a hard failure to a logged warning. That is a
            // behavioural change and belongs in its own issue; this test pins today's contract so the change
            // cannot happen by accident.
            var throwing = FakeGraphImportSection.Gated("Throwing section", FirstSectionCadence, 24);
            throwing.FailWith = new InvalidOperationException("section blew up");
            var later = FakeGraphImportSection.Gated("Later section", SecondSectionCadence, 24);

            var store = new RecordingImportLastRunStore();
            var importer = BuildImporter(GatingSettings(), store, new FixedClock(Now), throwing, later);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => importer.GetAndSaveAllGraphData(GatingSettings()));

            Assert.AreEqual(1, throwing.RunCount);
            Assert.AreEqual(0, later.RunCount, "The exception unwinds out of the loop, so later sections are skipped this cycle.");
            Assert.AreEqual(0, store.Writes.Count, "A section that threw must not be recorded as having run.");
        }

        [TestMethod]
        public async Task GraphImporter_SectionSucceeds_RecordsLastRunTimeFromInjectedClock()
        {
            var section = FakeGraphImportSection.Gated("Gated section", FirstSectionCadence, 24);
            var store = new RecordingImportLastRunStore();

            await BuildImporter(GatingSettings(), store, new FixedClock(Now), section)
                .GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(1, store.Writes.Count);
            Assert.AreEqual(FirstSectionCadence, store.Writes[0].Key);
            Assert.AreEqual(Now, store.Writes[0].Value, "The stamp must come from the injected clock, not from wall time.");
        }

        [TestMethod]
        public async Task GraphImporter_CadenceGate_UsesInjectedClock_NotWallClock()
        {
            // Named in issue #368 but never written, because GraphImporter could not be constructed without a
            // Graph client until #376. The injected clock is set 30 days in the PAST, and each section is
            // seeded so that wall time and the injected clock disagree about whether it is due - so an
            // implementation that read DateTime.UtcNow would get BOTH answers wrong, in both directions.
            var injectedNow = DateTime.UtcNow.AddDays(-30);

            var due = FakeGraphImportSection.Gated("Due by the injected clock", FirstSectionCadence, 24);
            var notDue = FakeGraphImportSection.Gated("Not due by the injected clock", SecondSectionCadence, 24);

            var store = new RecordingImportLastRunStore()
                // 25h before the injected now (due), but ~31 days before wall time - which would also be due,
                // so this one only proves the loop still runs things.
                .Seed(FirstSectionCadence, injectedNow.AddHours(-25))
                // 1h before the injected now (NOT due), but ~30 days before wall time, which WOULD be due.
                // This is the discriminating case: reading DateTime.UtcNow here runs a section that must be
                // skipped, re-crawling a whole tenant's Teams every cycle.
                .Seed(SecondSectionCadence, injectedNow.AddHours(-1));

            await BuildImporter(GatingSettings(), store, new FixedClock(injectedNow), due, notDue)
                .GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(1, due.RunCount, "25h before the injected 'now' is outside a 24h window.");
            Assert.AreEqual(0, notDue.RunCount,
                "1h before the injected 'now' is inside a 24h window - wall-clock time says otherwise, and must not be consulted.");

            Assert.AreEqual(1, store.Writes.Count);
            Assert.AreEqual(injectedNow, store.Writes[0].Value, "The recorded run time must be the injected instant, not wall time.");
        }

        [TestMethod]
        public async Task GraphImporter_LastRunStoreReadsFailOpenToNull_SectionStillRuns()
        {
            // RedisImportLastRunStore returns null when the cache is unreachable, deliberately, so a cache
            // blip can never skip an import. This pins the orchestrator's half of that contract.
            var recentlyRan = FakeGraphImportSection.Gated("Gated section", FirstSectionCadence, 24);
            var workingStore = new RecordingImportLastRunStore().Seed(FirstSectionCadence, Now.AddMinutes(-1));

            await BuildImporter(GatingSettings(), workingStore, new FixedClock(Now), recentlyRan)
                .GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(0, recentlyRan.RunCount, "Control: with a working store, a section that just ran is skipped.");

            var sameSection = FakeGraphImportSection.Gated("Gated section", FirstSectionCadence, 24);
            var failOpenStore = new RecordingImportLastRunStore { ReadsAlwaysReturnNull = true, WritesAreSwallowed = true };
            failOpenStore.Seed(FirstSectionCadence, Now.AddMinutes(-1));

            await BuildImporter(GatingSettings(), failOpenStore, new FixedClock(Now), sameSection)
                .GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(1, sameSection.RunCount, "With the same seeded timestamp but an unreachable cache, the import must still run.");
        }

        [TestMethod]
        public async Task GraphImporter_UngatedSection_RunsEveryCycleAndNeverTouchesTheLastRunStore()
        {
            // The activity/usage-report phase is the real case: it owns its own once-a-day throttle via
            // ISingleDateStore, so routing it through the cadence store would double-gate it.
            var section = FakeGraphImportSection.Ungated("Usage reports");
            var store = new RecordingImportLastRunStore();

            var importer = BuildImporter(GatingSettings(), store, new FixedClock(Now), section);
            await importer.GetAndSaveAllGraphData(GatingSettings());
            await importer.GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(2, section.RunCount);
            Assert.AreEqual(0, store.Reads.Count, "A section with no cadence key must not be looked up in the cadence store.");
            Assert.AreEqual(0, store.Writes.Count, "...nor written to it.");
        }

        [TestMethod]
        public async Task GraphImporter_UngatedSectionReturnsFalse_StillRunsSubsequentSections()
        {
            // "Throttled, nothing imported" is the ordinary answer from the usage-report phase, and it must
            // not stop the Copilot / Teams / sent-email sections that come after it.
            var throttled = FakeGraphImportSection.Ungated("Usage reports");
            throttled.Result = false;
            var later = FakeGraphImportSection.Gated("Teams import", FirstSectionCadence, 24);

            await BuildImporter(GatingSettings(), new RecordingImportLastRunStore(), new FixedClock(Now), throttled, later)
                .GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(1, throttled.RunCount);
            Assert.AreEqual(1, later.RunCount);
        }

        [TestMethod]
        public async Task GraphImporter_RunsSectionsInFactoryOrder()
        {
            // Order is behaviour: the two cheap tenant-aggregate Copilot reports are deliberately ahead of the
            // per-user one, and user metadata is ahead of everything that joins to the users table.
            var order = new List<string>();
            var sections = new[] { "first", "second", "third" }
                .Select(name =>
                {
                    var s = FakeGraphImportSection.Ungated(name);
                    s.OnRun = () => order.Add(name);
                    return s;
                })
                .ToArray();

            await BuildImporter(GatingSettings(), new RecordingImportLastRunStore(), new FixedClock(Now), sections)
                .GetAndSaveAllGraphData(GatingSettings());

            CollectionAssert.AreEqual(new[] { "first", "second", "third" }, order,
                "Sections must run in the order the factory returns them.");
        }

        [TestMethod]
        public async Task GraphImporter_SelectsSectionsUsingTheSettingsPassedToGetAndSaveAllGraphData()
        {
            // GetAndSaveAllGraphData takes an AppConfig argument AND the class holds one. In production they
            // are the same object, so a refactor that quietly started reading the field instead would look
            // fine in every real run - and change which flags select the sections for anyone who does not
            // pass the same instance.
            var section = FakeGraphImportSection.Ungated("Any section");
            var factory = new FakeGraphImportSectionFactory(section);

            var fieldSettings = GatingSettings();
            var argumentSettings = GatingSettings();
            Assert.AreNotSame(fieldSettings, argumentSettings, "Sanity: the two settings objects must be distinct for this test to mean anything.");

            var importer = new GraphImporter(AnalyticsLogger.ConsoleOnlyTracer(), fieldSettings, factory, new RecordingImportLastRunStore(), new FixedClock(Now));
            await importer.GetAndSaveAllGraphData(argumentSettings);

            Assert.AreSame(argumentSettings, factory.LastSettingsArgument, "The factory must be given the per-cycle settings argument.");
            Assert.AreSame(argumentSettings.ImportJobSettings, section.LastEnabledCheckArgument, "Section selection must use the per-cycle settings argument.");
        }

        [TestMethod]
        public async Task GraphImporter_BuildsItsSectionsOncePerCycle()
        {
            // Sections hold per-cycle state (the shared Graph client and user-group caches), so they must be
            // rebuilt each cycle rather than cached on the importer.
            var factory = new FakeGraphImportSectionFactory(FakeGraphImportSection.Ungated("Any section"));
            var importer = new GraphImporter(AnalyticsLogger.ConsoleOnlyTracer(), GatingSettings(), factory,
                new RecordingImportLastRunStore(), new FixedClock(Now));

            await importer.GetAndSaveAllGraphData(GatingSettings());
            await importer.GetAndSaveAllGraphData(GatingSettings());

            Assert.AreEqual(2, factory.CreateSectionsCallCount);
        }
    }
}
