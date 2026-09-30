using Common.Entities.State;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// A Teams channel's Graph delta token and when it was saved.
    /// </summary>
    public class TeamChannelDeltaTokenInfo
    {
        public string Token { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
    }

    /// <summary>
    /// Store for the per-channel Graph delta token that makes each Teams channel read incremental.
    /// Extracted so the crawl logic does not depend on a storage technology directly
    /// and can be tested without one. See issue #377.
    ///
    /// Losing a token is not fatal - the next read falls back to a full channel read - but silently
    /// *keeping a stale one* means missed messages, which is why the crawl only ever writes a token
    /// Graph actually handed back.
    /// </summary>
    public interface ITeamChannelDeltaTokenStore
    {
        /// <summary>The stored token for a channel, or <c>null</c> when there isn't one (full read).</summary>
        Task<TeamChannelDeltaTokenInfo> GetDeltaToken(string teamId, string channelId);

        Task SetDeltaToken(string teamId, string channelId, TeamChannelDeltaTokenInfo deltaTokenInfo);

        /// <summary>Forget a channel's token, so the next read is a full one.</summary>
        Task RemoveDeltaToken(string teamId, string channelId);
    }

    /// <summary>
    /// Durable <see cref="ITeamChannelDeltaTokenStore"/> - the production implementation - over the runtime state store:
    /// one row per channel in the <see cref="StatePartitions.TeamsChannels"/> partition of the state table, holding the
    /// token info as JSON.
    /// </summary>
    public class PersistedTeamChannelDeltaTokenStore : ITeamChannelDeltaTokenStore
    {
        private readonly IKeyValueStore _store;
        private readonly ILogger _logger;

        public PersistedTeamChannelDeltaTokenStore(IKeyValueStore store, ILogger logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>The row key for a channel: the Team (group) id and the channel id, which is unique only within its Team.</summary>
        internal static string KeyFor(string teamId, string channelId) => $"{teamId}|{channelId}";

        public async Task<TeamChannelDeltaTokenInfo> GetDeltaToken(string teamId, string channelId)
        {
            var tokenString = await _store.GetStringAsync(KeyFor(ValidId(teamId, nameof(teamId)), ValidId(channelId, nameof(channelId))));
            if (tokenString == null)
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<TeamChannelDeltaTokenInfo>(tokenString);
            }
            catch (JsonReaderException)
            {
                // Unreadable - behave as if there were no token, so the channel is read in full rather than not at all.
                return null;
            }
        }

        public async Task SetDeltaToken(string teamId, string channelId, TeamChannelDeltaTokenInfo deltaTokenInfo)
        {
            if (deltaTokenInfo is null)
            {
                throw new ArgumentNullException(nameof(deltaTokenInfo));
            }

            await _store.SetStringAsync(KeyFor(ValidId(teamId, nameof(teamId)), ValidId(channelId, nameof(channelId))),
                JsonConvert.SerializeObject(deltaTokenInfo));
            _logger.LogInformation($"Updated cached delta token for channel '{channelId}' in team '{teamId}'");
        }

        public async Task RemoveDeltaToken(string teamId, string channelId)
        {
            await _store.DeleteAsync(KeyFor(ValidId(teamId, nameof(teamId)), ValidId(channelId, nameof(channelId))));
            _logger.LogInformation($"Removed cached delta token for channel '{channelId}' in team '{teamId}'");
        }

        private static string ValidId(string id, string paramName)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException($"'{paramName}' cannot be null or empty.", paramName);
            }
            return id;
        }
    }

    /// <summary>
    /// In-memory <see cref="ITeamChannelDeltaTokenStore"/> for tests.
    ///
    /// NOT a production fallback: when no Storage connection string is configured the importer deliberately reads no
    /// channel messages at all (there is no stored refresh token to impersonate a user with), so substituting
    /// this in production would not make deep Teams analytics work - it would only hide that.
    /// </summary>
    public class InMemoryTeamChannelDeltaTokenStore : ITeamChannelDeltaTokenStore
    {
        private readonly ConcurrentDictionary<string, TeamChannelDeltaTokenInfo> _tokens
            = new ConcurrentDictionary<string, TeamChannelDeltaTokenInfo>(StringComparer.Ordinal);

        private static string Key(string teamId, string channelId) => $"{teamId}-{channelId}";

        public Task<TeamChannelDeltaTokenInfo> GetDeltaToken(string teamId, string channelId)
        {
            _tokens.TryGetValue(Key(teamId, channelId), out var info);
            return Task.FromResult(info);
        }

        public Task SetDeltaToken(string teamId, string channelId, TeamChannelDeltaTokenInfo deltaTokenInfo)
        {
            _tokens[Key(teamId, channelId)] = deltaTokenInfo;
            return Task.CompletedTask;
        }

        public Task RemoveDeltaToken(string teamId, string channelId)
        {
            _tokens.TryRemove(Key(teamId, channelId), out _);
            return Task.CompletedTask;
        }
    }
}
