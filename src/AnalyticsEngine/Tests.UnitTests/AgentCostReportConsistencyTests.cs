extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities;
using Common.Entities.AgentCosts;
using Common.Entities.Entities.AgentCosts;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Globalization;
using System.Linq;
using System.Net;
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
            SeedFloorRows();
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
            string environmentId = "env-contoso", string environmentName = "Contoso (default)", string feature = "Generative answer",
            int? users = null) =>
            new CopilotStudioCreditDaily
            {
                UsageDate = day,
                AgentId = agentId,
                AgentName = agentName,
                EnvironmentId = environmentId,
                EnvironmentName = environmentName,
                FeatureName = feature,
                BilledCredits = credits,
                DistinctUsers = users,
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

        #region The privacy floor for a reader without See PII (#844 G1)

        private static readonly DateTime FloorDay1 = new DateTime(2031, 4, 7);
        private static readonly DateTime FloorDay2 = FloorDay1.AddDays(1);
        private static readonly DateTime FloorDay3 = FloorDay1.AddDays(2);

        private const string SharedEnvironment = "env-contoso-shared";
        private const string DeveloperEnvironment = "env-contoso-dev";

        /// <summary>
        /// One agent of 12 people with a quiet day of 2, one of exactly 5, a developer's own agent in their own environment
        /// (1 person a day) and an agent Microsoft gave no people count for. Seeded with the database: every floor test reads
        /// these days, and no other test does.
        /// </summary>
        private static void SeedFloorRows() =>
            Seed(
                Row(FloorDay1, "agent-helpdesk", "Contoso helpdesk", 10m, SharedEnvironment, "Contoso shared", users: 12),
                Row(FloorDay2, "agent-helpdesk", "Contoso helpdesk", 4m, SharedEnvironment, "Contoso shared", users: 2),
                Row(FloorDay1, "agent-hr", "Contoso HR", 6m, SharedEnvironment, "Contoso shared", users: 5),
                Row(FloorDay1, "agent-sandbox", "Contoso sandbox bot", 3m, DeveloperEnvironment, "Contoso dev sandbox", users: 1),
                Row(FloorDay2, "agent-sandbox", "Contoso sandbox bot", 2m, DeveloperEnvironment, "Contoso dev sandbox",
                    feature: "Tenant graph grounding", users: 1),
                Row(FloorDay3, "agent-uncounted", "Contoso uncounted", 8m, SharedEnvironment, "Contoso shared", users: null));

        /// <summary>The Agent costs API over the scratch database, for a reader holding <paramref name="roles"/>.</summary>
        private static PortalTestHost AgentCostsHost(params string[] roles) =>
            new PortalTestHost(
                new[] { typeof(AgentCostsAPIController) },
                PortalTestHost.SignedIn(roles),
                PortalAccessPolicy.Enforcing,
                type => new AgentCostsAPIController(NewStore(), () => new ImportTaskSettings()));

        private static async Task<JToken> GetAsync(PortalTestHost host, string path, DateTime? from = null, DateTime? to = null)
        {
            var separator = path.Contains("?") ? "&" : "?";
            var url = "api/AgentCosts/" + path + separator
                + "from=" + (from ?? FloorDay1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + "&to=" + (to ?? FloorDay3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var response = await host.Client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, url + ": " + body);
            return JToken.Parse(body);
        }

        /// <summary>Every people count in the answers, wherever it sits, so none of 1-4 can hide in a field a test did not name.</summary>
        private static IEnumerable<int> PeopleCountsIn(params JToken[] answers) => answers
            .SelectMany(a => a is JContainer container ? container.DescendantsAndSelf() : Enumerable.Empty<JToken>())
            .OfType<JProperty>()
            .Where(p => p.Name == "distinctUsers" || p.Name == "peakDistinctUsers" || p.Name == "peakDistinctUsersOnASlice")
            .Where(p => p.Value.Type == JTokenType.Integer)
            .Select(p => (int)p.Value);

        /// <summary>A figure withheld from the reader: null on the wire - never zero.</summary>
        private static void AssertWithheld(JToken answer, string figure, string because)
        {
            var value = answer[figure];
            Assert.IsTrue(value == null || value.Type == JTokenType.Null, because + " " + figure + " was " + value);
        }

        [TestMethod]
        public async Task AReaderWithoutSeePii_IsGivenOnlySlicesOfFiveOrMorePeople_WithSmallAgentsAndEnvironmentsCombinedIntoOneRowWithoutFigures()
        {
            using (var host = AgentCostsHost())
            {
                var summary = await GetAsync(host, "summary");
                Assert.AreEqual(16m, (decimal)summary["billedCredits"],
                    "Only the 12- and the 5-person slices count: the helpdesk's quiet day, the sandbox and the uncounted agent are left out.");
                Assert.AreEqual(2, (int)summary["distinctAgents"]);
                Assert.AreEqual(1, (int)summary["distinctEnvironments"], "The developer's environment is in no figure.");
                Assert.AreEqual(1, (int)summary["daysWithUsage"]);
                Assert.AreEqual(12, (int)summary["peakDistinctUsersOnASlice"]);
                Assert.AreEqual(5, (int)summary["minPeoplePerSlice"]);
                Assert.IsTrue((bool)summary["smallSlicesWithheld"], "The page must say slices were left out, or its totals read as complete.");
                Assert.IsFalse((bool)summary["withheld"]);

                var trend = (JArray)await GetAsync(host, "trend");
                Assert.AreEqual(1, trend.Count, "Days whose slices are all small are left out, never drawn as zero.");
                Assert.AreEqual(16m, (decimal)trend[0]["billedCredits"]);

                var byAgent = (JArray)await GetAsync(host, "breakdown?dimension=agent");
                CollectionAssert.AreEqual(new[] { "agent-helpdesk", "agent-hr", null }, byAgent.Select(r => (string)r["key"]).ToList());
                Assert.AreEqual(10m, (decimal)byAgent[0]["billedCredits"], "The helpdesk's 2-person day is not in its row.");
                Assert.AreEqual(1, (int)byAgent[0]["activeDays"]);
                Assert.AreEqual(6m, (decimal)byAgent[1]["billedCredits"], "Five people is enough.");
                var combined = byAgent[2];
                Assert.IsTrue((bool)combined["withheld"]);
                Assert.AreEqual(2, (int)combined["folded"], "The sandbox agent and the agent with no people count.");
                Assert.IsNull((string)combined["label"]);
                foreach (var figure in new[] { "billedCredits", "nonBilledCredits", "activeDays", "peakDistinctUsers" })
                {
                    AssertWithheld(combined, figure, "On the combined row, withheld - never zero:");
                }

                var byEnvironment = (JArray)await GetAsync(host, "breakdown?dimension=environment");
                CollectionAssert.AreEqual(new[] { SharedEnvironment, null }, byEnvironment.Select(r => (string)r["key"]).ToList());
                Assert.AreEqual(1, (int)byEnvironment[1]["folded"], "The developer's own environment is combined, without its name.");
                Assert.IsFalse(byEnvironment.ToString().Contains("Contoso dev sandbox"), "The environment's name identifies its developer.");

                var detail = await GetAsync(host, "detail");
                Assert.AreEqual(2, (int)detail["totalRows"], "Only the two slices of five or more people are billed lines.");
                CollectionAssert.AreEquivalent(new[] { "agent-helpdesk", "agent-hr" }, detail["rows"].Select(r => (string)r["agentId"]).ToList());
                Assert.IsTrue((bool)detail["smallSlicesWithheld"]);

                var options = await GetAsync(host, "filters");
                CollectionAssert.AreEquivalent(new[] { "agent-helpdesk", "agent-hr" }, options["agents"].Select(a => (string)a["id"]).ToList(),
                    "A picker never offers a value that could only answer 'withheld', or the name of a personal agent.");
                CollectionAssert.AreEqual(new[] { SharedEnvironment }, options["environments"].Select(e => (string)e["id"]).ToList());

                var counts = PeopleCountsIn(summary, trend, byAgent, byEnvironment, detail).ToList();
                Assert.IsTrue(counts.Count > 0);
                Assert.IsTrue(counts.All(c => c >= 5), "No people count under five reaches this reader: " + string.Join(", ", counts));
            }
        }

        [TestMethod]
        public async Task AReaderWithoutSeePii_NarrowingOrSearchingToAGroupOfFewerThanFivePeople_GetsItsFiguresWithheld_NeverZero()
        {
            using (var host = AgentCostsHost())
            {
                foreach (var narrowing in new[] { "agentId=agent-sandbox", "environmentId=" + DeveloperEnvironment, "search=sandbox",
                    "agentId=agent-uncounted", "feature=Tenant%20graph%20grounding" })
                {
                    var summary = await GetAsync(host, "summary?" + narrowing);
                    Assert.IsTrue((bool)summary["withheld"], narrowing + ": withheld");
                    foreach (var figure in new[] { "billedCredits", "nonBilledCredits", "distinctAgents", "distinctEnvironments", "daysWithUsage",
                        "peakDistinctUsersOnASlice", "unclassifiedHarnessCredits" })
                    {
                        AssertWithheld(summary, figure, narrowing + ": withheld, never zero:");
                    }

                    Assert.AreEqual(0, ((JArray)await GetAsync(host, "trend?" + narrowing)).Count, narrowing + ": no day of it is drawn.");

                    var byAgent = (JArray)await GetAsync(host, "breakdown?dimension=agent&" + narrowing);
                    Assert.AreEqual(1, byAgent.Count, narrowing);
                    Assert.IsTrue((bool)byAgent[0]["withheld"], narrowing + ": only the combined row, without figures.");

                    var detail = await GetAsync(host, "detail?" + narrowing);
                    Assert.AreEqual(0, (int)detail["totalRows"], narrowing);
                    Assert.IsTrue((bool)detail["smallSlicesWithheld"], narrowing + ": the empty list says why.");
                }

                // A narrowing to a group of enough people is answered, from its slices of enough people.
                var helpdesk = await GetAsync(host, "summary?agentId=agent-helpdesk");
                Assert.AreEqual(10m, (decimal)helpdesk["billedCredits"]);
                Assert.IsTrue((bool)helpdesk["smallSlicesWithheld"], "Its 2-person day is left out, and the page says so.");

                // Nothing billed at all is zero, not withheld: there is nobody to protect.
                var nobody = await GetAsync(host, "summary?agentId=agent-not-in-this-tenant");
                Assert.IsFalse((bool)nobody["withheld"]);
                Assert.IsFalse((bool)nobody["smallSlicesWithheld"]);
                Assert.AreEqual(0m, (decimal)nobody["billedCredits"]);
            }
        }

        [TestMethod]
        public async Task ForAReaderWithoutSeePii_NoTotalLessTheFiguresShown_GivesBackAWithheldSlice()
        {
            using (var host = AgentCostsHost())
            {
                var total = (decimal)(await GetAsync(host, "summary"))["billedCredits"];

                // The withheld slices (17 credits) are in no figure, so every total equals the figures shown beside it.
                foreach (var dimension in new[] { "agent", "environment", "feature" })
                {
                    var rows = (JArray)await GetAsync(host, "breakdown?dimension=" + dimension);
                    Assert.AreEqual(total, rows.Where(r => !(bool)r["withheld"]).Sum(r => (decimal)r["billedCredits"]),
                        dimension + ": the total less the rows shown is nothing, not the combined row's credits.");
                }

                var detail = await GetAsync(host, "detail?pageSize=500");
                Assert.AreEqual(total, detail["rows"].Sum(r => (decimal)r["billedCredits"]), "The total less the billed lines shown is nothing.");
                Assert.AreEqual(total, ((JArray)await GetAsync(host, "trend")).Sum(p => (decimal)p["billedCredits"]));

                var agent = await GetAsync(host, "summary?agentId=agent-helpdesk");
                var agentLines = await GetAsync(host, "detail?agentId=agent-helpdesk");
                Assert.AreEqual((decimal)agent["billedCredits"], agentLines["rows"].Sum(r => (decimal)r["billedCredits"]),
                    "An agent's total less its billed lines does not give back its 2-person day.");

                // One period less a shorter one gives back only days of enough people.
                var twoDays = (decimal)(await GetAsync(host, "summary", FloorDay1, FloorDay2))["billedCredits"];
                var oneDay = (decimal)(await GetAsync(host, "summary", FloorDay1, FloorDay1))["billedCredits"];
                Assert.AreEqual(0m, twoDays - oneDay, "The second day's slices are all small, so adding the day adds nothing.");
                var shared = (decimal)(await GetAsync(host, "summary?environmentId=" + SharedEnvironment))["billedCredits"];
                Assert.AreEqual(total, shared, "Everything less one environment does not give back the developer's own environment.");
            }
        }

        [TestMethod]
        public async Task AReaderWithSeePii_IsGivenEverySlice_AsBefore()
        {
            using (var host = AgentCostsHost(PortalRoles.SeePii))
            {
                var summary = await GetAsync(host, "summary");
                Assert.AreEqual(33m, (decimal)summary["billedCredits"], "Every slice, small or uncounted.");
                Assert.AreEqual(4, (int)summary["distinctAgents"]);
                Assert.AreEqual(3, (int)summary["daysWithUsage"]);
                Assert.AreEqual(JTokenType.Null, summary["minPeoplePerSlice"].Type);
                Assert.IsFalse((bool)summary["smallSlicesWithheld"]);
                Assert.IsFalse((bool)summary["withheld"]);

                Assert.AreEqual(3, ((JArray)await GetAsync(host, "trend")).Count);

                var byAgent = (JArray)await GetAsync(host, "breakdown?dimension=agent");
                Assert.AreEqual(4, byAgent.Count, "No combined row.");
                Assert.IsTrue(byAgent.All(r => !(bool)r["withheld"]));
                Assert.AreEqual(1, (int)byAgent.Single(r => (string)r["key"] == "agent-sandbox")["peakDistinctUsers"]);

                var detail = await GetAsync(host, "detail");
                Assert.AreEqual(6, (int)detail["totalRows"]);
                Assert.IsFalse((bool)detail["smallSlicesWithheld"]);

                var sandbox = await GetAsync(host, "summary?agentId=agent-sandbox");
                Assert.AreEqual(5m, (decimal)sandbox["billedCredits"]);

                Assert.AreEqual(4, (await GetAsync(host, "filters"))["agents"].Count());
            }
        }

        #endregion
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
