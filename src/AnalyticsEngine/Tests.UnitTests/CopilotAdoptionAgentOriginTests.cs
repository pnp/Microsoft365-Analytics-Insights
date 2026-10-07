using Common.Entities.Copilot;
using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Copilot Adoption agent inventory derives each agent's origin when it is read (#639), so agents first
    /// seen since Stable build 1552 - stored with is_custom_agent NULL - are labelled correctly on upgrade, with
    /// no backfill. Synthetic agents only.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionAgentOriginTests
    {
        private static readonly DateTime Now = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);

        [DataTestMethod]
        [DataRow("CopilotStudio.Declarative.00000000-0000-0000-0000-000000000000", false, "customerBuilt", DisplayName = "Documented id; stored NULL reads as false")]
        [DataRow("Copilot.M365Copilot.CoworkChat", false, "microsoft", DisplayName = "Cowork")]
        [DataRow("11111111-1111-1111-1111-111111111111", true, "customerBuilt", DisplayName = "Bare GUID the importer flagged")]
        [DataRow("11111111-1111-1111-1111-111111111111", false, "unknown", DisplayName = "Bare GUID with nothing stored")]
        [DataRow("SPO_ContosoExampleItemId_01", false, "unknown", DisplayName = "SharePoint agent")]
        public void ScoreAgent_DerivesTheOriginWhenTheInventoryIsRead(string agentKey, bool storedFlag, string expectedOrigin)
        {
            var row = CopilotAdoptionScoring.ScoreAgent(Query(agentKey, storedFlag), Now);

            Assert.AreEqual(expectedOrigin, row.Origin);
            Assert.AreEqual(expectedOrigin == CopilotAgentOriginKeys.CustomerBuilt, row.IsCustomerBuilt);
            Assert.AreEqual(row.IsCustomerBuilt, row.IsCustomAgent, "isCustomAgent is kept, and means customer-built.");
        }

        [TestMethod]
        public void FinaliseSummary_CountsCustomerBuiltAndUnknownOriginAgentsSeparately()
        {
            var analysis = AnalysisWithOneAgentOfEachOrigin();

            var estate = analysis.Summary.Agents;
            Assert.AreEqual(3, estate.KnownAgents);
            Assert.AreEqual(1, estate.CustomAgents, "customAgents counts customer-built agents only.");
            Assert.AreEqual(1, estate.UnknownOriginAgents, "An agent of unknown origin is counted on its own, not as Microsoft's.");
        }

        [TestMethod]
        public void AgentRow_SendsTheOriginKeyAndTheCompatibilityFlag()
        {
            var row = CopilotAdoptionScoring.ScoreAgent(Query("CopilotStudio.CustomEngine.00000000-0000-0000-0000-000000000000", false), Now);

            var json = JsonConvert.SerializeObject(row);

            StringAssert.Contains(json, "\"origin\":\"customerBuilt\"");
            StringAssert.Contains(json, "\"isCustomAgent\":true");
            Assert.IsFalse(json.Contains("IsCustomerBuilt"), "The convenience property is for code, not the API.");

            var estate = JsonConvert.SerializeObject(new AgentEstateSummary { UnknownOriginAgents = 4 });
            StringAssert.Contains(estate, "\"unknownOriginAgents\":4");
        }

        [TestMethod]
        public void Workbook_AgentsSheetLabelsEachAgentsOrigin()
        {
            var cells = AgentsSheetCells(CopilotAdoptionWorkbook.Build(AnalysisWithOneAgentOfEachOrigin()));

            CollectionAssert.Contains(cells, "Customer-built");
            CollectionAssert.Contains(cells, "Microsoft");
            CollectionAssert.Contains(cells, "Unknown");
            CollectionAssert.DoesNotContain(cells, "Custom", "The old label read as 'custom-engine'.");
            CollectionAssert.Contains(cells, "Customer-built agents");
            CollectionAssert.Contains(cells, "Agents of unknown origin");
        }

        private static CopilotAdoptionAnalysis AnalysisWithOneAgentOfEachOrigin()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.Options = CopilotAdoptionOptions.Default;
            analysis.Summary.GeneratedUtc = Now;
            analysis.Summary.FromUtc = Now.AddDays(-28);
            analysis.Summary.ToUtc = Now;
            analysis.Agents.Add(CopilotAdoptionScoring.ScoreAgent(Query("CopilotStudio.CustomEngine.00000000-0000-0000-0000-000000000000", false, "Contoso Helpdesk"), Now));
            analysis.Agents.Add(CopilotAdoptionScoring.ScoreAgent(Query("Copilot.M365Copilot.CoworkChat", false, "Copilot Cowork"), Now));
            analysis.Agents.Add(CopilotAdoptionScoring.ScoreAgent(Query("11111111-1111-1111-1111-111111111111", false, "Καλημέρα κόσμε"), Now));
            new CopilotAdoptionService().FinaliseSummary(analysis);
            return analysis;
        }

        private static AgentUsageQueryRow Query(string agentKey, bool storedFlag, string name = "Contoso Agent")
        {
            return new AgentUsageQueryRow
            {
                AgentId = Math.Abs(agentKey.GetHashCode()),
                Name = name,
                AgentKey = agentKey,
                IsCustomAgent = storedFlag,
                Interactions = 40,
                WindowInteractions = 20,
                Users = 8,
                LicensedUsers = 4,
                ActiveDays = 10,
                AppsUsed = 2,
                FirstUsedUtc = Now.AddDays(-200),
                LastUsedUtc = Now.AddDays(-1),
            };
        }

        /// <summary>The text of every cell on the workbook's Agents sheet.</summary>
        private static List<string> AgentsSheetCells(byte[] workbook)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using (var stream = new MemoryStream(workbook))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                List<string> names;
                using (var part = zip.GetEntry("xl/workbook.xml").Open())
                {
                    names = XDocument.Load(part).Descendants(ns + "sheet").Select(s => (string)s.Attribute("name")).ToList();
                }

                var index = names.IndexOf("Agents");
                Assert.AreNotEqual(-1, index, "The Agents sheet is missing.");

                using (var part = zip.GetEntry("xl/worksheets/sheet" + (index + 1) + ".xml").Open())
                {
                    return XDocument.Load(part).Descendants(ns + "c")
                        .Select(c => string.Concat(c.Descendants().Where(d => !d.HasElements).Select(d => d.Value)))
                        .ToList();
                }
            }
        }
    }
}
