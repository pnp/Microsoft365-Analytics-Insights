extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using Common.Entities;
using Common.Entities.AgentCosts;
using Common.Entities.Entities.AgentCosts;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configuration = Common.Entities.Migrations.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Agent costs report's consistency rules (#851, R2-11), run by <see cref="SqlAgentCostReportStore"/> against a
    /// throwaway database with every migration applied: what a distinct count counts, which name an agent is shown under,
    /// and which values a capped picker keeps.
    /// </summary>
    /// <remarks>
    /// Each test seeds usage dates of its own, years from any real data, so no test reads another's rows. Synthetic
    /// Contoso names only.
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class AgentCostReportConsistencyTests
    {
        private static ScratchDatabase _database;
        private static int _rows;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _database = ScratchDatabase.Create("AgentCostReport");
            new DbMigrator(new Configuration
            {
                TargetDatabase = new DbConnectionInfo(_database.ConnectionString, "Microsoft.Data.SqlClient"),
            }).Update();
        }

        [ClassCleanup]
        public static void DropDatabase()
        {
            SqlConnection.ClearAllPools();
            _database?.Dispose();
        }

        private static SqlAgentCostReportStore NewStore() =>
            new SqlAgentCostReportStore(new ConnectionStringAnalyticsDbContextFactory(_database.ConnectionString));

        private static void Seed(params CopilotStudioCreditDaily[] rows)
        {
            using (var db = new AnalyticsEntitiesContext(_database.ConnectionString, true, false))
            {
                db.Configuration.AutoDetectChangesEnabled = false;
                db.CopilotStudioCreditDaily.AddRange(rows);
                db.ChangeTracker.DetectChanges();
                db.SaveChanges();
            }
        }

        private static CopilotStudioCreditDaily Row(DateTime day, string agentId, string agentName, decimal credits,
            string environmentId = "env-contoso", string environmentName = "Contoso (default)", string feature = "Generative answer") =>
            new CopilotStudioCreditDaily
            {
                UsageDate = day,
                AgentId = agentId,
                AgentName = agentName,
                EnvironmentId = environmentId,
                EnvironmentName = environmentName,
                FeatureName = feature,
                BilledCredits = credits,
                DimensionHash = "contoso-" + Interlocked.Increment(ref _rows),
                ImportedUtc = DateTime.UtcNow,
            };

        [TestMethod]
        public async Task Summary_DistinctCounts_DoNotCountAMissingAgentOrEnvironmentAsOneMore()
        {
            var day = new DateTime(2031, 1, 6);
            Seed(
                Row(day, "agent-hr", "Contoso HR", 2m),
                Row(day, "agent-finance", "Contoso Finance", 3m),
                Row(day, null, null, 5m, environmentId: null, environmentName: null));

            var summary = await NewStore().GetSummaryAsync(new AgentCostQuery { FromUtc = day, ToUtc = day });

            Assert.AreEqual(10m, summary.BilledCredits, "Credits with no agent still count towards the total.");
            Assert.AreEqual(2, summary.DistinctAgents, "Two agents: credits with no agent are not a third.");
            Assert.AreEqual(1, summary.DistinctEnvironments, "One environment: credits with none are not a second.");
        }

        [TestMethod]
        public async Task AnAgentAndAnEnvironment_AreShownUnderTheirLatestName_NotTheAlphabeticallyGreatest()
        {
            var monday = new DateTime(2031, 2, 3);
            var tuesday = monday.AddDays(1);
            var wednesday = monday.AddDays(2);
            Seed(
                // Renamed on Tuesday. MAX kept "Zeta helper" and "Zulu environment" for as long as Monday was in the window.
                Row(monday, "agent-renamed", "Zeta helper", 1m, environmentId: "env-renamed", environmentName: "Zulu environment"),
                Row(tuesday, "agent-renamed", "Alpha helper", 1m, environmentId: "env-renamed", environmentName: "Alpha environment"),
                // A later row that carried no name does not blank it.
                Row(wednesday, "agent-renamed", null, 1m, environmentId: "env-renamed", environmentName: null),
                // Tenant text, shown as written.
                Row(monday, "agent-greek", "Καλημέρα κόσμε", 1m, environmentId: "env-renamed", environmentName: "Zulu environment"));

            var store = NewStore();
            var query = new AgentCostQuery { FromUtc = monday, ToUtc = wednesday };

            var byAgent = await store.GetBreakdownAsync(query, AgentCostDimensions.Agent, 10);
            Assert.AreEqual("Alpha helper", byAgent.Single(r => r.Key == "agent-renamed").Label);
            Assert.AreEqual("Καλημέρα κόσμε", byAgent.Single(r => r.Key == "agent-greek").Label);

            var byEnvironment = await store.GetBreakdownAsync(query, AgentCostDimensions.Environment, 10);
            Assert.AreEqual("Alpha environment", byEnvironment.Single(r => r.Key == "env-renamed").Label);

            var options = await store.GetFilterOptionsAsync(query);
            Assert.AreEqual("Alpha helper", options.Agents.Single(a => a.Id == "agent-renamed").Label, "The picker names it the same way.");
            Assert.AreEqual("Καλημέρα κόσμε", options.Agents.Single(a => a.Id == "agent-greek").Label);
            Assert.AreEqual("Alpha environment", options.Environments.Single(e => e.Id == "env-renamed").Label);
        }

        [TestMethod]
        public async Task CappedPickers_KeepTheBiggestSpenders()
        {
            // One agent and one feature more than the pickers hold, with ids that sort in the reverse order of their spend:
            // an unordered cap, which SQL Server tends to fill in key order, left out the biggest spender of each.
            var day = new DateTime(2031, 3, 3);
            var agents = SqlAgentCostReportStore.MaxAgentOptions + 1;
            var features = SqlAgentCostReportStore.MaxFeatureOptions + 1;
            Seed(Enumerable.Range(0, agents)
                .Select(i => Row(day, "agent-" + i.ToString("D4"), null, credits: i + 1,
                    feature: "feature-" + Math.Min(i, features - 1).ToString("D4")))
                .ToArray());

            var options = await NewStore().GetFilterOptionsAsync(new AgentCostQuery { FromUtc = day, ToUtc = day });

            Assert.AreEqual(SqlAgentCostReportStore.MaxAgentOptions, options.Agents.Count);
            var top = "agent-" + (agents - 1).ToString("D4");
            Assert.IsTrue(options.Agents.Any(a => a.Id == top), "The agent behind the biggest share of the bill must be offered.");
            Assert.IsFalse(options.Agents.Any(a => a.Id == "agent-0000"), "The smallest spender is the one the cap leaves out.");
            Assert.AreEqual(top, options.Agents.Single(a => a.Id == top).Label, "An agent that never reported a name is shown by its id.");

            Assert.AreEqual(SqlAgentCostReportStore.MaxFeatureOptions, options.Features.Count);
            CollectionAssert.Contains(options.Features, "feature-" + (features - 1).ToString("D4"), "The biggest-spending feature must be offered.");
            CollectionAssert.DoesNotContain(options.Features, "feature-0000", "The smallest-spending feature is the one left out.");
        }
    }

    /// <summary>The Agent costs report's window, as the API reads it from the query string (#851, R2-11).</summary>
    [TestClass]
    public class AgentCostsWindowTests
    {
        [TestMethod]
        public async Task TheWindowIsAtMost180Dates_CountingBothEnds()
        {
            var store = new CapturingStore();
            var controller = new AgentCostsAPIController(store, () => new ImportTaskSettings());

            await controller.Summary(from: "2026-01-01", to: "2026-06-29");
            Assert.AreEqual(new DateTime(2026, 1, 1), store.Query.FromUtc, "180 dates is the widest window, and is answered as asked.");
            Assert.AreEqual(new DateTime(2026, 6, 29), store.Query.ToUtc);

            await controller.Summary(from: "2026-01-01", to: "2026-06-30");
            Assert.AreEqual(new DateTime(2026, 6, 30), store.Query.ToUtc);
            Assert.AreEqual(new DateTime(2026, 1, 2), store.Query.FromUtc, "181 dates is one too many, so the oldest is dropped.");

            await controller.Summary(from: "2025-01-01", to: "2026-06-30");
            Assert.AreEqual(180, (store.Query.ToUtc - store.Query.FromUtc).TotalDays + 1, "A wider request is answered with 180 dates.");
        }

        private sealed class CapturingStore : IAgentCostReportStore
        {
            internal AgentCostQuery Query { get; private set; }

            private Task<T> Capture<T>(AgentCostQuery query, T result)
            {
                Query = query;
                return Task.FromResult(result);
            }

            public Task<AgentCostAvailability> GetAvailabilityAsync(bool copilotStudioCreditsEnabled, bool azureCostsEnabled) =>
                Task.FromResult(new AgentCostAvailability());
            public Task<AgentCostSummary> GetSummaryAsync(AgentCostQuery query) => Capture(query, new AgentCostSummary());
            public Task<List<AgentCostDailyPoint>> GetDailyTrendAsync(AgentCostQuery query) => Capture(query, new List<AgentCostDailyPoint>());
            public Task<List<AgentCostBreakdownRow>> GetBreakdownAsync(AgentCostQuery query, string dimension, int top) =>
                Capture(query, new List<AgentCostBreakdownRow>());
            public Task<AgentCostDetailPage> GetDetailAsync(AgentCostQuery query) => Capture(query, new AgentCostDetailPage());
            public Task<List<AzureCostBreakdownRow>> GetAzureBreakdownAsync(AgentCostQuery query, string dimension, int top) =>
                Capture(query, new List<AzureCostBreakdownRow>());
            public Task<AgentCostFilterOptions> GetFilterOptionsAsync(AgentCostQuery query) => Capture(query, new AgentCostFilterOptions());
            public Task<List<AgentCostUserRow>> GetTopUsersAsync(AgentCostQuery query, int top) => Capture(query, new List<AgentCostUserRow>());
        }
    }
}
