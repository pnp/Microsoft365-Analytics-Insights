using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using Tests.FakeDataGen.Demo;

namespace Tests.UnitTests
{
    [TestClass]
    [TestCategory("DemoGenerator")]
    public class DemoPowerPlatformTests
    {
        [TestMethod]
        public void PowerPlatform_HasCoherentResourcesUsageSharingAndHistory()
        {
            var options = DemoGeneratorTests.Options("--areas", "powerapps,powerautomate,powerbi,copilot-studio");
            var summary = DemoCommand.NewSummary(options);
            var capture = new DemoGeneratorTests.RecordingSink(_ => true);
            using (var sink = new CountingDemoSink(summary, capture))
                new DemoGenerator(options).Generate(sink, summary, null);
            var audit = capture.For(DemoTables.Audit).ToDictionary(r => (Guid)r[0]);
            var operations = capture.For(DemoTables.Operations).ToDictionary(r => (int)r[0], r => (string)r[1]);
            var reports = capture.For(DemoTables.PowerReports).ToDictionary(r => (int)r[0]);
            var workspaces = capture.For(DemoTables.PowerWorkspaces).Select(r => (int)r[0]).ToList();
            var environments = capture.For(DemoTables.PowerEnvironments).Select(r => (int)r[0]).ToList();
            var apps = capture.For(DemoTables.PowerApps).ToDictionary(r => (int)r[0]);
            var flows = capture.For(DemoTables.PowerFlows).ToDictionary(r => (int)r[0]);
            var bots = capture.For(DemoTables.StudioBots).ToDictionary(r => (int)r[0]);
            foreach (var row in apps.Values.Concat(flows.Values).Concat(bots.Values))
                CollectionAssert.Contains(environments, (int)row[3]);
            foreach (var row in capture.For(DemoTables.PowerAppEvents))
            {
                Assert.IsTrue(apps.ContainsKey((int)row[1]));
                Assert.IsTrue(audit.ContainsKey((Guid)row[0]));
            }
            foreach (var row in capture.For(DemoTables.PowerFlowEvents))
            {
                Assert.IsTrue(flows.ContainsKey((int)row[1]));
                Assert.IsNull(row[2], "Flow audit administration is not flow-run telemetry.");
                Assert.IsTrue(audit.ContainsKey((Guid)row[0]));
            }
            foreach (var row in capture.For(DemoTables.PowerBIEvents))
            {
                CollectionAssert.Contains(workspaces, (int)row[1]);
                Assert.AreEqual(reports[(int)row[2]][4], row[1]);
                Assert.AreEqual("ViewReport", operations[(int)audit[(Guid)row[0]][2]],
                    "Do not generate operations the production loader drops.");
            }
            foreach (var row in capture.For(DemoTables.StudioEvents))
            {
                Assert.IsTrue(bots.ContainsKey((int)row[1]));
                StringAssert.StartsWith(operations[(int)audit[(Guid)row[0]][2]], "BotUpdateOperation-");
            }
            foreach (var table in new[] { DemoTables.PowerAppShares, DemoTables.PowerFlowShares })
            {
                var shares = capture.For(table);
                Assert.IsTrue(shares.Count > 0, table.Name);
                Assert.AreEqual(shares.Count, shares.Select(r => r[0] + "|" + r[2]).Distinct().Count());
                Assert.IsTrue(shares.All(r => (int)r[2] >= 1 && (int)r[2] <= options.Users));
            }
            foreach (var table in new[] { DemoTables.PowerAppConnectors, DemoTables.PowerFlowConnectors })
            {
                var bridges = capture.For(table);
                Assert.IsTrue(bridges.Count > 0);
                Assert.AreEqual(bridges.Count, bridges.Select(r => r[0] + "|" + r[1]).Distinct().Count());
            }
            var times = audit.Values.Select(r => (DateTime)r[3]).ToList();
            Assert.IsTrue(times.All(t => t >= options.Start && t < options.AsOf && DemoCalendar.IsWeekday(t)));
            Assert.IsTrue((times.Max() - times.Min()).TotalDays >= 21, "Examples must span history, not only the last day.");
            Assert.IsTrue(reports.Values.Select(r => r[3]).Distinct().Count() > 1);
            Assert.IsFalse(summary.Rows.ContainsKey(DemoTables.Teams.Name));
            Assert.IsFalse(summary.Rows.ContainsKey(DemoTables.Chats.Name));
        }
    }
}
