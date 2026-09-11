using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using Tests.FakeDataGen.Demo;

namespace Tests.UnitTests
{
    [TestClass]
    [TestCategory("DemoGenerator")]
    public class DemoAreaTests
    {
        [TestMethod]
        public void Areas_AreStrictCanonicalAndIncludedInFingerprint()
        {
            foreach (var value in new[] { "", "teams,", "unknown", "teams,teams", "all,teams" })
                Assert.ThrowsException<ArgumentException>(() => DemoAreas.Parse(value));
            Assert.AreEqual(DemoArea.All, DemoAreas.Parse("all"));
            Assert.AreEqual(DemoArea.Teams | DemoArea.PowerBI, DemoAreas.Parse(" POWERBI,Teams "));
            var first = DemoGeneratorTests.Options("--areas", "powerbi,teams");
            var second = DemoGeneratorTests.Options("--areas", "teams,powerbi");
            Assert.AreEqual(first.Fingerprint, second.Fingerprint);
            Assert.AreNotEqual(first.Fingerprint, DemoGeneratorTests.Options("--areas", "teams").Fingerprint);
            Assert.IsFalse(first.HasAllDailyWorkloads);
            Assert.IsTrue(DemoGeneratorTests.Options().HasAllDailyWorkloads);
            Assert.AreEqual(DemoArea.All, DemoAreas.Catalogue.Aggregate((DemoArea)0, (flags, area) => flags | area.Area));
            Assert.ThrowsException<ArgumentException>(() => DemoOptions.Parse(new[]
                { "--preview", "--users", "1000000", "--days", "730", "--areas", "web" }, DateTime.UtcNow));
        }

        [TestMethod]
        public void IndividualMenu_BuildsSelectedAreaForNewOrExistingDatabase()
        {
            var newArgs = DemoInteractive.BuildArgs(new Prompt(), new DateTime(2026, 9, 1), DemoArea.Teams);
            var fresh = DemoOptions.Parse(newArgs, DateTime.UtcNow);
            Assert.AreEqual(DemoArea.Teams, fresh.Areas);
            Assert.IsNotNull(fresh.Database);
            Assert.IsNull(DemoInteractive.BuildArgs(new Prompt(), DateTime.UtcNow, DemoArea.Teams, existing: true),
                "End of input must never confirm an append.");
            var existingArgs = DemoInteractive.BuildArgs(new Prompt("n", "n", "y"),
                DateTime.UtcNow, DemoArea.PowerBI, existing: true);
            var existing = DemoOptions.Parse(existingArgs, DateTime.UtcNow, existingTarget: true);
            Assert.AreEqual(DemoArea.PowerBI, existing.Areas);
            Assert.IsNull(existing.Database);
            Assert.IsFalse(existing.CompileProfiles);
            Assert.ThrowsException<ArgumentException>(() => DemoOptions.Parse(newArgs, DateTime.UtcNow, existingTarget: true));
        }

        [TestMethod]
        public void AppendCommand_RequiresExplicitAcknowledgementBeforeAnyDatabaseAccess()
        {
            Assert.AreEqual(2, DemoCommand.RunExisting(new string[0], "Server=contoso.example;Database=Example"));
            Assert.AreEqual(2, DemoCommand.RunExisting(new[] { "--confirm-existing" }, null));
            Assert.AreEqual(2, DemoCommand.RunExisting(new[] { "--confirm-existing", "--confirm-existing" },
                "Server=contoso.example;Database=Example"));
        }

        [DataTestMethod]
        [DataRow("directory", "users")]
        [DataRow("copilot", "copilot_chats")]
        [DataRow("copilot-history", "copilot_interactions")]
        [DataRow("teams", "call_records")]
        [DataRow("outlook", "outlook_user_activity_log")]
        [DataRow("sent-email", "sent_emails")]
        [DataRow("sharepoint", "event_meta_sharepoint")]
        [DataRow("web", "hits")]
        [DataRow("onedrive", "onedrive_user_activity_log")]
        [DataRow("engage", "yammer_user_activity_log")]
        [DataRow("office", "platform_user_activity_log")]
        [DataRow("powerapps", "event_meta_power_app")]
        [DataRow("powerautomate", "event_meta_power_automate_flow")]
        [DataRow("powerbi", "event_meta_power_bi")]
        [DataRow("copilot-studio", "event_meta_copilot_studio")]
        [DataRow("dlp", "copilot_dlp_events")]
        public void EveryIndividualArea_ProducesReportFactsAndUsesSameRowsAsFullDemo(string key, string factTable)
        {
            var options = DemoGeneratorTests.Options("--areas", key);
            var summary = DemoCommand.NewSummary(options);
            var selected = new DemoGeneratorTests.RecordingSink(t => t.Name == factTable);
            using (var sink = new CountingDemoSink(summary, selected))
                new DemoGenerator(options).Generate(sink, summary, null);
            Assert.IsTrue(summary.Rows.TryGetValue(factTable, out long rows) && rows > 0,
                key + " must generate usable report facts, not just lookup tables.");

            var full = new DemoGeneratorTests.RecordingSink(t => t.Name == factTable);
            var allOptions = DemoGeneratorTests.Options();
            var allSummary = DemoCommand.NewSummary(allOptions);
            using (var sink = new CountingDemoSink(allSummary, full))
                new DemoGenerator(allOptions).Generate(sink, allSummary, null);
            Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(full.Rows),
                Newtonsoft.Json.JsonConvert.SerializeObject(selected.Rows),
                "Selecting an area must not use a separate, lower-quality generator.");
        }

        [TestMethod]
        public void PortalSettings_AreLimitedToSelectedSources()
        {
            Assert.AreEqual("GraphUsersMetadata=True;ImportPowerPlatform=True",
                DemoPortalReadiness.ImportSettings(DemoArea.PowerBI));
            Assert.AreEqual("GraphUsersMetadata=True;GraphUsageReports=True;Calls=True;GraphTeams=True",
                DemoPortalReadiness.ImportSettings(DemoArea.Teams));
            Assert.AreEqual("GraphUsersMetadata=True;CopilotInteractionHistory=True",
                DemoPortalReadiness.ImportSettings(DemoArea.CopilotHistory));
            Assert.AreEqual("GraphUsersMetadata=True;Copilot=True;ImportDlp=True",
                DemoPortalReadiness.ImportSettings(DemoArea.Dlp));
        }

        [TestMethod]
        public void SelectingDailyWorkload_DoesNotInventOtherWorkloadFacts()
        {
            var mappings = new[]
            {
                (DemoArea.Teams, DemoTables.Teams), (DemoArea.Outlook, DemoTables.Outlook),
                (DemoArea.SharePoint, DemoTables.SharePoint), (DemoArea.OneDrive, DemoTables.OneDrive),
                (DemoArea.Engage, DemoTables.Engage), (DemoArea.Office, DemoTables.Platforms)
            };
            foreach (var mapping in mappings)
            {
                var options = DemoGeneratorTests.Options("--areas", DemoAreas.Format(mapping.Item1));
                var summary = DemoCommand.NewSummary(options);
                using (var sink = new CountingDemoSink(summary)) new DemoGenerator(options).Generate(sink, summary, null);
                Assert.AreEqual(options.Users, summary.Rows[DemoTables.Users.Name]);
                Assert.AreEqual(options.Users * (options.Days - 2), summary.Rows[mapping.Item2.Name]);
                foreach (var other in mappings.Where(m => m.Item1 != mapping.Item1))
                    Assert.IsFalse(summary.Rows.ContainsKey(other.Item2.Name), other.Item2.Name);
                Assert.IsFalse(summary.Rows.ContainsKey(DemoTables.Chats.Name));
                Assert.IsFalse(summary.Rows.ContainsKey(DemoTables.Interactions.Name));
                Assert.IsFalse(summary.Rows.ContainsKey(DemoTables.CopilotUsage.Name));
            }
        }

        private sealed class Prompt : IDemoPrompt
        {
            private readonly Queue<string> _answers;
            public Prompt(params string[] answers) { _answers = new Queue<string>(answers); }
            public void Write(string message) { }
            public string Ask(string question) => _answers.Count == 0 ? null : _answers.Dequeue();
        }
    }
}
