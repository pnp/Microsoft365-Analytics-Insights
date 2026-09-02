using Common.Entities.Redis.Teams;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="ITeamsGroupSourceLoader"/>, so the Teams crawl's group-selection rules can
    /// be exercised with no Graph call. See issue #377.
    /// </summary>
    public class FakeTeamsGroupSourceLoader : ITeamsGroupSourceLoader
    {
        private readonly List<Group> _groups;

        public FakeTeamsGroupSourceLoader(params Group[] groups)
        {
            _groups = new List<Group>(groups ?? Array.Empty<Group>());
        }

        /// <summary>How many times the v1 "read everything and filter locally" path was used.</summary>
        public int ProvisioningOptionsReadCount { get; private set; }

        /// <summary>How many times the beta "server-side filter" path was used.</summary>
        public int FilteredToTeamsReadCount { get; private set; }

        public Task<List<Group>> LoadGroupsWithProvisioningOptions()
        {
            ProvisioningOptionsReadCount++;
            return Task.FromResult(new List<Group>(_groups));
        }

        public Task<List<Group>> LoadGroupsFilteredToTeams()
        {
            FilteredToTeamsReadCount++;
            return Task.FromResult(new List<Group>(_groups));
        }

        /// <summary>
        /// Build a group as the v1 Graph endpoint returns it, with its provisioned workloads in
        /// <c>AdditionalData</c>. Pass <c>null</c> for <paramref name="provisioningOptionsJson"/> to
        /// model a group that didn't return the property at all.
        /// </summary>
        /// <remarks>
        /// The property name is written as a literal on purpose, NOT as
        /// <c>TeamsCrawlRules.ResourceProvisioningOptionsProperty</c>: this is the Graph wire contract,
        /// and a typo in the production constant must fail these tests rather than move the reader and
        /// the writer together.
        /// </remarks>
        public static Group GroupWithProvisioningOptions(string id, string displayName, string provisioningOptionsJson)
        {
            var group = new Group { Id = id, DisplayName = displayName };
            if (provisioningOptionsJson != null)
            {
                group.AdditionalData["resourceProvisioningOptions"] = provisioningOptionsJson;
            }
            return group;
        }
    }

    /// <summary>
    /// In-memory <see cref="IChannelMessagesSourceLoader"/>. Returns a scripted delta token (or throws)
    /// per channel id, so <see cref="TeamsChannelCrawler"/> can be tested with no Graph and no Redis.
    /// See issue #377.
    /// </summary>
    public class FakeChannelMessagesSourceLoader : IChannelMessagesSourceLoader
    {
        private readonly Dictionary<string, TeamsRedisManager.TeamChannelDeltaTokenInfo> _tokensByChannelId
            = new Dictionary<string, TeamsRedisManager.TeamChannelDeltaTokenInfo>(StringComparer.Ordinal);

        private readonly HashSet<string> _failingChannelIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Channel ids this loader was asked to read, in order.</summary>
        public List<string> ChannelsRead { get; } = new List<string>();

        /// <summary>Script a channel read that returns a new delta token.</summary>
        public FakeChannelMessagesSourceLoader ReturningToken(string channelId, string token)
        {
            _tokensByChannelId[channelId] = new TeamsRedisManager.TeamChannelDeltaTokenInfo { Token = token, LastUpdated = DateTime.Now };
            return this;
        }

        /// <summary>Script a channel read that succeeds but hands back no delta token.</summary>
        public FakeChannelMessagesSourceLoader ReturningNoToken(string channelId)
        {
            _tokensByChannelId[channelId] = null;
            return this;
        }

        /// <summary>Script a channel read that fails the way an expired user token does.</summary>
        public FakeChannelMessagesSourceLoader Failing(string channelId)
        {
            _failingChannelIds.Add(channelId);
            return this;
        }

        public Task<TeamsRedisManager.TeamChannelDeltaTokenInfo> LoadMessagesAndReactions(ChannelWithReactions channel, string teamId)
        {
            ChannelsRead.Add(channel.Id);

            if (_failingChannelIds.Contains(channel.Id))
            {
                throw new ChannelMessagesReadException(new InvalidOperationException("simulated expired user token"));
            }

            _tokensByChannelId.TryGetValue(channel.Id, out var token);
            return Task.FromResult(token);
        }
    }
}
