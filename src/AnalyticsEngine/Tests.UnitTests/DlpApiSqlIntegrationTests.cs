extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity.Infrastructure.Interception;
using System.Data.Entity.Migrations;
using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configuration = Common.Entities.Migrations.Configuration;
using DlpAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.DlpAPIController;
using DlpImpactRow = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpImpactRow;
using DlpSummary = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpSummary;

namespace Tests.UnitTests
{
    /// <summary>
    /// Runs the DLP report's queries against a real, throwaway SQL Server database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The controller's aggregates are EF LINQ, and EF6 decides at RUN TIME whether an expression can
    /// be translated to SQL. A <c>GroupBy</c> projecting into a class, <c>SqlFunctions.StringConvert</c>
    /// and <c>DbFunctions.TruncateTime</c> all compile happily and can still throw
    /// <c>NotSupportedException</c> the first time the page is opened - so compiling proves nothing
    /// here.
    /// </para>
    /// <para>
    /// This is the second defect of exactly that shape in this feature: the first was a JSON casing
    /// mismatch that also compiled cleanly and only failed in a browser (see
    /// <see cref="DlpApiContractTests"/>). Both were invisible to every other test, which is why this
    /// one pays the cost of building a real schema.
    /// </para>
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class DlpApiSqlIntegrationTests
    {
        private static string _database;
        private static string _connectionString;

        private const string MasterConnection =
            @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=True";

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _database = "DlpApiIntegration_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _connectionString =
                $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={_database};Integrated Security=true;MultipleActiveResultSets=True;TrustServerCertificate=True";

            Execute(MasterConnection, ScratchDatabase.CreateDatabaseSql(_database));

            // The full migration chain, so the queries run against the schema customers actually get
            // rather than a hand-built subset that could omit the very column a query needs.
            var migrationConfig = new Configuration
            {
                TargetDatabase = new System.Data.Entity.Infrastructure.DbConnectionInfo(_connectionString, "Microsoft.Data.SqlClient")
            };
            new DbMigrator(migrationConfig).Update();
        }

        [ClassCleanup]
        public static void DropDatabase()
        {
            if (_database == null) return;
            SqlConnection.ClearAllPools();
            Execute(MasterConnection,
                $"IF DB_ID('{_database}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END");
        }

        private static void Execute(string connectionString, string sql)
        {
            using (var connection = new SqlConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = sql;
                command.CommandTimeout = 0;
                command.ExecuteNonQuery();
            }
        }

        private static DlpAPIController NewController() =>
            new DlpAPIController(new ConnectionStringAnalyticsDbContextFactory(_connectionString));

        /// <summary>
        /// Every aggregate in one pass, against seeded data whose expected answers are known.
        /// </summary>
        [TestMethod]
        public async Task SummaryExecutesEveryQueryAndCountsBlockedSeparatelyFromAudited()
        {
            var now = DateTime.UtcNow;

            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var user = new User { UserPrincipalName = "ada@contoso.com" };
                db.users.Add(user);

                var agent = new CopilotAgent { AgentID = "CopilotStudio.Declarative.demo", Name = "Contoso HR Agent" };
                db.CopilotAgents.Add(agent);

                // audit_events.operation_id is a real foreign key, so the operation has to exist.
                var operation = new EventOperation { Name = "CopilotInteraction" };
                db.event_operations.Add(operation);

                var policy = new DlpPolicy { PolicyId = "pol-1", Name = "Block Copilot on Confidential" };
                db.dlp_policies.Add(policy);
                var action = new DlpAction { Name = "BlockAccess" };
                db.dlp_actions.Add(action);
                var label = new SensitivityLabel { LabelId = "00000000-0000-0000-0000-00000000c001" };
                db.SensitivityLabels.Add(label);
                db.SaveChanges();

                var rule = new DlpRule
                {
                    RuleId = "rule-1",
                    Name = "Confidential label",
                    DlpPolicyId = policy.ID,
                    Severity = "High",
                    RuleMode = "Enforce",
                };
                db.dlp_rules.Add(rule);
                db.SaveChanges();

                // Two interactions in the window: one blocked, one audited-only.
                for (int i = 0; i < 2; i++)
                {
                    var eventId = Guid.NewGuid();
                    db.AuditEventsCommon.Add(new CommonAuditEvent
                    {
                        Id = eventId,
                        TimeStamp = now.AddDays(-1),
                        User = user,
                        Operation = operation,
                    });
                    db.CopilotChats.Add(new CopilotChat
                    {
                        EventID = eventId,
                        AppHost = "BizChat",
                        Agent = agent,
                        UserId = user.ID,
                        TimeStampUtc = now.AddDays(-1),
                    });
                    db.SaveChanges();

                    db.copilot_dlp_events.Add(new CopilotDlpEvent
                    {
                        ChatId = eventId,
                        DlpPolicyId = policy.ID,
                        DlpRuleId = rule.ID,
                        DlpActionId = action.ID,
                        SensitivityLabelId = label.ID,
                        IsBlocked = i == 0,
                    });
                    db.dlp_rule_matches.Add(new DlpRuleMatch
                    {
                        EventId = eventId,
                        DlpPolicyId = policy.ID,
                        DlpRuleId = rule.ID,
                        DlpActionId = action.ID,
                        IsBlocked = i == 0,
                    });
                }

                // Outside the 28-day window: must be excluded from every figure.
                var oldEventId = Guid.NewGuid();
                db.AuditEventsCommon.Add(new CommonAuditEvent { Id = oldEventId, TimeStamp = now.AddDays(-200), User = user, Operation = operation });
                db.CopilotChats.Add(new CopilotChat
                {
                    EventID = oldEventId,
                    Agent = agent,
                    UserId = user.ID,
                    TimeStampUtc = now.AddDays(-200),
                });
                db.SaveChanges();
                db.copilot_dlp_events.Add(new CopilotDlpEvent
                {
                    ChatId = oldEventId,
                    DlpPolicyId = policy.ID,
                    DlpRuleId = rule.ID,
                    IsBlocked = true,
                });
                db.SaveChanges();
            }

            var summary = await NewController().BuildSummaryAsync(28);

            Assert.AreEqual(1, summary.CopilotBlockedCount, "One blocked event falls inside the window.");
            Assert.AreEqual(1, summary.CopilotAuditedCount, "One audited-only event falls inside the window.");
            Assert.AreEqual(1, summary.UsersImpacted);
            Assert.AreEqual(1, summary.AgentsImpacted);
            Assert.AreEqual(1, summary.PoliciesInvolved);

            // The ranked tables - these are the GroupBy projections most at risk of not translating.
            Assert.AreEqual("Contoso HR Agent", summary.TopAgents.Single().Name);
            Assert.AreEqual(1, summary.TopAgents.Single().BlockedCount);
            Assert.AreEqual(1, summary.TopAgents.Single().AuditedCount);
            Assert.AreEqual(1, summary.TopAgents.Single().UsersAffected);

            // Per-agent policy attribution: "which policies blocked THIS agent". The separate Agents
            // and Policies tables cannot answer this, which is the whole reason the breakdown exists.
            var agentPolicies = summary.TopAgents.Single().Policies;
            Assert.IsNotNull(agentPolicies, "An agent row must carry its policy breakdown.");
            Assert.AreEqual(1, agentPolicies.Count);
            Assert.AreEqual("Block Copilot on Confidential", agentPolicies.Single().Name);
            Assert.AreEqual(1, agentPolicies.Single().BlockedCount, "One of this agent's two events was a block.");
            Assert.AreEqual(1, agentPolicies.Single().AuditedCount);

            // Exercises SqlFunctions.StringConvert, which EF only translates at run time.
            Assert.AreEqual("ada@contoso.com", summary.TopUsers.Single().Name);
            Assert.IsFalse(summary.TopUsers.Single().Id.Contains(" "), "The user id must be trimmed of STR() padding.");

            // Per-user policy attribution, matched back on the integer id rather than STR()'s formatting.
            var userPolicies = summary.TopUsers.Single().Policies;
            Assert.IsNotNull(userPolicies, "A person row must carry its policy breakdown.");
            Assert.AreEqual(1, userPolicies.Count);
            Assert.AreEqual("Block Copilot on Confidential", userPolicies.Single().Name);
            Assert.AreEqual(1, userPolicies.Single().BlockedCount, "One of this person's two events was a block.");
            Assert.AreEqual(1, userPolicies.Single().AuditedCount);

            Assert.AreEqual("Block Copilot on Confidential", summary.TopPolicies.Single().Name);
            Assert.AreEqual("00000000-0000-0000-0000-00000000c001", summary.TopSensitivityLabels.Single().Name);

            // Exercises DbFunctions.TruncateTime.
            Assert.AreEqual(1, summary.Trend.Count, "Both in-window events fall on the same day.");
            Assert.AreEqual(1, summary.Trend.Single().BlockedCount);
            Assert.AreEqual(1, summary.Trend.Single().AuditedCount);

            // Tenant-wide feed, reported separately and never merged with the Copilot figures.
            Assert.AreEqual(1, summary.TenantBlockedCount);
            Assert.AreEqual(1, summary.TenantAuditedCount);
            Assert.AreEqual("Block Copilot on Confidential", summary.TenantTopPolicies.Single().Name);
            Assert.IsNull(summary.TenantTopPolicies.Single().UsersAffected,
                "DLP.All records carry no user attribution worth ranking, so this stays null.");
            Assert.IsNull(summary.TopPolicies.Single().Policies,
                "Only agent rows carry a nested breakdown; a policy row nesting policies would be meaningless.");

            // A reader without the portal's See PII permission gets every aggregate above except the people (#661) - and,
            // because one person is behind every agent, policy and label here, none of those rows either (#844): one
            // person's matches are that person's.
            var aggregateOnly = await NewController().BuildSummaryAsync(28, includeIndividuals: false);
            Assert.AreEqual(0, aggregateOnly.TopUsers.Count, "The top-users rows are sign-in names.");
            Assert.AreEqual(1, aggregateOnly.UsersImpacted, "How many people were affected names nobody.");
            Assert.AreEqual(0, aggregateOnly.TopAgents.Count, "One person's agent is withheld.");
            Assert.AreEqual(0, aggregateOnly.TopPolicies.Count + aggregateOnly.TopSensitivityLabels.Count + aggregateOnly.TenantTopPolicies.Count);
            Assert.AreEqual(4, aggregateOnly.WithheldRows);
            Assert.AreEqual(5, aggregateOnly.MinimumGroupSize);
            Assert.IsNull(summary.MinimumGroupSize, "A See PII reader is shown every row, as before.");
            Assert.AreEqual(0, summary.WithheldRows);
        }

        /// <summary>
        /// The floor a reader without See PII is held to (#844). The release review's example: policies with 100, 25 and 7
        /// blocked matches, the 7 two people's, beside 132 blocked matches in all. Leaving out only the 7 gave it back as
        /// 132 - 100 - 25, so whenever a table leaves out a row of fewer than five people, the smallest row it still lists - by
        /// people, not by its place in the ranking - goes too, and the total less the rows listed always holds a row of five or
        /// more. The same in every table, for matches with nobody to count, and among an agent's policies against the agent's
        /// own figures. A table with no small row is shown as it is.
        /// </summary>
        [TestMethod]
        public void WithoutSmallGroups_LeavesOutTheSmallestRowListedBesideASmallOne_SoNoTotalLessTheRowsListedIsOneSmallRow()
        {
            DlpImpactRow Row(string id, int blocked, int people, List<DlpImpactRow> policies = null) => new DlpImpactRow
            {
                Id = id,
                Name = "Contoso " + id,
                BlockedCount = blocked,
                AuditedCount = 1,
                UsersAffected = people,
                People = people,
                Policies = policies,
            };

            var summary = new DlpSummary
            {
                CopilotBlockedCount = 132,
                TenantBlockedCount = 132,
                TopAgents = new List<DlpImpactRow>
                {
                    Row("agent-busy", 132, 40, new List<DlpImpactRow> { Row("agent-policy-1", 100, 30), Row("agent-policy-2", 25, 8), Row("agent-policy-3", 7, 2) }),
                    Row("agent-quiet", 9, 6),
                },
                TopPolicies = new List<DlpImpactRow> { Row("policy-1", 100, 30), Row("policy-2", 25, 8), Row("policy-3", 7, 2) },
                // Ranked by blocked matches, but the fewest people are behind the first.
                TopSensitivityLabels = new List<DlpImpactRow> { Row("label-1", 100, 6), Row("label-2", 25, 30), Row("label-3", 7, 2) },
                TenantTopPolicies = new List<DlpImpactRow> { Row("tenant-policy-1", 100, 30), Row("tenant-policy-2", 25, 8), Row("tenant-policy-3", 7, 0) },
            };

            DlpAPIController.WithoutSmallGroups(summary, 5);

            CollectionAssert.AreEqual(new[] { "policy-1" }, summary.TopPolicies.Select(r => r.Id).ToArray(),
                "The 7 two people had is left out, and the 25, the smallest policy listed beside it, goes too.");
            Assert.AreEqual(32, summary.CopilotBlockedCount - summary.TopPolicies.Sum(r => r.BlockedCount),
                "132 less the policies listed is the 25 and the 7 together - never the 7 on its own.");

            CollectionAssert.AreEqual(new[] { "label-2" }, summary.TopSensitivityLabels.Select(r => r.Id).ToArray(),
                "The smallest label listed is the one with the fewest people, wherever it ranks.");
            Assert.AreEqual(107, summary.CopilotBlockedCount - summary.TopSensitivityLabels.Sum(r => r.BlockedCount));

            CollectionAssert.AreEqual(new[] { "tenant-policy-1" }, summary.TenantTopPolicies.Select(r => r.Id).ToArray(),
                "Matches with nobody to count are left out as a small row is - nothing says they are five people's - and take "
                + "the smallest policy listed with them.");
            Assert.AreEqual(32, summary.TenantBlockedCount - summary.TenantTopPolicies.Sum(r => r.BlockedCount));

            CollectionAssert.AreEqual(new[] { "agent-busy", "agent-quiet" }, summary.TopAgents.Select(r => r.Id).ToArray(),
                "A table with no row of fewer than five people, and nothing past the rows it lists, is shown as it is.");
            var busy = summary.TopAgents[0];
            CollectionAssert.AreEqual(new[] { "agent-policy-1" }, busy.Policies.Select(r => r.Id).ToArray(),
                "An agent's policies are held against the agent's own figures the same way.");
            Assert.AreEqual(32, busy.BlockedCount - busy.Policies.Sum(r => r.BlockedCount), "Never the 7 on its own.");

            Assert.AreEqual(6, summary.WithheldRows, "Two rows from each of the policy, label and tenant policy tables.");
            Assert.AreEqual("0|2|2|2", DescribeWithheld(summary.WithheldRowsByTable), "Table by table, so a table left empty can say so.");
            Assert.AreEqual(5, summary.MinimumGroupSize);
        }

        /// <summary>
        /// A table lists the first <see cref="DlpAPIController.TopN"/> rows of a ranking read one row longer. A row past them is
        /// in the totals but not in the table, as a row left out is, and can be one of fewer than five people: with exactly one
        /// such row past the cut and every row listed of five or more, the table still leaves out its smallest row listed, so
        /// the totals less the rows listed are never that row's alone. A table that ends within its rows loses nothing.
        /// </summary>
        [TestMethod]
        public void WithoutSmallGroups_ATableThatGoesOnPastTheRowsItLists_LeavesOutItsSmallestRowListed()
        {
            var top = DlpAPIController.TopN;

            // Ranked by blocked matches. Listed: rows of 11 to 30 people, but for the twelfth, of five. Past the cut: two people.
            List<DlpImpactRow> Ranked(string prefix, int rows) => Enumerable.Range(1, rows).Select(i => new DlpImpactRow
            {
                Id = prefix + "-" + i.ToString("D2", CultureInfo.InvariantCulture),
                Name = "Contoso " + prefix + " " + i.ToString(CultureInfo.InvariantCulture),
                BlockedCount = 100 - i,
                People = i > top ? 2 : i == 12 ? 5 : 10 + i,
            }).ToList();

            var summary = new DlpSummary
            {
                TopAgents = Ranked("agent", top + 1),
                TopPolicies = Ranked("policy", top + 1),
                TopSensitivityLabels = Ranked("label", top + 1),
                TenantTopPolicies = Ranked("tenant-policy", top),
            };

            DlpAPIController.WithoutSmallGroups(summary, 5);

            foreach (var (table, rows) in new[]
            {
                ("agents", summary.TopAgents),
                ("policies", summary.TopPolicies),
                ("labels", summary.TopSensitivityLabels),
            })
            {
                Assert.AreEqual(top - 1, rows.Count, table + ": the row past the cut counts as a row left out.");
                Assert.IsFalse(rows.Any(r => r.Id.EndsWith("-12", StringComparison.Ordinal)), table + ": the smallest row listed goes.");
                Assert.IsFalse(rows.Any(r => r.Id.EndsWith("-21", StringComparison.Ordinal)), table + ": a row past the cut is never shown.");
            }

            Assert.AreEqual(top, summary.TenantTopPolicies.Count, "A table that ends within its rows, none small, is shown as it is.");
            Assert.AreEqual(3, summary.WithheldRows, "One of the rows listed, from each table that goes on.");
            Assert.AreEqual("1|1|1|0", DescribeWithheld(summary.WithheldRowsByTable));
        }

        /// <summary>
        /// The release review's example end to end, unfiltered and under a filter: one agent's 132 blocked matches, 100 of
        /// them under one policy and label from ten people, 25 under another from five and 7 under a third from two - and the
        /// same matches on the DLP.All feed. A reader without See PII is shown neither the 7 nor the 25 in any table, nor among
        /// the agent's policies, so the totals - and the agent's own figures - less the rows listed are 32, never the 7. A
        /// reader with See PII is shown every row, as before.
        /// </summary>
        /// <remarks>On a clock fixed in March 2019, years before every other test's rows, so these figures are this test's alone.</remarks>
        [TestMethod]
        public async Task Summary_WithoutSeePii_NoTotalLessTheRowsListedIsOneSmallRow_AndWithSeePiiEveryRowIsListed()
        {
            var now = new DateTime(2019, 3, 15, 12, 0, 0, DateTimeKind.Utc);
            var people = new List<int>();

            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var operation = new EventOperation { Name = "CopilotInteractionBesideTheTotals" };
                db.event_operations.Add(operation);
                var agent = new CopilotAgent { AgentID = "CopilotStudio.Declarative.totals", Name = "Contoso Totals Agent" };
                db.CopilotAgents.Add(agent);
                db.SaveChanges();

                foreach (var (suffix, blocked, persons) in new[] { ("1", 100, 10), ("2", 25, 5), ("3", 7, 2) })
                {
                    var policy = new DlpPolicy { PolicyId = "pol-totals-" + suffix, Name = "Contoso totals policy " + suffix };
                    var label = new SensitivityLabel { LabelId = "00000000-0000-0000-0000-00000000d10" + suffix };
                    var users = Enumerable.Range(1, persons).Select(i => new User { UserPrincipalName = $"totals{suffix}-{i}@contoso.com" }).ToList();
                    db.dlp_policies.Add(policy);
                    db.SensitivityLabels.Add(label);
                    db.users.AddRange(users);
                    db.SaveChanges();
                    people.AddRange(users.Select(u => u.ID));

                    SeedBlockedMatches(db, operation, users, agent, policy, label, blocked, now.AddDays(-1));
                }
            }

            var controller = new DlpAPIController(new ConnectionStringAnalyticsDbContextFactory(_connectionString), utcNow: () => now);
            foreach (var scope in new[] { null, ReportUserScope.ForUsers(people) })
            {
                var because = scope == null ? " (unfiltered)" : " (under a filter)";

                var named = await controller.BuildSummaryAsync(7, includeIndividuals: true, scope: scope);
                Assert.AreEqual(132, named.CopilotBlockedCount, because);
                foreach (var (table, rows) in new[]
                {
                    ("policies", named.TopPolicies),
                    ("labels", named.TopSensitivityLabels),
                    ("tenant policies", named.TenantTopPolicies),
                    ("the agent's policies", named.TopAgents.Single().Policies),
                })
                {
                    CollectionAssert.AreEqual(new[] { 100, 25, 7 }, rows.Select(r => r.BlockedCount).ToArray(),
                        table + ": a reader with See PII is shown every row, as before" + because);
                }

                Assert.IsNull(named.MinimumGroupSize, because);
                Assert.AreEqual(0, named.WithheldRows, because);
                Assert.IsNull(named.WithheldRowsByTable, because);

                var anonymous = await controller.BuildSummaryAsync(7, includeIndividuals: false, scope: scope);
                Assert.AreEqual(132, anonymous.CopilotBlockedCount, "The totals stay" + because);
                Assert.AreEqual(132, anonymous.TenantBlockedCount, because);
                var agentRow = anonymous.TopAgents.Single();
                Assert.AreEqual(17, agentRow.UsersAffected, "Seventeen people's agent is listed" + because);
                foreach (var (table, total, rows) in new[]
                {
                    ("policies", anonymous.CopilotBlockedCount, anonymous.TopPolicies),
                    ("labels", anonymous.CopilotBlockedCount, anonymous.TopSensitivityLabels),
                    ("tenant policies", anonymous.TenantBlockedCount, anonymous.TenantTopPolicies),
                    ("the agent's policies", agentRow.BlockedCount, agentRow.Policies),
                })
                {
                    CollectionAssert.AreEqual(new[] { 100 }, rows.Select(r => r.BlockedCount).ToArray(),
                        table + ": the 7 two people had is left out, and the 25 listed beside it goes too" + because);
                    Assert.AreEqual(32, total - rows.Sum(r => r.BlockedCount),
                        table + ": the total less the rows listed is the 25 and the 7 together, never the 7" + because);
                }

                Assert.AreEqual(6, anonymous.WithheldRows, "Two rows from each of the policy, label and tenant policy tables" + because);
                Assert.AreEqual("0|2|2|2", DescribeWithheld(anonymous.WithheldRowsByTable), because);
                Assert.AreEqual(5, anonymous.MinimumGroupSize, because);
            }
        }

        /// <summary>
        /// A table cut at <see cref="DlpAPIController.TopN"/> rows, end to end, unfiltered and under a filter: twenty agents,
        /// policies and labels of five people each and, ranked after them, one of two people - exactly one small row past the
        /// cut. A reader with See PII is shown the first twenty, as before. A reader without it is not shown the row past the
        /// cut either, but the totals less the rows listed would be its figures alone, so the table also leaves out its
        /// smallest row listed: of twenty rows of five people, the first by id.
        /// </summary>
        /// <remarks>On a clock fixed in March 2018, years before every other test's rows, so these figures are this test's alone.</remarks>
        [TestMethod]
        public async Task Summary_WithoutSeePii_ATableWithOneSmallRowPastItsTwentyRows_LeavesOutItsSmallestRowListed()
        {
            var now = new DateTime(2018, 3, 15, 12, 0, 0, DateTimeKind.Utc);
            var top = DlpAPIController.TopN;
            List<int> people;

            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var operation = new EventOperation { Name = "CopilotInteractionPastTheCut" };
                db.event_operations.Add(operation);
                var users = Enumerable.Range(1, 5).Select(i => new User { UserPrincipalName = $"cut-{i}@contoso.com" }).ToList();
                db.users.AddRange(users);
                db.SaveChanges();
                people = users.Select(u => u.ID).ToList();

                for (var row = 1; row <= top + 1; row++)
                {
                    var id = row.ToString("D2", CultureInfo.InvariantCulture);
                    var agent = new CopilotAgent { AgentID = "CopilotStudio.Declarative.cut" + id, Name = "Contoso Cut Agent " + id };
                    var policy = new DlpPolicy { PolicyId = "pol-cut-" + id, Name = "Contoso cut policy " + id };
                    var label = new SensitivityLabel { LabelId = "00000000-0000-0000-0000-0000000e00" + id };
                    db.CopilotAgents.Add(agent);
                    db.dlp_policies.Add(policy);
                    db.SensitivityLabels.Add(label);
                    db.SaveChanges();

                    // A match for each of five people on every row listed; for two people on the row past the cut.
                    var who = row <= top ? users : users.Take(2).ToList();
                    SeedBlockedMatches(db, operation, who, agent, policy, label, who.Count, now.AddDays(-1));
                }
            }

            string[] Rows(int first, int last) =>
                Enumerable.Range(first, last - first + 1).Select(i => i.ToString("D2", CultureInfo.InvariantCulture)).ToArray();
            string[] Ids(IEnumerable<DlpImpactRow> rows) => rows.Select(r => r.Id.Substring(r.Id.Length - 2)).ToArray();

            var controller = new DlpAPIController(new ConnectionStringAnalyticsDbContextFactory(_connectionString), utcNow: () => now);
            foreach (var scope in new[] { null, ReportUserScope.ForUsers(people) })
            {
                var because = scope == null ? " (unfiltered)" : " (under a filter)";
                var named = await controller.BuildSummaryAsync(7, includeIndividuals: true, scope: scope);
                var anonymous = await controller.BuildSummaryAsync(7, includeIndividuals: false, scope: scope);

                foreach (var (table, listed, shown) in new[]
                {
                    ("agents", named.TopAgents, anonymous.TopAgents),
                    ("policies", named.TopPolicies, anonymous.TopPolicies),
                    ("labels", named.TopSensitivityLabels, anonymous.TopSensitivityLabels),
                    ("tenant policies", named.TenantTopPolicies, anonymous.TenantTopPolicies),
                })
                {
                    CollectionAssert.AreEquivalent(Rows(1, top), Ids(listed), table + ": a reader with See PII is shown the first twenty, as before" + because);
                    CollectionAssert.AreEquivalent(Rows(2, top), Ids(shown),
                        table + ": the row past the cut counts as one left out, so the smallest row listed goes too" + because);
                }

                Assert.AreEqual(102, anonymous.CopilotBlockedCount, because);
                Assert.AreEqual(7, anonymous.CopilotBlockedCount - anonymous.TopPolicies.Sum(r => r.BlockedCount),
                    "The total less the policies listed is the row past the cut with the first one's five - never its two alone" + because);
                Assert.IsTrue(anonymous.TopAgents.All(a => a.Policies.Count == 1), "Five people's policy under each agent is listed" + because);
                Assert.AreEqual(4, anonymous.WithheldRows, "One of the rows listed, from each table" + because);
                Assert.AreEqual("1|1|1|1", DescribeWithheld(anonymous.WithheldRowsByTable), because);
                Assert.AreEqual(0, named.WithheldRows, because);
                Assert.IsNull(named.MinimumGroupSize, because);
            }
        }

        /// <summary>
        /// <paramref name="blocked"/> blocked Copilot interactions with <paramref name="agent"/> at <paramref name="when"/>,
        /// taken by each of <paramref name="users"/> in turn, each matched by <paramref name="policy"/> on content labelled
        /// <paramref name="label"/> - and the same matches on the DLP.All feed.
        /// </summary>
        private static void SeedBlockedMatches(AnalyticsEntitiesContext db, EventOperation operation, IReadOnlyList<User> users,
            CopilotAgent agent, DlpPolicy policy, SensitivityLabel label, int blocked, DateTime when)
        {
            var interactions = new List<Guid>();
            for (var i = 0; i < blocked; i++)
            {
                var user = users[i % users.Count];
                var eventId = Guid.NewGuid();
                db.AuditEventsCommon.Add(new CommonAuditEvent { Id = eventId, TimeStamp = when, User = user, Operation = operation });
                db.CopilotChats.Add(new CopilotChat { EventID = eventId, Agent = agent, UserId = user.ID, TimeStampUtc = when });
                interactions.Add(eventId);
            }

            db.SaveChanges();

            foreach (var eventId in interactions)
            {
                db.copilot_dlp_events.Add(new CopilotDlpEvent { ChatId = eventId, DlpPolicyId = policy.ID, SensitivityLabelId = label.ID, IsBlocked = true });
                db.dlp_rule_matches.Add(new DlpRuleMatch { EventId = eventId, DlpPolicyId = policy.ID, IsBlocked = true });
            }

            db.SaveChanges();
        }

        /// <summary>
        /// An empty database must return zeroes and empty arrays - never null, which is what the page
        /// calls .map() on.
        /// </summary>
        [TestMethod]
        public async Task SummaryOnAnEmptyPeriodReturnsEmptyCollectionsRatherThanNull()
        {
            // A 7-day window on data seeded a day ago still has rows, so use the narrowest window against
            // a period the other test's rows cannot reach: assert shape, not emptiness.
            var summary = await NewController().BuildSummaryAsync(7);

            Assert.IsNotNull(summary.TopAgents);
            Assert.IsNotNull(summary.TopUsers);
            Assert.IsNotNull(summary.TopPolicies);
            Assert.IsNotNull(summary.TopSensitivityLabels);
            Assert.IsNotNull(summary.Trend);
            Assert.IsNotNull(summary.TenantTopPolicies);
        }

        /// <summary>
        /// The summary is a dozen or more statements run one after another, each allowed 90 seconds. Once the shared
        /// deadline gives up on it, the statements not yet run must not still be sent to a database that is already
        /// struggling - with or without the administrator's filter.
        /// </summary>
        [TestMethod]
        public async Task Summary_OnceCancelled_SendsNoFurtherStatements()
        {
            foreach (var scope in new[] { null, ReportUserScope.ForUsers(new[] { 1, 2 }) })
            {
                var interceptor = new CancelAtStatement(_database, cancelAt: 2);
                DbInterception.Add(interceptor);
                try
                {
                    await NewController().BuildSummaryAsync(28, includeIndividuals: true, scope: scope, cancellationToken: interceptor.Token);
                    Assert.Fail("A cancelled summary must not run to completion" + (scope == null ? "." : " under a filter."));
                }
                catch (Exception ex) when (IsCancellation(ex))
                {
                }
                finally
                {
                    DbInterception.Remove(interceptor);
                }

                Assert.AreEqual(2, interceptor.StatementsBeforeCancellation, "It got as far as the statement that was cancelled.");
                Assert.AreEqual(0, interceptor.StatementsAfterCancellation, scope == null ? "unfiltered" : "under a filter");
            }
        }

        private static bool IsCancellation(Exception ex)
        {
            // EF wraps a query's cancelled command in EntityCommandExecutionException.
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is OperationCanceledException) return true;
            }

            return false;
        }

        /// <summary>Cancels its token as the summary's N-th statement starts, and counts any summary statement started after.</summary>
        private sealed class CancelAtStatement : IDbCommandInterceptor
        {
            private readonly string _database;
            private readonly int _cancelAt;
            private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

            internal CancelAtStatement(string database, int cancelAt)
            {
                _database = database;
                _cancelAt = cancelAt;
            }

            internal CancellationToken Token => _cancellation.Token;
            internal int StatementsBeforeCancellation { get; private set; }
            internal int StatementsAfterCancellation { get; private set; }

            private void Starting(DbCommand command)
            {
                // Only the summary's own statements: every one reads a DLP table, and the session set-up for a filter
                // and EF's own model checks do not.
                if (!string.Equals(command.Connection?.Database, _database, StringComparison.OrdinalIgnoreCase)
                    || command.CommandText.IndexOf("dlp_", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return;
                }

                if (_cancellation.IsCancellationRequested)
                {
                    StatementsAfterCancellation++;
                    return;
                }

                if (++StatementsBeforeCancellation == _cancelAt) _cancellation.Cancel();
            }

            public void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext) => Starting(command);
            public void ScalarExecuting(DbCommand command, DbCommandInterceptionContext<object> interceptionContext) => Starting(command);
            public void NonQueryExecuting(DbCommand command, DbCommandInterceptionContext<int> interceptionContext) => Starting(command);
            public void ReaderExecuted(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext) { }
            public void ScalarExecuted(DbCommand command, DbCommandInterceptionContext<object> interceptionContext) { }
            public void NonQueryExecuted(DbCommand command, DbCommandInterceptionContext<int> interceptionContext) { }
        }

        /// <summary>A hand-edited window snaps to an offered one rather than scanning years.</summary>
        [TestMethod]
        public void WindowsSnapToTheOfferedRanges()
        {
            Assert.AreEqual(7, DlpAPIController.SnapWindow(1));
            Assert.AreEqual(28, DlpAPIController.SnapWindow(30));
            Assert.AreEqual(180, DlpAPIController.SnapWindow(100000));
        }

        /// <summary>
        /// "Last 7 days" is seven calendar dates including today - Copilot Adoption's window - not midnight seven days ago,
        /// which spans eight (#851, R3-9). An interaction a second either side of each end shows exactly where the window
        /// stops, on every path that reads it: the summary, the summary under a filter, and the governance figures.
        /// </summary>
        /// <remarks>
        /// On a clock fixed in 2020, years before every other test's rows, so these counts are this test's alone whichever
        /// order the tests run in.
        /// </remarks>
        [TestMethod]
        public async Task Window_CoversNCalendarDatesIncludingToday_AsCopilotAdoptionDoes()
        {
            var now = new DateTime(2020, 6, 15, 10, 30, 0, DateTimeKind.Utc);
            var start = new DateTime(2020, 6, 9, 0, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(start, Common.Entities.CopilotAdoption.CopilotAdoptionScoring.WindowStartUtc(now, 7),
                "The window the DLP page must match.");

            int frances;
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var operation = new EventOperation { Name = "CopilotInteractionAtTheWindowEdges" };
                db.event_operations.Add(operation);
                var user = new User { UserPrincipalName = "frances@contoso.com" };
                db.users.Add(user);
                var agent = new CopilotAgent { AgentID = "CopilotStudio.Declarative.edges", Name = "Contoso Edge Agent" };
                db.CopilotAgents.Add(agent);
                var policy = new DlpPolicy { PolicyId = "pol-edges", Name = "Block at the edges" };
                db.dlp_policies.Add(policy);
                db.SaveChanges();
                frances = user.ID;

                foreach (var when in new[] { start.AddSeconds(-1), start, now, now.AddSeconds(1) })
                {
                    var eventId = Guid.NewGuid();
                    db.AuditEventsCommon.Add(new CommonAuditEvent { Id = eventId, TimeStamp = when, User = user, Operation = operation });
                    db.CopilotChats.Add(new CopilotChat { EventID = eventId, Agent = agent, UserId = user.ID, TimeStampUtc = when });
                    db.SaveChanges();

                    db.copilot_dlp_events.Add(new CopilotDlpEvent { ChatId = eventId, DlpPolicyId = policy.ID, IsBlocked = true });
                    db.dlp_rule_matches.Add(new DlpRuleMatch { EventId = eventId, DlpPolicyId = policy.ID, IsBlocked = true });
                    db.SaveChanges();
                }
            }

            var controller = new DlpAPIController(new ConnectionStringAnalyticsDbContextFactory(_connectionString), utcNow: () => now);

            foreach (var scope in new[] { null, ReportUserScope.ForUsers(new[] { frances }) })
            {
                var because = scope == null ? "unfiltered" : "under a filter";
                var summary = await controller.BuildSummaryAsync(7, scope: scope);

                Assert.AreEqual(start, summary.FromUtc, because);
                Assert.AreEqual(now, summary.ToUtc, because);
                Assert.AreEqual(2, summary.CopilotBlockedCount,
                    "Midnight six days ago and now are inside; a second before that midnight and a second after now are not - " + because);
                Assert.AreEqual(2, summary.TenantBlockedCount, "The DLP.All figures use the same window - " + because);
                CollectionAssert.AreEqual(
                    new[] { start, now.Date },
                    summary.Trend.Select(p => p.Date).ToArray(),
                    "The trend has no point for the day before the window - " + because);
            }

            var governance = await controller.BuildGovernanceAsync(7);
            Assert.AreEqual(start, governance.FromUtc);
            Assert.AreEqual(now, governance.ToUtc);
            Assert.AreEqual(2, governance.Interactions, "The governance figures use the same window.");
        }

        /// <summary>
        /// Under the administrator's global filter every figure is narrowed in SQL, statement by statement. A
        /// scope holding everyone must give exactly the unfiltered summary; one person, only theirs; nobody,
        /// nothing.
        /// </summary>
        /// <remarks>
        /// Seeded 40-41 days back and read over 90 days, so neither this test nor the 28-day one above sees the
        /// other's rows in its assertions, whichever runs first.
        /// </remarks>
        [TestMethod]
        public async Task ScopedSummary_NarrowsEveryFigureInSql_AndMatchesTheUnfilteredSummaryForEveryone()
        {
            var now = DateTime.UtcNow;
            int grace;

            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var operation = new EventOperation { Name = "CopilotInteractionUnderAScope" };
                db.event_operations.Add(operation);
                var graceUser = new User { UserPrincipalName = "grace@contoso.com" };
                var alanUser = new User { UserPrincipalName = "alan@contoso.com" };
                db.users.Add(graceUser);
                db.users.Add(alanUser);
                var graceAgent = new CopilotAgent { AgentID = "CopilotStudio.Declarative.finance", Name = "Contoso Finance Agent" };
                var alanAgent = new CopilotAgent { AgentID = "CopilotStudio.Declarative.engineering", Name = "Contoso Engineering Agent" };
                db.CopilotAgents.Add(graceAgent);
                db.CopilotAgents.Add(alanAgent);
                var gracePolicy = new DlpPolicy { PolicyId = "pol-finance", Name = "Protect Καλημέρα κόσμε" };
                var alanPolicy = new DlpPolicy { PolicyId = "pol-engineering", Name = "Protect source code" };
                db.dlp_policies.Add(gracePolicy);
                db.dlp_policies.Add(alanPolicy);
                var label = new SensitivityLabel { LabelId = "00000000-0000-0000-0000-00000000c002" };
                db.SensitivityLabels.Add(label);
                db.SaveChanges();
                grace = graceUser.ID;

                void Interaction(User user, CopilotAgent agent, DlpPolicy policy, bool blocked, int daysAgo, bool alsoOnDlpAll)
                {
                    var eventId = Guid.NewGuid();
                    db.AuditEventsCommon.Add(new CommonAuditEvent { Id = eventId, TimeStamp = now.AddDays(-daysAgo), User = user, Operation = operation });
                    db.CopilotChats.Add(new CopilotChat { EventID = eventId, Agent = agent, UserId = user.ID, TimeStampUtc = now.AddDays(-daysAgo) });
                    db.SaveChanges();

                    db.copilot_dlp_events.Add(new CopilotDlpEvent { ChatId = eventId, DlpPolicyId = policy.ID, SensitivityLabelId = label.ID, IsBlocked = blocked });
                    if (alsoOnDlpAll)
                    {
                        db.dlp_rule_matches.Add(new DlpRuleMatch { EventId = eventId, DlpPolicyId = policy.ID, IsBlocked = blocked });
                    }

                    db.SaveChanges();
                }

                Interaction(graceUser, graceAgent, gracePolicy, blocked: true, daysAgo: 41, alsoOnDlpAll: false);
                Interaction(graceUser, graceAgent, gracePolicy, blocked: false, daysAgo: 41, alsoOnDlpAll: false);
                Interaction(graceUser, graceAgent, gracePolicy, blocked: true, daysAgo: 40, alsoOnDlpAll: true);
                Interaction(alanUser, alanAgent, alanPolicy, blocked: false, daysAgo: 40, alsoOnDlpAll: true);
            }

            int[] everyoneIds;
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                everyoneIds = db.users.Select(u => u.ID).ToArray();
            }

            var unfiltered = await NewController().BuildSummaryAsync(90);
            var everyone = await NewController().BuildSummaryAsync(90, scope: ReportUserScope.ForUsers(everyoneIds));
            AssertSameFigures(unfiltered, everyone);

            var onlyGrace = await NewController().BuildSummaryAsync(90, scope: ReportUserScope.ForUsers(new[] { grace }));
            Assert.AreEqual(2, onlyGrace.CopilotBlockedCount);
            Assert.AreEqual(1, onlyGrace.CopilotAuditedCount);
            Assert.AreEqual(1, onlyGrace.UsersImpacted);
            Assert.AreEqual(1, onlyGrace.AgentsImpacted);
            Assert.AreEqual(1, onlyGrace.PoliciesInvolved);

            var financeAgent = onlyGrace.TopAgents.Single();
            Assert.AreEqual("Contoso Finance Agent", financeAgent.Name);
            Assert.AreEqual(2, financeAgent.BlockedCount);
            Assert.AreEqual(1, financeAgent.AuditedCount);
            Assert.AreEqual(1, financeAgent.UsersAffected);
            Assert.AreEqual("Protect Καλημέρα κόσμε", financeAgent.Policies.Single().Name, "Tenant text survives the SQL path.");
            Assert.AreEqual(2, financeAgent.Policies.Single().BlockedCount);

            Assert.AreEqual("Protect Καλημέρα κόσμε", onlyGrace.TopPolicies.Single().Name);
            Assert.AreEqual(3, onlyGrace.TopSensitivityLabels.Single().BlockedCount + onlyGrace.TopSensitivityLabels.Single().AuditedCount);
            Assert.AreEqual("grace@contoso.com", onlyGrace.TopUsers.Single().Name);
            Assert.AreEqual(grace.ToString(System.Globalization.CultureInfo.InvariantCulture), onlyGrace.TopUsers.Single().Id);
            Assert.AreEqual(2, onlyGrace.TopUsers.Single().BlockedCount);
            CollectionAssert.AreEqual(
                new[] { 2, 1 },
                onlyGrace.Trend.Select(t => t.BlockedCount + t.AuditedCount).ToArray(),
                "Two matches 41 days ago, one 40 days ago, oldest first.");

            Assert.AreEqual(1, onlyGrace.TenantBlockedCount);
            Assert.AreEqual(0, onlyGrace.TenantAuditedCount, "Alan's audited DLP.All match is outside the scope.");
            Assert.AreEqual("Protect Καλημέρα κόσμε", onlyGrace.TenantTopPolicies.Single().Name);
            Assert.IsNull(onlyGrace.TenantTopPolicies.Single().UsersAffected);

            var nobody = await NewController().BuildSummaryAsync(90, scope: ReportUserScope.ForUsers(new int[0]));
            Assert.AreEqual(0, nobody.CopilotBlockedCount + nobody.CopilotAuditedCount + nobody.TenantBlockedCount + nobody.TenantAuditedCount);
            Assert.AreEqual(0, nobody.UsersImpacted + nobody.AgentsImpacted + nobody.PoliciesInvolved);
            Assert.AreEqual(
                0,
                nobody.TopAgents.Count + nobody.TopPolicies.Count + nobody.TopSensitivityLabels.Count
                + nobody.TopUsers.Count + nobody.Trend.Count + nobody.TenantTopPolicies.Count);

            // A reader without See PII still gets no people, filtered or not.
            var aggregateOnly = await NewController().BuildSummaryAsync(90, includeIndividuals: false, scope: ReportUserScope.ForUsers(new[] { grace }));
            Assert.AreEqual(0, aggregateOnly.TopUsers.Count);
            Assert.AreEqual(1, aggregateOnly.UsersImpacted);
        }

        /// <summary>
        /// The governance section (#648) against the real schema: every interaction counted once however many
        /// prompts or resources it has, a NULL flag in neither side of a rate, models and plugins counted per
        /// interaction, and the administrator's filter applied in SQL.
        /// </summary>
        /// <remarks>
        /// Asserted under a scope of the two people seeded here, because the other tests in this class share the
        /// database and seed interactions inside the same 28-day window. The unfiltered statements are then
        /// checked against a scope holding everyone, as the summary's are.
        /// </remarks>
        [TestMethod]
        public async Task Governance_CountsEachInteractionOnce_AndLeavesUnreportedFlagsOutOfBothSidesOfTheRate()
        {
            var now = DateTime.UtcNow;
            int nadia;
            int omar;

            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var operation = new EventOperation { Name = "CopilotInteractionForGovernance" };
                db.event_operations.Add(operation);
                var nadiaUser = new User { UserPrincipalName = "nadia@contoso.com" };
                var omarUser = new User { UserPrincipalName = "omar@contoso.com" };
                db.users.Add(nadiaUser);
                db.users.Add(omarUser);
                var label = new SensitivityLabel { LabelId = "00000000-0000-0000-0000-00000000c648" };
                db.SensitivityLabels.Add(label);

                // One model under two versions, which must still count as one model per interaction.
                var deepReasoningV1 = new CopilotAIModel { Name = "DEEP_LEO", ProviderName = "Contoso AI", Version = "2026-01-01" };
                var deepReasoningV2 = new CopilotAIModel { Name = "DEEP_LEO", ProviderName = "Contoso AI", Version = "2026-06-01" };
                var otherModel = new CopilotAIModel { Name = "contoso-model" };
                db.CopilotAIModels.Add(deepReasoningV1);
                db.CopilotAIModels.Add(deepReasoningV2);
                db.CopilotAIModels.Add(otherModel);

                // A plugin with no id is named by its name, which is tenant-visible text and must survive as Unicode.
                var webSearch = new CopilotAISystemPlugin { PluginId = "BingWebSearch", Name = "BuiltIn" };
                var namedOnly = new CopilotAISystemPlugin { Name = "Καλημέρα κόσμε" };
                db.CopilotAISystemPlugins.Add(webSearch);
                db.CopilotAISystemPlugins.Add(namedOnly);
                db.SaveChanges();
                nadia = nadiaUser.ID;
                omar = omarUser.ID;

                Guid Interaction(User user, int daysAgo)
                {
                    var eventId = Guid.NewGuid();
                    db.AuditEventsCommon.Add(new CommonAuditEvent { Id = eventId, TimeStamp = now.AddDays(-daysAgo), User = user, Operation = operation });
                    db.CopilotChats.Add(new CopilotChat { EventID = eventId, AppHost = "BizChat", UserId = user.ID, TimeStampUtc = now.AddDays(-daysAgo) });
                    db.SaveChanges();
                    return eventId;
                }

                void Message(Guid chat, bool isPrompt, bool? jailbreak) => db.CopilotMessages.Add(new CopilotMessage
                {
                    ChatId = chat,
                    MessageId = Guid.NewGuid().ToString("N"),
                    IsPrompt = isPrompt,
                    JailbreakDetected = jailbreak,
                });

                void Resource(Guid chat, bool? xpia, bool labelled) => db.CopilotEventAccessedResources.Add(new CopilotEventAccessedResource
                {
                    ChatId = chat,
                    XpiaDetected = xpia,
                    SensitivityLabelId = labelled ? label.ID : (int?)null,
                });

                void Model(Guid chat, CopilotAIModel model) =>
                    db.CopilotEventAIModels.Add(new CopilotEventAIModel { ChatId = chat, ModelId = model.ID });

                void Plugin(Guid chat, CopilotAISystemPlugin plugin) =>
                    db.CopilotEventAISystemPlugins.Add(new CopilotEventAISystemPlugin { ChatId = chat, AISystemPluginId = plugin.ID });

                // A: a jailbreak reported and raised on its prompt (the response's NULL changes nothing); XPIA reported
                // clean on one resource and absent on the other; deep reasoning under both versions; web search.
                var a = Interaction(nadiaUser, 2);
                Message(a, isPrompt: true, jailbreak: true);
                Message(a, isPrompt: false, jailbreak: null);
                Resource(a, xpia: false, labelled: true);
                Resource(a, xpia: null, labelled: false);
                Model(a, deepReasoningV1);
                Model(a, deepReasoningV2);
                Plugin(a, webSearch);

                // B: a jailbreak reported and not raised; XPIA raised on one of its two resources.
                var b = Interaction(nadiaUser, 3);
                Message(b, isPrompt: true, jailbreak: false);
                Message(b, isPrompt: false, jailbreak: null);
                Resource(b, xpia: true, labelled: true);
                Resource(b, xpia: false, labelled: false);
                Model(b, deepReasoningV1);
                Model(b, otherModel);

                // C: every flag NULL. Not reported is not clean, so C is in neither side of either rate.
                var c = Interaction(omarUser, 4);
                Message(c, isPrompt: true, jailbreak: null);
                Message(c, isPrompt: false, jailbreak: null);
                Resource(c, xpia: null, labelled: false);
                Plugin(c, webSearch);
                Plugin(c, namedOnly);

                // D: an interaction with nothing recorded against it, which counts only as an interaction.
                Interaction(omarUser, 5);

                // E: everything raised, but outside every window the page offers.
                var e = Interaction(nadiaUser, 200);
                Message(e, isPrompt: true, jailbreak: true);
                Resource(e, xpia: true, labelled: true);
                Model(e, otherModel);
                Plugin(e, webSearch);

                db.SaveChanges();
            }

            var governance = await NewController().BuildGovernanceAsync(28, ReportUserScope.ForUsers(new[] { nadia, omar }));

            Assert.AreEqual(4, governance.Interactions, "A, B, C and D; E is outside the window.");

            Assert.AreEqual(2, governance.Jailbreak.ReportedInteractions, "A and B reported the flag; C reported nothing.");
            Assert.AreEqual(1, governance.Jailbreak.FlaggedInteractions);
            Assert.AreEqual(5000.0, governance.Jailbreak.RatePer10000.Value, 1e-9, "1 of the 2 that reported it - not 1 of 4.");

            Assert.AreEqual(2, governance.Xpia.ReportedInteractions, "A (one clean resource) and B; C's only resource reported nothing.");
            Assert.AreEqual(1, governance.Xpia.FlaggedInteractions, "B, counted once although only one of its resources was flagged.");

            Assert.AreEqual(5, governance.SensitivityLabels.Resources, "A's two, B's two and C's one.");
            Assert.AreEqual(2, governance.SensitivityLabels.LabelledResources);
            Assert.AreEqual(3, governance.SensitivityLabels.InteractionsWithResources);
            Assert.AreEqual(0.4, governance.SensitivityLabels.Share.Value, 1e-9);

            Assert.AreEqual(2, governance.InteractionsWithModel);
            Assert.AreEqual(
                "DEEP_LEO:2|contoso-model:1",
                string.Join("|", governance.Models.Select(m => m.Name + ":" + m.Interactions)),
                "A used deep reasoning under two versions and still counts once.");
            Assert.AreEqual(0.5, governance.Models[0].Share.Value, 1e-9, "A share of all 4 interactions.");

            Assert.AreEqual(2, governance.InteractionsWithPlugin);
            Assert.AreEqual(
                "BingWebSearch:2|Καλημέρα κόσμε:1",
                string.Join("|", governance.Plugins.Select(p => p.Name + ":" + p.Interactions)),
                "Named by plugin id, or by name when there is no id - as Microsoft reported it.");

            // One person in scope: only theirs, and with nothing reported the rates are unknown, never zero.
            var onlyOmar = await NewController().BuildGovernanceAsync(28, ReportUserScope.ForUsers(new[] { omar }));
            Assert.AreEqual(2, onlyOmar.Interactions);
            Assert.AreEqual(0, onlyOmar.Jailbreak.ReportedInteractions);
            Assert.IsNull(onlyOmar.Jailbreak.RatePer10000);
            Assert.AreEqual(0, onlyOmar.Xpia.ReportedInteractions);
            Assert.IsNull(onlyOmar.Xpia.RatePer10000);
            Assert.AreEqual(1, onlyOmar.SensitivityLabels.Resources);
            Assert.AreEqual(0, onlyOmar.InteractionsWithModel);
            Assert.AreEqual(0, onlyOmar.Models.Count);
            Assert.AreEqual(1, onlyOmar.InteractionsWithPlugin);

            var nobody = await NewController().BuildGovernanceAsync(28, ReportUserScope.ForUsers(new int[0]));
            Assert.AreEqual(0, nobody.Interactions);
            Assert.IsNull(nobody.Jailbreak.RatePer10000);
            Assert.IsNull(nobody.Xpia.RatePer10000);
            Assert.IsNull(nobody.SensitivityLabels.Share);
            Assert.AreEqual(0, nobody.InteractionsWithModel + nobody.InteractionsWithPlugin + nobody.Models.Count + nobody.Plugins.Count);

            int[] everyoneIds;
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                everyoneIds = db.users.Select(u => u.ID).ToArray();
            }

            var unfiltered = await NewController().BuildGovernanceAsync(28);
            var everyone = await NewController().BuildGovernanceAsync(28, ReportUserScope.ForUsers(everyoneIds));
            Assert.AreEqual(DescribeGovernance(unfiltered), DescribeGovernance(everyone),
                "A scope holding everyone must give exactly the unfiltered figures.");
            Assert.IsTrue(unfiltered.Interactions >= governance.Interactions);
        }

        private static string DescribeGovernance(AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpGovernanceSummary g)
        {
            return string.Join(";",
                g.Interactions,
                g.Jailbreak.FlaggedInteractions + "/" + g.Jailbreak.ReportedInteractions,
                g.Xpia.FlaggedInteractions + "/" + g.Xpia.ReportedInteractions,
                g.SensitivityLabels.LabelledResources + "/" + g.SensitivityLabels.Resources + "/" + g.SensitivityLabels.InteractionsWithResources,
                g.InteractionsWithModel + ":" + string.Join(",", g.Models.Select(m => m.Name + "=" + m.Interactions)),
                g.InteractionsWithPlugin + ":" + string.Join(",", g.Plugins.Select(p => p.Name + "=" + p.Interactions)));
        }

        private static void AssertSameFigures(DlpSummary expected, DlpSummary actual)
        {
            Assert.AreEqual(expected.CopilotBlockedCount, actual.CopilotBlockedCount, "CopilotBlockedCount");
            Assert.AreEqual(expected.CopilotAuditedCount, actual.CopilotAuditedCount, "CopilotAuditedCount");
            Assert.AreEqual(expected.UsersImpacted, actual.UsersImpacted, "UsersImpacted");
            Assert.AreEqual(expected.AgentsImpacted, actual.AgentsImpacted, "AgentsImpacted");
            Assert.AreEqual(expected.PoliciesInvolved, actual.PoliciesInvolved, "PoliciesInvolved");
            Assert.AreEqual(expected.TenantBlockedCount, actual.TenantBlockedCount, "TenantBlockedCount");
            Assert.AreEqual(expected.TenantAuditedCount, actual.TenantAuditedCount, "TenantAuditedCount");

            // Equal rows can rank in either order in the unfiltered SQL, so the tables are compared as sets.
            CollectionAssert.AreEquivalent(expected.TopAgents.Select(Describe).ToList(), actual.TopAgents.Select(Describe).ToList(), "TopAgents");
            CollectionAssert.AreEquivalent(expected.TopPolicies.Select(Describe).ToList(), actual.TopPolicies.Select(Describe).ToList(), "TopPolicies");
            CollectionAssert.AreEquivalent(
                expected.TopSensitivityLabels.Select(Describe).ToList(), actual.TopSensitivityLabels.Select(Describe).ToList(), "TopSensitivityLabels");
            CollectionAssert.AreEquivalent(expected.TopUsers.Select(Describe).ToList(), actual.TopUsers.Select(Describe).ToList(), "TopUsers");
            CollectionAssert.AreEquivalent(
                expected.TenantTopPolicies.Select(Describe).ToList(), actual.TenantTopPolicies.Select(Describe).ToList(), "TenantTopPolicies");
            CollectionAssert.AreEqual(
                expected.Trend.Select(p => $"{p.Date:yyyy-MM-dd}|{p.BlockedCount}|{p.AuditedCount}").ToList(),
                actual.Trend.Select(p => $"{p.Date:yyyy-MM-dd}|{p.BlockedCount}|{p.AuditedCount}").ToList(),
                "Trend");
        }

        private static string Describe(DlpImpactRow row)
        {
            var policies = row.Policies == null
                ? "-"
                : string.Join(";", row.Policies
                    .Select(p => $"{p.Id}:{p.Name}:{p.BlockedCount}:{p.AuditedCount}")
                    .OrderBy(s => s, StringComparer.Ordinal));
            return $"{row.Id}|{row.Name}|{row.BlockedCount}|{row.AuditedCount}|{row.UsersAffected}|{policies}";
        }

        /// <summary>Rows left out of the agents, policies, labels and tenant policies tables, in that order.</summary>
        private static string DescribeWithheld(AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpWithheldRows withheld) =>
            withheld == null ? "-" : $"{withheld.Agents}|{withheld.Policies}|{withheld.SensitivityLabels}|{withheld.TenantPolicies}";
    }
}
