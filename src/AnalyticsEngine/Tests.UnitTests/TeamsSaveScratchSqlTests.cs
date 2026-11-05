using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.Teams;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Infrastructure.Interception;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;
using Configuration = Common.Entities.Migrations.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Teams crawl's SQL save against a throwaway database with every migration applied: what a channel read adds to the
    /// stored statistics when it is read again, and how many queries the per-Team logs cost.
    /// </summary>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class TeamsSaveScratchSqlTests
    {
        private static ScratchDatabase _database;
        private static string _catalog;

        [ClassInitialize]
        public static void CreateDatabase(TestContext context)
        {
            _database = ScratchDatabase.Create("TeamsSave");
            _catalog = new SqlConnectionStringBuilder(_database.ConnectionString).InitialCatalog;
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

        private static AnalyticsEntitiesContext NewContext() => new AnalyticsEntitiesContext(_database.ConnectionString, true, false);

        #region A channel read again after Graph rejected its delta token (R8-4)

        private static readonly DateTimeOffset LastWeek = DateTimeOffset.UtcNow.AddDays(-7);
        private static readonly DateTimeOffset LastWeekNextDay = LastWeek.AddDays(1);

        /// <summary>
        /// Regression: when Graph rejected a channel's delta token, the full read that replaced it added the channel's whole history
        /// to the statistics already stored - every message to the per-day counts, every reaction as a new row - and sent it all to
        /// Azure AI Language again. A reset after a normal import must leave the same totals, and count only what is new since.
        /// </summary>
        [TestMethod]
        public async Task ARejectedDeltaToken_AfterANormalImport_LeavesTheSameTotals_AndCountsOnlyWhatIsNew()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var teamId = "team-" + suffix;
            var channelId = "channel-" + suffix;
            var tokens = new InMemoryTeamChannelDeltaTokenStore();

            var history = new List<ChatMessage>
            {
                Message("old-1", LastWeek, Reaction(LastWeek)),
                Message("old-2", LastWeekNextDay, Reaction(LastWeekNextDay)),
            };
            var historyReplies = new List<ChatMessage> { Message("old-reply", LastWeekNextDay) };

            // Cycle 1: no checkpoint yet, so the whole channel is new.
            var firstRead = new ScriptedChannel(info => info == null ? Read(history, "token-1") : throw new AssertFailedException("No token stored yet."));
            firstRead.Replies["old-2"] = historyReplies;
            var analysedFirst = await CrawlAndSave(teamId, channelId, firstRead, tokens);
            var afterNormalImport = await Totals(channelId);
            Assert.AreEqual(3, afterNormalImport.Chats);
            Assert.AreEqual(2, afterNormalImport.Reactions);
            Assert.AreEqual(3, analysedFirst.Count);
            Assert.AreEqual("token-1", (await tokens.GetDeltaToken(teamId, channelId)).Token);

            // Cycle 2: Graph rejects that token, and the channel is read in full - its history and one thread started since.
            var created = DateTimeOffset.UtcNow.AddHours(1);
            var reset = new ScriptedChannel(info => info != null
                ? throw new ODataError { ResponseStatusCode = 400, Error = new MainError { Code = "UnknownError" } }
                : Read(history.Concat(new[] { Message("new-1", created, Reaction(created)) }).ToList(), "token-2"));
            reset.Replies["old-2"] = historyReplies;
            var analysedAfterReset = await CrawlAndSave(teamId, channelId, reset, tokens);

            var afterReset = await Totals(channelId);
            Assert.AreEqual(afterNormalImport.Chats + 1, afterReset.Chats, "Only the message created since the checkpoint is counted.");
            Assert.AreEqual(afterNormalImport.Reactions + 1, afterReset.Reactions, "Only the reaction added since the checkpoint is stored.");
            CollectionAssert.AreEqual(new[] { "new-1" }, analysedAfterReset.ToArray(), "Text already analysed is not sent to Azure AI Language again.");
            Assert.AreEqual(afterNormalImport.ChatsByDay[LastWeek.Date], afterReset.ChatsByDay[LastWeek.Date]);
            Assert.AreEqual(afterNormalImport.ChatsByDay[LastWeekNextDay.Date], afterReset.ChatsByDay[LastWeekNextDay.Date]);
            Assert.AreEqual("token-2", (await tokens.GetDeltaToken(teamId, channelId)).Token, "The full read's token replaced the rejected one.");
        }

        private static ChatMessage Message(string id, DateTimeOffset created, params ChatMessageReaction[] reactions) => new ChatMessage
        {
            Id = id,
            CreatedDateTime = created,
            Body = new ItemBody { Content = "Synthetic message text", ContentType = BodyType.Text },
            Replies = new List<ChatMessage>(),
            Reactions = reactions.ToList(),
        };

        private static ChatMessageReaction Reaction(DateTimeOffset created) => new ChatMessageReaction
        {
            ReactionType = "like",
            CreatedDateTime = created,
            User = new ChatMessageReactionIdentitySet { User = new Identity { Id = "00000000-0000-0000-0000-000000000000" } },
        };

        private static ChannelRootMessagesPageResult Read(List<ChatMessage> messages, string token) => new ChannelRootMessagesPageResult
        {
            Messages = messages,
            Deltalink = $"https://graph.microsoft.com/v1.0/teams/t/channels/c/messages/delta?$deltatoken={token}",
            Completed = true,
        };

        /// <summary>One cycle of the crawl for one channel, as <c>O365Team.LoadTeamFull</c> and the Teams importer run it.</summary>
        /// <returns>The messages whose text was handed to Azure AI Language.</returns>
        private static async Task<List<string>> CrawlAndSave(string teamId, string channelId, ScriptedChannel graph, ITeamChannelDeltaTokenStore tokens)
        {
            var channel = new ChannelWithReactions { Id = channelId, DisplayName = "Contoso Καλημέρα" };
            var team = new O365Team { Id = teamId, DisplayName = "Contoso", HasRefreshToken = true, Channels = new List<ChannelWithReactions> { channel } };

            var source = new ChannelReadThroughLoader(graph, tokens);
            await new TeamsChannelCrawler(source).PopulateNewMessagesAndReactions(team.Channels, team.Id, team.PendingChannelDeltaTokenCommits);

            // Nobody Graph can find, so each reaction is stored against "Unknown User", as for a deleted person.
            var noUsers = new TeamsLoadContext(TeamsCrawlTests.StubGraphHandler.Client(new TeamsCrawlTests.StubGraphHandler()));
            await team.ProcessAllReactionsFromMessages(noUsers, channel);

            var cognitive = new AnalysedMessages();
            using (var db = NewContext())
            using (var store = new SqlTeamsPersistenceStore(new TeamsAndCallsDBLookupManager(db)))
            {
                Assert.IsNotNull(await team.SaveToSQL(store, cognitive, tokens, NullLogger.Instance));
            }
            return cognitive.MessageIds;
        }

        private static async Task<(int Chats, int Reactions, Dictionary<DateTime, int> ChatsByDay)> Totals(string channelId)
        {
            using (var db = NewContext())
            {
                var channel = await db.TeamChannels.AsNoTracking().SingleAsync(c => c.GraphID == channelId);
                var stats = await db.TeamChannelStats.AsNoTracking().Where(s => s.ChannelID == channel.ID).ToListAsync();
                var reactions = await db.TeamsUserReactions.CountAsync(r => r.ChannelID == channel.ID);
                return (stats.Sum(s => s.ChatsCount ?? 0), reactions, stats.GroupBy(s => s.Date.Date).ToDictionary(g => g.Key, g => g.Sum(s => s.ChatsCount ?? 0)));
            }
        }

        private sealed class ScriptedChannel : IChannelMessagesPageReader
        {
            private readonly Func<TeamChannelDeltaTokenInfo, ChannelRootMessagesPageResult> _root;

            public ScriptedChannel(Func<TeamChannelDeltaTokenInfo, ChannelRootMessagesPageResult> root) { _root = root; }

            public Dictionary<string, List<ChatMessage>> Replies { get; } = new Dictionary<string, List<ChatMessage>>(StringComparer.Ordinal);

            public Task<ChannelRootMessagesPageResult> LoadRootMessages(string teamId, string channelId, TeamChannelDeltaTokenInfo channelDeltaInfo)
                => Task.FromResult(_root(channelDeltaInfo));

            public Task<ChannelRepliesPageResult> LoadReplies(string teamId, string channelId, string messageId)
                => Task.FromResult(new ChannelRepliesPageResult
                {
                    Replies = Replies.TryGetValue(messageId, out var replies) ? replies.ToList() : new List<ChatMessage>(),
                    Completed = true,
                });
        }

        private sealed class ChannelReadThroughLoader : IChannelMessagesSourceLoader
        {
            private readonly ChannelMessagesLoader _loader;

            public ChannelReadThroughLoader(IChannelMessagesPageReader graph, ITeamChannelDeltaTokenStore tokens)
            {
                _loader = new ChannelMessagesLoader(graph, tokens, NullLogger.Instance);
            }

            public Task<TeamChannelDeltaTokenInfo> LoadMessagesAndReactions(ChannelWithReactions channel, string teamId)
                => _loader.LoadTeamMessagesAndReplies(channel, teamId);
        }

        /// <summary>Daily counts as the real loader makes them, recording each message it would send to Azure AI Language.</summary>
        private sealed class AnalysedMessages : ITeamsCognitiveStatsLoader
        {
            public List<string> MessageIds { get; } = new List<string>();

            public Task<List<MessageCognitiveStats>> LoadStats(ChannelWithReactions channel, ILogger logger)
            {
                MessageIds.AddRange(channel.Messages.Select(m => m.Id));
                return Task.FromResult(channel.Messages.GetUniqueDates()
                    .Select(day => new MessageCognitiveStats(channel, day) { ChatsCount = channel.Messages.GetByDate(day).Count, Sentiment = 0.5 })
                    .ToList());
            }
        }

        #endregion

        #region Per-Team logs: one query each, not one per person (R8-5)

        /// <summary>
        /// Regression: whether each member was already logged today was one query per member, comparing the date's year, month and
        /// day - which no index can seek - so each read the member's whole membership history. Now it is one query per Team, and a
        /// person is still logged once a day: on a repeat crawl, when listed twice, and when SQL meets them for the first time.
        /// </summary>
        [TestMethod]
        public async Task SaveMembers_ReadsTodaysMembershipOnceForTheTeam_AndLogsEachPersonOncePerDay()
        {
            var suffix = Guid.NewGuid().ToString("N");
            string Upn(string who) => $"{who}.{suffix}@contoso.com";
            var source = new O365Team { Id = "team-" + suffix, DisplayName = "Contoso Καλημέρα" };
            var today = DateTime.UtcNow.Date;

            int teamDbId;
            using (var db = NewContext())
            {
                var team = new TeamDefinition { GraphID = source.Id, Name = source.DisplayName, FirstDiscovered = today.AddDays(-30) };
                var known = new[] { "logged-today", "not-yet-today", "history-only" }.Select(who => new Common.Entities.User { UserPrincipalName = Upn(who) }).ToList();
                db.Teams.Add(team);
                db.users.AddRange(known);
                await db.SaveChangesAsync();
                foreach (var daysAgo in Enumerable.Range(1, 30))
                {
                    db.TeamMembershipLogs.AddRange(known.Select(u => new TeamMembershipLog { TeamID = team.ID, UserID = u.ID, Date = today.AddDays(-daysAgo) }));
                }

                // A crawl earlier today already logged this one.
                db.TeamMembershipLogs.Add(new TeamMembershipLog { TeamID = team.ID, UserID = known[0].ID, Date = today });
                await db.SaveChangesAsync();
                teamDbId = team.ID;
            }

            var members = new[] { "logged-today", "not-yet-today", "history-only", "not-yet-today", "new-to-sql" }
                .Select(who => new BaseUser { UserPrincipalName = Upn(who) }).ToList();
            for (var crawl = 1; crawl <= 2; crawl++)
            {
                var queries = await RecordQueries(async db =>
                {
                    using (var store = new SqlTeamsPersistenceStore(new TeamsAndCallsDBLookupManager(db)))
                    {
                        var team = await store.GetOrCreateTeam(source);
                        await store.SaveMembers(members, source);
                        await store.SaveChanges(source, team);
                    }
                });
                Assert.AreEqual(1, queries.Count(q => IsSelectFrom(q, "team_membership_log")), $"Crawl {crawl}: one membership query for the Team, not one per member.");
            }

            using (var db = NewContext())
            {
                var loggedToday = await db.TeamMembershipLogs.Where(m => m.TeamID == teamDbId && m.Date == today).Select(m => m.User.UserPrincipalName).ToListAsync();
                CollectionAssert.AreEquivalent(new[] { Upn("logged-today"), Upn("not-yet-today"), Upn("history-only"), Upn("new-to-sql") }, loggedToday,
                    "Everyone is logged today exactly once, after two crawls and one person listed twice.");
                Assert.AreEqual(90, await db.TeamMembershipLogs.CountAsync(m => m.TeamID == teamDbId && m.Date < today), "Earlier days are untouched.");
            }
        }

        /// <summary>
        /// Owners and each channel's tabs follow the same rule: one query for the Team's owners and one for a channel's tabs logged
        /// today - not one per owner and per tab - and nothing is recorded twice when the Team is saved again.
        /// </summary>
        [TestMethod]
        public async Task SaveOwnersAndChannelTabs_ReadWhatIsRecordedOnceForTheTeamOrChannel_AndRecordNothingTwice()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var source = new O365Team { Id = "team-" + suffix, DisplayName = "Contoso" };
            var owners = Enumerable.Range(1, 3).Select(i => new Microsoft.Graph.Models.User { UserPrincipalName = $"owner{i}.{suffix}@contoso.com" }).ToList();
            owners.Add(owners[0]);
            var channel = new ChannelWithReactions
            {
                Id = "channel-" + suffix,
                DisplayName = "Καλημέρα κόσμε",
                Tabs = Enumerable.Range(1, 3).Select(i => new TeamsTab { Id = $"tab{i}-{suffix}", DisplayName = "Contoso Καλημέρα " + i, WebUrl = "https://contoso.example/" + i }).ToList(),
            };

            for (var save = 1; save <= 2; save++)
            {
                var queries = await RecordQueries(async db =>
                {
                    using (var store = new SqlTeamsPersistenceStore(new TeamsAndCallsDBLookupManager(db)))
                    {
                        var team = await store.GetOrCreateTeam(source);
                        await store.SaveOwners(owners, team);
                        await store.SaveChannel(channel, team);
                        await store.SaveChanges(source, team);
                    }
                });
                Assert.AreEqual(1, queries.Count(q => IsSelectFrom(q, "team_owners")), $"Save {save}: one owners query for the Team.");
                Assert.AreEqual(save == 1 ? 0 : 1, queries.Count(q => IsSelectFrom(q, "teams_channel_tabs_log")),
                    $"Save {save}: a channel new to SQL has no tabs logged; a known one costs one query, not one per tab.");
            }

            using (var db = NewContext())
            {
                var team = await db.Teams.AsNoTracking().SingleAsync(t => t.GraphID == source.Id);
                var savedChannel = await db.TeamChannels.AsNoTracking().SingleAsync(c => c.GraphID == channel.Id);
                Assert.AreEqual(3, await db.TeamOwners.CountAsync(o => o.TeamID == team.ID), "Each owner once, though listed twice and saved twice.");
                Assert.AreEqual(3, await db.ChannelTabLogs.CountAsync(t => t.ChannelID == savedChannel.ID), "Each tab once today, though saved twice.");
            }
        }

        private static bool IsSelectFrom(string sql, string table)
            => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && sql.IndexOf($"[dbo].[{table}]", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Runs <paramref name="work"/> on a new context and returns every reader command it sent to this test's database.</summary>
        private static async Task<List<string>> RecordQueries(Func<AnalyticsEntitiesContext, Task> work)
        {
            var recorder = new CommandRecorder(_catalog);
            DbInterception.Add(recorder);
            try
            {
                using (var db = NewContext())
                {
                    await work(db);
                }
            }
            finally
            {
                DbInterception.Remove(recorder);
            }
            return recorder.Commands;
        }

        private sealed class CommandRecorder : DbCommandInterceptor
        {
            private readonly string _catalog;

            public CommandRecorder(string catalog) { _catalog = catalog; }

            public List<string> Commands { get; } = new List<string>();

            public override void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext)
            {
                if (string.Equals(command.Connection?.Database, _catalog, StringComparison.OrdinalIgnoreCase))
                {
                    lock (Commands)
                    {
                        Commands.Add(command.CommandText);
                    }
                }
            }
        }

        #endregion
    }
}
