extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity.Migrations;
using Microsoft.Data.SqlClient;
using System.Linq;
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

            Execute(MasterConnection, $"CREATE DATABASE [{_database}];");

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

            // A reader without the portal's See PII permission gets every figure above except the people (#661).
            var aggregateOnly = await NewController().BuildSummaryAsync(28, includeIndividuals: false);
            Assert.AreEqual(0, aggregateOnly.TopUsers.Count, "The top-users rows are sign-in names.");
            Assert.AreEqual(1, aggregateOnly.UsersImpacted, "How many people were affected names nobody.");
            Assert.AreEqual("Contoso HR Agent", aggregateOnly.TopAgents.Single().Name);
            Assert.AreEqual(1, aggregateOnly.TopAgents.Single().UsersAffected);
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

        /// <summary>A hand-edited window snaps to an offered one rather than scanning years.</summary>
        [TestMethod]
        public void WindowsSnapToTheOfferedRanges()
        {
            Assert.AreEqual(7, DlpAPIController.SnapWindow(1));
            Assert.AreEqual(28, DlpAPIController.SnapWindow(30));
            Assert.AreEqual(180, DlpAPIController.SnapWindow(100000));
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
    }
}
