using Common.Entities.Redis.Teams;
using DataUtils;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace Tests.UnitTests
{
    /// <summary>
    /// First unit coverage for the Teams import (issue #377). Everything here runs with no Graph, no
    /// Redis and no SQL: the crawl's decisions are now behind pure rules and ports.
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
            await store.SetDeltaToken("team-1", "channel-2", new TeamsRedisManager.TeamChannelDeltaTokenInfo { Token = "previous-delta-2" });

            var crawler = new TeamsChannelCrawler(source, store);
            await crawler.PopulateNewMessagesAndReactions(new List<ChannelWithReactions> { Channel("channel-1"), Channel("channel-2") }, "team-1");

            CollectionAssert.AreEqual(new[] { "channel-1", "channel-2" }, source.ChannelsRead.ToArray(),
                "Every channel in the team must be crawled, in order.");
            Assert.AreEqual("delta-1", (await store.GetDeltaToken("team-1", "channel-1"))?.Token);
            Assert.AreEqual("previous-delta-2", (await store.GetDeltaToken("team-1", "channel-2"))?.Token,
                "A read that returned no delta token must not overwrite the token already stored.");
        }

        /// <summary>
        /// An expired user-delegated token aborts the whole team's crawl (the caller deletes the team's
        /// auth token and retries next cycle), but the channels already crawled keep their new delta
        /// tokens so the next run doesn't re-read them from scratch.
        /// </summary>
        [TestMethod]
        public async Task TeamsChannelCrawler_AbortsTheTeamOnAReadFailureButKeepsEarlierTokens()
        {
            var source = new FakeChannelMessagesSourceLoader()
                .ReturningToken("channel-1", "delta-1")
                .Failing("channel-2")
                .ReturningToken("channel-3", "delta-3");

            var store = new InMemoryTeamChannelDeltaTokenStore();
            var crawler = new TeamsChannelCrawler(source, store);

            await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                crawler.PopulateNewMessagesAndReactions(
                    new List<ChannelWithReactions> { Channel("channel-1"), Channel("channel-2"), Channel("channel-3") }, "team-1"));

            CollectionAssert.AreEqual(new[] { "channel-1", "channel-2" }, source.ChannelsRead.ToArray(),
                "The crawl stops at the failing channel rather than carrying on with a token we know is bad.");
            Assert.AreEqual("delta-1", (await store.GetDeltaToken("team-1", "channel-1"))?.Token);
            Assert.IsNull(await store.GetDeltaToken("team-1", "channel-3"));
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
