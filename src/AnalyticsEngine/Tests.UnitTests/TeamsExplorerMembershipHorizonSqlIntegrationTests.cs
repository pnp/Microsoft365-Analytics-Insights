extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.Teams;
using Common.Entities.TeamsExplorer;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Threading.Tasks;
using Configuration = Common.Entities.Migrations.Configuration;
using ReportScopeResolver = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.ReportScopeResolver;

namespace Tests.UnitTests
{
    /// <summary>
    /// Pins how far back the Teams Explorer looks for a team's members when it decides whether a reader without See PII may
    /// see the team's channel and sentiment rows (#844): the team's latest membership snapshot within
    /// <see cref="TeamsExplorerSql.LatestMembershipHorizonDays"/> days of the window's end, never the whole append-only
    /// <c>team_membership_log</c>. Run against a real, migrated throwaway database, because the bound is SQL.
    /// </summary>
    /// <remarks>
    /// Two teams of five members each, both busy inside the window. The current team's only snapshot is on the oldest day the
    /// horizon still reaches; the lapsed team's is on the day before it. The window is computed once, and the snapshots are
    /// placed relative to its end, so the test cannot straddle midnight. All data is synthetic.
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class TeamsExplorerMembershipHorizonSqlIntegrationTests
    {
        private const string CurrentTeam = "Contoso Current";
        private const string LapsedTeam = "Contoso Lapsed";

        private const string MasterConnection =
            @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=True";

        private static string _database;
        private static string _connectionString;
        private static TeamsExplorerQuery _window;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _window = TeamsExplorerQuery.Create(28, DateTime.UtcNow);
            _database = "TeamsExplorerMembership_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _connectionString =
                $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={_database};Integrated Security=true;MultipleActiveResultSets=True;TrustServerCertificate=True";

            Execute(MasterConnection, ScratchDatabase.CreateDatabaseSql(_database));
            new DbMigrator(new Configuration
            {
                TargetDatabase = new System.Data.Entity.Infrastructure.DbConnectionInfo(_connectionString, "Microsoft.Data.SqlClient"),
            }).Update();

            Seed();
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

        private static void Seed()
        {
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var members = Enumerable.Range(1, ReportScopeResolver.MinimumPeopleWithoutSeePii)
                    .Select(i => new User { UserPrincipalName = $"member{i}@contoso.com", AccountEnabled = true })
                    .ToList();
                db.users.AddRange(members);
                db.SaveChanges();

                var oldestDayInside = _window.ToExclusiveUtc.AddDays(-TeamsExplorerSql.LatestMembershipHorizonDays);
                SeedTeam(db, CurrentTeam, "00000000-0000-0000-0000-000000000011", members, oldestDayInside);
                SeedTeam(db, LapsedTeam, "00000000-0000-0000-0000-000000000012", members, oldestDayInside.AddDays(-1));
                db.SaveChanges();
            }
        }

        private static void SeedTeam(AnalyticsEntitiesContext db, string name, string graphId, IEnumerable<User> members, DateTime snapshot)
        {
            var team = new TeamDefinition { Name = name, GraphID = graphId, FirstDiscovered = snapshot.AddDays(-30), HasRefreshToken = true };
            db.Teams.Add(team);
            db.SaveChanges();

            foreach (var member in members)
            {
                db.TeamMembershipLogs.Add(new TeamMembershipLog { TeamID = team.ID, UserID = member.ID, Date = snapshot });
            }

            var general = new TeamChannel { TeamID = team.ID, Name = "General", GraphID = graphId + "-general" };
            db.TeamChannels.Add(general);
            db.SaveChanges();

            // A scored day inside the window, so the team has a channel row and both sentiment rows.
            db.TeamChannelStats.Add(new ChannelStatsLog
            {
                ChannelID = general.ID,
                Date = _window.ToExclusiveUtc.AddDays(-2),
                ChatsCount = 12,
                SentimentScore = 0.8,
            });
        }

        [TestMethod]
        public async Task ATeamWhoseOnlyMembershipSnapshotIsOlderThanTheHorizon_IsWithheldFromAReaderWithoutSeePii()
        {
            var (collaboration, conversations) = await ReadSectionsAsync();

            // A reader with See PII is served the sections as they are: the team's rows are there whatever its membership.
            var channel = collaboration.Channels.SingleOrDefault(c => c.TeamName == LapsedTeam);
            Assert.IsNotNull(channel, "A reader with See PII still sees the lapsed team's channel.");
            Assert.IsTrue(conversations.SentimentByTeam.Any(r => r.Name == LapsedTeam), "...and its sentiment.");
            Assert.IsTrue(conversations.SentimentByChannel.Any(r => r.Name == LapsedTeam + " / General"), "...and its channel's sentiment.");

            var floor = ReportScopeResolver.MinimumPeopleWithoutSeePii;
            var trimmedCollaboration = collaboration.WithoutSmallGroups(floor);
            var trimmedConversations = conversations.WithoutSmallGroups(floor);

            // Five people were members, but on a day older than the horizon, so nothing read says who the members are now.
            // Fail safe: that counts as below the floor. The current team goes with it: the only other team, so the tenant's
            // figures less its rows would be the lapsed team's (TeamsSmallGroups.WithoutSmallRowsOfList).
            Assert.IsFalse(trimmedCollaboration.Channels.Any(c => c.TeamName == LapsedTeam), "The lapsed team's channel is withheld.");
            Assert.AreEqual(2, trimmedCollaboration.WithheldChannels);
            Assert.IsFalse(trimmedConversations.SentimentByTeam.Any(r => r.Name == LapsedTeam), "The lapsed team's sentiment is withheld.");
            Assert.AreEqual(2, trimmedConversations.WithheldSentimentTeams);
            Assert.IsFalse(trimmedConversations.SentimentByChannel.Any(r => r.Name == LapsedTeam + " / General"),
                "The lapsed team's channel sentiment is withheld.");
            Assert.AreEqual(2, trimmedConversations.WithheldSentimentChannels);
            Assert.AreEqual(0, channel.TeamMembers, "No snapshot inside the horizon counts as no members.");
        }

        [TestMethod]
        public async Task ATeamOfFiveOnASnapshotInsideTheHorizon_IsShownToAReaderWithoutSeePii()
        {
            var (collaboration, conversations) = await ReadSectionsAsync();

            Assert.AreEqual(ReportScopeResolver.MinimumPeopleWithoutSeePii,
                collaboration.Channels.Single(c => c.TeamName == CurrentTeam).TeamMembers,
                "The oldest day the horizon reaches still counts.");
            Assert.AreEqual(ReportScopeResolver.MinimumPeopleWithoutSeePii, conversations.SentimentByTeam.Single(r => r.Name == CurrentTeam).Members);

            // Read on its own - beside the lapsed team it goes with it, so that the lapsed team cannot be worked out.
            var floor = ReportScopeResolver.MinimumPeopleWithoutSeePii;
            var trimmedCollaboration = new TeamsCollaboration
            {
                Channels = collaboration.Channels.Where(c => c.TeamName != LapsedTeam).ToList(),
            }.WithoutSmallGroups(floor);
            var trimmedConversations = new TeamsConversations
            {
                SentimentByTeam = conversations.SentimentByTeam.Where(r => r.Name != LapsedTeam).ToList(),
                SentimentByChannel = conversations.SentimentByChannel.Where(r => r.Name != LapsedTeam + " / General").ToList(),
            }.WithoutSmallGroups(floor);

            Assert.IsTrue(trimmedCollaboration.Channels.Any(c => c.TeamName == CurrentTeam), "The current team's channel is shown.");
            Assert.IsTrue(trimmedConversations.SentimentByTeam.Any(r => r.Name == CurrentTeam), "The current team's sentiment is shown.");
            Assert.IsTrue(trimmedConversations.SentimentByChannel.Any(r => r.Name == CurrentTeam + " / General"),
                "The current team's channel sentiment is shown.");
        }

        private static async Task<(TeamsCollaboration Collaboration, TeamsConversations Conversations)> ReadSectionsAsync()
        {
            var store = new SqlTeamsExplorerStore(new ConnectionStringAnalyticsDbContextFactory(_connectionString));
            var collaboration = await store.GetCollaborationAsync(_window);
            var conversations = await store.GetConversationsAsync(_window, true);

            var failed = collaboration.Queries.Concat(conversations.Queries).Where(q => q.Error != null).ToList();
            Assert.AreEqual(0, failed.Count, string.Join(" | ", failed.Select(q => $"{q.Key}: {q.Error}")));
            return (collaboration, conversations);
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
    }
}
