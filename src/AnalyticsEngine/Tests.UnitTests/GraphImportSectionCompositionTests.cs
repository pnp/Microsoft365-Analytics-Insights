using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Sections;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace Tests.UnitTests
{
    /// <summary>
    /// Pins what <see cref="ProductionGraphImportSectionFactory"/> composes: which sections exist, in what
    /// order, which <c>ImportJobSettings</c> flag switches each one on, which cadence key and interval it
    /// carries, and the exact "Skipping ..." line an operator greps for.
    ///
    /// This is the evidence that lifting the section bodies out of <c>GraphImporter</c> (issue #376) changed
    /// nothing an operator can observe. It runs offline: the factory builds a Graph client and user-group
    /// cache but neither touches the network until a call is actually made, and the section bodies are
    /// lambdas that are never invoked here.
    /// </summary>
    [TestClass]
    public class GraphImportSectionCompositionTests
    {
        private static AppConfig SettingsWithDistinctIntervals()
        {
            // Deliberately distinct so a section reading the wrong interval property fails rather than
            // matching by coincidence.
            return new AppConfig
            {
                ImportJobSettings = new ImportTaskSettings(),
                GraphMetadataImportIntervalHours = 11,
                GraphCopilotUsageReportsIntervalHours = 22,
                GraphTeamsImportIntervalHours = 33,
                CopilotInteractionHistoryIntervalHours = 44,
                UserGroupsFilter = "Pilot Group;Καλημέρα κόσμε",
                DaysBeforeNowToDownload = 9,
            };
        }

        private static ProductionGraphImportSectionFactory BuildFactory(AppConfig settings, ActivityReportsImport activityReports)
        {
            return new ProductionGraphImportSectionFactory(
                AnalyticsLogger.ConsoleOnlyTracer(),
                settings,
                graphAppIndentityOAuthContext: null,
                graphClient: null,
                sentEmailMailboxSkipList: null,
                activityReportsImport: activityReports,
                dbContextFactory: null,
                clock: null);
        }

        private static ActivityReportsImport NeverCalled()
        {
            return (days, client, cache, filter) => throw new AssertFailedException("The activity-report phase must not run while sections are merely being composed.");
        }

        [TestMethod]
        public void ProductionSections_AreTheSixSectionsInTheOriginalOrder()
        {
            var sections = BuildFactory(SettingsWithDistinctIntervals(), NeverCalled())
                .CreateSections(SettingsWithDistinctIntervals());

            CollectionAssert.AreEqual(
                new[]
                {
                    "User metadata refresh",
                    "Usage reports",
                    "Copilot usage reports",
                    "Teams import",
                    "Sent emails import",
                    "Copilot interaction history import",
                },
                sections.Select(s => s.Name).ToArray(),
                "Section order is behaviour: user metadata runs before everything that joins to the users table, "
                + "and the cheap tenant-aggregate Copilot reports run before the per-user one.");
        }

        [TestMethod]
        public void ProductionSections_KeepTheOriginalSkippedMessages()
        {
            // These lines are what an operator greps the WebJob log for when an import "isn't running".
            var expected = new Dictionary<string, string>
            {
                { "User metadata refresh", "Skipping user metadata import" },
                { "Usage reports", "Skipping usage reports import" },
                { "Copilot usage reports", "Skipping Graph Copilot usage reports import" },
                { "Teams import", "Skipping Teams import" },
                { "Sent emails import", "Skipping sent emails import" },
                { "Copilot interaction history import", "Skipping Copilot interaction history import" },
            };

            foreach (var section in BuildFactory(SettingsWithDistinctIntervals(), NeverCalled()).CreateSections(SettingsWithDistinctIntervals()))
            {
                Assert.AreEqual(expected[section.Name], section.DisabledMessage, $"Wrong 'skipping' message for {section.Name}.");
            }
        }

        [TestMethod]
        public void ProductionSections_CarryTheOriginalCadenceKeysAndIntervals()
        {
            var settings = SettingsWithDistinctIntervals();
            var sections = BuildFactory(settings, NeverCalled()).CreateSections(settings).ToDictionary(s => s.Name);

            AssertGated(sections["User metadata refresh"], "GraphUsersMetadataLastImported", 11);
            AssertGated(sections["Copilot usage reports"], "GraphCopilotUsageReportsLastImported", 22);
            AssertGated(sections["Teams import"], "GraphTeamsLastImported", 33);
            AssertGated(sections["Copilot interaction history import"], "CopilotInteractionHistoryLastImported", 44);

            // The activity/usage-report phase throttles itself via ISingleDateStore, and the sent-email
            // import has never been gated at all. Giving either a cadence key would double-gate it.
            AssertUngated(sections["Usage reports"]);
            AssertUngated(sections["Sent emails import"]);
        }

        private static void AssertGated(IGraphImportSection section, string expectedKey, int expectedIntervalHours)
        {
            Assert.AreEqual(expectedKey, section.CadenceKey, $"{section.Name} must keep its Redis cadence key - operators clear these by hand.");
            Assert.AreEqual(expectedIntervalHours, section.IntervalHours, $"{section.Name} is reading the wrong interval setting.");
        }

        private static void AssertUngated(IGraphImportSection section)
        {
            Assert.IsNull(section.CadenceKey, $"{section.Name} must not be cadence-gated.");
        }

        [TestMethod]
        public void ProductionSections_EachMapToExactlyOneImportSettingFlag()
        {
            // One flag at a time, deliberately: turning several on at once would let two crossed mappings
            // both read as correct.
            var flags = new List<Tuple<string, Action<ImportTaskSettings>>>
            {
                Tuple.Create<string, Action<ImportTaskSettings>>("User metadata refresh", s => s.GraphUsersMetadata = true),
                Tuple.Create<string, Action<ImportTaskSettings>>("Usage reports", s => s.GraphUsageReports = true),
                Tuple.Create<string, Action<ImportTaskSettings>>("Copilot usage reports", s => s.GraphCopilotUsageReports = true),
                Tuple.Create<string, Action<ImportTaskSettings>>("Teams import", s => s.GraphTeams = true),
                Tuple.Create<string, Action<ImportTaskSettings>>("Sent emails import", s => s.SentEmails = true),
                Tuple.Create<string, Action<ImportTaskSettings>>("Copilot interaction history import", s => s.CopilotInteractionHistory = true),
            };

            var sections = BuildFactory(SettingsWithDistinctIntervals(), NeverCalled()).CreateSections(SettingsWithDistinctIntervals());

            var allOff = new ImportTaskSettings();
            CollectionAssert.AreEqual(new string[0], sections.Where(s => s.IsEnabled(allOff)).Select(s => s.Name).ToArray(),
                "With every import flag off, no section may run.");

            foreach (var flag in flags)
            {
                var settings = new ImportTaskSettings();
                flag.Item2(settings);

                CollectionAssert.AreEqual(new[] { flag.Item1 }, sections.Where(s => s.IsEnabled(settings)).Select(s => s.Name).ToArray(),
                    $"Turning on only the flag for '{flag.Item1}' must enable exactly that section.");
            }
        }

        [TestMethod]
        public async Task UsageReportSection_PassesTheDaysWindowAndTheConfiguredUserGroupsFilter()
        {
            // Guards the one place the section list reads the per-cycle settings ARGUMENT rather than the
            // factory's own AppConfig, plus that the user-group filter is really built from configuration.
            var fieldSettings = SettingsWithDistinctIntervals();
            var argumentSettings = SettingsWithDistinctIntervals();
            argumentSettings.DaysBeforeNowToDownload = 42;
            argumentSettings.UserGroupsFilter = "ignored - the filter comes from the factory's settings";

            int observedDays = -1;
            List<string> observedPatterns = null;

            var factory = BuildFactory(fieldSettings, (days, client, cache, filter) =>
            {
                observedDays = days;
                observedPatterns = filter.Patterns;
                return Task.FromResult(true);
            });

            var usageReports = factory.CreateSections(argumentSettings).Single(s => s.Name == "Usage reports");

            Assert.AreEqual(-1, observedDays, "Composing the sections must not run any of them.");

            Assert.IsTrue(await usageReports.RunAsync());
            Assert.AreEqual(42, observedDays, "The days window comes from the settings passed to GetAndSaveAllGraphData.");
            CollectionAssert.AreEqual(new[] { "Pilot Group", "Καλημέρα κόσμε" }, observedPatterns,
                "The user-group filter is parsed from configuration - including non-Latin group names.");
        }

        [TestMethod]
        public void TeamTokenManager_HoldsNoProcessWideState()
        {
            // TeamTokenManager used to keep a static Lazy<Dictionary<O365Team, RefreshOAuthToken>> keyed by
            // object identity. O365Team overrides neither Equals nor GetHashCode and GetRefreshToken has a
            // single call site that always passes a freshly-constructed team, so the lookup could never hit -
            // it only leaked a fully-populated O365Team per team per cycle for the life of the process, and
            // raced, because the Teams crawl runs teams in parallel. Removing it is what issue #376 is for;
            // this stops it coming back.
            var statics = typeof(TeamTokenManager)
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => !f.IsLiteral)   // const is fine; a static field is not
                .Select(f => f.Name)
                .ToArray();

            CollectionAssert.AreEqual(new string[0], statics,
                "TeamTokenManager must hold no process-wide state. Found: " + string.Join(", ", statics));
        }

        [TestMethod]
        public void O365Team_UsesReferenceEquality()
        {
            // The premise of the test above: identical teams are still different dictionary keys, which is
            // why the removed cache could never produce a hit. If O365Team ever gains value equality this
            // will fail, and the reasoning in TeamTokenManager's summary will need revisiting.
            Assert.AreEqual(typeof(object), typeof(O365Team).GetMethod("GetHashCode").DeclaringType,
                "O365Team does not override GetHashCode, so it is compared by reference.");
            Assert.AreEqual(typeof(object), typeof(O365Team).GetMethod("Equals", new[] { typeof(object) }).DeclaringType,
                "O365Team does not override Equals, so it is compared by reference.");
        }
    }
}
