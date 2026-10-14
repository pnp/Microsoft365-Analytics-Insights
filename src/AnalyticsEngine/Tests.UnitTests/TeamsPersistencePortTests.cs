using Common.Entities.Config;
using Common.Entities.Entities;
using Common.Entities.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace Tests.UnitTests
{
    [TestClass]
    public class TeamsPersistencePortTests
    {
        private static readonly DateTime Day = new DateTime(2026, 1, 10);

        private static O365Team Team()
        {
            var team = new O365Team { Id = "contoso-team", DisplayName = "Contoso Καλημέρα", HasRefreshToken = true, LastRefreshed = Day };
            team.OwnerUserAccounts.Add(new Microsoft.Graph.Models.User { UserPrincipalName = "owner@contoso.example" });
            team.Users.Add(new BaseUser { UserPrincipalName = "member@contoso.example" });
            team.Channels.Add(new ChannelWithReactions { Id = "channel-one", DisplayName = "Καλημέρα", Messages = new List<ChatMessage> { Msg(Day), Msg(Day.AddDays(1)) } });
            team.Channels.Add(new ChannelWithReactions { Id = "channel-two", DisplayName = "Contoso General", Messages = new List<ChatMessage> { Msg(Day) } });
            team.AllReactions.Add(new UserReaction { ChannelId = "channel-one", Reaction = "like", When = Day, GraphUser = null });
            team.AllReactions.Add(new UserReaction { ChannelId = "channel-two", Reaction = "heart", When = Day, GraphUser = new Microsoft.Graph.Models.User { UserPrincipalName = "member@contoso.example" } });
            foreach (var channel in team.Channels)
                team.PendingChannelDeltaTokenCommits.Add(new TeamChannelDeltaTokenCommit(channel.Id, new TeamChannelDeltaTokenInfo { Token = channel.Id + "-new", LastUpdated = Day }));
            return team;
        }

        private static ChatMessage Msg(DateTime date) => new ChatMessage { CreatedDateTime = new DateTimeOffset(date, TimeSpan.Zero), Body = new ItemBody { Content = "Synthetic message text", ContentType = BodyType.Text } };

        [TestMethod]
        public async Task Save_Success_PreservesStagesDataAndCheckpointOrder()
        {
            var team = Team();
            var events = new List<string>();
            var store = new RecordingStore(events);
            var cognitive = new RecordingCognitive(events);
            var tokens = new RecordingTokens(events);
            var logger = new RecordingLogger();

            var result = await team.SaveToSQL(store, cognitive, tokens, logger);

            Assert.AreSame(store.Team, result);
            CollectionAssert.AreEqual(new[] { "team", "owners", "members", "channel:channel-one", "channel:channel-two",
                "ai:channel-one", "ai:channel-two", "stats:channel-one", "stats:channel-one", "stats:channel-two",
                "reactions", "commit", "token:channel-one", "token:channel-two" }, events);
            Assert.AreSame(team.OwnerUserAccounts, store.Owners);
            Assert.AreSame(team.Users, store.Members);
            Assert.AreSame(team.AllReactions, store.Reactions);
            Assert.AreEqual("Contoso Καλημέρα", result.Name);
            Assert.AreEqual(team.LastRefreshed, result.LastRefreshed);
            Assert.IsTrue(result.HasRefreshToken);
            Assert.AreEqual(3, store.Stats.Sum(s => s.ChatsCount));
            Assert.AreEqual(0.75, store.Stats[0].Sentiment);
            Assert.AreEqual(1, store.Stats[0].KeyWords["Contoso"]);
            Assert.AreEqual("en", store.Stats[0].Languages.Single());
            Assert.IsNull(store.Reactions[0].GraphUser, "Unknown reaction authors still reach the SQL fallback.");
            Assert.AreEqual("Saving Team 'Contoso Καλημέρα' to SQL...", logger.Entries.Single().Message);
            Assert.AreSame(team.PendingChannelDeltaTokenCommits[0].DeltaTokenInfo, await tokens.GetDeltaToken(team.Id, "channel-one"));
            Assert.AreEqual(team.Id, tokens.LastTeamId);
        }

        [TestMethod]
        public async Task Save_PartialChannelRead_PersistsCompletedChannelAndOnlyItsCheckpoint()
        {
            var team = Team();
            team.Channels.ForEach(c => c.Messages.Clear());
            team.PendingChannelDeltaTokenCommits.Clear();
            var source = new PartialSource();
            var pending = team.PendingChannelDeltaTokenCommits;
            await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                new TeamsChannelCrawler(source).PopulateNewMessagesAndReactions(team.Channels, team.Id, pending));

            var events = new List<string>();
            var store = new RecordingStore(events);
            var tokens = new RecordingTokens(events);
            await team.SaveToSQL(store, new RecordingCognitive(events), tokens, new RecordingLogger());

            Assert.AreEqual(2, store.Channels.Count, "Channel metadata is still saved after a partial message read.");
            Assert.AreEqual("channel-one", store.Stats.Single().Channel.Id);
            Assert.IsNotNull(await tokens.GetDeltaToken(team.Id, "channel-one"));
            Assert.IsNull(await tokens.GetDeltaToken(team.Id, "channel-two"));
            Assert.IsTrue(events.IndexOf("commit") < events.IndexOf("token:channel-one"));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Save_CognitiveUnavailable_PersistsBasicDailyCountsAndOriginalWarning(bool validConfig)
        {
            var events = new List<string>();
            var store = new RecordingStore(events);
            var logger = new RecordingLogger();
            var config = new AppConfig { CognitiveEndpoint = validConfig ? "https://contoso.example" : null, CognitiveKey = "synthetic-key" };
            var builds = 0;
            var cognitive = new TeamsCognitiveStatsLoader(config, logger, () => { builds++; return null; });
            var team = Team();
            await team.SaveToSQL(store, cognitive, new RecordingTokens(events), logger);
            Assert.AreEqual(validConfig ? 1 : 0, builds, "Reuse one lazy client for all channels; invalid config never builds it.");
            Assert.AreEqual(3, store.Stats.Sum(s => s.ChatsCount));
            Assert.IsTrue(store.Stats.All(s => s.Sentiment == null && s.KeyWords.Count == 0 && s.Languages.Count == 0));
            var warning = validConfig
                ? "Could not build cognitive client for channel Καλημέρα (channel-one). Adding basic stats with no cognitive insights."
                : "Cognitive config not valid. Cannot load cognitive stats for channel Καλημέρα (channel-one). Adding basic stats with no cognitive insights.";
            Assert.AreEqual(warning, logger.Entries.First(e => e.Level == LogLevel.Warning).Message);
            Assert.AreEqual(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
        }

        [TestMethod]
        public async Task Cognitive_ClientLifetimes_AreIndependentBetweenImporters()
        {
            var logger = new RecordingLogger();
            var config = new AppConfig { CognitiveEndpoint = "https://contoso.example", CognitiveKey = "synthetic-key" };
            var firstBuilds = 0;
            var secondBuilds = 0;
            var first = new TeamsCognitiveStatsLoader(config, logger, () => { firstBuilds++; return null; });
            var second = new TeamsCognitiveStatsLoader(config, logger, () => { secondBuilds++; return null; });
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => first.LoadStats(Team().Channels[0], logger))));
            await second.LoadStats(Team().Channels[0], logger);
            Assert.AreEqual(1, firstBuilds);
            Assert.AreEqual(1, secondBuilds, "No process-wide mutable client contaminates another importer.");
        }

        [TestMethod]
        public async Task Cognitive_FactoryFailure_IsRetriedRatherThanCachedAsACompletedBuild()
        {
            var logger = new RecordingLogger();
            var config = new AppConfig { CognitiveEndpoint = "https://contoso.example", CognitiveKey = "synthetic-key" };
            var builds = 0;
            var cognitive = new TeamsCognitiveStatsLoader(config, logger, () =>
            {
                if (++builds == 1) throw new InvalidOperationException("synthetic client failure");
                return null;
            });
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => cognitive.LoadStats(Team().Channels[0], logger));
            var stats = await cognitive.LoadStats(Team().Channels[0], logger);
            Assert.AreEqual(2, builds, "The original client cache did not mark a failed factory as built.");
            Assert.AreEqual(2, stats.Sum(s => s.ChatsCount));
        }

        [TestMethod]
        public async Task Save_FinalDbUpdateFailure_ReturnsNullLogsAndLeavesBothOldCheckpoints()
        {
            var events = new List<string>();
            var failure = new DbUpdateException("synthetic SQL failure");
            var store = new RecordingStore(events) { FailStage = "commit", Failure = failure };
            var tokens = new RecordingTokens(events);
            var team = Team();
            await tokens.SetDeltaToken(team.Id, "channel-one", new TeamChannelDeltaTokenInfo { Token = "one-old" });
            await tokens.SetDeltaToken(team.Id, "channel-two", new TeamChannelDeltaTokenInfo { Token = "two-old" });
            events.Clear();
            var logger = new RecordingLogger();
            var result = await team.SaveToSQL(store, new RecordingCognitive(events), tokens, logger);
            Assert.IsNull(result);
            Assert.AreEqual("one-old", (await tokens.GetDeltaToken(team.Id, "channel-one")).Token);
            Assert.AreEqual("two-old", (await tokens.GetDeltaToken(team.Id, "channel-two")).Token);
            Assert.AreEqual(2, team.PendingChannelDeltaTokenCommits.Count, "Pending checkpoints are not consumed on failure.");
            Assert.AreEqual("Got SQL exception saving Team: synthetic SQL failure. Will try again next cycle.", logger.Entries.Last().Message);
            Assert.AreSame(failure, logger.Entries.Last().Exception);
            Assert.AreEqual(LogLevel.Error, logger.Entries.Last().Level);
        }

        [DataTestMethod]
        [DataRow("team")]
        [DataRow("owners")]
        [DataRow("members")]
        [DataRow("channel:channel-two")]
        [DataRow("stats:channel-one")]
        [DataRow("reactions")]
        public async Task Save_StagingFailure_PropagatesWithoutFinalCommitOrCheckpoints(string stage)
        {
            var events = new List<string>();
            var failure = new DbUpdateException("synthetic staging failure");
            var store = new RecordingStore(events) { FailStage = stage, Failure = failure };
            var tokens = new RecordingTokens(events);
            var thrown = await Assert.ThrowsExceptionAsync<DbUpdateException>(() => Team().SaveToSQL(store, new RecordingCognitive(events), tokens, new RecordingLogger()));
            Assert.AreSame(failure, thrown, "Only the final DbUpdateException was previously swallowed.");
            Assert.AreEqual(stage, events.Last());
            Assert.IsFalse(events.Contains("commit"));
            Assert.IsFalse(events.Any(e => e.StartsWith("token:")));
        }

        [TestMethod]
        public async Task Save_NonDbUpdateCommitFailure_StillPropagatesWithoutTokens()
        {
            var events = new List<string>();
            var store = new RecordingStore(events) { FailStage = "commit", Failure = new InvalidOperationException("synthetic failure") };
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                Team().SaveToSQL(store, new RecordingCognitive(events), new RecordingTokens(events), new RecordingLogger()));
            Assert.IsFalse(events.Any(e => e.StartsWith("token:")));
        }

        [TestMethod]
        public async Task Save_CognitiveFailure_DoesNotBroadenFallbackOrCommit()
        {
            var events = new List<string>();
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Team().SaveToSQL(new RecordingStore(events),
                new RecordingCognitive(events) { Fail = true }, new RecordingTokens(events), new RecordingLogger()));
            Assert.IsFalse(events.Contains("commit"));
            Assert.IsFalse(events.Any(e => e.StartsWith("stats:") || e.StartsWith("token:")));
        }

        [TestMethod]
        public async Task Save_NoStateStore_StillCommitsSql()
        {
            var events = new List<string>();
            Assert.IsNotNull(await Team().SaveToSQL(new RecordingStore(events), new RecordingCognitive(events), null, new RecordingLogger()));
            Assert.AreEqual("commit", events.Last());
        }

        [TestMethod]
        public async Task Save_TokenFailure_PropagatesAfterSqlWithoutRollbackOrAdvancingLaterChannel()
        {
            var events = new List<string>();
            var tokens = new RecordingTokens(events) { FailChannel = "channel-one" };
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                Team().SaveToSQL(new RecordingStore(events), new RecordingCognitive(events), tokens, new RecordingLogger()));
            CollectionAssert.AreEqual(new[] { "commit", "token:channel-one" }, events.Skip(events.Count - 2).ToArray());
            Assert.IsNull(await tokens.GetDeltaToken("contoso-team", "channel-two"));
        }

        private sealed class RecordingStore : ITeamsPersistenceStore
        {
            private readonly List<string> _events;
            public RecordingStore(List<string> events) { _events = events; }
            public TeamDefinition Team { get; } = new TeamDefinition();
            public List<Microsoft.Graph.Models.User> Owners { get; private set; }
            public List<BaseUser> Members { get; private set; }
            public List<UserReaction> Reactions { get; private set; }
            public List<ChannelWithReactions> Channels { get; } = new List<ChannelWithReactions>();
            public List<MessageCognitiveStats> Stats { get; } = new List<MessageCognitiveStats>();
            public string FailStage { get; set; }
            public Exception Failure { get; set; }
            private Task Stage(string stage) { _events.Add(stage); return stage == FailStage ? Task.FromException(Failure) : Task.CompletedTask; }
            public async Task<TeamDefinition> GetOrCreateTeam(O365Team team) { await Stage("team"); Team.Name = team.DisplayName; return Team; }
            public async Task SaveOwners(List<Microsoft.Graph.Models.User> owners, TeamDefinition team) { await Stage("owners"); Owners = owners; }
            public async Task SaveMembers(List<BaseUser> members, O365Team team) { await Stage("members"); Members = members; }
            public async Task<TeamChannel> SaveChannel(ChannelWithReactions channel, TeamDefinition team) { await Stage("channel:" + channel.Id); Channels.Add(channel); return new TeamChannel(); }
            public async Task SaveStats(MessageCognitiveStats stats, TeamDefinition team) { await Stage("stats:" + stats.Channel.Id); Stats.Add(stats); }
            public async Task SaveReactions(List<UserReaction> reactions) { await Stage("reactions"); Reactions = reactions; }
            public async Task SaveChanges(O365Team source, TeamDefinition team) { await Stage("commit"); team.HasRefreshToken = source.HasRefreshToken; team.LastRefreshed = source.LastRefreshed; }
        }

        private sealed class RecordingCognitive : ITeamsCognitiveStatsLoader
        {
            private readonly List<string> _events;
            public RecordingCognitive(List<string> events) { _events = events; }
            public bool Fail { get; set; }
            public Task<List<MessageCognitiveStats>> LoadStats(ChannelWithReactions channel, ILogger logger)
            {
                _events.Add("ai:" + channel.Id);
                if (Fail) return Task.FromException<List<MessageCognitiveStats>>(new InvalidOperationException("synthetic cognitive failure"));
                return Task.FromResult(channel.Messages.GroupBy(m => m.CreatedDateTime.Value.Date).Select(g => new MessageCognitiveStats(channel, g.Key)
                {
                    ChatsCount = g.Count(), Sentiment = 0.75, KeyWords = new Dictionary<string, int> { ["Contoso"] = 1 }, Languages = new List<string> { "en" }
                }).ToList());
            }
        }

        private sealed class RecordingTokens : ITeamChannelDeltaTokenStore
        {
            private readonly List<string> _events;
            private readonly InMemoryTeamChannelDeltaTokenStore _store = new InMemoryTeamChannelDeltaTokenStore();
            public RecordingTokens(List<string> events) { _events = events; }
            public string FailChannel { get; set; }
            public string LastTeamId { get; private set; }
            public Task<TeamChannelDeltaTokenInfo> GetDeltaToken(string teamId, string channelId) => _store.GetDeltaToken(teamId, channelId);
            public Task RemoveDeltaToken(string teamId, string channelId) => _store.RemoveDeltaToken(teamId, channelId);
            public Task SetDeltaToken(string teamId, string channelId, TeamChannelDeltaTokenInfo token)
            {
                LastTeamId = teamId;
                _events.Add("token:" + channelId);
                return channelId == FailChannel ? Task.FromException(new InvalidOperationException("synthetic state failure")) : _store.SetDeltaToken(teamId, channelId, token);
            }
        }

        private sealed class PartialSource : IChannelMessagesSourceLoader
        {
            public Task<TeamChannelDeltaTokenInfo> LoadMessagesAndReactions(ChannelWithReactions channel, string teamId)
            {
                if (channel.Id == "channel-two") return Task.FromException<TeamChannelDeltaTokenInfo>(new ChannelMessagesReadException(new InvalidOperationException("synthetic Graph failure")));
                channel.Messages.Add(Msg(Day));
                return Task.FromResult(new TeamChannelDeltaTokenInfo { Token = "one-new", LastUpdated = Day });
            }
        }
    }
}
