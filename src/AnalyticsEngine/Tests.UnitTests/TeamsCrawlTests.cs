using Common.Entities.Config;
using Common.Entities.Models;
using Common.Entities.State;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace Tests.UnitTests
{
    /// <summary>
    /// First unit coverage for the Teams import (issue #377). Everything here runs with no Graph, no
    /// storage and no SQL: the crawl's decisions are now behind pure rules and ports.
    /// </summary>
    [TestClass]
    public class TeamsCrawlTests
    {
        #region Which groups get crawled

        /// <summary>
        /// A group only counts as having a Team if its <c>resourceProvisioningOptions</c> array says so.
        /// The three-way answer matters: a group that didn't report the property at all is neither
        /// crawled nor reported as "has no Team associated" — reporting it would spam the importer log
        /// with every security group and distribution list in the tenant, on every cycle.
        /// </summary>
        [TestMethod]
        public void ClassifyGroup_DistinguishesNoTeamFromWorkloadsNotReported()
        {
            var withTeam = FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g-team", "Contoso Marketing", "[\"Team\",\"Exchange\"]");
            var withoutTeam = FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g-noteam", "Contoso Distribution List", "[\"Exchange\"]");
            var noPropertyAtAll = FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g-unknown", "Contoso Security Group", null);

            Assert.AreEqual(GroupTeamStatus.HasTeam, TeamsCrawlRules.ClassifyGroup(withTeam));
            Assert.AreEqual(GroupTeamStatus.NoTeam, TeamsCrawlRules.ClassifyGroup(withoutTeam));
            Assert.AreEqual(GroupTeamStatus.WorkloadsNotReported, TeamsCrawlRules.ClassifyGroup(noPropertyAtAll),
                "A group with no resourceProvisioningOptions property must be distinguishable from one that reported no Team.");

            Assert.IsFalse(TeamsCrawlRules.GroupHasTeam(noPropertyAtAll), "Either way it is not crawled.");
        }

        /// <summary>
        /// Graph has returned this value with varying casing; the importer has always compared
        /// case-insensitively, and a group whose workload reads "TEAM" must still be crawled.
        /// </summary>
        [TestMethod]
        public void ProvisioningOptionsIncludeTeam_IgnoresCase()
        {
            Assert.IsTrue(TeamsCrawlRules.ProvisioningOptionsIncludeTeam("[\"TEAM\"]"));
            Assert.IsTrue(TeamsCrawlRules.ProvisioningOptionsIncludeTeam("[\"Team\"]"));
            Assert.IsFalse(TeamsCrawlRules.ProvisioningOptionsIncludeTeam("[\"Teams\"]"),
                "'Teams' is not the workload name - matching it would crawl groups that have no Team.");
            Assert.IsFalse(TeamsCrawlRules.ProvisioningOptionsIncludeTeam("[]"));
        }

        /// <summary>
        /// The typed list the SDK deserialises is what the crawl reads; a group carrying only the raw v4-style array in
        /// <c>AdditionalData</c> (built by hand) still classifies, but never overrides what the SDK said.
        /// </summary>
        [TestMethod]
        public void ClassifyGroup_ReadsTheTypedList_FallingBackToTheRawArrayInAdditionalData()
        {
            Assert.AreEqual(GroupTeamStatus.HasTeam, TeamsCrawlRules.ClassifyGroup(new Group { ResourceProvisioningOptions = new List<string> { "TEAM" } }));
            Assert.AreEqual(GroupTeamStatus.NoTeam, TeamsCrawlRules.ClassifyGroup(new Group { ResourceProvisioningOptions = new List<string>() }));

            var legacyShape = new Group { Id = "g-legacy" };
            legacyShape.AdditionalData["resourceProvisioningOptions"] = "[\"Team\"]";
            Assert.AreEqual(GroupTeamStatus.HasTeam, TeamsCrawlRules.ClassifyGroup(legacyShape));

            var both = new Group { Id = "g-both", ResourceProvisioningOptions = new List<string> { "Exchange" } };
            both.AdditionalData["resourceProvisioningOptions"] = "[\"Team\"]";
            Assert.AreEqual(GroupTeamStatus.NoTeam, TeamsCrawlRules.ClassifyGroup(both), "What the SDK deserialised is authoritative.");
        }

        /// <summary>
        /// The blacklist beats the whitelist, and both partitions keep the order the groups arrived in -
        /// that ordering is what keeps the importer's "Excluding group ..." log identical.
        /// </summary>
        [TestMethod]
        public void PartitionByCrawlConfig_AppliesWhitelistAndBlacklistPreservingOrder()
        {
            var config = new TeamsCrawlConfig();
            config.WhitelistTeamsIds.Add("g1");
            config.WhitelistTeamsIds.Add("g3");
            config.BlacklistTeamsIds.Add("g3");

            var groups = new[]
            {
                FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g1", "One", "[\"Team\"]"),
                FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g2", "Two", "[\"Team\"]"),
                FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g3", "Three", "[\"Team\"]"),
            };

            var partition = TeamsCrawlRules.PartitionByCrawlConfig(groups, config);

            CollectionAssert.AreEqual(new[] { "g1" }, partition.ToCrawl.Select(g => g.Id).ToArray(),
                "Only whitelisted groups that aren't also blacklisted should be crawled.");
            CollectionAssert.AreEqual(new[] { "g2", "g3" }, partition.Excluded.Select(g => g.Id).ToArray(),
                "Excluded groups must stay in source order so the log lines are emitted in the same order as before.");
        }

        /// <summary>
        /// End to end through the real <see cref="TeamsFinder"/> with a fake Graph source: only groups
        /// that both have a Team and pass the crawl config come back, and the importer still uses the v1
        /// "read everything then filter" path rather than the unfinished beta server-side filter.
        /// </summary>
        [TestMethod]
        public async Task FindGroupsWithTeamToCrawl_ReturnsOnlyCrawlableGroupsWithATeam()
        {
            var source = new FakeTeamsGroupSourceLoader(
                FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g-crawl", "Contoso Marketing", "[\"Team\"]"),
                FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g-blocked", "Contoso HR", "[\"Team\"]"),
                FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g-noteam", "Contoso Distribution List", "[\"Exchange\"]"),
                FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g-unknown", "Contoso Security Group", null));

            var crawlConfig = new TeamsCrawlConfig();
            crawlConfig.BlacklistTeamsIds.Add("g-blocked");

            var finder = new TeamsFinder(AnalyticsLogger.ConsoleOnlyTracer(), new Common.Entities.Config.AppConfig(), source);
            var toCrawl = await finder.FindGroupsWithTeamToCrawl(crawlConfig);

            CollectionAssert.AreEqual(new[] { "g-crawl" }, toCrawl.Select(g => g.Id).ToArray());
            Assert.AreEqual(1, source.ProvisioningOptionsReadCount, "The finder should read groups from Graph exactly once per crawl.");
            Assert.AreEqual(0, source.FilteredToTeamsReadCount, "The beta server-side filter is not in use; switching to it changes which groups are crawled.");
        }

        /// <summary>
        /// Contract test against the real Graph SDK: Graph's own JSON goes through the production loader, Kiota's
        /// deserialiser and the real <see cref="TeamsFinder"/>. Since Microsoft.Graph 5 the SDK puts
        /// <c>resourceProvisioningOptions</c> into the typed <see cref="Group.ResourceProvisioningOptions"/> rather than
        /// <c>AdditionalData</c>, and a crawl that only looked in <c>AdditionalData</c> found no Teams at all while reporting
        /// success. A fake that builds the groups by hand cannot catch that; this test does.
        /// </summary>
        [TestMethod]
        public async Task FindGroupsWithTeamToCrawl_RealGraphJsonThroughTheSdk_FindsTheTeams()
        {
            const string teamGroupId = "00000000-0000-0000-0000-000000000001";
            var handler = new StubGraphHandler().Json("/groups", "{\"value\":[" +
                "{\"id\":\"" + teamGroupId + "\",\"displayName\":\"Contoso Team\",\"resourceProvisioningOptions\":[\"Team\"]}," +
                "{\"id\":\"00000000-0000-0000-0000-000000000002\",\"displayName\":\"Contoso Distribution List\",\"resourceProvisioningOptions\":[]}," +
                "{\"id\":\"00000000-0000-0000-0000-000000000003\",\"displayName\":\"Contoso Security Group\"}]}");
            var source = new GraphTeamsGroupSourceLoader(StubGraphHandler.Client(handler), NullLogger.Instance);

            var groups = await source.LoadGroupsWithProvisioningOptions();

            Assert.AreEqual(3, groups.Count);
            Assert.AreEqual(GroupTeamStatus.HasTeam, TeamsCrawlRules.ClassifyGroup(groups[0]));
            Assert.AreEqual(GroupTeamStatus.NoTeam, TeamsCrawlRules.ClassifyGroup(groups[1]), "An empty array means the group reported no Team.");
            Assert.AreEqual(GroupTeamStatus.WorkloadsNotReported, TeamsCrawlRules.ClassifyGroup(groups[2]));

            var toCrawl = await new TeamsFinder(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), source)
                .FindGroupsWithTeamToCrawl(TeamsCrawlConfig.AllGroupsConfig);

            CollectionAssert.AreEqual(new[] { teamGroupId }, toCrawl.Select(g => g.Id).ToArray());
            Assert.IsTrue(handler.Requests.All(r => Uri.UnescapeDataString(r.Query).Contains("resourceProvisioningOptions")),
                "The v1 endpoint only returns the property when it is selected.");
        }

        #endregion

        #region What counts as new in a channel

        // Local (unspecified-kind) instants on purpose: the stored delta timestamp is written as
        // DateTime.Now and compared against Graph's DateTimeOffset, so this mirrors production and
        // stays correct whatever timezone the test machine is in.
        private static readonly DateTime DeltaTokenWrittenAt = new DateTime(2026, 1, 10, 12, 0, 0);
        private static readonly DateTime BeforeToken = DeltaTokenWrittenAt.AddHours(-1);
        private static readonly DateTime AfterToken = DeltaTokenWrittenAt.AddHours(1);

        private static ChatMessage Msg(string id, DateTime created, params DateTime[] reactionTimes)
        {
            return new ChatMessage
            {
                Id = id,
                CreatedDateTime = new DateTimeOffset(created),
                Replies = new List<ChatMessage>(),
                Reactions = reactionTimes.Select(t => new ChatMessageReaction
                {
                    ReactionType = "like",
                    CreatedDateTime = new DateTimeOffset(t)
                }).ToList()
            };
        }

        /// <summary>
        /// With no delta token (the first read of a channel) everything Graph returned is new, replies
        /// included.
        /// </summary>
        [TestMethod]
        public void SelectNewMessagesAndReactions_FirstRead_TakesEverything()
        {
            var root = Msg("root", BeforeToken, BeforeToken);
            root.Replies.Add(Msg("reply", AfterToken, AfterToken));

            var scope = ChannelMessageScopeRules.SelectNewMessagesAndReactions(new[] { root }, null);

            CollectionAssert.AreEqual(new[] { "root", "reply" }, scope.NewMessages.Select(m => m.Id).ToArray());
            Assert.AreEqual(2, scope.NewReactions.Count, "Reactions on both the root message and its replies count on a full read.");
            Assert.AreEqual(1, scope.RepliesSeen);
        }

        /// <summary>
        /// This is the rule the delta read depends on. Graph re-serves a thread's parent whenever a
        /// reply or a reaction changes, so without the created-date filter the importer would re-count
        /// old messages on every cycle and inflate every channel's message stats.
        /// </summary>
        [TestMethod]
        public void SelectNewMessagesAndReactions_DeltaRead_TakesOnlyContentCreatedAfterTheToken()
        {
            // An old parent message, re-served by the delta only because someone reacted to it and
            // replied to it since the last read.
            var reServedParent = Msg("old-parent", BeforeToken, BeforeToken, AfterToken);
            reServedParent.Replies.Add(Msg("new-reply", AfterToken));
            reServedParent.Replies.Add(Msg("old-reply", BeforeToken));

            var scope = ChannelMessageScopeRules.SelectNewMessagesAndReactions(new[] { reServedParent }, DeltaTokenWrittenAt);

            CollectionAssert.AreEqual(new[] { "new-reply" }, scope.NewMessages.Select(m => m.Id).ToArray(),
                "Only content created after the delta token is new; the re-served parent and the old reply are not.");
            Assert.AreEqual(1, scope.NewReactions.Count, "Only the reaction added after the delta token is new.");
            Assert.AreEqual(new DateTimeOffset(AfterToken), scope.NewReactions[0].CreatedDateTime);
            Assert.AreEqual(2, scope.RepliesSeen);
        }

        /// <summary>
        /// The channel object must end up holding what the rule selected - and must be holding it
        /// because the delta filter ran. The fixture deliberately mixes before-token and after-token
        /// content, so a wrapper that passed <c>null</c> for the delta timestamp, or assigned everything
        /// Graph returned, fails here rather than looking correct.
        /// </summary>
        [TestMethod]
        public void CalculateAndSetNewMessagesAndReactions_ReplacesChannelMessagesAndReactions()
        {
            var channel = new ChannelWithReactions { Id = "channel-1", DisplayName = "General" };
            channel.Messages.Add(Msg("stale-from-a-previous-cycle", BeforeToken));
            channel.Reactions.Add(new ChatMessageReaction { ReactionType = "stale" });

            // An old thread parent, re-served by the delta only because it was replied to and reacted to.
            var reServedParent = Msg("old-parent", BeforeToken, BeforeToken, AfterToken);
            reServedParent.Replies.Add(Msg("new-reply", AfterToken));

            channel.CalculateAndSetNewMessagesAndReactions(new List<ChatMessage> { reServedParent }, DeltaTokenWrittenAt, AnalyticsLogger.ConsoleOnlyTracer());

            CollectionAssert.AreEqual(new[] { "new-reply" }, channel.Messages.Select(m => m.Id).ToArray(),
                "The channel must end up with the delta-filtered set, not everything Graph returned.");
            Assert.AreEqual(1, channel.Reactions.Count, "Only the reaction added since the delta token is new.");
            Assert.AreEqual(new DateTimeOffset(AfterToken), channel.Reactions[0].CreatedDateTime);
        }

        #endregion

        #region Crawling a team's channels

        private static ChannelWithReactions Channel(string id) => new ChannelWithReactions { Id = id, DisplayName = id };

        private static async Task CrawlPersistAndCommitTokens(
            TeamsChannelCrawler crawler,
            List<ChannelWithReactions> channels,
            string teamId,
            ITeamChannelDeltaTokenStore store,
            Func<Task> persist)
        {
            var pendingTokens = await crawler.PopulateNewMessagesAndReactions(channels, teamId);
            await persist();
            await TeamChannelDeltaTokenCommitter.CommitPendingTokens(store, teamId, pendingTokens);
        }

        /// <summary>
        /// A channel read that hands back no delta token must leave the stored token alone. Overwriting
        /// it with null would silently turn every later read into a full channel crawl (or, worse,
        /// discard the incremental position), and every channel must still be visited.
        /// </summary>
        [TestMethod]
        public async Task TeamsChannelCrawler_SavesADeltaTokenOnlyWhenTheReadReturnedOne()
        {
            var source = new FakeChannelMessagesSourceLoader()
                .ReturningToken("channel-1", "delta-1")
                .ReturningNoToken("channel-2");

            var store = new InMemoryTeamChannelDeltaTokenStore();
            await store.SetDeltaToken("team-1", "channel-2", new TeamChannelDeltaTokenInfo { Token = "previous2" });

            var crawler = new TeamsChannelCrawler(source);
            await CrawlPersistAndCommitTokens(
                crawler,
                new List<ChannelWithReactions> { Channel("channel-1"), Channel("channel-2") },
                "team-1",
                store,
                () => Task.CompletedTask);

            CollectionAssert.AreEqual(new[] { "channel-1", "channel-2" }, source.ChannelsRead.ToArray(),
                "Every channel in the team must be crawled, in order.");
            Assert.AreEqual("delta-1", (await store.GetDeltaToken("team-1", "channel-1"))?.Token);
            Assert.AreEqual("previous2", (await store.GetDeltaToken("team-1", "channel-2"))?.Token,
                "A read that returned no delta token must not overwrite the token already stored.");
        }

        /// <summary>
        /// Regression for #451: a channel delta token is unsafe when any reply iterator stopped at the
        /// cap. Graph has no reply-level checkpoint, so the channel token must be withheld and the
        /// stored token left alone for a later re-read, even though additive Teams stats may be counted
        /// again.
        /// </summary>
        [TestMethod]
        public async Task ChannelMessagesLoader_WithholdsDeltaTokenWhenReplyPageHitsCap()
        {
            var channel = Channel("channel-1");
            var rootMessage = Msg("root-1", AfterToken);
            var pageReader = new FakeChannelMessagesPageReader()
                .ReturningRootMessages(channel.Id, DeltaLink("delta-after-partial-replies"), completed: true, rootMessage)
                .ReturningReplies(rootMessage.Id, completed: false, Msg("reply-kept", AfterToken));
            var store = new InMemoryTeamChannelDeltaTokenStore();
            await store.SetDeltaToken("team-1", channel.Id, new TeamChannelDeltaTokenInfo
            {
                Token = "previous-delta",
                LastUpdated = DeltaTokenWrittenAt
            });

            var crawler = new TeamsChannelCrawler(new DelegatingChannelMessagesSourceLoader(
                (ch, teamId) => new ChannelMessagesLoader(pageReader, store, AnalyticsLogger.ConsoleOnlyTracer())
                    .LoadTeamMessagesAndReplies(ch, teamId)));

            var pendingTokens = await crawler.PopulateNewMessagesAndReactions(new List<ChannelWithReactions> { channel }, "team-1");
            await TeamChannelDeltaTokenCommitter.CommitPendingTokens(store, "team-1", pendingTokens);

            Assert.AreEqual(0, pendingTokens.Count,
                "A partial reply page must not queue the channel delta token for commit.");
            Assert.AreEqual("previous-delta", (await store.GetDeltaToken("team-1", channel.Id))?.Token,
                "Withholding the token must leave the old checkpoint untouched rather than deleting it.");
            CollectionAssert.AreEqual(new[] { "root-1", "reply-kept" }, channel.Messages.Select(m => m.Id).ToArray(),
                "The partial data is still returned for persistence; only the checkpoint is withheld.");
        }

        /// <summary>
        /// The opposite #451 guard: if the root-message and reply iterators both complete, the channel
        /// delta token is still queued and committed. This prevents an over-fix that would make every
        /// Teams channel re-read forever.
        /// </summary>
        [TestMethod]
        public async Task ChannelMessagesLoader_CommitsDeltaTokenWhenAllMessageAndReplyPagesComplete()
        {
            var channel = Channel("channel-1");
            var rootMessage = Msg("root-1", AfterToken);
            var pageReader = new FakeChannelMessagesPageReader()
                .ReturningRootMessages(channel.Id, DeltaLink("delta-after-complete-read"), completed: true, rootMessage)
                .ReturningReplies(rootMessage.Id, completed: true, Msg("reply-1", AfterToken));
            var store = new InMemoryTeamChannelDeltaTokenStore();

            var crawler = new TeamsChannelCrawler(new DelegatingChannelMessagesSourceLoader(
                (ch, teamId) => new ChannelMessagesLoader(pageReader, store, AnalyticsLogger.ConsoleOnlyTracer())
                    .LoadTeamMessagesAndReplies(ch, teamId)));

            var pendingTokens = await crawler.PopulateNewMessagesAndReactions(new List<ChannelWithReactions> { channel }, "team-1");
            await TeamChannelDeltaTokenCommitter.CommitPendingTokens(store, "team-1", pendingTokens);

            Assert.AreEqual(1, pendingTokens.Count,
                "A complete channel read must still queue its delta token.");
            Assert.AreEqual("delta-after-complete-read", (await store.GetDeltaToken("team-1", channel.Id))?.Token);
            CollectionAssert.AreEqual(new[] { "root-1", "reply-1" }, channel.Messages.Select(m => m.Id).ToArray());
        }

        /// <summary>
        /// The root-message cap has the same checkpoint risk if the page iterator is paused while a
        /// delta link is present: the loader must treat any paused channel-level iterator as incomplete,
        /// regardless of whether Graph supplied a token.
        /// </summary>
        [TestMethod]
        public async Task ChannelMessagesLoader_WithholdsDeltaTokenWhenRootMessagePageHitsCap()
        {
            var channel = Channel("channel-1");
            var pageReader = new FakeChannelMessagesPageReader()
                .ReturningRootMessages(channel.Id, DeltaLink("delta-after-partial-roots"), completed: false, Msg("root-1", AfterToken));
            var store = new InMemoryTeamChannelDeltaTokenStore();
            await store.SetDeltaToken("team-1", channel.Id, new TeamChannelDeltaTokenInfo
            {
                Token = "previous-delta",
                LastUpdated = DeltaTokenWrittenAt
            });

            var crawler = new TeamsChannelCrawler(new DelegatingChannelMessagesSourceLoader(
                (ch, teamId) => new ChannelMessagesLoader(pageReader, store, AnalyticsLogger.ConsoleOnlyTracer())
                    .LoadTeamMessagesAndReplies(ch, teamId)));

            var pendingTokens = await crawler.PopulateNewMessagesAndReactions(new List<ChannelWithReactions> { channel }, "team-1");
            await TeamChannelDeltaTokenCommitter.CommitPendingTokens(store, "team-1", pendingTokens);

            Assert.AreEqual(0, pendingTokens.Count,
                "A paused root-message iterator must not queue a channel delta token even if one is present.");
            Assert.AreEqual("previous-delta", (await store.GetDeltaToken("team-1", channel.Id))?.Token);
        }

        /// <summary>
        /// An expired user-delegated token aborts the whole team's crawl, but tokens returned by
        /// earlier channels stay pending so they can be committed if those earlier channels are still
        /// persisted successfully.
        /// </summary>
        [TestMethod]
        public async Task TeamsChannelCrawler_AbortsTheTeamOnAReadFailureLeavingEarlierTokensPending()
        {
            var source = new FakeChannelMessagesSourceLoader()
                .ReturningToken("channel-1", "delta-1")
                .Failing("channel-2")
                .ReturningToken("channel-3", "delta-3");

            var store = new InMemoryTeamChannelDeltaTokenStore();
            var crawler = new TeamsChannelCrawler(source);
            var pendingTokens = new List<TeamChannelDeltaTokenCommit>();

            await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                crawler.PopulateNewMessagesAndReactions(
                    new List<ChannelWithReactions> { Channel("channel-1"), Channel("channel-2"), Channel("channel-3") },
                    "team-1",
                    pendingTokens));

            CollectionAssert.AreEqual(new[] { "channel-1", "channel-2" }, source.ChannelsRead.ToArray(),
                "The crawl stops at the failing channel rather than carrying on with a token we know is bad.");
            Assert.IsNull(await store.GetDeltaToken("team-1", "channel-1"),
                "The first channel's token must stay pending until the team's SQL changes are durable.");
            Assert.IsNull(await store.GetDeltaToken("team-1", "channel-3"));
            CollectionAssert.AreEqual(new[] { "channel-1" }, pendingTokens.Select(t => t.ChannelId).ToArray(),
                "The caller must be able to commit earlier-channel checkpoints after persisting that partial channel data.");
        }

        /// <summary>
        /// If an earlier channel was loaded and then a later channel fails, the earlier channel's data
        /// is still present on the Team and may be saved. In that partial-success case the checkpoint
        /// is committed at the same per-channel granularity after SQL succeeds.
        /// </summary>
        [TestMethod]
        public async Task TeamsChannelCrawler_CommitsEarlierChannelTokenAfterPartialCrawlPersistenceSucceeds()
        {
            var source = new FakeChannelMessagesSourceLoader()
                .ReturningToken("channel-1", "delta-1")
                .Failing("channel-2");
            var store = new InMemoryTeamChannelDeltaTokenStore();
            var crawler = new TeamsChannelCrawler(source);
            var pendingTokens = new List<TeamChannelDeltaTokenCommit>();

            await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                crawler.PopulateNewMessagesAndReactions(
                    new List<ChannelWithReactions> { Channel("channel-1"), Channel("channel-2") },
                    "team-1",
                    pendingTokens));

            await TeamChannelDeltaTokenCommitter.CommitPendingTokens(store, "team-1", pendingTokens);

            Assert.AreEqual("delta-1", (await store.GetDeltaToken("team-1", "channel-1"))?.Token,
                "The successfully persisted channel can advance without waiting for the failed channel.");
            Assert.IsNull(await store.GetDeltaToken("team-1", "channel-2"));
        }

        /// <summary>
        /// Regression for #432: a channel delta token is only a pending checkpoint until SQL
        /// persistence succeeds. If persistence fails, the token store is left at its previous value
        /// and the next run reads the same channel again instead of resuming past unsaved messages.
        /// </summary>
        [TestMethod]
        public async Task TeamsChannelCrawler_DoesNotAdvanceDeltaTokenWhenPersistenceFails()
        {
            var source = new FakeChannelMessagesSourceLoader()
                .ReturningToken("channel-1", "delta-after-message");
            var store = new InMemoryTeamChannelDeltaTokenStore();
            var crawler = new TeamsChannelCrawler(source);
            var channels = new List<ChannelWithReactions> { Channel("channel-1") };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                CrawlPersistAndCommitTokens(
                    crawler,
                    channels,
                    "team-1",
                    store,
                    () => Task.FromException(new InvalidOperationException("simulated SQL persistence failure"))));

            Assert.IsNull(await store.GetDeltaToken("team-1", "channel-1"),
                "A failed SQL commit must not advance the channel checkpoint.");

            await CrawlPersistAndCommitTokens(
                crawler,
                new List<ChannelWithReactions> { Channel("channel-1") },
                "team-1",
                store,
                () => Task.CompletedTask);

            CollectionAssert.AreEqual(new[] { "channel-1", "channel-1" }, source.ChannelsRead.ToArray(),
                "The retry must re-fetch the same channel because no durable checkpoint was written for the failed run.");
            CollectionAssert.AreEqual(new[] { "channel-1-message-1", "channel-1-message-2" }, source.MessageIdsReturned.ToArray(),
                "Both runs reached message retrieval; the failed run did not let the token skip the retry.");
            Assert.AreEqual("delta-after-message", (await store.GetDeltaToken("team-1", "channel-1"))?.Token);
        }

        /// <summary>
        /// The happy path still writes the returned token once, but only after the persistence delegate
        /// has completed successfully.
        /// </summary>
        [TestMethod]
        public async Task TeamsChannelCrawler_AdvancesDeltaTokenExactlyOnceAfterPersistenceSucceeds()
        {
            var source = new FakeChannelMessagesSourceLoader()
                .ReturningToken("channel-1", "delta-after-success");
            var store = new CountingTeamChannelDeltaTokenStore();
            var crawler = new TeamsChannelCrawler(source);
            var persisted = false;

            await CrawlPersistAndCommitTokens(
                crawler,
                new List<ChannelWithReactions> { Channel("channel-1") },
                "team-1",
                store,
                () =>
                {
                    persisted = true;
                    return Task.CompletedTask;
                });

            Assert.IsTrue(persisted, "The test must execute the persistence boundary before committing the token.");
            Assert.AreEqual(1, store.SetCalls.Count, "A successful crawl should advance each returned channel token exactly once.");
            CollectionAssert.AreEqual(new[] { "team-1/channel-1" }, store.SetCalls.ToArray());
            Assert.AreEqual("delta-after-success", (await store.GetDeltaToken("team-1", "channel-1"))?.Token);
        }

        private class CountingTeamChannelDeltaTokenStore : ITeamChannelDeltaTokenStore
        {
            private readonly InMemoryTeamChannelDeltaTokenStore _inner = new InMemoryTeamChannelDeltaTokenStore();

            public List<string> SetCalls { get; } = new List<string>();

            public Task<TeamChannelDeltaTokenInfo> GetDeltaToken(string teamId, string channelId)
                => _inner.GetDeltaToken(teamId, channelId);

            public Task SetDeltaToken(string teamId, string channelId, TeamChannelDeltaTokenInfo deltaTokenInfo)
            {
                SetCalls.Add($"{teamId}/{channelId}");
                return _inner.SetDeltaToken(teamId, channelId, deltaTokenInfo);
            }

            public Task RemoveDeltaToken(string teamId, string channelId)
                => _inner.RemoveDeltaToken(teamId, channelId);
        }

        private static string DeltaLink(string token)
            => $"https://graph.microsoft.com/v1.0/teams/team-1/channels/channel-1/messages/delta?$deltatoken={token}";

        private class DelegatingChannelMessagesSourceLoader : IChannelMessagesSourceLoader
        {
            private readonly Func<ChannelWithReactions, string, Task<TeamChannelDeltaTokenInfo>> _load;

            public DelegatingChannelMessagesSourceLoader(Func<ChannelWithReactions, string, Task<TeamChannelDeltaTokenInfo>> load)
            {
                _load = load;
            }

            public Task<TeamChannelDeltaTokenInfo> LoadMessagesAndReactions(ChannelWithReactions channel, string teamId)
                => _load(channel, teamId);
        }

        private class FakeChannelMessagesPageReader : IChannelMessagesPageReader
        {
            private readonly Dictionary<string, ChannelRootMessagesPageResult> _rootMessagesByChannelId
                = new Dictionary<string, ChannelRootMessagesPageResult>(StringComparer.Ordinal);

            private readonly Dictionary<string, ChannelRepliesPageResult> _repliesByMessageId
                = new Dictionary<string, ChannelRepliesPageResult>(StringComparer.Ordinal);

            public FakeChannelMessagesPageReader ReturningRootMessages(
                string channelId,
                string deltaLink,
                bool completed,
                params ChatMessage[] messages)
            {
                _rootMessagesByChannelId[channelId] = new ChannelRootMessagesPageResult
                {
                    Messages = messages.ToList(),
                    Deltalink = deltaLink,
                    Completed = completed
                };
                return this;
            }

            public FakeChannelMessagesPageReader ReturningReplies(
                string messageId,
                bool completed,
                params ChatMessage[] replies)
            {
                _repliesByMessageId[messageId] = new ChannelRepliesPageResult
                {
                    Replies = replies.ToList(),
                    Completed = completed
                };
                return this;
            }

            public Task<ChannelRootMessagesPageResult> LoadRootMessages(
                string teamId,
                string channelId,
                TeamChannelDeltaTokenInfo channelDeltaInfo)
            {
                _rootMessagesByChannelId.TryGetValue(channelId, out var result);
                return Task.FromResult(result);
            }

            public Task<ChannelRepliesPageResult> LoadReplies(string teamId, string channelId, string messageId)
            {
                if (_repliesByMessageId.TryGetValue(messageId, out var result))
                {
                    return Task.FromResult(result);
                }

                return Task.FromResult(new ChannelRepliesPageResult { Completed = true });
            }
        }

        #endregion

        #region People Entra ID can't find

        private static string GraphUserJson(string id) => "{\"@odata.type\":\"#microsoft.graph.user\",\"id\":\"" + id + "\"}";

        private static AppConfig NoStateStorage()
        {
            var config = new AppConfig();
            config.ConnectionStrings.StorageConnectionString = null;     // no stored Teams tokens, so no channel-message read
            return config;
        }

        /// <summary>
        /// Regression: an owner Entra ID can't find (a deleted account, or an app that owns the group) used to throw out
        /// of the owner loop, abandoning every remaining Team in the chunk and the Graph sections after it. It must be
        /// skipped instead. Members and channels are empty, so no SQL is touched.
        /// </summary>
        [TestMethod]
        public async Task LoadTeamFull_AnOwnerEntraCannotFind_IsSkipped_AndTheTeamStillLoads()
        {
            const string teamId = "00000000-0000-0000-0000-0000000000a1";
            const string ownerId = "00000000-0000-0000-0000-0000000000b1";
            const string goneOwnerId = "00000000-0000-0000-0000-0000000000b2";
            var handler = new StubGraphHandler()
                .Json("/teams/" + teamId, "{\"id\":\"" + teamId + "\",\"displayName\":\"Contoso Καλημέρα\"}")
                .Json("/groups/" + teamId, "{\"id\":\"" + teamId + "\",\"owners\":[" + GraphUserJson(goneOwnerId) + "," + GraphUserJson(ownerId) + "]}")
                .Json("/groups/" + teamId + "/members", "{\"value\":[]}")
                .Json("/users/" + ownerId, "{\"id\":\"" + ownerId + "\",\"userPrincipalName\":\"owner@contoso.example\"}")
                .Json("/teams/" + teamId + "/channels", "{\"value\":[]}");
            // goneOwnerId has no route, so the stub answers 404 Request_ResourceNotFound, exactly as Graph does.

            var team = await O365Team.LoadTeamFull(new Group { Id = teamId, DisplayName = "Contoso Καλημέρα" },
                new TeamsLoadContext(StubGraphHandler.Client(handler)), NullLogger.Instance, NoStateStorage(), null);

            CollectionAssert.AreEqual(new[] { "owner@contoso.example" }, team.OwnerUserAccounts.Select(o => o.UserPrincipalName).ToArray());
        }

        /// <summary>
        /// Regression: a reaction by someone Entra ID can't find must neither abort the Team nor be dropped - it is kept
        /// with no Graph user, which the SQL store records against "Unknown User" (how Teams itself shows them). And a
        /// deleted person's many reactions cost one Graph lookup per crawl, not one each.
        /// </summary>
        [TestMethod]
        public async Task ProcessAllReactionsFromMessages_AReactorEntraCannotFind_IsKeptAsUnknown_AndLookedUpOnce()
        {
            const string knownId = "00000000-0000-0000-0000-0000000000c1";
            const string goneId = "00000000-0000-0000-0000-0000000000c2";
            var handler = new StubGraphHandler()
                .Json("/users/" + knownId, "{\"id\":\"" + knownId + "\",\"userPrincipalName\":\"reactor@contoso.example\"}");
            var channel = new ChannelWithReactions { Id = "channel-1", DisplayName = "General" };
            foreach (var userId in new[] { goneId, knownId, goneId, goneId })
            {
                channel.Reactions.Add(new ChatMessageReaction
                {
                    ReactionType = "like",
                    CreatedDateTime = new DateTimeOffset(AfterToken),
                    User = new ChatMessageReactionIdentitySet { User = new Identity { Id = userId } }
                });
            }
            var team = new O365Team();

            await team.ProcessAllReactionsFromMessages(new TeamsLoadContext(StubGraphHandler.Client(handler)), channel);

            Assert.AreEqual(4, team.AllReactions.Count, "Every reaction still counts.");
            CollectionAssert.AreEqual(new[] { null, "reactor@contoso.example", null, null },
                team.AllReactions.Select(r => r.GraphUser?.UserPrincipalName).ToArray());
            Assert.AreEqual(1, handler.Requests.Count(u => u.AbsolutePath.EndsWith("/users/" + goneId, StringComparison.OrdinalIgnoreCase)),
                "A person Entra ID can't find is remembered as missing for the rest of the crawl.");
        }

        /// <summary>
        /// D6: members are resolved from one crawl-wide object id to UPN map rather than one SQL query each (on an
        /// unindexable nvarchar(max) column). The context is null here, so a per-member query would throw.
        /// </summary>
        [TestMethod]
        public async Task LoadTeamFull_ResolvesMembersFromTheCrawlWideMap_WithoutSqlOrGraph()
        {
            const string teamId = "00000000-0000-0000-0000-0000000000d1";
            const string knownId = "00000000-0000-0000-0000-0000000000e1";
            const string graphOnlyId = "00000000-0000-0000-0000-0000000000e2";
            const string goneId = "00000000-0000-0000-0000-0000000000e3";
            var handler = new StubGraphHandler()
                .Json("/teams/" + teamId, "{\"id\":\"" + teamId + "\",\"displayName\":\"Contoso\"}")
                .Json("/groups/" + teamId, "{\"id\":\"" + teamId + "\",\"owners\":[]}")
                .Json("/groups/" + teamId + "/members", "{\"value\":[" + GraphUserJson(knownId) + "," + GraphUserJson(graphOnlyId) + "," + GraphUserJson(goneId) + "]}")
                .Json("/users/" + graphOnlyId, "{\"id\":\"" + graphOnlyId + "\",\"userPrincipalName\":\"graph-only@contoso.example\"}")
                .Json("/teams/" + teamId + "/channels", "{\"value\":[]}");
            var context = new TeamsLoadContext(StubGraphHandler.Client(handler))
            {
                UpnByAzureAdId = TeamsCrawlRules.BuildUpnByAzureAdId(new[] { (1, knownId.ToUpperInvariant(), "known@contoso.example") })
            };

            var team = await O365Team.LoadTeamFull(new Group { Id = teamId, DisplayName = "Contoso" }, context, NullLogger.Instance, NoStateStorage(), null);

            CollectionAssert.AreEqual(new[] { "known@contoso.example", "graph-only@contoso.example" }, team.Users.Select(u => u.UserPrincipalName).ToArray(),
                "SQL's answer first, then Graph; someone Entra ID can't find isn't recorded.");
            Assert.IsFalse(handler.Requests.Any(u => u.AbsolutePath.EndsWith("/users/" + knownId, StringComparison.OrdinalIgnoreCase)),
                "A member SQL already knows costs no Graph call.");
        }

        /// <summary>
        /// F-4: several users rows can share an Entra object id - a renamed user's rows, from before renames were done in
        /// place. A member resolves to the same row manager links use (<see cref="UserIdentityRules.IsPreferredRow(int, string, int, string, string)"/>):
        /// the newest, which carries the latest sign-in name - not the lowest, the row the rename left behind - whatever
        /// order SQL returns them in.
        /// </summary>
        [TestMethod]
        public void BuildUpnByAzureAdId_NewestUserRowWins_WhateverTheOrder()
        {
            var map = TeamsCrawlRules.BuildUpnByAzureAdId(new (int, string, string)[]
            {
                (7, "00000000-0000-0000-0000-00000000000a", "later@contoso.example"),
                (9, "00000000-0000-0000-0000-00000000000a", "latest@contoso.example"),
                (3, "00000000-0000-0000-0000-00000000000A", "first@contoso.example"),
                (5, "00000000-0000-0000-0000-00000000000b", ""),
                (6, null, "no-object-id@contoso.example"),
            });

            Assert.AreEqual("latest@contoso.example", map["00000000-0000-0000-0000-00000000000a"]);
            Assert.IsFalse(map.ContainsKey("00000000-0000-0000-0000-00000000000b"), "A row with no UPN is left to Graph.");
            Assert.AreEqual(1, map.Count);
        }

        #endregion

        #region A failing Team

        /// <summary>
        /// D4: one Team that fails - an exception of any kind, or a save that gave up - is counted and skipped, and the
        /// rest of its chunk still runs. The next Team gets a new EF context, so it doesn't inherit whatever the failed
        /// Team left tracked.
        /// </summary>
        [TestMethod]
        public async Task CrawlChunk_AFailedTeamIsCountedAndSkipped_AndTheNextTeamGetsAFreshUnitOfWork()
        {
            var opened = new List<FakeUnitOfWork>();
            var crawled = new List<Tuple<string, int>>();
            var summary = new TeamsCrawlSummary();
            var groups = new[] { "ok-1", "throws", "save-failed", "ok-2" }.Select(id => new Group { Id = id, DisplayName = "Contoso " + id }).ToList();

            await TeamsChunkCrawler.CrawlChunk(groups,
                () =>
                {
                    var unit = new FakeUnitOfWork(opened.Count);
                    opened.Add(unit);
                    return unit;
                },
                (unit, group) =>
                {
                    crawled.Add(Tuple.Create(group.Id, unit.Number));
                    if (group.Id == "throws")
                    {
                        throw new ArgumentException("synthetic: no such user");
                    }
                    return Task.FromResult(group.Id != "save-failed");
                },
                summary, NullLogger.Instance);

            CollectionAssert.AreEqual(new[] { "ok-1", "throws", "save-failed", "ok-2" }, crawled.Select(c => c.Item1).ToArray(), "Every Team in the chunk is attempted.");
            CollectionAssert.AreEqual(new[] { 0, 0, 1, 2 }, crawled.Select(c => c.Item2).ToArray(), "After a failure the next Team gets a new unit of work.");
            Assert.IsTrue(opened.All(u => u.Disposed));
            Assert.AreEqual(2, summary.TeamsSaved);
            Assert.AreEqual(2, summary.TeamsFailed);
        }

        [TestMethod]
        public async Task CrawlChunk_CancellationIsNotSwallowed()
        {
            var unit = new FakeUnitOfWork(0);
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => TeamsChunkCrawler.CrawlChunk(
                new[] { new Group { Id = "g", DisplayName = "Contoso" } }, () => unit,
                (u, g) => { throw new OperationCanceledException(); }, new TeamsCrawlSummary(), NullLogger.Instance));
            Assert.IsTrue(unit.Disposed);
        }

        private sealed class FakeUnitOfWork : IDisposable
        {
            public FakeUnitOfWork(int number) { Number = number; }
            public int Number { get; }
            public bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;
        }

        /// <summary>
        /// When the Teams section counts as having run - which stamps its cadence gate until the next due run. Every Team
        /// failing is an outage, and no group reporting its workloads is an importer fault: stamping either would hide it.
        /// A partial failure still counts, or one broken Team would make every cycle re-crawl every Team.
        /// </summary>
        [DataTestMethod]
        [DataRow(3, 0, false, true)]
        [DataRow(2, 1, false, true)]
        [DataRow(0, 0, false, true)]
        [DataRow(0, 2, false, false)]
        [DataRow(0, 0, true, false)]
        public void TeamsCrawlSummary_OnlyAnOutageOrUnidentifiableTeamsFailTheSection(int saved, int failed, bool noWorkloadsReported, bool succeeded)
        {
            var summary = new TeamsCrawlSummary { NoGroupReportedWorkloads = noWorkloadsReported };
            for (var i = 0; i < saved; i++) summary.RecordSaved();
            for (var i = 0; i < failed; i++) summary.RecordFailed();

            Assert.AreEqual(succeeded, summary.Succeeded);
        }

        /// <summary>
        /// Through the production Teams section: a crawl in which every Team failed - Graph answering 503 for each - is not
        /// a completed run, so the cadence gate isn't stamped and the crawl is retried next cycle instead of an outage being
        /// hidden until the next due run. Before per-Team isolation the outage aborted the section; with it, and without this
        /// check, the section reported success. Reads the crawl configuration and the user map from SQL.
        /// </summary>
        [TestMethod]
        [TestCategory("SqlIntegration")]
        public async Task TeamsSection_WhenEveryTeamFails_IsNotACompletedRun()
        {
            const string teamA = "00000000-0000-0000-0000-0000000000a7";
            const string teamB = "00000000-0000-0000-0000-0000000000a8";
            var graph = new StubGraphHandler()
                .Json("/groups", "{\"value\":[" +
                    "{\"id\":\"" + teamA + "\",\"displayName\":\"Contoso A\",\"resourceProvisioningOptions\":[\"Team\"]}," +
                    "{\"id\":\"" + teamB + "\",\"displayName\":\"Contoso Καλημέρα\",\"resourceProvisioningOptions\":[\"Team\"]}]}")
                .Json("/teams/" + teamA, StubGraphHandler.Error("ServiceUnavailable"), HttpStatusCode.ServiceUnavailable)
                .Json("/teams/" + teamB, StubGraphHandler.Error("ServiceUnavailable"), HttpStatusCode.ServiceUnavailable);
            var settings = new AppConfig { ImportJobSettings = new Common.Entities.ImportTaskSettings { GraphTeams = true } };
            var sections = new WebJob.Office365ActivityImporter.Engine.Graph.Sections.ProductionGraphImportSectionFactory(
                    AnalyticsLogger.ConsoleOnlyTracer(), settings,
                    graphAppIndentityOAuthContext: null,
                    graphClient: StubGraphHandler.Client(graph),
                    sentEmailMailboxSkipList: null,
                    activityReportsImport: (days, client, scope) => Task.FromResult(true),
                    dbContextFactory: null,
                    clock: null)
                .CreateSections(settings);

            var completed = await sections.Single(s => s.Name == "Teams import").RunAsync();

            Assert.IsTrue(graph.Requests.Any(u => u.AbsolutePath.EndsWith("/teams/" + teamA)) && graph.Requests.Any(u => u.AbsolutePath.EndsWith("/teams/" + teamB)),
                "Both Teams were attempted - isolation means the first failure no longer aborts the rest.");
            Assert.IsFalse(completed, "A crawl in which every Team failed must not be stamped as a completed run.");
        }

        #endregion

        #region Team authorisation: which failures delete the stored token

        private const string TeamId = "team-1";
        private const string StoredRefreshToken = "synthetic-refresh";

        private static async Task<TeamsTokenStore> TokenStoreWithAuthorisedTeam()
        {
            var store = new TeamsTokenStore(new InMemoryKeyValueStore());
            await store.SetRefreshTokenAsync(TeamId, StoredRefreshToken);
            return store;
        }

        private static TeamTokenManager TokenManager(TeamsTokenStore store, Func<string, Task<RefreshOAuthToken>> redeem)
            => new TeamTokenManager(new O365Team { Id = TeamId, DisplayName = "Contoso Καλημέρα" }, store, new InMemoryTeamChannelDeltaTokenStore(), redeem);

        private static RefreshTokenRequestException Refusal(int status, string oauthError)
        {
            var body = oauthError == null ? "<html><body>Synthetic gateway page</body></html>" : "{\"error\":\"" + oauthError + "\",\"error_codes\":[700082]}";
            return new RefreshTokenRequestException("Response status code does not indicate success.", (HttpStatusCode)status, body, null);
        }

        /// <summary>
        /// D5: only Entra ID saying the refresh token is dead (400 invalid_grant) deletes it. The old rule was inverted:
        /// a 400 kept it, and anything else - including the importer's own expired client secret (401 invalid_client),
        /// which would wipe every Team at once - deleted it.
        /// </summary>
        [DataTestMethod]
        [DataRow(400, "invalid_grant", true)]
        [DataRow(401, "invalid_client", false)]
        [DataRow(400, "invalid_request", false)]
        [DataRow(429, null, false)]
        [DataRow(500, null, false)]
        [DataRow(503, "temporarily_unavailable", false)]
        public async Task TeamToken_OnlyAnInvalidGrantDeletesTheStoredToken(int status, string oauthError, bool deleted)
        {
            var store = await TokenStoreWithAuthorisedTeam();
            var manager = TokenManager(store, _ => Task.FromException<RefreshOAuthToken>(Refusal(status, oauthError)));

            Assert.IsNull(await manager.GetRefreshToken(NullLogger.Instance));

            Assert.AreEqual(deleted ? null : StoredRefreshToken, await store.GetRefreshTokenAsync(TeamId));
            Assert.AreEqual(deleted ? TeamTokenStatus.Revoked : TeamTokenStatus.RefreshFailed, manager.Status);
        }

        [TestMethod]
        public async Task TeamToken_EntraUnreachableOrSlow_KeepsTheStoredToken()
        {
            foreach (var failure in new Exception[] { new HttpRequestException("synthetic DNS failure"), new TaskCanceledException("synthetic timeout") })
            {
                var store = await TokenStoreWithAuthorisedTeam();
                var manager = TokenManager(store, _ => Task.FromException<RefreshOAuthToken>(failure));

                Assert.IsNull(await manager.GetRefreshToken(NullLogger.Instance));

                Assert.AreEqual(StoredRefreshToken, await store.GetRefreshTokenAsync(TeamId), failure.GetType().Name);
                Assert.AreEqual(TeamTokenStatus.RefreshFailed, manager.Status);
            }
        }

        [TestMethod]
        public async Task TeamToken_Redeemed_ReturnsTheAccessToken()
        {
            var store = await TokenStoreWithAuthorisedTeam();
            string redeemedWith = null;
            var manager = TokenManager(store, refreshToken =>
            {
                redeemedWith = refreshToken;
                return Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-access" });
            });

            Assert.AreEqual("synthetic-access", (await manager.GetRefreshToken(NullLogger.Instance))?.AccessToken);
            Assert.AreEqual(StoredRefreshToken, redeemedWith);
            Assert.AreEqual(TeamTokenStatus.Refreshed, manager.Status);
            Assert.AreEqual(StoredRefreshToken, await store.GetRefreshTokenAsync(TeamId), "No renewed token was issued, so nothing changes.");
        }

        /// <summary>
        /// Entra ID issues a new refresh token every time one is redeemed, each lasting about 90 days from issue. Keeping only
        /// the token captured at authorisation meant every Team's authorisation lapsed about 90 days later. The renewed token
        /// is stored, and is what any further access token this crawl needs is minted from. No token ever reaches a log.
        /// </summary>
        [TestMethod]
        public async Task TeamToken_TheRenewedRefreshTokenIsStored_AndUsedForTheNextAccessToken()
        {
            var store = await TokenStoreWithAuthorisedTeam();
            var logger = new FakeLoaderClasses.RecordingLogger();
            var redeemedWith = new List<string>();
            var manager = new TeamTokenManager(new O365Team { Id = TeamId, DisplayName = "Contoso Καλημέρα" }, store,
                new InMemoryTeamChannelDeltaTokenStore(), refreshToken =>
                {
                    redeemedWith.Add(refreshToken);
                    return Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-access-" + redeemedWith.Count, RefreshToken = "synthetic-renewed-" + redeemedWith.Count });
                }, logger);

            await manager.GetRefreshToken(logger);
            // Held in a variable: "Token, <literal>" on one line reads to gitleaks' generic-api-key rule as an assignment.
            var firstRenewal = "synthetic-renewed-1";
            Assert.AreEqual(firstRenewal, await store.GetRefreshTokenAsync(TeamId));

            await manager.RedeemRefreshTokenAsync();
            CollectionAssert.AreEqual(new[] { StoredRefreshToken, firstRenewal }, redeemedWith);
            Assert.AreEqual("synthetic-renewed-2", await store.GetRefreshTokenAsync(TeamId));

            Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("synthetic-renewed") || e.Message.Contains(StoredRefreshToken) || e.Message.Contains("synthetic-access")),
                string.Join(" | ", logger.Entries.Select(e => e.Message)));
        }

        /// <summary>
        /// An administrator who removes a Team's authorisation, or authorises it again, while its token is being redeemed
        /// must win: the renewed token from the older grant is not stored over it.
        /// </summary>
        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task TeamToken_AnAuthorisationRemovedOrRenewedWhileRedeeming_IsNotUndone(bool removed)
        {
            var store = await TokenStoreWithAuthorisedTeam();
            var manager = TokenManager(store, async refreshToken =>
            {
                if (removed) await store.RemoveRefreshTokenAsync(TeamId);
                else await store.SetRefreshTokenAsync(TeamId, "synthetic-admin-reauthorised");
                return new RefreshOAuthToken { AccessToken = "synthetic-access", RefreshToken = "synthetic-renewed" };
            });

            Assert.IsNotNull(await manager.GetRefreshToken(NullLogger.Instance), "This cycle still reads with the token it was given.");

            Assert.AreEqual(removed ? null : "synthetic-admin-reauthorised", await store.GetRefreshTokenAsync(TeamId));
        }

        [TestMethod]
        public async Task TeamToken_FailingToStoreTheRenewedToken_StillReturnsTheAccessToken()
        {
            var state = new FailingWritesStore();
            await state.Inner.SetStringAsync(TeamId, StoredRefreshToken);
            var store = new TeamsTokenStore(state);
            var manager = TokenManager(store, _ => Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-access", RefreshToken = "synthetic-renewed" }));

            Assert.AreEqual("synthetic-access", (await manager.GetRefreshToken(NullLogger.Instance))?.AccessToken);
            Assert.AreEqual(TeamTokenStatus.Refreshed, manager.Status);
            Assert.AreEqual(StoredRefreshToken, await store.GetRefreshTokenAsync(TeamId));
        }

        /// <summary>A versioned store whose conditional writes fail, as when storage is unreachable.</summary>
        private sealed class FailingWritesStore : IConditionalKeyValueStore
        {
            public InMemoryKeyValueStore Inner { get; } = new InMemoryKeyValueStore();
            public string Description => Inner.Description;
            public Task<VersionedValue> GetVersionedAsync(string key, System.Threading.CancellationToken cancellationToken = default) => Inner.GetVersionedAsync(key, cancellationToken);
            public Task<bool> TrySetStringAsync(string key, string value, string expectedVersionToken, System.Threading.CancellationToken cancellationToken = default)
                => Task.FromException<bool>(new InvalidOperationException("synthetic storage outage"));
            public Task<bool> TryDeleteAsync(string key, string expectedVersionToken, System.Threading.CancellationToken cancellationToken = default)
                => Task.FromException<bool>(new InvalidOperationException("synthetic storage outage"));
            public Task<string> GetStringAsync(string key, System.Threading.CancellationToken cancellationToken = default) => Inner.GetStringAsync(key, cancellationToken);
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, System.Threading.CancellationToken cancellationToken = default)
                => Task.FromException(new InvalidOperationException("synthetic storage outage"));
            public Task<bool> DeleteAsync(string key, System.Threading.CancellationToken cancellationToken = default) => Inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, System.Threading.CancellationToken cancellationToken = default) => Inner.ExistsAsync(key, cancellationToken);
        }

        /// <summary>
        /// The token endpoint's refusal now carries Entra ID's OAuth error, but is still the same
        /// <see cref="HttpRequestException"/>, with the same message, that the portal's site-token API catches.
        /// </summary>
        [TestMethod]
        public async Task GetNewRefreshToken_RefusalCarriesTheOAuthError_AndIsStillAnHttpRequestException()
        {
            var handler = new StubGraphHandler().Json("/oauth2/v2.0/token",
                "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS700082: Synthetic.\",\"error_codes\":[700082],\"suberror\":\"bad_token\"}",
                HttpStatusCode.BadRequest);

            var ex = await Assert.ThrowsExceptionAsync<RefreshTokenRequestException>(() =>
                RefreshOAuthToken.GetNewRefreshToken(StoredRefreshToken, new AppConfig(), new HttpClient(handler)));

            Assert.IsInstanceOfType(ex, typeof(HttpRequestException));
            Assert.AreEqual("Response status code does not indicate success: 400 (Bad Request).", ex.Message);
            Assert.AreEqual(HttpStatusCode.BadRequest, ex.StatusCode);
            Assert.AreEqual("invalid_grant", ex.Error);
            Assert.AreEqual("bad_token", ex.SubError);
            CollectionAssert.AreEqual(new[] { 700082 }, ex.ErrorCodes.ToArray());
            Assert.IsTrue(TeamTokenRefreshRules.IsRefreshTokenRejected(ex));
            Assert.IsFalse(TeamTokenRefreshRules.Describe(ex).Contains(StoredRefreshToken), "A log line never carries the token.");
        }

        private static string DeltaPage(string deltaToken)
            => "{\"value\":[],\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/teams/team-1/channels/c/messages/delta?$deltatoken=" + deltaToken + "\"}";

        private static ChannelWithReactions TypedChannel(string id, ChannelMembershipType? membershipType)
            => new ChannelWithReactions { Id = id, DisplayName = "Contoso Καλημέρα", MembershipType = membershipType };

        private static GraphChannelMessagesSourceLoader DelegatedReader(StubGraphHandler handler, Func<Task<RefreshOAuthToken>> remint = null,
            Func<DateTime> utcNow = null)
            => new GraphChannelMessagesSourceLoader(new RefreshOAuthToken { AccessToken = "synthetic-access", ExpiresIn = 3600 }, remint,
                new InMemoryTeamChannelDeltaTokenStore(), NullLogger.Instance, new HttpClient(handler), utcNow);

        /// <summary>
        /// D5: what Graph's answer to a delegated channel read means, by status and channel type, through the real SDK.
        /// It used to be "any Graph error: delete the Team's token".
        /// </summary>
        [DataTestMethod]
        [DataRow(403, "Private", ChannelReadFailureKind.SkipChannel)]
        [DataRow(403, "Shared", ChannelReadFailureKind.SkipChannel)]
        [DataRow(404, "Private", ChannelReadFailureKind.SkipChannel)]
        [DataRow(404, "Standard", ChannelReadFailureKind.SkipChannel)]
        [DataRow(403, "Standard", ChannelReadFailureKind.TokenRejected)]
        [DataRow(403, null, ChannelReadFailureKind.Transient)]
        [DataRow(401, "Standard", ChannelReadFailureKind.Transient)]
        [DataRow(429, "Standard", ChannelReadFailureKind.Transient)]
        [DataRow(500, "Standard", ChannelReadFailureKind.ChannelFailed)]
        [DataRow(503, "Private", ChannelReadFailureKind.ChannelFailed)]
        [DataRow(400, "Standard", ChannelReadFailureKind.ChannelFailed)]
        public async Task GraphChannelRead_FailureIsClassifiedByStatusAndChannelType(int status, string membershipType, ChannelReadFailureKind expected)
        {
            var handler = new StubGraphHandler().Json("/channels/channel-1/messages/delta", StubGraphHandler.Error("Synthetic" + status), (HttpStatusCode)status);
            var type = membershipType == null ? (ChannelMembershipType?)null : (ChannelMembershipType)Enum.Parse(typeof(ChannelMembershipType), membershipType);

            var ex = await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                DelegatedReader(handler).LoadMessagesAndReactions(TypedChannel("channel-1", type), TeamId));

            Assert.AreEqual(expected, ex.Kind);
        }

        [TestMethod]
        public async Task GraphChannelRead_NoAnswerFromGraph_IsAChannelFailure()
        {
            foreach (var failure in new Exception[] { new HttpRequestException("synthetic network failure"), new TaskCanceledException("synthetic timeout") })
            {
                var handler = new StubGraphHandler().Route("/channels/channel-1/messages/delta", _ => throw failure);

                var ex = await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                    DelegatedReader(handler).LoadMessagesAndReactions(TypedChannel("channel-1", ChannelMembershipType.Standard), TeamId));

                Assert.AreEqual(ChannelReadFailureKind.ChannelFailed, ex.Kind, failure.GetType().Name);
            }
        }

        /// <summary>
        /// A private channel the authorising person isn't in is skipped, and the Team's other channels are still read;
        /// a throttled read after that stops the Team's crawl but keeps the completed channel's checkpoint pending.
        /// </summary>
        [TestMethod]
        public async Task GraphChannelRead_SkipsAnUnreadableChannel_AndStopsOnlyOnARealFailure()
        {
            var handler = new StubGraphHandler()
                .Json("/channels/private-1/messages/delta", StubGraphHandler.Error("Forbidden"), HttpStatusCode.Forbidden)
                .Json("/channels/general/messages/delta", DeltaPage("delta-general"))
                .Json("/channels/busy/messages/delta", StubGraphHandler.Error("TooManyRequests"), (HttpStatusCode)429)
                .Json("/channels/never-reached/messages/delta", DeltaPage("delta-never"));
            var pending = new List<TeamChannelDeltaTokenCommit>();
            var channels = new List<ChannelWithReactions>
            {
                TypedChannel("private-1", ChannelMembershipType.Private),
                TypedChannel("general", ChannelMembershipType.Standard),
                TypedChannel("busy", ChannelMembershipType.Standard),
                TypedChannel("never-reached", ChannelMembershipType.Standard),
            };

            var ex = await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                new TeamsChannelCrawler(DelegatedReader(handler)).PopulateNewMessagesAndReactions(channels, TeamId, pending));

            Assert.AreEqual(ChannelReadFailureKind.Transient, ex.Kind);
            CollectionAssert.AreEqual(new[] { "general" }, pending.Select(p => p.ChannelId).ToArray(),
                "The skipped channel has no checkpoint to commit; the completed one is still committed after SQL.");
            Assert.AreEqual("delta-general", pending.Single().DeltaTokenInfo.Token);
            Assert.IsFalse(handler.Requests.Any(u => u.AbsolutePath.Contains("/never-reached/")));
        }

        /// <summary>
        /// An access token lasts about an hour, and a big Team can take longer to read: a refused token is replaced with
        /// a freshly minted one and the channel retried once - and if Graph refuses that too, the authorisation itself is
        /// what's bad.
        /// </summary>
        [TestMethod]
        public async Task GraphChannelRead_ARefusedTokenIsReplacedOnce_AndAFreshTokenRefusedMeansTheTokenIsRejected()
        {
            var handler = new StubGraphHandler().Route("/channels/channel-1/messages/delta", request =>
                request.Headers.Authorization?.Parameter == "synthetic-fresh"
                    ? StubGraphHandler.Response(HttpStatusCode.OK, DeltaPage("delta-1"))
                    : StubGraphHandler.Response(HttpStatusCode.Unauthorized, StubGraphHandler.Error("InvalidAuthenticationToken")));
            var remints = 0;
            Func<Task<RefreshOAuthToken>> remint = () =>
            {
                remints++;
                return Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-fresh" });
            };

            var delta = await DelegatedReader(handler, remint).LoadMessagesAndReactions(TypedChannel("channel-1", ChannelMembershipType.Standard), TeamId);

            Assert.AreEqual("delta-1", delta?.Token);
            Assert.AreEqual(1, remints);

            var alwaysRefused = new StubGraphHandler().Json("/channels/channel-1/messages/delta",
                StubGraphHandler.Error("InvalidAuthenticationToken"), HttpStatusCode.Unauthorized);
            var ex = await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                DelegatedReader(alwaysRefused, remint).LoadMessagesAndReactions(TypedChannel("channel-1", ChannelMembershipType.Standard), TeamId));
            Assert.AreEqual(ChannelReadFailureKind.TokenRejected, ex.Kind);
            Assert.AreEqual(2, remints, "One new token per refusal, not a loop.");
        }

        [DataTestMethod]
        [DataRow(400, "invalid_grant", ChannelReadFailureKind.TokenRejected)]
        [DataRow(401, "invalid_client", ChannelReadFailureKind.Transient)]
        [DataRow(503, null, ChannelReadFailureKind.Transient)]
        public async Task GraphChannelRead_ATokenThatCantBeReminted_IsRejectedOnlyOnInvalidGrant(int status, string oauthError, ChannelReadFailureKind expected)
        {
            var handler = new StubGraphHandler().Json("/channels/channel-1/messages/delta",
                StubGraphHandler.Error("InvalidAuthenticationToken"), HttpStatusCode.Unauthorized);

            var ex = await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                DelegatedReader(handler, () => Task.FromException<RefreshOAuthToken>(Refusal(status, oauthError)))
                    .LoadMessagesAndReactions(TypedChannel("channel-1", ChannelMembershipType.Standard), TeamId));

            Assert.AreEqual(expected, ex.Kind);
        }

        private static readonly DateTime CrawlStart = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

        private static HttpResponseMessage Refused() => StubGraphHandler.Response(HttpStatusCode.Unauthorized, StubGraphHandler.Error("InvalidAuthenticationToken"));

        /// <summary>
        /// Regression: a replacement token refused because the channel read that followed outlived it - a big bot or alert
        /// channel can take longer to read than one token lasts - used to count as "the authorisation is no good", and the
        /// Team's working refresh token was deleted. Only a replacement refused within minutes of being minted counts.
        /// </summary>
        [DataTestMethod]
        [DataRow(4, true)]
        [DataRow(70, false)]
        public async Task GraphChannelRead_AReplacementTokenRefused_IsRejectedOnlyWhenItWasFresh(int minutesAfterMinting, bool rejected)
        {
            var now = CrawlStart;
            var handler = new StubGraphHandler().Route("/channels/channel-1/messages/delta", request =>
            {
                switch (request.Headers.Authorization?.Parameter)
                {
                    case "synthetic-remint-1":
                        // The read with the first replacement runs this long before Graph refuses it.
                        now = now.AddMinutes(minutesAfterMinting);
                        return Refused();
                    case "synthetic-remint-2":
                        return StubGraphHandler.Response(HttpStatusCode.OK, DeltaPage("delta-1"));
                    default:
                        return Refused();
                }
            });
            var remints = 0;
            Func<Task<RefreshOAuthToken>> remint = () =>
            {
                remints++;
                return Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-remint-" + remints, ExpiresIn = 3600 });
            };
            var reader = DelegatedReader(handler, remint, () => now);

            if (rejected)
            {
                var ex = await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                    reader.LoadMessagesAndReactions(TypedChannel("channel-1", ChannelMembershipType.Standard), TeamId));
                Assert.AreEqual(ChannelReadFailureKind.TokenRejected, ex.Kind, "Two tokens refused, the second within minutes of minting.");
                Assert.AreEqual(1, remints);
            }
            else
            {
                var delta = await reader.LoadMessagesAndReactions(TypedChannel("channel-1", ChannelMembershipType.Standard), TeamId);
                Assert.AreEqual("delta-1", delta?.Token, "A token refused 70 minutes after it was minted had expired; the authorisation is fine.");
                Assert.AreEqual(2, remints);
            }
        }

        /// <summary>
        /// The access token is renewed shortly before it expires, so a channel read longer than one token's life carries
        /// on - the next page is read with the renewed token - instead of being refused part-way and starting again.
        /// </summary>
        [TestMethod]
        public async Task GraphChannelRead_TheTokenIsRenewedBeforeItExpires_SoALongReadCarriesOn()
        {
            var now = CrawlStart;
            var firstPageReads = 0;
            string nextPageToken = null;
            var handler = new StubGraphHandler().Route("/channels/channel-1/messages/delta", request =>
            {
                var token = request.Headers.Authorization?.Parameter;
                if (token == "synthetic-access" && now > CrawlStart.AddMinutes(60))
                {
                    return Refused();
                }
                if (!request.RequestUri.Query.Contains("skiptoken"))
                {
                    firstPageReads++;
                    now = now.AddMinutes(61);
                    return StubGraphHandler.Response(HttpStatusCode.OK, "{\"value\":[],\"@odata.nextLink\":" +
                        "\"https://graph.microsoft.com/v1.0/teams/team-1/channels/channel-1/messages/delta?$skiptoken=page-2\"}");
                }
                nextPageToken = token;
                return StubGraphHandler.Response(HttpStatusCode.OK, DeltaPage("delta-1"));
            });
            var remints = 0;
            Func<Task<RefreshOAuthToken>> remint = () =>
            {
                remints++;
                return Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-renewed", ExpiresIn = 3600 });
            };

            var delta = await DelegatedReader(handler, remint, () => now)
                .LoadMessagesAndReactions(TypedChannel("channel-1", ChannelMembershipType.Standard), TeamId);

            Assert.AreEqual("delta-1", delta?.Token);
            Assert.AreEqual("synthetic-renewed", nextPageToken, "The next page is read with the renewed token.");
            Assert.AreEqual(1, firstPageReads, "The read carried on rather than starting the channel again.");
            Assert.AreEqual(1, remints);
        }

        /// <summary>
        /// D5 follow-up: Entra ID answers <c>invalid_grant</c> both for a dead refresh token and for a sign-in policy that
        /// blocks a silent refresh. A policy applies to every Team the same person authorised - often all of them - and the
        /// tokens work again once it allows them, so only a dead-token reason deletes. An unrecognised reason keeps the
        /// token: wrongly keeping costs a failed refresh per cycle; wrongly deleting costs re-authorising every Team.
        /// </summary>
        [DataTestMethod]
        [DataRow(null, "70008", true)]
        [DataRow(null, "700082", true)]
        [DataRow("bad_token", "50173", true)]
        [DataRow(null, "50132", true)]
        [DataRow("consent_required", "65001", true)]
        [DataRow("token_expired", "99999", true)]
        [DataRow(null, "", true)]
        [DataRow("basic_action", "50076", false)]
        [DataRow(null, "50076", false)]
        [DataRow(null, "50079", false)]
        [DataRow("message_only", "53003", false)]
        [DataRow(null, "53000", false)]
        [DataRow("additional_action", "50158", false)]
        [DataRow("user_password_expired", "50055", false)]
        [DataRow(null, "70043", false)]
        [DataRow("basic_action", "70008", false)]
        [DataRow(null, "50057", false)]
        public void IsRefreshTokenRejected_ADeadTokenYes_ASignInPolicyOrAnUnrecognisedReasonNo(string subError, string errorCodes, bool dead)
        {
            var body = "{\"error\":\"invalid_grant\"" + (subError == null ? "" : ",\"suberror\":\"" + subError + "\"") +
                       ",\"error_codes\":[" + errorCodes + "]}";
            var refusal = new RefreshTokenRequestException("Response status code does not indicate success: 400 (Bad Request).",
                HttpStatusCode.BadRequest, body, null);

            Assert.AreEqual(dead, TeamTokenRefreshRules.IsRefreshTokenRejected(refusal));
        }

        [TestMethod]
        public async Task TeamToken_AConditionalAccessBlock_KeepsTheToken_AndSaysWhatToDo()
        {
            var store = await TokenStoreWithAuthorisedTeam();
            var logger = new FakeLoaderClasses.RecordingLogger();
            var blocked = new RefreshTokenRequestException("Response status code does not indicate success: 400 (Bad Request).", HttpStatusCode.BadRequest,
                "{\"error\":\"invalid_grant\",\"suberror\":\"message_only\",\"error_codes\":[53003]}", null);
            var manager = new TeamTokenManager(new O365Team { Id = TeamId, DisplayName = "Contoso" }, store, new InMemoryTeamChannelDeltaTokenStore(),
                _ => Task.FromException<RefreshOAuthToken>(blocked), logger);

            Assert.IsNull(await manager.GetRefreshToken(logger));

            Assert.AreEqual(StoredRefreshToken, await store.GetRefreshTokenAsync(TeamId), "The token works again once the policy allows it.");
            Assert.AreEqual(TeamTokenStatus.RefreshFailed, manager.Status);
            Assert.IsTrue(logger.Entries.Any(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error
                && e.Message.Contains("AADSTS53003") && e.Message.Contains("Conditional Access")),
                string.Join(" | ", logger.Entries.Select(e => e.Message)));

            var reread = new StubGraphHandler().Json("/channels/channel-1/messages/delta", StubGraphHandler.Error("InvalidAuthenticationToken"), HttpStatusCode.Unauthorized);
            var ex = await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                DelegatedReader(reread, () => Task.FromException<RefreshOAuthToken>(blocked))
                    .LoadMessagesAndReactions(TypedChannel("channel-1", ChannelMembershipType.Standard), TeamId));
            Assert.AreEqual(ChannelReadFailureKind.Transient, ex.Kind, "Nor does a policy block met part-way through a crawl delete it.");
        }

        /// <summary>
        /// Only a failure that proves the token can't read the Team deletes it, and the Team's stored status follows the
        /// token store. It used to delete the token on any channel failure and then record the Team as still authorised.
        /// </summary>
        [DataTestMethod]
        [DataRow(ChannelReadFailureKind.TokenRejected, false)]
        [DataRow(ChannelReadFailureKind.Transient, true)]
        public async Task RecordChannelReadFailure_DeletesOnlyARejectedToken_AndTheTeamStatusMatches(ChannelReadFailureKind kind, bool stillAuthorised)
        {
            var store = await TokenStoreWithAuthorisedTeam();
            var manager = TokenManager(store, _ => Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-access" }));
            await manager.GetRefreshToken(NullLogger.Instance);
            var team = new O365Team { Id = TeamId, DisplayName = "Contoso" };

            await RecordChannelReadFailure(team, new ChannelMessagesReadException(kind, "synthetic", null), manager);

            Assert.AreEqual(stillAuthorised ? StoredRefreshToken : null, await store.GetRefreshTokenAsync(TeamId),
                "A rejected token that is still the one this crawl read is deleted.");
            Assert.AreEqual(stillAuthorised, team.HasRefreshToken);
        }

        private static RefreshTokenRequestException DeadTokenRefusal() => new RefreshTokenRequestException(
            "Response status code does not indicate success: 400 (Bad Request).", HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\",\"suberror\":\"bad_token\",\"error_codes\":[50173]}", null);

        /// <summary>Records a failed channel read the way the crawl does, deleting through the Team's token manager.</summary>
        private static Task RecordChannelReadFailure(O365Team team, ChannelMessagesReadException failure, TeamTokenManager tokens,
            Microsoft.Extensions.Logging.ILogger logger = null)
            => O365Team.RecordChannelReadFailure(team, failure, tokens, logger ?? NullLogger.Instance);

        /// <summary>
        /// Regression: a dead-token verdict is evidence about the token THIS crawl read. If an administrator authorised the
        /// Team again while the crawl ran - say the person whose token it held had their password reset - deleting whatever
        /// is stored now deleted the new authorisation. Here the re-authorisation lands while the crawl redeems its token.
        /// </summary>
        [TestMethod]
        public async Task TeamToken_ReauthorisedWhileTheOldTokenIsRefused_KeepsTheNewAuthorisation()
        {
            var store = await TokenStoreWithAuthorisedTeam();
            var manager = TokenManager(store, async refreshToken =>
            {
                await store.SetRefreshTokenAsync(TeamId, "synthetic-admin-reauthorised");
                throw DeadTokenRefusal();
            });

            Assert.IsNull(await manager.GetRefreshToken(NullLogger.Instance));

            Assert.AreEqual("synthetic-admin-reauthorised", await store.GetRefreshTokenAsync(TeamId));
            Assert.AreEqual(TeamTokenStatus.RefreshFailed, manager.Status, "The Team is still authorised, so it is saved as authorised.");
        }

        /// <summary>The token the crawl read is still the one stored, so a dead-token verdict deletes it, as before.</summary>
        [TestMethod]
        public async Task TeamToken_TheTokenReadIsRefused_AndStillStored_IsDeleted()
        {
            var store = await TokenStoreWithAuthorisedTeam();
            var manager = TokenManager(store, _ => Task.FromException<RefreshOAuthToken>(DeadTokenRefusal()));

            Assert.IsNull(await manager.GetRefreshToken(NullLogger.Instance));

            Assert.IsNull(await store.GetRefreshTokenAsync(TeamId));
            Assert.AreEqual(TeamTokenStatus.Revoked, manager.Status);
        }

        /// <summary>
        /// A renewed refresh token this crawl stored in place of the one it read is the crawl's own: when Entra ID later
        /// calls it dead, during a re-mint part-way through the crawl, it is deleted.
        /// </summary>
        [TestMethod]
        public async Task ChannelReadFailure_ARenewedTokenTheCrawlStoredItself_IsDeletedWhenRefused()
        {
            var store = await TokenStoreWithAuthorisedTeam();
            var redeemedWith = new List<string>();
            var manager = TokenManager(store, refreshToken =>
            {
                redeemedWith.Add(refreshToken);
                return redeemedWith.Count == 1
                    ? Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-access", RefreshToken = "synthetic-renewed", ExpiresIn = 3600 })
                    : Task.FromException<RefreshOAuthToken>(DeadTokenRefusal());
            });
            var crawlToken = await manager.GetRefreshToken(NullLogger.Instance);
            Assert.AreEqual("synthetic-renewed", await store.GetRefreshTokenAsync(TeamId), "The crawl stored the renewed token itself.");

            var graph = new StubGraphHandler().Json("/channels/channel-1/messages/delta", StubGraphHandler.Error("Refused"), HttpStatusCode.Unauthorized);
            var reader = new GraphChannelMessagesSourceLoader(crawlToken, manager.RedeemRefreshTokenAsync, new InMemoryTeamChannelDeltaTokenStore(),
                NullLogger.Instance, new HttpClient(graph));
            var failure = await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                reader.LoadMessagesAndReactions(TypedChannel("channel-1", ChannelMembershipType.Standard), TeamId));
            Assert.AreEqual(ChannelReadFailureKind.TokenRejected, failure.Kind);
            CollectionAssert.AreEqual(new[] { StoredRefreshToken, "synthetic-renewed" }, redeemedWith, "The re-mint used the renewed token.");

            var team = new O365Team { Id = TeamId, DisplayName = "Contoso" };
            await RecordChannelReadFailure(team, failure, manager);

            Assert.IsNull(await store.GetRefreshTokenAsync(TeamId));
            Assert.IsFalse(team.HasRefreshToken);
        }

        /// <summary>An authorisation an administrator removed while the crawl ran stays removed, and the Team is saved as such.</summary>
        [TestMethod]
        public async Task ChannelReadFailure_TheTeamDeauthorisedDuringTheCrawl_IsSavedAsNotAuthorised()
        {
            var store = await TokenStoreWithAuthorisedTeam();
            var manager = TokenManager(store, _ => Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-access" }));
            await manager.GetRefreshToken(NullLogger.Instance);
            await store.RemoveRefreshTokenAsync(TeamId);

            var team = new O365Team { Id = TeamId, DisplayName = "Contoso" };
            await RecordChannelReadFailure(team, new ChannelMessagesReadException(ChannelReadFailureKind.TokenRejected, "synthetic", null), manager);

            Assert.IsNull(await store.GetRefreshTokenAsync(TeamId));
            Assert.IsFalse(team.HasRefreshToken);
        }

        /// <summary>
        /// The same race part-way through a crawl, which proactive renewal lets run for hours: Graph refuses the crawl's
        /// token (a 401 whose re-mint Entra ID calls dead, or a 403 on a standard channel) after the Team was authorised again.
        /// </summary>
        [DataTestMethod]
        [DataRow(401)]
        [DataRow(403)]
        public async Task ChannelReadFailure_TheTeamReauthorisedDuringTheCrawl_KeepsTheNewAuthorisation(int status)
        {
            var store = await TokenStoreWithAuthorisedTeam();
            var redemptions = 0;
            var manager = TokenManager(store, _ => ++redemptions == 1
                ? Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-access", ExpiresIn = 3600 })
                : Task.FromException<RefreshOAuthToken>(DeadTokenRefusal()));
            var crawlToken = await manager.GetRefreshToken(NullLogger.Instance);

            // While the crawl runs, the person whose token it holds loses access and an administrator authorises the Team again.
            await store.SetRefreshTokenAsync(TeamId, "synthetic-admin-reauthorised");

            var graph = new StubGraphHandler().Json("/channels/channel-1/messages/delta", StubGraphHandler.Error("Refused"), (HttpStatusCode)status);
            var reader = new GraphChannelMessagesSourceLoader(crawlToken, manager.RedeemRefreshTokenAsync, new InMemoryTeamChannelDeltaTokenStore(),
                NullLogger.Instance, new HttpClient(graph));
            var failure = await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                reader.LoadMessagesAndReactions(TypedChannel("channel-1", ChannelMembershipType.Standard), TeamId));
            Assert.AreEqual(ChannelReadFailureKind.TokenRejected, failure.Kind, "The evidence is about the token this crawl read.");

            var team = new O365Team { Id = TeamId, DisplayName = "Contoso" };
            var logger = new FakeLoaderClasses.RecordingLogger();
            await RecordChannelReadFailure(team, failure, manager, logger);

            Assert.AreEqual("synthetic-admin-reauthorised", await store.GetRefreshTokenAsync(TeamId),
                "The new authorisation must survive evidence about the old one.");
            Assert.IsTrue(team.HasRefreshToken, "...and the Team is still recorded as authorised.");
            Assert.IsTrue(logger.Entries.Any(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information && e.Message.Contains("authorised again")),
                string.Join(" | ", logger.Entries.Select(e => e.Message)));
            Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("synthetic-admin-reauthorised") || e.Message.Contains(StoredRefreshToken)
                || e.Message.Contains("synthetic-access")), "No token value is ever logged.");
        }

        #endregion

        #region Graph stub

        /// <summary>
        /// Answers Graph requests from canned responses by path suffix (the longest matching suffix wins) and records every
        /// request. An unrouted path gets Graph's own 404 for a missing directory object.
        /// </summary>
        internal sealed class StubGraphHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes
                = new Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>>(StringComparer.OrdinalIgnoreCase);

            public List<Uri> Requests { get; } = new List<Uri>();

            public static Microsoft.Graph.GraphServiceClient Client(HttpMessageHandler handler)
                => new Microsoft.Graph.GraphServiceClient(new HttpClient(handler), new BearerTokenAuthenticationProvider("synthetic-token"));

            public static HttpResponseMessage Response(HttpStatusCode status, string json)
                => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

            public static string Error(string code) => "{\"error\":{\"code\":\"" + code + "\",\"message\":\"Synthetic " + code + ".\"}}";

            public StubGraphHandler Json(string pathSuffix, string json, HttpStatusCode status = HttpStatusCode.OK)
                => Route(pathSuffix, _ => Response(status, json));

            public StubGraphHandler Route(string pathSuffix, Func<HttpRequestMessage, HttpResponseMessage> respond)
            {
                _routes[pathSuffix] = respond;
                return this;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (Requests)
                {
                    Requests.Add(request.RequestUri);
                }

                var path = request.RequestUri.AbsolutePath;
                var respond = _routes.Where(r => path.EndsWith(r.Key, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.Key.Length).Select(r => r.Value).FirstOrDefault();
                HttpResponseMessage response;
                try
                {
                    response = respond != null ? respond(request) : Response(HttpStatusCode.NotFound, Error("Request_ResourceNotFound"));
                }
                catch (Exception ex)
                {
                    // A route that throws models a request that never got an answer (network failure, timeout).
                    return Task.FromException<HttpResponseMessage>(ex);
                }
                response.RequestMessage = request;
                return Task.FromResult(response);
            }
        }

        #endregion

        #region Invalid channel delta tokens (F-1)

        private sealed class ScriptedPageReader : IChannelMessagesPageReader
        {
            private readonly Func<TeamChannelDeltaTokenInfo, ChannelRootMessagesPageResult> _root;

            public ScriptedPageReader(Func<TeamChannelDeltaTokenInfo, ChannelRootMessagesPageResult> root) { _root = root; }

            public List<string> TokensRequested { get; } = new List<string>();

            public Task<ChannelRootMessagesPageResult> LoadRootMessages(string teamId, string channelId, TeamChannelDeltaTokenInfo channelDeltaInfo)
            {
                TokensRequested.Add(channelDeltaInfo?.Token ?? "(none)");
                return Task.FromResult(_root(channelDeltaInfo));
            }

            public Task<ChannelRepliesPageResult> LoadReplies(string teamId, string channelId, string messageId)
                => Task.FromResult(new ChannelRepliesPageResult { Completed = true });
        }

        private static Microsoft.Graph.Models.ODataErrors.ODataError GraphRefusal(int status, string code)
            => new Microsoft.Graph.Models.ODataErrors.ODataError
            {
                ResponseStatusCode = status,
                Error = new Microsoft.Graph.Models.ODataErrors.MainError { Code = code },
            };

        private static ChannelRootMessagesPageResult FullRead() => new ChannelRootMessagesPageResult
        {
            Messages = new List<ChatMessage> { Msg("root-1", AfterToken) },
            Deltalink = DeltaLink("fresh-token"),
            Completed = true,
        };

        /// <summary>
        /// Graph refuses a stored channel delta token with 410, <c>syncStateNotFound</c>, or - its documented persistent answer - a
        /// bare 400. The channel used to be abandoned until the next cycle (and only for a 400 coded "BadRequest"); now it is read in
        /// full at once, and only then is the dead token dropped.
        /// </summary>
        [DataTestMethod]
        [DataRow(410, null)]
        [DataRow(410, "syncStateNotFound")]
        [DataRow(400, "UnknownError")]
        [DataRow(400, "BadRequest")]
        public async Task ChannelMessagesLoader_ARejectedDeltaToken_IsDropped_AndTheChannelReadInFullOnce(int status, string code)
        {
            var channel = Channel("channel-1");
            var store = new InMemoryTeamChannelDeltaTokenStore();
            await store.SetDeltaToken("team-1", "channel-1", new TeamChannelDeltaTokenInfo { Token = "stale-token", LastUpdated = DeltaTokenWrittenAt });
            var reader = new ScriptedPageReader(info => info != null ? throw GraphRefusal(status, code) : FullRead());

            var newToken = await new ChannelMessagesLoader(reader, store, NullLogger.Instance).LoadTeamMessagesAndReplies(channel, "team-1");

            CollectionAssert.AreEqual(new[] { "stale-token", "(none)" }, reader.TokensRequested.ToArray(), "The retry carries no token, so it can't be refused for one.");
            Assert.AreEqual("fresh-token", newToken?.Token, "The full read's token is handed on to be committed after SQL.");
            Assert.IsNull(await store.GetDeltaToken("team-1", "channel-1"), "The dead token is gone.");
            Assert.IsTrue(channel.DeltaTokenWasReset);
            Assert.AreEqual(1, channel.Messages.Count, "The full read's messages are kept.");
        }

        [TestMethod]
        public async Task ChannelMessagesLoader_IfTheFullReadFailsToo_TheStoredTokenIsKept_AndTheFailureIsThrown()
        {
            var store = new InMemoryTeamChannelDeltaTokenStore();
            await store.SetDeltaToken("team-1", "channel-1", new TeamChannelDeltaTokenInfo { Token = "stale-token" });
            var reader = new ScriptedPageReader(info => throw GraphRefusal(info != null ? 400 : 500, "UnknownError"));

            var ex = await Assert.ThrowsExceptionAsync<Microsoft.Graph.Models.ODataErrors.ODataError>(() =>
                new ChannelMessagesLoader(reader, store, NullLogger.Instance).LoadTeamMessagesAndReplies(Channel("channel-1"), "team-1"));

            Assert.AreEqual(500, ex.ResponseStatusCode);
            CollectionAssert.AreEqual(new[] { "stale-token", "(none)" }, reader.TokensRequested.ToArray(), "Once - never a loop.");
            Assert.AreEqual("stale-token", (await store.GetDeltaToken("team-1", "channel-1"))?.Token,
                "The channel failed for a reason that isn't its token, so it keeps its place.");
        }

        [DataTestMethod]
        [DataRow(401)]
        [DataRow(403)]
        [DataRow(429)]
        [DataRow(500)]
        [DataRow(503)]
        public async Task ChannelMessagesLoader_OtherFailuresOfATokenRequest_AreNotMistakenForARejectedToken(int status)
        {
            var store = new InMemoryTeamChannelDeltaTokenStore();
            await store.SetDeltaToken("team-1", "channel-1", new TeamChannelDeltaTokenInfo { Token = "good-token" });
            var reader = new ScriptedPageReader(info => throw GraphRefusal(status, "Synthetic"));

            await Assert.ThrowsExceptionAsync<Microsoft.Graph.Models.ODataErrors.ODataError>(() =>
                new ChannelMessagesLoader(reader, store, NullLogger.Instance).LoadTeamMessagesAndReplies(Channel("channel-1"), "team-1"));

            Assert.AreEqual(1, reader.TokensRequested.Count);
            Assert.AreEqual("good-token", (await store.GetDeltaToken("team-1", "channel-1"))?.Token);
        }

        [TestMethod]
        public async Task ChannelMessagesLoader_ABadRequestWithNoStoredToken_IsNotRetried()
        {
            var reader = new ScriptedPageReader(info => throw GraphRefusal(400, "UnknownError"));

            await Assert.ThrowsExceptionAsync<Microsoft.Graph.Models.ODataErrors.ODataError>(() =>
                new ChannelMessagesLoader(reader, new InMemoryTeamChannelDeltaTokenStore(), NullLogger.Instance).LoadTeamMessagesAndReplies(Channel("channel-1"), "team-1"));

            Assert.AreEqual(1, reader.TokensRequested.Count, "A request with no token has no token to blame.");
        }

        #endregion

        #region The group scan is one summary line (F-6)

        [TestMethod]
        public async Task FindGroupsWithTeamToCrawl_LogsOneSummary_NotALinePerGroupWithoutATeam()
        {
            var groups = new List<Group> { FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g-team", "Contoso Marketing", "[\"Team\"]") };
            for (var i = 0; i < 50; i++)
            {
                groups.Add(FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g-dl-" + i, "Contoso Distribution List " + i, "[]"));
            }
            groups.Add(FakeTeamsGroupSourceLoader.GroupWithProvisioningOptions("g-unknown", "Contoso Security Group", null));
            var source = new FakeTeamsGroupSourceLoader(groups.ToArray());

            using (var captured = new CapturedAnalyticsLogger())
            {
                var finder = new TeamsFinder(captured.Logger, new AppConfig(), source);
                var toCrawl = await finder.FindGroupsWithTeamToCrawl(new TeamsCrawlConfig());

                Assert.AreEqual(1, toCrawl.Count);
                var traces = captured.Messages(Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Information);
                Assert.IsFalse(traces.Any(m => m.Contains("Contoso Distribution List")), "A group's name is only a Debug (Verbose) line now, not Information.");
                Assert.AreEqual(1, traces.Count(m => m.StartsWith("TeamsGroupScan:") && m.Contains("groups=52; withTeam=1; noTeam=50; notReported=1")),
                    "One line with the counts: " + string.Join(" | ", traces));
                Assert.AreEqual(50, captured.Messages(Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Verbose).Count(m => m.Contains("has no Team associated")));
                Assert.AreEqual(52, finder.GroupsScanned);
                Assert.AreEqual(50, finder.GroupsWithoutTeam);
            }
        }

        #endregion

        /// <summary>
        /// The paging caps are the crawl's only defence against a runaway Graph <c>nextLink</c>. The
        /// stop condition is evaluated after the item has been counted, so the cap is the size of the
        /// set we keep - an off-by-one here either drops a message or lets the loop run one page longer
        /// than intended.
        /// </summary>
        [TestMethod]
        public void ShouldContinuePaging_StopsExactlyAtTheCap()
        {
            Assert.IsTrue(TeamsCrawlPagingPolicy.ShouldContinuePaging(1, 3));
            Assert.IsTrue(TeamsCrawlPagingPolicy.ShouldContinuePaging(2, 3));
            Assert.IsFalse(TeamsCrawlPagingPolicy.ShouldContinuePaging(3, 3));
            Assert.IsFalse(TeamsCrawlPagingPolicy.ShouldContinuePaging(4, 3));
        }
    }
}
