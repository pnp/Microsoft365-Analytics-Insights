using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Linq;
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
        /// Build a group the way the Graph SDK hands it back: its provisioned workloads in the typed
        /// <see cref="Group.ResourceProvisioningOptions"/> list (Microsoft.Graph 5+), parsed from the JSON array Graph sends.
        /// Pass <c>null</c> for <paramref name="provisioningOptionsJson"/> to model a group that didn't return the property at all.
        /// </summary>
        /// <remarks>
        /// This used to write the raw array into <c>AdditionalData</c>, where the v4 SDK put it - so it pinned the very
        /// bug that left the crawl finding no Teams under SDK 6. The wire name itself is covered by
        /// <c>TeamsCrawlTests.FindGroupsWithTeamToCrawl_RealGraphJsonThroughTheSdk_FindsTheTeams</c>, which pushes Graph's
        /// JSON through the real SDK.
        /// </remarks>
        public static Group GroupWithProvisioningOptions(string id, string displayName, string provisioningOptionsJson)
        {
            var group = new Group { Id = id, DisplayName = displayName };
            if (provisioningOptionsJson != null)
            {
                group.ResourceProvisioningOptions = Newtonsoft.Json.Linq.JArray.Parse(provisioningOptionsJson)
                    .Select(option => option.ToString()).ToList();
            }
            return group;
        }
    }

    /// <summary>
    /// In-memory <see cref="IChannelMessagesSourceLoader"/>. Returns a scripted delta token (or throws)
    /// per channel id, so <see cref="TeamsChannelCrawler"/> can be tested with no Graph and no storage.
    /// See issue #377.
    /// </summary>
    public class FakeChannelMessagesSourceLoader : IChannelMessagesSourceLoader
    {
        private readonly Dictionary<string, TeamChannelDeltaTokenInfo> _tokensByChannelId
            = new Dictionary<string, TeamChannelDeltaTokenInfo>(StringComparer.Ordinal);

        private readonly Dictionary<string, ChannelReadFailureKind> _failingChannelIds = new Dictionary<string, ChannelReadFailureKind>(StringComparer.Ordinal);

        /// <summary>Channel ids this loader was asked to read, in order.</summary>
        public List<string> ChannelsRead { get; } = new List<string>();
        public List<string> MessageIdsReturned { get; } = new List<string>();

        /// <summary>Script a channel read that returns a new delta token.</summary>
        public FakeChannelMessagesSourceLoader ReturningToken(string channelId, string token)
        {
            _tokensByChannelId[channelId] = new TeamChannelDeltaTokenInfo { Token = token, LastUpdated = DateTime.Now };
            return this;
        }

        /// <summary>Script a channel read that succeeds but hands back no delta token.</summary>
        public FakeChannelMessagesSourceLoader ReturningNoToken(string channelId)
        {
            _tokensByChannelId[channelId] = null;
            return this;
        }

        /// <summary>
        /// Script a channel read that fails. By default it fails the way an unclassified error does (which never
        /// deletes the Team's token); pass a <paramref name="kind"/> to script a classified failure.
        /// </summary>
        public FakeChannelMessagesSourceLoader Failing(string channelId, ChannelReadFailureKind kind = ChannelReadFailureKind.Transient)
        {
            _failingChannelIds[channelId] = kind;
            return this;
        }

        public Task<TeamChannelDeltaTokenInfo> LoadMessagesAndReactions(ChannelWithReactions channel, string teamId)
        {
            ChannelsRead.Add(channel.Id);

            if (_failingChannelIds.TryGetValue(channel.Id, out var kind))
            {
                var inner = new InvalidOperationException("simulated channel read failure");
                return Task.FromException<TeamChannelDeltaTokenInfo>(kind == ChannelReadFailureKind.Transient
                    ? new ChannelMessagesReadException(inner)
                    : new ChannelMessagesReadException(kind, "simulated " + kind, inner));
            }

            var messageId = $"{channel.Id}-message-{ChannelsRead.Count}";
            channel.Messages.Add(new ChatMessage { Id = messageId });
            MessageIdsReturned.Add(messageId);

            _tokensByChannelId.TryGetValue(channel.Id, out var token);
            return Task.FromResult(token);
        }
    }
}
