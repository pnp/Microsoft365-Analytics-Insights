using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// How the agent growth series (#645) reaches the Excel workbook: its own sheet, its definitions and
    /// caveat on "How this is calculated", and fixed keys for windows 0 and 13 on "Snapshot facts" that are
    /// blank - never zero - when a window was not measured.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionAgentGrowthWorkbookTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 7, 10, 30, 0, DateTimeKind.Utc);
        private static readonly DateTime LastSettled = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);

        private static readonly string[] GrowthKeys =
        {
            "agentGrowthActiveAgentsWindow0", "agentGrowthActiveAgentsWindow13",
            "agentGrowthAgentUsersWindow0", "agentGrowthAgentUsersWindow13",
            "agentGrowthInteractionsWindow0", "agentGrowthInteractionsWindow13",
            "agentGrowthInteractionsPerAgentUserWindow0", "agentGrowthInteractionsPerAgentUserWindow13",
            "agentGrowthCopilotStudioBilledAgentsWindow0", "agentGrowthCopilotStudioBilledAgentsWindow13",
            "agentGrowthScope", "agentGrowthSettledThroughUtc",
        };

        [TestMethod]
        public void Workbook_AgentGrowthSheet_HasEveryWindowOldestFirst_WithUnmeasuredWindowsBlank()
        {
            var bytes = CopilotAdoptionWorkbook.Build(AnalysisWithGrowth());
            var rows = SheetRows(bytes, "Agent growth");

            var header = rows.Single(r => r.Value.TryGetValue("A", out var a) && a == "Windows ago");
            CollectionAssert.AreEqual(
                new[] { "Windows ago", "From", "To", "Active agents", "Agent users", "Agent interactions",
                        "Interactions per agent user", "Copilot Studio billed agents (separate: autonomous-run evidence)" },
                new[] { "A", "B", "C", "D", "E", "F", "G", "H" }.Select(c => header.Value[c]).ToArray());

            var data = rows.Where(r => r.Key > header.Key && r.Key <= header.Key + 14).Select(r => r.Value).ToList();
            Assert.AreEqual(14, data.Count);
            CollectionAssert.AreEqual(
                Enumerable.Range(0, 14).Reverse().Select(n => n.ToString()).ToList(),
                data.Select(r => r["A"]).ToList(),
                "Oldest first, so the chart reads left to right.");

            var latest = data.Last();
            Assert.AreEqual("12", latest["D"]);
            Assert.AreEqual("40", latest["E"]);
            Assert.AreEqual("100", latest["F"]);
            Assert.AreEqual("2.5", latest["G"]);
            Assert.AreEqual("2", latest["H"]);

            var yearAgo = data.First();
            Assert.AreEqual("3", yearAgo["D"]);
            Assert.IsFalse(yearAgo.ContainsKey("H"), "No billing rows a year ago: the cell is blank, not 0.");

            var unmeasured = data.Single(r => r["A"] == "6");
            foreach (var column in new[] { "D", "E", "F", "G", "H" })
            {
                Assert.IsFalse(unmeasured.ContainsKey(column),
                    $"Window 6 was not measured, so column {column} must be blank - a zero would read as a collapse.");
            }

            Assert.IsTrue(unmeasured.ContainsKey("B") && unmeasured.ContainsKey("C"), "Its dates are still known.");
        }

        [TestMethod]
        public void Workbook_AgentGrowthSheet_NamesItsSourcesItsScopeAndTheCaveat()
        {
            var text = string.Join("\n", SheetCells(CopilotAdoptionWorkbook.Build(AnalysisWithGrowth()), "Agent growth"));

            StringAssert.Contains(text, "ending on 2026-10-04, the last settled day: 3 days before the analysis ran");
            StringAssert.Contains(text, "Counts agents.");
            StringAssert.Contains(text, "the Copilot audit log (copilot_chats)");
            StringAssert.Contains(text, "the Power Platform billing import (copilot_studio_credit_daily)");
            StringAssert.Contains(text, "never added to the active agents");
            StringAssert.Contains(text, "This compares the tenant with itself.");
            StringAssert.Contains(text, "not a target, and not a benchmark for one organisation");
            StringAssert.Contains(text, "The Copilot audit history starts on 2025-01-15.");
        }

        [TestMethod]
        public void Workbook_MethodSheet_DefinesTheGrowthSeriesAndItsAutonomousEvidence()
        {
            var cells = SheetCells(CopilotAdoptionWorkbook.Build(AnalysisWithGrowth()), "How this is calculated");

            var growth = cells[cells.IndexOf("Agent growth (year on year)") + 1];
            StringAssert.Contains(growth, "14 consecutive, closed 28-day windows");
            StringAssert.Contains(growth, "ending on the last settled day - 3 days before the analysis ran");
            StringAssert.Contains(growth, "window 13 starts 364 days earlier");
            StringAssert.Contains(growth, "It counts agents:");
            StringAssert.Contains(growth, "at least one day in it");
            StringAssert.Contains(growth, "never zero");
            StringAssert.Contains(growth, "removing old Copilot interactions from the database blanks the windows they fed");
            StringAssert.Contains(growth, "not a target");

            var evidence = cells[cells.IndexOf("Autonomous-run evidence") + 1];
            StringAssert.Contains(evidence, "never added to it");
            StringAssert.Contains(evidence, "copilot_studio_credit_daily");
            StringAssert.Contains(evidence, "evidence that agents ran, not a count of autonomous runs");
            // The Cowork guard (#692): the product names Microsoft's Cowork report only as something it does not import.
            StringAssert.Contains(evidence, "Microsoft 365 admin centre (Copilot > Cowork > Usage)");
            StringAssert.Contains(evidence, "does not import");
        }

        [TestMethod]
        public void Workbook_SnapshotFacts_CarryFixedKeysForWindows0And13_BlankNotZeroWhenUnknown()
        {
            var measured = SnapshotFacts(CopilotAdoptionWorkbook.Build(AnalysisWithGrowth()));
            Assert.AreEqual("12", measured["agentGrowthActiveAgentsWindow0"]);
            Assert.AreEqual("3", measured["agentGrowthActiveAgentsWindow13"]);
            Assert.AreEqual("40", measured["agentGrowthAgentUsersWindow0"]);
            Assert.AreEqual("100", measured["agentGrowthInteractionsWindow0"]);
            Assert.AreEqual("2", measured["agentGrowthCopilotStudioBilledAgentsWindow0"]);
            Assert.AreEqual(string.Empty, measured["agentGrowthCopilotStudioBilledAgentsWindow13"],
                "No billing rows a year ago: blank.");
            Assert.AreEqual(AgentGrowthScopes.AllAgents, measured["agentGrowthScope"]);

            var notComputed = AnalysisWithGrowth();
            notComputed.Summary.Agents.Growth = new List<AgentGrowthWindow>();
            var unknown = SnapshotFacts(CopilotAdoptionWorkbook.Build(notComputed));

            foreach (var key in GrowthKeys.Where(k => k != "agentGrowthScope"))
            {
                Assert.IsTrue(unknown.ContainsKey(key), $"{key} must be on Snapshot facts whether or not the series was computed.");
                Assert.AreEqual(string.Empty, unknown[key], $"{key} must be blank when the series was not computed, never 0.");
            }

            CollectionAssert.AreEqual(measured.Keys.ToList(), unknown.Keys.ToList(),
                "The key list is a function of the model, not of the data: two exports must line up row for row.");
            CollectionAssert.DoesNotContain(WorkbookSheetNames(CopilotAdoptionWorkbook.Build(notComputed)), "Agent growth",
                "No series, no sheet - but the Snapshot facts keys stay.");
        }

        #region Fixture

        /// <summary>A small, synthetic analysis whose agent growth series has measured, unmeasured and billed windows.</summary>
        private static CopilotAdoptionAnalysis AnalysisWithGrowth()
        {
            var options = CopilotAdoptionOptions.Default;
            var analysis = new CopilotAdoptionAnalysis();
            var summary = analysis.Summary;
            summary.GeneratedUtc = Now;
            summary.WindowDays = options.WindowDays;
            summary.FromUtc = Now.AddDays(-options.WindowDays);
            summary.ToUtc = Now;
            summary.Options = options;
            summary.DataSources.AuditAvailable = true;

            var usage = Enumerable.Range(0, CopilotAdoptionAgentGrowth.WindowCount)
                .Select(n => new AgentGrowthQueryRow
                {
                    WindowsAgo = n,
                    ActiveAgents = n == 0 ? 12 : n == 13 ? 3 : 5,
                    AgentUsers = n == 0 ? 40 : n == 13 ? 8 : 10,
                    AgentInteractions = n == 0 ? 100 : n == 13 ? 20 : 30,
                    HasCopilotData = n != 6,
                    FirstCopilotInteractionUtc = new DateTime(2025, 1, 15, 9, 0, 0),
                })
                .ToList();
            var billing = new List<AgentGrowthBillingRow> { new AgentGrowthBillingRow { WindowsAgo = 0, BilledAgents = 2 } };

            summary.Agents.Growth = CopilotAdoptionAgentGrowth.Build(LastSettled, usage, billing);
            summary.Agents.GrowthAuditHistoryStartUtc = new DateTime(2025, 1, 15, 9, 0, 0);
            return analysis;
        }

        /// <summary>Snapshot facts as key to value text, in sheet order.</summary>
        private static Dictionary<string, string> SnapshotFacts(byte[] bytes)
        {
            var facts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in SheetRows(bytes, "Snapshot facts").Values)
            {
                if (!row.TryGetValue("A", out var key) || !key.StartsWith("agentGrowth", StringComparison.Ordinal)) continue;
                facts[key] = row.TryGetValue("B", out var value) ? value : string.Empty;
            }

            return facts;
        }

        /// <summary>Every row of a sheet as column letter to cell text. A blank cell is absent from its row.</summary>
        private static SortedDictionary<int, Dictionary<string, string>> SheetRows(byte[] bytes, string sheetName)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var rows = new SortedDictionary<int, Dictionary<string, string>>();

            foreach (var cell in SheetDocument(bytes, sheetName).Descendants(ns + "c"))
            {
                var reference = (string)cell.Attribute("r");
                var column = new string(reference.TakeWhile(char.IsLetter).ToArray());
                var row = int.Parse(reference.Substring(column.Length));
                if (!rows.TryGetValue(row, out var cells)) rows[row] = cells = new Dictionary<string, string>();
                cells[column] = string.Concat(cell.Descendants().Where(d => !d.HasElements).Select(d => d.Value));
            }

            return rows;
        }

        private static List<string> SheetCells(byte[] bytes, string sheetName)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            return SheetDocument(bytes, sheetName).Descendants(ns + "c")
                .Select(c => string.Concat(c.Descendants().Where(d => !d.HasElements).Select(d => d.Value)))
                .ToList();
        }

        private static XDocument SheetDocument(byte[] bytes, string sheetName)
        {
            var index = WorkbookSheetNames(bytes).IndexOf(sheetName);
            Assert.AreNotEqual(-1, index, $"Sheet '{sheetName}' is missing from the workbook.");

            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            using (var part = zip.GetEntry("xl/worksheets/sheet" + (index + 1) + ".xml").Open())
            {
                return XDocument.Load(part);
            }
        }

        private static List<string> WorkbookSheetNames(byte[] bytes)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            using (var part = zip.GetEntry("xl/workbook.xml").Open())
            {
                return XDocument.Load(part).Descendants(ns + "sheet").Select(s => (string)s.Attribute("name")).ToList();
            }
        }

        #endregion
    }
}
