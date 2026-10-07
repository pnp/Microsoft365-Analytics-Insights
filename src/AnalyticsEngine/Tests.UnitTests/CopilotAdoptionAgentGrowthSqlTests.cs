using Common.Entities.CopilotAdoption;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// The agent growth queries (#645) against a real, throwaway SQL Server database, through the same
    /// <c>Database.SqlQuery&lt;T&gt;</c> materialisation the service uses - so a column that does not map, a
    /// window boundary that is off by a day, or a batch that does not parse fails here rather than on a
    /// customer's Agents tab. Follows <see cref="CopilotAdoptionSqlIntegrationTests"/>: each test builds only
    /// the tables it reads, with the production column types, and drops the database afterwards.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionAgentGrowthSqlTests
    {
        /// <summary>A fixed last settled day, so every boundary below is an exact instant.</summary>
        private static readonly DateTime LastSettled = new DateTime(2026, 10, 4);

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

        [TestMethod]
        public void AgentGrowthSql_PutsEachInteractionInItsClosedWindowAndLeavesOutTheUnsettledDays()
        {
            using (var db = ScratchDatabase.Create("AgentGrowth"))
            {
                CreateCopilotTables(db);
                var windows = CopilotAdoptionAgentGrowth.Windows(LastSettled);

                db.Execute(
                    @"INSERT INTO dbo.copilot_agents (id, name, agent_id, is_custom_agent) VALUES
                          (1, N'Contoso Expenses', N'CopilotStudio.Declarative.00000000-0000-0000-0000-000000000001', 1),
                          (2, N'Καλημέρα κόσμε', N'SPO_00000000-0000-0000-0000-000000000002', NULL),
                          (3, N'Contoso Archive', N'CopilotStudio.CustomEngine.00000000-0000-0000-0000-000000000003', 1);");

                // The first Copilot interaction the audit log holds: well before the series.
                Seed(db, new DateTime(2025, 1, 15, 9, 0, 0), userId: 9, agentId: null, appHost: "Teams");

                // Window 0: the last instant of the last settled day, and the first instant of the window.
                // One person, two agents - one agent user, two active agents.
                Seed(db, LastSettled.AddDays(1).AddSeconds(-1), userId: 1, agentId: 1, appHost: "Teams");
                Seed(db, windows[0].FromUtc, userId: 1, agentId: 2, appHost: "Word");

                // Unsettled: the day after the last settled day is not read at all.
                Seed(db, LastSettled.AddDays(1), userId: 2, agentId: 1, appHost: "Teams");
                Seed(db, LastSettled.AddDays(3).AddHours(8), userId: 2, agentId: 3, appHost: "Teams");

                // Window 1: one second before window 0 starts.
                Seed(db, windows[0].FromUtc.AddSeconds(-1), userId: 3, agentId: 2, appHost: "cowork");

                // Window 5: Copilot used, but no agent - a measured zero.
                Seed(db, windows[5].FromUtc.AddDays(3), userId: 4, agentId: null, appHost: "Outlook");

                // Window 7: nothing at all - unmeasured. (No row.)

                // Window 13: an agent used only a year ago, at the first instant of the series.
                Seed(db, windows[13].FromUtc, userId: 5, agentId: 3, appHost: "Teams");
                Seed(db, windows[13].FromUtc.AddDays(2), userId: 5, agentId: 3, appHost: "Teams");

                // Before the series: not read.
                Seed(db, windows[13].FromUtc.AddSeconds(-1), userId: 6, agentId: 1, appHost: "Teams");

                // An agent interaction attributed to nobody is not user-initiated use.
                db.Execute(
                    $@"INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, user_id, time_stamp)
                           VALUES (NEWID(), N'Teams', 1, NULL, '{windows[2].FromUtc.AddDays(1):yyyy-MM-dd HH:mm:ss}');");

                // Every other window gets a non-agent interaction, so only window 7 is uncovered.
                foreach (var window in windows.Where(w => w.WindowsAgo != 7 && w.WindowsAgo != 5))
                {
                    Seed(db, window.FromUtc.AddDays(14), userId: 9, agentId: null, appHost: "Teams");
                }

                var rows = Query<AgentGrowthQueryRow>(db, CopilotAdoptionSql.AgentGrowthSql, GrowthParameters(null));

                Assert.AreEqual(14, rows.Count, "Every window gets a row, data or not.");
                CollectionAssert.AreEqual(Enumerable.Range(0, 14).ToList(), rows.Select(r => r.WindowsAgo).ToList());
                Assert.IsTrue(rows.All(r => r.FirstCopilotInteractionUtc == new DateTime(2025, 1, 15, 9, 0, 0)));

                AssertRow(rows[0], activeAgents: 2, users: 1, interactions: 2, hasData: true);
                AssertRow(rows[1], activeAgents: 1, users: 1, interactions: 1, hasData: true);
                AssertRow(rows[2], activeAgents: 0, users: 0, interactions: 0, hasData: true);
                AssertRow(rows[5], activeAgents: 0, users: 0, interactions: 0, hasData: true);
                AssertRow(rows[7], activeAgents: 0, users: 0, interactions: 0, hasData: false);
                AssertRow(rows[13], activeAgents: 1, users: 1, interactions: 2, hasData: true);

                var built = CopilotAdoptionAgentGrowth.Build(LastSettled, rows, billing: null);
                Assert.AreEqual(2, built[0].ActiveAgents);
                Assert.AreEqual(2d, built[0].InteractionsPerAgentUser);
                Assert.AreEqual(0, built[5].ActiveAgents, "Copilot was used, no agent was: zero.");
                Assert.IsNull(built[7].ActiveAgents, "The audit log holds nothing for window 7: blank, not zero.");
                Assert.AreEqual(1, built[13].ActiveAgents, "The agent used only a year ago counts there.");

                // The scope seam: an agent left out as out of scope is left out of every figure.
                var scoped = Query<AgentGrowthQueryRow>(db,
                    CopilotAdoptionSql.AgentGrowthSql,
                    GrowthParameters(new[] { new AgentGrowthExclusion { AgentId = 2, UnknownOrigin = false } }));
                AssertRow(scoped[0], activeAgents: 1, users: 1, interactions: 1, hasData: true);
                AssertRow(scoped[1], activeAgents: 0, users: 0, interactions: 0, hasData: true);
                AssertRow(scoped[13], activeAgents: 1, users: 1, interactions: 2, hasData: true);
                Assert.IsTrue(scoped.All(r => r.UnknownOriginAgents == 0), "Out of scope is not the unknown-origin gap.");

                var agents = Query<AgentGrowthAgentRow>(db, CopilotAdoptionSql.AgentGrowthAgentsSql);
                Assert.AreEqual(3, agents.Count);
                Assert.AreEqual("SPO_00000000-0000-0000-0000-000000000002", agents.Single(a => a.AgentId == 2).AgentKey);
                Assert.IsNull(agents.Single(a => a.AgentId == 2).IsCustomAgent, "A stored NULL reaches the classifier as NULL.");
                Assert.IsTrue(agents.Single(a => a.AgentId == 1).IsCustomAgent == true);
            }
        }

        [TestMethod]
        public void AgentGrowthSql_CustomerBuiltScope_LeavesOutMicrosoftAndUnknownOriginAgents_AndCountsTheGap()
        {
            using (var db = ScratchDatabase.Create("AgentGrowthScope"))
            {
                CreateCopilotTables(db);
                var windows = CopilotAdoptionAgentGrowth.Windows(LastSettled);

                db.Execute(
                    @"INSERT INTO dbo.copilot_agents (id, name, agent_id, is_custom_agent) VALUES
                          (1, N'Contoso Expenses', N'CopilotStudio.Declarative.00000000-0000-0000-0000-000000000001', NULL),
                          (2, N'Καλημέρα κόσμε', N'SPO_00000000-0000-0000-0000-000000000002', NULL),
                          (3, N'Copilot Cowork', N'Copilot.M365Copilot.CoworkChat', NULL),
                          (4, N'Contoso declared custom', N'T_00000000-0000-0000-0000-000000000004', 1),
                          (5, N'Contoso built-in', N'BuiltIn_Contoso', 0);");

                Seed(db, new DateTime(2025, 1, 15, 9, 0, 0), userId: 9, agentId: null, appHost: "Teams");

                // Window 0: two customer-built agents (one only by its stored flag), two of unknown origin, and
                // Microsoft's Cowork.
                Seed(db, windows[0].FromUtc.AddDays(1), userId: 1, agentId: 1, appHost: "Teams");
                Seed(db, windows[0].FromUtc.AddDays(2), userId: 1, agentId: 1, appHost: "Word");
                Seed(db, windows[0].FromUtc.AddDays(3), userId: 4, agentId: 4, appHost: "Teams");
                Seed(db, windows[0].FromUtc.AddDays(4), userId: 2, agentId: 2, appHost: "Teams");
                Seed(db, windows[0].FromUtc.AddDays(5), userId: 1, agentId: 5, appHost: "Teams");
                Seed(db, windows[0].FromUtc.AddDays(6), userId: 3, agentId: 3, appHost: "cowork");

                // Window 13: only Microsoft's agent was used - a measured zero, not a gap.
                Seed(db, windows[13].FromUtc.AddDays(1), userId: 5, agentId: 3, appHost: "cowork");

                var agents = Query<AgentGrowthAgentRow>(db, CopilotAdoptionSql.AgentGrowthAgentsSql);
                var exclusions = CopilotAdoptionAgentGrowth.Exclusions(agents, CopilotAdoptionAgentGrowth.Scope);
                CollectionAssert.AreEqual(new[] { 2, 3, 5 }, exclusions.Select(x => x.AgentId).ToArray());
                CollectionAssert.AreEqual(new[] { true, false, true }, exclusions.Select(x => x.UnknownOrigin).ToArray(),
                    "The SharePoint agent and the built-in are of unknown origin; Cowork is Microsoft's.");

                var rows = Query<AgentGrowthQueryRow>(db, CopilotAdoptionSql.AgentGrowthSql, GrowthParameters(exclusions));

                AssertRow(rows[0], activeAgents: 2, users: 2, interactions: 3, hasData: true);
                Assert.AreEqual(2, rows[0].UnknownOriginAgents,
                    "Agents of unknown origin are counted on their own, not added to the active agents.");
                AssertRow(rows[13], activeAgents: 0, users: 0, interactions: 0, hasData: true);
                Assert.AreEqual(0, rows[13].UnknownOriginAgents, "Microsoft's own agent is out of scope, not part of the gap.");

                var built = CopilotAdoptionAgentGrowth.Build(LastSettled, rows, billing: null);
                Assert.AreEqual(2, built[0].ActiveAgents);
                Assert.AreEqual(2, built[0].UnknownOriginAgents);
                Assert.AreEqual(1.5, built[0].InteractionsPerAgentUser);
                Assert.AreEqual(0, built[13].ActiveAgents, "Cowork alone a year ago: a measured zero for customer-built agents.");
            }
        }

        [TestMethod]
        public void AgentGrowthBillingSql_CountsBilledAgentsPerWindow_AndReturnsNothingForAWindowWithoutRows()
        {
            using (var db = ScratchDatabase.Create("AgentGrowthBilling"))
            {
                CreateCopilotStudioCreditDaily(db);
                var windows = CopilotAdoptionAgentGrowth.Windows(LastSettled);

                // Window 0: two agents billed (one on two days, one on two features), one consumed nothing.
                SeedBilling(db, windows[0].ToUtc, "00000000-0000-0000-0000-0000000000a1", 2.5m, "h1");
                SeedBilling(db, windows[0].FromUtc, "00000000-0000-0000-0000-0000000000a1", 1m, "h1");
                SeedBilling(db, windows[0].FromUtc, "00000000-0000-0000-0000-0000000000a2", 10m, "h2");
                SeedBilling(db, windows[0].FromUtc, "00000000-0000-0000-0000-0000000000a2", 4m, "h3");
                SeedBilling(db, windows[0].FromUtc, "00000000-0000-0000-0000-0000000000a3", 0m, "h4");

                // Window 2: the import ran and nothing was billed.
                SeedBilling(db, windows[2].FromUtc.AddDays(4), "00000000-0000-0000-0000-0000000000a3", 0m, "h4");

                // The unsettled day after the last settled day is not read.
                SeedBilling(db, LastSettled.AddDays(1), "00000000-0000-0000-0000-0000000000a4", 9m, "h5");

                var rows = Query<AgentGrowthBillingRow>(db, CopilotAdoptionSql.AgentGrowthBillingSql, Parameters());

                Assert.AreEqual(2, rows.Count, "Only windows with billing rows come back.");
                Assert.AreEqual(2, rows.Single(r => r.WindowsAgo == 0).BilledAgents);
                Assert.AreEqual(0, rows.Single(r => r.WindowsAgo == 2).BilledAgents);

                var built = CopilotAdoptionAgentGrowth.Build(LastSettled, usage: null, billing: rows);
                Assert.AreEqual(2, built[0].CopilotStudioBilledAgents);
                Assert.AreEqual(0, built[2].CopilotStudioBilledAgents);
                Assert.IsNull(built[1].CopilotStudioBilledAgents, "No billing rows for window 1: blank, not zero.");
            }
        }

        [TestMethod]
        public void AgentGrowthBillingSql_OnADatabaseWithoutTheBillingTable_ReturnsAnEmptyResult()
        {
            using (var db = ScratchDatabase.Create("AgentGrowthNoBilling"))
            {
                var rows = Query<AgentGrowthBillingRow>(db, CopilotAdoptionSql.AgentGrowthBillingSql, Parameters());

                Assert.AreEqual(0, rows.Count,
                    "A database that predates the agent-cost migration has no billing table; the evidence is blank, not an error.");
            }
        }

        #region Fixture

        private static SqlParameter[] Parameters()
        {
            return new[]
            {
                new SqlParameter("@lastSettledDay", LastSettled),
                new SqlParameter("@seriesFrom", CopilotAdoptionAgentGrowth.SeriesFromUtc(LastSettled)),
                new SqlParameter("@seriesToExclusive", CopilotAdoptionAgentGrowth.SeriesToExclusiveUtc(LastSettled)),
            };
        }

        /// <summary>The window parameters plus the scope's, exactly as the service sends them.</summary>
        private static SqlParameter[] GrowthParameters(IEnumerable<AgentGrowthExclusion> exclusions)
        {
            return Parameters().Concat(CopilotAdoptionSql.AgentGrowthScopeParameters(exclusions)).ToArray();
        }

        private static void AssertRow(AgentGrowthQueryRow row, int activeAgents, int users, long interactions, bool hasData)
        {
            Assert.AreEqual(activeAgents, row.ActiveAgents, $"Active agents in window {row.WindowsAgo}.");
            Assert.AreEqual(users, row.AgentUsers, $"Agent users in window {row.WindowsAgo}.");
            Assert.AreEqual(interactions, row.AgentInteractions, $"Agent interactions in window {row.WindowsAgo}.");
            Assert.AreEqual(hasData, row.HasCopilotData, $"Copilot data in window {row.WindowsAgo}.");
        }

        private static List<T> Query<T>(ScratchDatabase db, string sql, params SqlParameter[] parameters)
        {
            using (var context = new RawSqlContext(db.ConnectionString))
            {
                context.Database.CommandTimeout = 120;
                return context.Database.SqlQuery<T>(sql, parameters).ToList();
            }
        }

        /// <summary>
        /// The Copilot tables with production column types: <c>copilot_chats</c> GUID-clustered with the
        /// denormalised <c>user_id</c> / <c>time_stamp</c> and <c>IX_copilot_chats_time_stamp_user_id</c>
        /// (migration DenormaliseCopilotChatUserAndTime), and <c>copilot_agents</c> as EF maps it.
        /// </summary>
        private static void CreateCopilotTables(ScratchDatabase db)
        {
            db.Execute(
                @"CREATE TABLE dbo.copilot_chats (
                      event_id uniqueidentifier NOT NULL PRIMARY KEY,
                      app_host nvarchar(max) NULL,
                      agent_id int NULL,
                      user_id int NULL,
                      time_stamp datetime NULL);

                  CREATE NONCLUSTERED INDEX IX_copilot_chats_time_stamp_user_id
                      ON dbo.copilot_chats ([time_stamp], [user_id])
                      INCLUDE ([app_host], [agent_id]);

                  CREATE TABLE dbo.copilot_agents (
                      id int NOT NULL PRIMARY KEY,
                      name nvarchar(100) NULL,
                      agent_id nvarchar(max) NULL,
                      is_custom_agent bit NULL);");
        }

        /// <summary>
        /// <c>copilot_studio_credit_daily</c> as migrations AgentCostTables and
        /// DropUnreportedCopilotStudioCreditColumns leave it.
        /// </summary>
        private static void CreateCopilotStudioCreditDaily(ScratchDatabase db)
        {
            db.Execute(
                @"CREATE TABLE dbo.copilot_studio_credit_daily (
                      id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                      usage_date datetime NOT NULL,
                      environment_id nvarchar(200) NULL,
                      environment_name nvarchar(255) NULL,
                      agent_id nvarchar(200) NULL,
                      agent_name nvarchar(255) NULL,
                      harness nvarchar(50) NULL,
                      feature_name nvarchar(200) NULL,
                      billed_credits decimal(18,6) NOT NULL,
                      non_billed_credits decimal(18,6) NULL,
                      distinct_users int NULL,
                      last_refreshed_utc datetime NULL,
                      dimension_hash nvarchar(64) NOT NULL,
                      imported_utc datetime NOT NULL);

                  CREATE UNIQUE NONCLUSTERED INDEX IX_usage_date_dimension_hash
                      ON dbo.copilot_studio_credit_daily (usage_date, dimension_hash);");
        }

        private static void Seed(ScratchDatabase db, DateTime whenUtc, int userId, int? agentId, string appHost)
        {
            db.Execute(
                $@"INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, user_id, time_stamp)
                       VALUES (NEWID(), N'{appHost}', {(agentId.HasValue ? agentId.Value.ToString() : "NULL")}, {userId},
                               '{whenUtc:yyyy-MM-dd HH:mm:ss}');");
        }

        private static void SeedBilling(ScratchDatabase db, DateTime usageDate, string agentId, decimal billed, string hash)
        {
            db.Execute(
                $@"INSERT INTO dbo.copilot_studio_credit_daily
                       (usage_date, environment_id, agent_id, agent_name, feature_name, billed_credits, dimension_hash, imported_utc)
                   VALUES ('{usageDate:yyyy-MM-dd}', N'00000000-0000-0000-0000-000000000000', N'{agentId}',
                           N'Contoso Studio agent', N'Generative answer', {billed.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                           N'{hash}', '{usageDate:yyyy-MM-dd}');");
        }

        #endregion
    }
}
