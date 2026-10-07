using Common.Entities.CopilotAdoption;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Agent breadth and depth by department (#646), and who builds agents and how far agents reach across
    /// departments (#647) - from hand-built rows, plus the two new queries against a scratch database.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionAgentBreadthReachTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        private const int FinanceId = 1;
        private const int LegalId = 2;
        private const int GreekId = 3;
        private const int SalesId = 4;
        private const int TinyId = 5;
        private const string Greek = "Καλημέρα κόσμε";

        private static readonly Dictionary<int, string> DepartmentNames = new Dictionary<int, string>
        {
            { FinanceId, "Finance" },
            { LegalId, "Legal" },
            { GreekId, Greek },
            { SalesId, "Sales" },
            { TinyId, "Tiny" },
        };

        #region Builders

        /// <summary>A Copilot seat holder scored from the audit log - active when it has interactions.</summary>
        private static LicensedUserAdoptionRow Seat(int id, string department, long interactions = 5, string domain = "contoso.com")
        {
            var band = interactions > 0 ? AdoptionBand.Established : AdoptionBand.NeverUsed;
            var row = new LicensedUserAdoptionRow
            {
                UserId = id,
                UserPrincipalName = $"user{id}@{domain}",
                EmailDomain = domain,
                Department = department,
                AccountEnabled = true,
                AccountCreatedUtc = Now.AddDays(-120),
                TenureStartUtc = Now.AddDays(-120),
                TenureBasis = CopilotAdoptionScoring.TenureBasisAccountAge,
                DaysSinceTenureStart = 120,
                Interactions = interactions,
                ActiveDays = interactions > 0 ? 2 : 0,
                AppsUsed = interactions > 0 ? 1 : 0,
                AdoptionScore = interactions > 0 ? 50 : 0,
                Band = band,
                BandName = CopilotAdoptionScoring.BandDisplayName(band),
                SignalSource = CopilotAdoptionScoring.SignalSourceAudit,
            };
            CopilotAdoptionScoring.ApplyReclaimEligibility(row);
            row.RecommendedActionCode = CopilotAdoptionScoring.RecommendedActionCode(row);
            row.RecommendedActionLabel = CopilotAdoptionScoring.ActionLabel(row.RecommendedActionCode);
            return row;
        }

        private static UnlicensedUsageQueryRow Chat(int id, string department, string domain = "contoso.com")
        {
            return new UnlicensedUsageQueryRow
            {
                UserId = id,
                UserPrincipalName = $"user{id}@{domain}",
                EmailDomain = domain,
                Department = department,
                Interactions = 3,
                ActiveDays = 1,
                AppsUsed = 1,
                LastInteractionUtc = Now.AddDays(-1),
            };
        }

        private static AgentUsageRow Agent(int id, string name)
        {
            return new AgentUsageRow
            {
                AgentId = id,
                Name = name,
                Users = 1,
                Interactions = 10,
                WindowInteractions = 10,
                FirstUsedUtc = Now.AddDays(-60),
                LastUsedUtc = Now.AddDays(-1),
            };
        }

        private static AgentReachRow Pair(int agentId, int userId, int? departmentId, long interactions = 4)
        {
            return new AgentReachRow { AgentId = agentId, UserId = userId, DepartmentId = departmentId, Interactions = interactions };
        }

        private static AgentBuilderRow Builder(int userId, string department, string domain = "contoso.com")
        {
            return new AgentBuilderRow
            {
                UserId = userId,
                UserPrincipalName = $"user{userId}@{domain}",
                EmailDomain = domain,
                Department = department,
            };
        }

        /// <summary>Pairs for an analysis whose inventory and reporting periods coincide, as they do unless a past range is chosen.</summary>
        private static void WithPairs(CopilotAdoptionAnalysis analysis, params AgentReachRow[] pairs)
        {
            analysis.AgentReachRows = pairs.ToList();
            analysis.AgentInventoryReachRows = analysis.AgentReachRows;
            analysis.AgentReachDepartments = new Dictionary<int, string>(DepartmentNames);
        }

        /// <summary>
        /// Finance: 6 active seat holders (1-6) and one scored from Microsoft's usage report (17).
        /// Legal: 5 active unlicensed users (7-11). Sales: 5 seats, 2 active (12, 13). Tiny: 2 active seats
        /// (18, 19) - below the privacy floor of 5, so it gets no row.
        /// </summary>
        private static CopilotAdoptionAnalysis Population()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(Enumerable.Range(1, 6).Select(i => Seat(i, "Finance")));

            var reportSourced = Seat(17, "Finance");
            reportSourced.SignalSource = CopilotAdoptionScoring.SignalSourceUsageReport;
            analysis.LicensedUsers.Add(reportSourced);

            analysis.UnlicensedUsers.AddRange(Enumerable.Range(7, 5).Select(i => Chat(i, "Legal")));
            analysis.LicensedUsers.Add(Seat(12, "Sales"));
            analysis.LicensedUsers.Add(Seat(13, "Sales"));
            analysis.LicensedUsers.AddRange(Enumerable.Range(14, 3).Select(i => Seat(i, "Sales", interactions: 0)));
            analysis.LicensedUsers.Add(Seat(18, "Tiny"));
            analysis.LicensedUsers.Add(Seat(19, "Tiny"));
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count;

            analysis.Agents.Add(Agent(10, "Expenses helper"));
            analysis.Agents.Add(Agent(20, "Contract reviewer"));
            analysis.Agents.Add(Agent(30, "Researcher"));
            return analysis;
        }

        private static CopilotAdoptionAnalysis PopulationWithPairs()
        {
            var analysis = Population();
            WithPairs(analysis,
                Pair(10, 1, FinanceId), Pair(10, 2, FinanceId), Pair(10, 3, FinanceId), Pair(10, 7, LegalId),
                Pair(20, 1, FinanceId), Pair(20, 8, LegalId), Pair(20, 18, TinyId),
                // Scored from Microsoft's report, so not in the population the audit log can see.
                Pair(30, 17, FinanceId),
                // Someone neither query returned a row for - outside the population entirely.
                Pair(30, 99, SalesId));
            return analysis;
        }

        #endregion

        #region Reach (#647)

        [TestMethod]
        public void Reach_AnAgentUsedByOneDepartment_StaysHome_AndOneUsedByMany_HasSpread()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Agents.Add(Agent(10, "Expenses helper"));
            analysis.Agents.Add(Agent(20, "Contract reviewer"));
            WithPairs(analysis,
                // One department only: six people in Finance.
                Pair(10, 1, FinanceId), Pair(10, 2, FinanceId), Pair(10, 3, FinanceId),
                Pair(10, 4, FinanceId), Pair(10, 5, FinanceId), Pair(10, 6, FinanceId),
                // Many: five in Finance, two in Legal, two in the Greek-named department, one in Sales and one
                // with no department at all.
                Pair(20, 1, FinanceId), Pair(20, 2, FinanceId), Pair(20, 3, FinanceId), Pair(20, 4, FinanceId),
                Pair(20, 5, FinanceId), Pair(20, 7, LegalId), Pair(20, 8, LegalId), Pair(20, 9, GreekId),
                Pair(20, 10, GreekId), Pair(20, 11, SalesId), Pair(20, 12, null));

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var local = analysis.Agents.Single(a => a.AgentId == 10);
            Assert.AreEqual(6, local.WindowUsers);
            Assert.AreEqual(1, local.Departments);
            Assert.AreEqual("Finance", local.HomeDepartment);
            Assert.AreEqual(100d, local.HomeDepartmentSharePct);

            var spread = analysis.Agents.Single(a => a.AgentId == 20);
            Assert.AreEqual(11, spread.WindowUsers, "Everyone counts towards the agent's users, with or without a department.");
            Assert.AreEqual(4, spread.Departments, "People with no department are not a fifth department.");
            Assert.AreEqual("Finance", spread.HomeDepartment, "Five of its users are in Finance - at the privacy floor, so it is named.");
            Assert.AreEqual(45.5, spread.HomeDepartmentSharePct, "5 of 11 users, to one decimal place.");

            Assert.AreEqual(1, analysis.Summary.AgentsInThreeOrMoreDepartments,
                "Only the agent with users in three or more departments has spread.");
        }

        [TestMethod]
        public void Reach_WithholdsTheHomeDepartmentsName_WhenFewerThanMinSeatsPerSegmentOfItsUsersAreInIt()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Agents.Add(Agent(10, "Expenses helper"));
            WithPairs(analysis, Pair(10, 1, LegalId), Pair(10, 2, LegalId), Pair(10, 3, LegalId), Pair(10, 4, FinanceId));

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var agent = analysis.Agents.Single();
            Assert.IsNull(agent.HomeDepartment, "Three people are below the floor of five: the name would point at them.");
            Assert.AreEqual(75d, agent.HomeDepartmentSharePct, "The share names nobody, so it is still published.");
            Assert.AreEqual(2, agent.Departments);
        }

        [TestMethod]
        public void Reach_BreaksATieOnInteractions_ThenAlphabetically()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Agents.Add(Agent(10, "Expenses helper"));
            analysis.Agents.Add(Agent(20, "Contract reviewer"));
            WithPairs(analysis,
                Pair(10, 1, SalesId, interactions: 1), Pair(10, 2, SalesId, interactions: 1),
                Pair(10, 3, LegalId, interactions: 9), Pair(10, 4, LegalId, interactions: 9),
                Pair(20, 1, SalesId), Pair(20, 2, SalesId), Pair(20, 3, LegalId), Pair(20, 4, LegalId));

            new CopilotAdoptionService(new CopilotAdoptionOptions { MinSeatsPerSegment = 2 }).FinaliseSummary(analysis);

            Assert.AreEqual("Legal", analysis.Agents.Single(a => a.AgentId == 10).HomeDepartment,
                "Two users each: the department with more interactions is home.");
            Assert.AreEqual("Legal", analysis.Agents.Single(a => a.AgentId == 20).HomeDepartment,
                "Users and interactions tie: alphabetical, so the same data always names the same department.");
        }

        [TestMethod]
        public void Reach_AnAgentUnusedInThePeriod_ReachesNoDepartment()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Agents.Add(Agent(10, "Expenses helper"));
            WithPairs(analysis);

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var agent = analysis.Agents.Single();
            Assert.AreEqual(0, agent.WindowUsers);
            Assert.AreEqual(0, agent.Departments);
            Assert.IsNull(agent.HomeDepartment);
            Assert.IsNull(agent.HomeDepartmentSharePct);
            Assert.AreEqual(0, analysis.Summary.AgentsInThreeOrMoreDepartments);
        }

        [TestMethod]
        public void Reach_IsUnknown_NotZero_WhenThePairsCouldNotBeLoaded()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Agents.Add(Agent(10, "Expenses helper"));

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsNull(analysis.Agents.Single().Departments);
            Assert.IsNull(analysis.Agents.Single().WindowUsers);
            Assert.IsNull(analysis.Summary.AgentsInThreeOrMoreDepartments,
                "A query that did not run must reach Snapshot facts blank, never as a fall to zero.");
        }

        [TestMethod]
        public void Reach_ForAPastRange_ComesFromTheInventorysOwnPeriod()
        {
            // A past range reads the pairs twice: the reporting period for breadth and depth, and the inventory's
            // latest period for reach - the inventory itself reads up to now.
            var analysis = Population();
            analysis.AgentReachRows = new List<AgentReachRow> { Pair(10, 1, FinanceId) };
            analysis.AgentInventoryReachRows = new List<AgentReachRow>
            {
                Pair(10, 1, FinanceId), Pair(10, 7, LegalId), Pair(10, 12, SalesId),
            };
            analysis.AgentReachDepartments = new Dictionary<int, string>(DepartmentNames);

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(3, analysis.Agents.Single(a => a.AgentId == 10).Departments);
            Assert.AreEqual(1, analysis.Summary.AgentBreadthAgentUsers, "Breadth stays on the reporting period.");
        }

        #endregion

        #region Breadth and depth (#646)

        [TestMethod]
        public void BreadthAndDepth_PerDepartment_FromHandBuiltRows()
        {
            var analysis = PopulationWithPairs();

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var finance = analysis.Summary.CombinedByDepartment.Single(r => r.Segment == "Finance");
            Assert.AreEqual(6, finance.AgentActiveUsers,
                "The seat holder scored from Microsoft's report is left out: that report carries no agent identity.");
            Assert.AreEqual(3, finance.AgentUsers);
            Assert.AreEqual(50d, finance.AgentUserPct);
            Assert.AreEqual(2, finance.DistinctAgents);
            Assert.AreEqual(33.3, finance.AgentsPer100ActiveUsers);
            Assert.AreEqual(16L, finance.AgentInteractions, "Three people x 4 with one agent, and 4 with the other.");
            Assert.AreEqual(8d, finance.InteractionsPerActiveAgent);

            var legal = analysis.Summary.CombinedByDepartment.Single(r => r.Segment == "Legal");
            Assert.AreEqual(5, legal.AgentActiveUsers, "Unlicensed users are in the population.");
            Assert.AreEqual(2, legal.AgentUsers);
            Assert.AreEqual(40d, legal.AgentUserPct);
            Assert.AreEqual(2, legal.DistinctAgents);
            Assert.AreEqual(40d, legal.AgentsPer100ActiveUsers);
            Assert.AreEqual(4d, legal.InteractionsPerActiveAgent);

            var sales = analysis.Summary.CombinedByDepartment.Single(r => r.Segment == "Sales");
            Assert.AreEqual(2, sales.AgentActiveUsers, "Only the two seat holders who used Copilot are active.");
            Assert.AreEqual(0, sales.AgentUsers, "The pair for someone outside the population is not Sales' agent use.");
            Assert.AreEqual(0d, sales.AgentUserPct);
            Assert.AreEqual(0, sales.DistinctAgents);
            Assert.AreEqual(0d, sales.AgentsPer100ActiveUsers);
            Assert.AreEqual(0L, sales.AgentInteractions);
            Assert.IsNull(sales.InteractionsPerActiveAgent, "No agents: a per-agent figure has no denominator.");
        }

        [TestMethod]
        public void BreadthAndDepth_TenantLevel_CountTheSuppressedDepartmentsPeople_ButNotTheDepartment()
        {
            var analysis = PopulationWithPairs();

            new CopilotAdoptionService().FinaliseSummary(analysis);
            var summary = analysis.Summary;

            Assert.IsFalse(summary.CombinedByDepartment.Any(r => r.Segment == "Tiny"),
                "Two seat holders are below the privacy floor of five, so Tiny gets no row.");

            Assert.AreEqual(15, summary.AgentActiveUsers, "6 Finance + 5 Legal + 2 Sales + 2 Tiny.");
            Assert.AreEqual(6, summary.AgentBreadthAgentUsers, "Tiny's agent user still counts tenant-wide.");
            Assert.AreEqual(40d, summary.AgentBreadthUserPct);

            Assert.AreEqual(3, summary.AgentBreadthDepartments, "Finance, Legal and Sales are large enough; Tiny is not.");
            Assert.AreEqual(2, summary.AgentBreadthDepartmentsWithAgentUsers);
            Assert.AreEqual(66.7, summary.AgentBreadthDepartmentPct);

            Assert.AreEqual(2, summary.AgentDepthDistinctAgents, "The third agent was only used outside the population.");
            Assert.AreEqual(13.3, summary.AgentDepthAgentsPer100ActiveUsers);
            Assert.AreEqual(28L, summary.AgentDepthInteractions, "Seven counted pairs of four.");
            Assert.AreEqual(14d, summary.AgentDepthInteractionsPerActiveAgent);
            Assert.AreEqual(CopilotAgentFigureScope.AllAgents, summary.AgentFiguresScope);
        }

        [TestMethod]
        public void BreadthAndDepth_NoDepartmentBucket_IsNeverCountedAsADepartment()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(Enumerable.Range(1, 5).Select(i => Seat(i, null)));
            analysis.LicensedUsers.AddRange(Enumerable.Range(6, 5).Select(i => Seat(i, "Finance")));
            WithPairs(analysis, Pair(10, 1, null), Pair(10, 6, FinanceId));

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsTrue(analysis.Summary.CombinedByDepartment.Any(r => r.Segment == "(no department)"),
                "The table still shows the bucket, as before.");
            Assert.AreEqual(1, analysis.Summary.AgentBreadthDepartments, "But breadth counts departments, and that is one.");
            Assert.AreEqual(1, analysis.Summary.AgentBreadthDepartmentsWithAgentUsers);
            Assert.AreEqual(2, analysis.Summary.AgentBreadthAgentUsers, "Its people still count as people.");
        }

        [TestMethod]
        public void BreadthAndDepth_AreUnknown_NotZero_WhenThePairsCouldNotBeLoaded()
        {
            var analysis = Population();

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var summary = analysis.Summary;
            Assert.IsNull(summary.AgentActiveUsers);
            Assert.IsNull(summary.AgentBreadthAgentUsers);
            Assert.IsNull(summary.AgentBreadthUserPct);
            Assert.IsNull(summary.AgentBreadthDepartments);
            Assert.IsNull(summary.AgentBreadthDepartmentPct);
            Assert.IsNull(summary.AgentDepthDistinctAgents);
            Assert.IsNull(summary.AgentDepthAgentsPer100ActiveUsers);
            Assert.IsNull(summary.AgentDepthInteractions);
            Assert.IsNull(summary.AgentDepthInteractionsPerActiveAgent);

            var finance = summary.CombinedByDepartment.Single(r => r.Segment == "Finance");
            Assert.IsNull(finance.AgentUsers);
            Assert.IsNull(finance.AgentUserPct);
            Assert.IsNull(finance.DistinctAgents);
            Assert.IsNull(finance.AgentsPer100ActiveUsers);
            Assert.AreEqual(7, finance.LicensedUsers, "The existing columns are untouched.");
        }

        [TestMethod]
        public void EmptyWindow_MeasuresZero_AndLeavesEveryRatioWithoutADenominatorBlank()
        {
            var analysis = new CopilotAdoptionAnalysis();
            WithPairs(analysis);
            analysis.AgentBuildersAssessed = true;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var summary = analysis.Summary;
            Assert.AreEqual(0, summary.AgentActiveUsers);
            Assert.AreEqual(0, summary.AgentBreadthAgentUsers);
            Assert.IsNull(summary.AgentBreadthUserPct, "0 of 0 is not 0%.");
            Assert.AreEqual(0, summary.AgentBreadthDepartments);
            Assert.IsNull(summary.AgentBreadthDepartmentPct);
            Assert.AreEqual(0, summary.AgentDepthDistinctAgents);
            Assert.IsNull(summary.AgentDepthAgentsPer100ActiveUsers);
            Assert.AreEqual(0L, summary.AgentDepthInteractions);
            Assert.IsNull(summary.AgentDepthInteractionsPerActiveAgent);
            Assert.AreEqual(0, summary.AgentBuilders, "The feed was imported and nobody built anything: a real zero.");
            Assert.AreEqual(0, summary.AgentsInThreeOrMoreDepartments);
            Assert.AreEqual(0, summary.CombinedByDepartment.Count);
        }

        #endregion

        #region Builders (#647)

        [TestMethod]
        public void Builders_AreCountedOncePerPerson_TenantWideAndByDepartment_IncludingOneInASuppressedDepartment()
        {
            var analysis = PopulationWithPairs();
            analysis.AgentBuildersAssessed = true;
            analysis.AgentBuilders.AddRange(new[]
            {
                Builder(1, "Finance"),
                Builder(1, "Finance"),
                Builder(2, "Finance"),
                // Tiny is below the privacy floor: no row, but the builder still counts in the tenant total.
                Builder(18, "Tiny"),
                // A builder with no Copilot activity at all still built an agent.
                Builder(50, Greek),
            });

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(4, analysis.Summary.AgentBuilders);
            Assert.AreEqual(2, analysis.Summary.CombinedByDepartment.Single(r => r.Segment == "Finance").AgentBuilders);
            Assert.AreEqual(0, analysis.Summary.CombinedByDepartment.Single(r => r.Segment == "Legal").AgentBuilders);
            Assert.IsFalse(analysis.Summary.CombinedByDepartment.Any(r => r.Segment == "Tiny"));
        }

        [TestMethod]
        public void Builders_AreUnknown_NotZero_WhenNoAuthoringEventWasEverImported()
        {
            var analysis = PopulationWithPairs();

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsNull(analysis.Summary.AgentBuilders,
                "A tenant that does not import the Power Platform audit feed must not read as 'nobody builds agents'.");
            Assert.IsTrue(analysis.Summary.CombinedByDepartment.All(r => r.AgentBuilders == null));
        }

        #endregion

        #region Filtered views

        [TestMethod]
        public void FilteredView_NarrowsBreadthDepthAndBuilders_ToItsPeople_AndCarriesTheTenantsReach()
        {
            var service = new CopilotAdoptionService();
            var analysis = PopulationWithPairs();

            // A second organisation in the tenant: five Finance seat holders, all using the first agent, and a
            // builder.
            analysis.LicensedUsers.AddRange(Enumerable.Range(60, 5).Select(i => Seat(i, "Finance", domain: "fabrikam.com")));
            analysis.AgentReachRows.AddRange(Enumerable.Range(60, 5).Select(i => Pair(10, i, FinanceId)));
            analysis.AgentBuildersAssessed = true;
            analysis.AgentBuilders.Add(Builder(1, "Finance"));
            analysis.AgentBuilders.Add(Builder(60, "Finance", domain: "fabrikam.com"));
            service.FinaliseSummary(analysis);

            var tenantReach = analysis.Agents.Single(a => a.AgentId == 10).Departments;
            Assert.AreEqual(8, analysis.Summary.CombinedByDepartment.Single(r => r.Segment == "Finance").AgentUsers,
                "Eight Finance agent users tenant-wide: three from contoso, five from fabrikam.");

            var scoped = CopilotAdoptionScopeFilter.Apply(analysis, CopilotAdoptionScope.ForEmailDomain("contoso.com"), service.FinaliseSummary);

            var finance = scoped.Summary.CombinedByDepartment.Single(r => r.Segment == "Finance");
            Assert.AreEqual(6, finance.AgentActiveUsers, "Only contoso's active Finance seat holders.");
            Assert.AreEqual(3, finance.AgentUsers);
            Assert.AreEqual(1, finance.AgentBuilders);
            Assert.AreEqual(6, scoped.Summary.AgentBreadthAgentUsers);
            Assert.AreEqual(1, scoped.Summary.AgentBuilders);

            Assert.AreEqual(analysis.Summary.AgentsInThreeOrMoreDepartments, scoped.Summary.AgentsInThreeOrMoreDepartments,
                "Reach belongs to the tenant-wide inventory and travels with it.");
            Assert.AreEqual(tenantReach, scoped.Summary.Agents.Agents.Single(a => a.AgentId == 10).Departments,
                "A filtered view must not rewrite the shared inventory's reach.");
            Assert.AreEqual(11, analysis.Summary.AgentBreadthAgentUsers, "The cached tenant summary is not modified.");
            Assert.AreEqual(2, analysis.Summary.AgentBuilders);
        }

        #endregion

        #region Workbook

        [TestMethod]
        public void Workbook_CarriesTheNewColumns_TheDefinitions_AndBlankFactsWhenUnknown()
        {
            var service = new CopilotAdoptionService();
            var analysis = PopulationWithPairs();
            analysis.Summary.Options = service.Options;
            // One agent with six Finance users, so a home department is named.
            analysis.Agents.Add(Agent(40, "Finance close"));
            analysis.AgentReachRows.AddRange(Enumerable.Range(1, 6).Select(i => Pair(40, i, FinanceId)));
            service.FinaliseSummary(analysis);

            var bytes = CopilotAdoptionWorkbook.Build(analysis);

            var departments = WorkbookCells(bytes, "Departments and apps");
            foreach (var header in new[] { "Using agents %", "Distinct agents", "Agents per 100 active users", "Interactions per agent", "Agent builders", "Departments counted" })
            {
                CollectionAssert.Contains(departments, header);
            }

            var agents = WorkbookCells(bytes, "Agents");
            foreach (var header in new[] { "Users this period", "Departments", "Home department", "Home share %" })
            {
                CollectionAssert.Contains(agents, header);
            }

            CollectionAssert.Contains(agents, "Finance", "The home department of an agent with six Finance users is named.");
            Assert.IsTrue(agents.Any(c => c.StartsWith("Agents used in 3 or more departments (all agents, Microsoft's and your own)", StringComparison.Ordinal)),
                "The reach count names which agents it counts.");

            var method = string.Join("\n", WorkbookCells(bytes, "How this is calculated"));
            foreach (var expected in new[] { "Agent breadth", "Agent depth", "Agent builders", "Agent reach", "BotCreate", "BotUpdateOperation-BotPublish", "BotUpdateOperation-BotShare" })
            {
                StringAssert.Contains(method, expected);
            }

            var facts = WorkbookCells(bytes, "Snapshot facts");
            var builders = facts.IndexOf("agentBuilders");
            Assert.AreNotEqual(-1, builders, "Tenant-level figures reach Snapshot facts as their own keys.");
            Assert.AreEqual(string.Empty, facts[builders + 1],
                "Not measured, so the value is written blank - never as a zero.");
            var reach = facts.IndexOf("agentsInThreeOrMoreDepartments");
            Assert.AreEqual("1", facts[reach + 1]);
            Assert.IsFalse(facts.Any(f => f == "Finance" || f == "Legal"), "Department names are tenant data and never a fact key.");
        }

        private static List<string> WorkbookCells(byte[] bytes, string sheetName)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                List<string> names;
                using (var part = zip.GetEntry("xl/workbook.xml").Open())
                {
                    names = XDocument.Load(part).Descendants(ns + "sheet").Select(s => (string)s.Attribute("name")).ToList();
                }

                var index = names.IndexOf(sheetName);
                Assert.AreNotEqual(-1, index, $"Sheet '{sheetName}' is missing.");

                using (var part = zip.GetEntry("xl/worksheets/sheet" + (index + 1) + ".xml").Open())
                {
                    return XDocument.Load(part).Descendants(ns + "c")
                        .Select(c => string.Concat(c.Descendants().Where(d => !d.HasElements).Select(d => d.Value)))
                        .ToList();
                }
            }
        }

        #endregion

        #region Queries

        [TestMethod]
        public void AgentReachSql_IsOneGroupedPassOverTheWindow_ThatNeverJoinsAuditEvents()
        {
            var sql = CopilotAdoptionSql.AgentReachSql();

            Assert.AreEqual(1, Occurrences(sql, "FROM dbo.copilot_chats"), "One pass over the window.");
            StringAssert.Contains(sql, "c.time_stamp >= @from");
            StringAssert.Contains(sql, "c.time_stamp < @toExclusive");
            StringAssert.Contains(sql, "GROUP BY c.agent_id, c.user_id");
            StringAssert.Contains(sql, "TOP (@maxRows)");
            StringAssert.Contains(sql, "OPTION (RECOMPILE)");
            Assert.AreEqual(0, Occurrences(sql, "audit_events"), "Copilot queries read copilot_chats' own user and time.");
            Assert.AreEqual(0, Occurrences(sql, "COUNT(DISTINCT"));
        }

        [TestMethod]
        public void AgentBuildersSql_ReadsOnlyCopilotStudioAuthoringEvents_ForCreatePublishAndShare()
        {
            var sql = CopilotAdoptionSql.AgentBuildersSql();

            StringAssert.Contains(sql, "FROM dbo.event_meta_copilot_studio AS m");
            StringAssert.Contains(sql, "N'BotCreate'");
            StringAssert.Contains(sql, "N'BotUpdateOperation-BotPublish'");
            StringAssert.Contains(sql, "N'BotUpdateOperation-BotShare'");
            Assert.AreEqual(0, Occurrences(sql, "copilot_chats"), "Building an agent is not using one.");
            Assert.AreEqual(0, Occurrences(sql, "copilot_agents"),
                "Copilot Studio bot ids are not proven to equal copilot_agents.agent_id, so they are never joined.");
            Assert.AreEqual(0, Occurrences(sql, "copilot_studio_bots"));
        }

        [TestMethod]
        public void ReachAndBuilderQueries_ReturnTheExpectedRows_AgainstARealDatabase()
        {
            using (var db = ScratchDatabase.Create("AgentReach"))
            {
                // Column types mirror production (Create DB.sql and the migrations), so a Unicode department
                // name crosses the same nvarchar(100) boundary it does in a customer database.
                db.Execute(
                    @"CREATE TABLE dbo.user_departments (id int NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
                      CREATE TABLE dbo.users (
                          id int NOT NULL PRIMARY KEY,
                          user_name varchar(250) NOT NULL,
                          mail nvarchar(max) NULL,
                          department_id int NULL);
                      CREATE TABLE dbo.event_operations (id int NOT NULL PRIMARY KEY, operation_name nvarchar(max) NULL);
                      CREATE TABLE dbo.audit_events (
                          id uniqueidentifier NOT NULL PRIMARY KEY,
                          time_stamp datetime NOT NULL,
                          operation_id int NULL,
                          user_id int NULL);
                      CREATE TABLE dbo.copilot_studio_bots (id int NOT NULL PRIMARY KEY, bot_id nvarchar(200) NULL, name nvarchar(255) NULL);
                      CREATE TABLE dbo.event_meta_copilot_studio (event_id uniqueidentifier NOT NULL PRIMARY KEY, bot_id int NULL);
                      CREATE TABLE dbo.copilot_chats (
                          event_id uniqueidentifier NOT NULL PRIMARY KEY,
                          app_host nvarchar(max) NULL,
                          agent_id int NULL,
                          user_id int NULL,
                          time_stamp datetime NULL);
                      CREATE NONCLUSTERED INDEX IX_copilot_chats_time_stamp_user_id
                          ON dbo.copilot_chats ([time_stamp], [user_id]) INCLUDE ([app_host], [agent_id]);

                      INSERT INTO dbo.user_departments (id, name) VALUES (1, N'Finance'), (3, N'Καλημέρα κόσμε');
                      INSERT INTO dbo.users (id, user_name, mail, department_id) VALUES
                          (1, 'one@contoso.com', N'one@contoso.com', 1),
                          (2, 'two@contoso.com', NULL, 3),
                          (3, 'three@contoso.com', NULL, NULL);
                      INSERT INTO dbo.event_operations (id, operation_name) VALUES
                          (1, N'BotCreate'), (2, N'BotUpdateOperation-BotPublish'), (3, N'BotUpdateOperation-BotShare'),
                          (4, N'BotComponentUpdate'), (5, N'CopilotInteraction');
                      INSERT INTO dbo.copilot_studio_bots (id, bot_id, name) VALUES (1, N'00000000-0000-0000-0000-000000000000', N'Contoso helper');");

                var from = new DateTime(2026, 9, 1);
                var to = new DateTime(2026, 9, 29);

                // Agent 7: user 1 twice and user 2 once inside the window; agent 8: user 3 inside, user 1 outside.
                Chat(db, 1, 7, new DateTime(2026, 9, 2));
                Chat(db, 1, 7, new DateTime(2026, 9, 3));
                Chat(db, 2, 7, new DateTime(2026, 9, 3));
                Chat(db, 3, 8, new DateTime(2026, 9, 4));
                Chat(db, 1, 8, new DateTime(2026, 8, 20));
                // Interactions with no agent, and one with no user, never become pairs.
                Chat(db, 2, null, new DateTime(2026, 9, 5));
                Chat(db, null, 7, new DateTime(2026, 9, 5));

                var pairs = Query<AgentReachRow>(db, CopilotAdoptionSql.AgentReachSql(),
                    new SqlParameter("@from", from),
                    new SqlParameter("@toExclusive", to),
                    new SqlParameter("@maxRows", CopilotAdoptionSql.MaxAgentReachRows + 1));

                Assert.AreEqual(3, pairs.Count);
                var oneWithSeven = pairs.Single(p => p.AgentId == 7 && p.UserId == 1);
                Assert.AreEqual(2L, oneWithSeven.Interactions);
                Assert.AreEqual(1, oneWithSeven.DepartmentId);
                Assert.AreEqual(3, pairs.Single(p => p.AgentId == 7 && p.UserId == 2).DepartmentId);
                Assert.IsNull(pairs.Single(p => p.AgentId == 8).DepartmentId);

                var names = Query<DepartmentNameRow>(db, CopilotAdoptionSql.AgentReachDepartmentsSql);
                Assert.AreEqual("Καλημέρα κόσμε", names.Single(n => n.Id == 3).Name,
                    "A non-Latin department name must survive the round trip.");

                Assert.AreEqual(0, Query<int>(db, CopilotAdoptionSql.CopilotStudioAuthoringImportedSql).Single(),
                    "Nothing imported yet.");

                // Builders: user 1 created and published, user 2 only edited a topic, user 3 shared - but last month.
                Authoring(db, 1, 1, new DateTime(2026, 9, 2));
                Authoring(db, 1, 2, new DateTime(2026, 9, 3));
                Authoring(db, 2, 4, new DateTime(2026, 9, 3));
                Authoring(db, 3, 3, new DateTime(2026, 8, 3));

                Assert.AreEqual(1, Query<int>(db, CopilotAdoptionSql.CopilotStudioAuthoringImportedSql).Single());

                var builders = Query<AgentBuilderRow>(db, CopilotAdoptionSql.AgentBuildersSql(),
                    new SqlParameter("@from", from),
                    new SqlParameter("@toExclusive", to));

                Assert.AreEqual(1, builders.Count, "Editing a topic does not make a builder, and August is outside the window.");
                Assert.AreEqual(1, builders[0].UserId);
                Assert.AreEqual("Finance", builders[0].Department);
            }
        }

        private static void Chat(ScratchDatabase db, int? userId, int? agentId, DateTime when)
        {
            db.Execute(
                $@"INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, user_id, time_stamp)
                   VALUES (NEWID(), N'Teams', {(agentId.HasValue ? agentId.Value.ToString() : "NULL")},
                           {(userId.HasValue ? userId.Value.ToString() : "NULL")}, '{when:yyyy-MM-dd HH:mm:ss}');");
        }

        private static void Authoring(ScratchDatabase db, int userId, int operationId, DateTime when)
        {
            var id = Guid.NewGuid();
            db.Execute(
                $@"INSERT INTO dbo.audit_events (id, time_stamp, operation_id, user_id)
                       VALUES ('{id}', '{when:yyyy-MM-dd HH:mm:ss}', {operationId}, {userId});
                   INSERT INTO dbo.event_meta_copilot_studio (event_id, bot_id) VALUES ('{id}', 1);");
        }

        private static List<T> Query<T>(ScratchDatabase db, string sql, params SqlParameter[] parameters)
        {
            using (var context = new RawSqlContext(db.ConnectionString))
            {
                context.Database.CommandTimeout = 120;
                return context.Database.SqlQuery<T>(sql, parameters).ToList();
            }
        }

        /// <summary>A model-less context for raw SQL against the scratch database.</summary>
        private sealed class RawSqlContext : DbContext
        {
            static RawSqlContext()
            {
                Database.SetInitializer<RawSqlContext>(null);
            }

            public RawSqlContext(string connectionString) : base(connectionString)
            {
            }
        }

        private static int Occurrences(string text, string value)
        {
            var count = 0;
            for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        #endregion
    }
}
