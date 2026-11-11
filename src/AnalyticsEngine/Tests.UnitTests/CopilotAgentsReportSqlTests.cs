extern alias AnalyticsWeb;

using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using ReportAreaData = AnalyticsWeb::Web.AnalyticsWeb.Models.ReportAreaData;
using ReportChart = AnalyticsWeb::Web.AnalyticsWeb.Models.ReportChart;
using ReportsAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.ReportsAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// Reports → Copilot agents (#844, G3) against a real, throwaway SQL Server database: the query the area runs, with
    /// the production column types, materialised through <c>Database.SqlQuery&lt;T&gt;</c> as the controller does, then
    /// shaped into the two charts and trimmed for a reader without See PII.
    /// </summary>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class CopilotAgentsReportSqlTests
    {
        /// <summary>A Monday, so the week buckets below are exact.</summary>
        private static readonly DateTime From = new DateTime(2026, 6, 1);

        private static readonly List<DateTime> Weeks = Enumerable.Range(0, 4).Select(w => From.AddDays(7 * w)).ToList();

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
        public void TheAgentsQuery_CountsTheDistinctPeopleBehindEachAgent_AndRanksAgentsOfFewerThanFiveApart()
        {
            using (var db = ScratchDatabase.Create("CopilotAgentsReport"))
            {
                CreateTables(db);
                SeedAgents(db);

                var top2 = Query(db, top: 2, agentName: null);
                var people = top2.GroupBy(r => r.SeriesName).ToDictionary(g => g.Key, g => g.Select(r => r.People).Distinct().Single());
                CollectionAssert.AreEquivalent(new[] { "Contoso Personal", "Contoso Helpdesk", "Contoso Five" }, people.Keys.ToArray(),
                    "The top two, and the next agent five or more people used; nothing else.");
                Assert.AreEqual(1, people["Contoso Personal"], "One person, however many executions.");
                Assert.AreEqual(6, people["Contoso Helpdesk"], "Six people, one of them in two weeks, and an event with no user is nobody.");
                Assert.AreEqual(5, people["Contoso Five"], "Maker testing and a duplicate record of a turn add nobody.");

                CollectionAssert.AreEquivalent(new[] { "Contoso Personal", "Contoso Helpdesk" },
                    top2.Where(r => r.InTop).Select(r => r.SeriesName).Distinct().ToArray());
                Assert.AreEqual(14d, top2.Where(r => r.SeriesName == "Contoso Helpdesk").Sum(r => r.Value),
                    "Executions are counted as before: every counted turn, with or without a user.");
                Assert.AreEqual(10d, top2.Where(r => r.SeriesName == "Contoso Five").Sum(r => r.Value));

                var everyone = Query(db, top: 8, agentName: "Contoso");
                CollectionAssert.AreEquivalent(new[] { "Contoso Personal", "Contoso Helpdesk", "Contoso Five", "Contoso Pair" },
                    everyone.Select(r => r.SeriesName).Distinct().ToArray(), "The name filter, and the window: Contoso Old is all before it.");
                Assert.AreEqual(2, everyone.First(r => r.SeriesName == "Contoso Pair").People);

                // Under the administrator's global filter, people are counted within the reader's scope.
                var scoped = Query(db, top: 8, agentName: null, scope: ReportUserScope.ForUsers(new[] { 2, 3, 4, 5, 6 }));
                CollectionAssert.AreEquivalent(new[] { "Contoso Helpdesk", "Contoso Five" }, scoped.Select(r => r.SeriesName).Distinct().ToArray());
                Assert.AreEqual(5, scoped.First(r => r.SeriesName == "Contoso Helpdesk").People);
                Assert.AreEqual(10d, scoped.Where(r => r.SeriesName == "Contoso Helpdesk").Sum(r => r.Value));
            }
        }

        [TestMethod]
        public void AReaderWithoutSeePii_GetsTheTopAgentsOfFiveOrMorePeople_AndASeePiiReaderTheTopAgents()
        {
            using (var db = ScratchDatabase.Create("CopilotAgentsReport"))
            {
                CreateTables(db);
                SeedAgents(db);

                var area = Area(Query(db, top: 2, agentName: null));
                var trimmed = area.WithoutSmallGroups(5);

                CollectionAssert.AreEqual(new[] { "Contoso Personal", "Contoso Helpdesk" },
                    area.Charts[0].Categories.Select(c => c.Label).ToArray(), "A See PII reader keeps today's top two.");
                CollectionAssert.AreEqual(new[] { 30d, 14d }, area.Charts[0].Categories.Select(c => c.Value).ToArray());
                CollectionAssert.AreEqual(new[] { "Contoso Personal", "Contoso Helpdesk" }, area.Charts[1].Series.Select(s => s.Name).ToArray());
                Assert.IsNull(area.Charts[0].MinimumGroupSize);

                CollectionAssert.AreEqual(new[] { "Contoso Helpdesk", "Contoso Five" },
                    trimmed.Charts[0].Categories.Select(c => c.Label).ToArray(),
                    "One person's agent is left out, and the next agent five people used takes its place.");
                CollectionAssert.AreEqual(new[] { "Contoso Helpdesk", "Contoso Five" }, trimmed.Charts[1].Series.Select(s => s.Name).ToArray(),
                    "Its weekly line goes with it.");
                CollectionAssert.AreEqual(new double?[] { 13, 1, 0, 0 }, trimmed.Charts[1].Series[0].Points.Select(p => p.Value).ToArray());
                Assert.AreEqual(5, trimmed.Charts[0].MinimumGroupSize);
                Assert.AreEqual(5, trimmed.Charts[1].MinimumGroupSize);

                var json = JsonConvert.SerializeObject(trimmed);
                Assert.IsFalse(json.Contains("Contoso Personal") || json.Contains("Contoso Pair"), "No trace of an agent of fewer than five.");
                Assert.IsFalse(json.Contains("withheldItems"), "How many were left out would say where they rank.");
            }
        }

        [TestMethod]
        public void AReaderWithoutSeePii_NarrowingToAnAgentOfFewerThanFive_GetsWhatANameMatchingNothingGets()
        {
            using (var db = ScratchDatabase.Create("CopilotAgentsReport"))
            {
                CreateTables(db);
                SeedAgents(db);

                var pair = Area(Query(db, top: 8, agentName: "Contoso Pair"));
                var nothing = Area(Query(db, top: 8, agentName: "Contoso Nobody"));

                Assert.AreEqual("Contoso Pair", pair.Charts[0].Categories.Single().Label, "A See PII reader still finds it.");
                Assert.AreEqual(0, nothing.Charts[0].Categories.Count);

                var trimmedPair = pair.WithoutSmallGroups(5);
                Assert.AreEqual(0, trimmedPair.Charts[0].Categories.Count);
                Assert.AreEqual(0, trimmedPair.Charts[1].Series.Count);
                Assert.AreEqual(5, trimmedPair.Charts[0].MinimumGroupSize, "Withheld, so said to be: never an empty chart that reads as none.");
                Assert.AreEqual(
                    JsonConvert.SerializeObject(nothing.WithoutSmallGroups(5)),
                    JsonConvert.SerializeObject(trimmedPair),
                    "A name that matches only agents of fewer than five must not tell this reader that one exists.");
            }
        }

        /// <summary>Both charts of the area, as the controller builds them from the query's rows.</summary>
        private static ReportAreaData Area(List<ReportsAPIController.NamedWeekValueRow> rows)
        {
            var totals = new ReportChart { Key = "copilot-agent-totals", Type = "bar", Sql = "SELECT 1" };
            var weekly = new ReportChart { Key = "copilot-agent-interactions", Type = "timeseries", Sql = "SELECT 1" };
            ReportsAPIController.SetAgentTotals(totals, rows);
            ReportsAPIController.SetAgentSeries(weekly, rows, Weeks);
            return new ReportAreaData { Area = "copilot-agents", Charts = new List<ReportChart> { totals, weekly } };
        }

        private static List<ReportsAPIController.NamedWeekValueRow> Query(ScratchDatabase db, int top, string agentName, ReportUserScope scope = null)
        {
            scope = scope ?? ReportUserScope.Everyone;
            var sql = ReportScopeSql.Apply(ReportsAPIController.BuildCopilotAgentsQuery(From, top, agentName != null), scope);
            using (var context = new RawSqlContext(db.ConnectionString))
            {
                return context.Database
                    .SqlQuery<ReportsAPIController.NamedWeekValueRow>(
                        sql, ReportScopeSql.WithScope(ReportsAPIController.CopilotAgentsParameters(From, agentName), scope))
                    .ToList();
            }
        }

        /// <summary>
        /// Contoso Personal: one person, 30 executions, the most used. Contoso Helpdesk: six people, one of them in two
        /// weeks, and an event with no user. Contoso Five: five people, plus a sixth who only tested it in Copilot Studio and
        /// a seventh whose only record is a duplicate of a turn. Contoso Pair: two people. Fabrikam Team: seven people, one
        /// execution each. Contoso Old: nine people, all before the window.
        /// </summary>
        private static void SeedAgents(ScratchDatabase db)
        {
            db.Execute(
                @"INSERT INTO dbo.copilot_agents (id, name, agent_id, is_custom_agent) VALUES
                      (1, N'Contoso Personal', N'CopilotStudio.Declarative.00000000-0000-0000-0000-000000000001', 1),
                      (2, N'Contoso Helpdesk', N'CopilotStudio.Declarative.00000000-0000-0000-0000-000000000002', 1),
                      (3, N'Contoso Five', N'CopilotStudio.Declarative.00000000-0000-0000-0000-000000000003', 1),
                      (4, N'Contoso Pair', N'CopilotStudio.Declarative.00000000-0000-0000-0000-000000000004', 1),
                      (5, N'Fabrikam Team', N'CopilotStudio.Declarative.00000000-0000-0000-0000-000000000005', 1),
                      (6, N'Contoso Old', N'CopilotStudio.Declarative.00000000-0000-0000-0000-000000000006', 1);");

            var turns = new List<(int Agent, int? User, DateTime When, string Host)>();
            void Add(int agent, int? user, int week, int count, string host = "Teams")
            {
                for (var i = 0; i < count; i++) turns.Add((agent, user, From.AddDays(7 * week + 1).AddMinutes(i), host));
            }

            Add(1, 1, 0, 10);
            Add(1, 1, 1, 10);
            Add(1, 1, 2, 10);
            for (var user = 1; user <= 6; user++) Add(2, user, 0, 2);
            Add(2, 1, 1, 1);
            Add(2, null, 0, 1);
            for (var user = 2; user <= 6; user++) Add(3, user, 0, 2);
            Add(3, 7, 0, 3, host: Common.Entities.Copilot.CopilotTurnSql.MakerTestAppHost);
            Add(4, 7, 0, 4);
            Add(4, 8, 1, 4);
            for (var user = 10; user <= 16; user++) Add(5, user, 0, 1);
            for (var user = 20; user <= 28; user++) turns.Add((6, user, From.AddDays(-30), "Teams"));

            var values = turns.Select(t =>
            {
                var when = t.When.ToString("yyyy-MM-ddTHH:mm:ss");
                return $"(NEWID(), {t.Agent}, {(t.User.HasValue ? t.User.Value.ToString() : "NULL")}, '{when}', N'{t.Host}')";
            });
            db.Execute(
                "CREATE TABLE #turns (event_id uniqueidentifier, agent_id int, user_id int NULL, time_stamp datetime, app_host nvarchar(100));\r\n" +
                "INSERT INTO #turns VALUES " + string.Join(",\r\n", values) + ";\r\n" +
                "INSERT INTO dbo.audit_events (id, time_stamp, operation_id, user_id) SELECT event_id, time_stamp, 1, user_id FROM #turns;\r\n" +
                "INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, user_id, time_stamp) SELECT event_id, app_host, agent_id, user_id, time_stamp FROM #turns;\r\n" +
                // A seventh person on Contoso Five whose only record is the extra record of a turn counted on another.
                "DECLARE @duplicate uniqueidentifier = NEWID(), @counted uniqueidentifier = (SELECT TOP 1 event_id FROM #turns WHERE agent_id = 3 AND user_id = 2);\r\n" +
                $"INSERT INTO dbo.audit_events (id, time_stamp, operation_id, user_id) VALUES (@duplicate, '{From.AddDays(2):yyyy-MM-dd}', 1, 9);\r\n" +
                $"INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, user_id, time_stamp) VALUES (@duplicate, N'Teams', 3, 9, '{From.AddDays(2):yyyy-MM-dd}');\r\n" +
                $"INSERT INTO dbo.copilot_chat_duplicates (event_id, time_stamp, counted_event_id, reason) VALUES (@duplicate, '{From.AddDays(2):yyyy-MM-dd}', @counted, 1);");
        }

        /// <summary>
        /// The tables the query reads, with the production column types and indexes: <c>audit_events</c> with
        /// <c>IX_audit_events_time_stamp</c> (migration IndexReportDateQueries), and the Copilot tables as
        /// <see cref="CopilotAdoptionAgentGrowthSqlTests"/> builds them.
        /// </summary>
        private static void CreateTables(ScratchDatabase db)
        {
            db.Execute(
                @"CREATE TABLE dbo.audit_events (
                      id uniqueidentifier NOT NULL PRIMARY KEY,
                      time_stamp datetime NOT NULL,
                      operation_id int NULL,
                      user_id int NULL,
                      event_data nvarchar(max) NULL);
                  CREATE NONCLUSTERED INDEX IX_audit_events_time_stamp
                      ON dbo.audit_events ([time_stamp]) INCLUDE ([operation_id], [user_id]);

                  CREATE TABLE dbo.copilot_chats (
                      event_id uniqueidentifier NOT NULL PRIMARY KEY,
                      app_host nvarchar(max) NULL,
                      agent_id int NULL,
                      user_id int NULL,
                      time_stamp datetime NULL);
                  CREATE NONCLUSTERED INDEX IX_copilot_chats_time_stamp_user_id
                      ON dbo.copilot_chats ([time_stamp], [user_id])
                      INCLUDE ([app_host], [agent_id]);

                  CREATE TABLE dbo.copilot_chat_duplicates (
                      event_id uniqueidentifier NOT NULL PRIMARY KEY,
                      time_stamp datetime NOT NULL,
                      counted_event_id uniqueidentifier NOT NULL,
                      reason tinyint NOT NULL);
                  CREATE NONCLUSTERED INDEX IX_copilot_chat_duplicates_time_stamp
                      ON dbo.copilot_chat_duplicates (time_stamp);

                  CREATE TABLE dbo.copilot_agents (
                      id int NOT NULL PRIMARY KEY,
                      name nvarchar(100) NULL,
                      agent_id nvarchar(max) NULL,
                      is_custom_agent bit NULL);");
        }
    }
}
