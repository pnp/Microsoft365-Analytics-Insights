using Common.Entities.Redis;
using Common.Entities.Redis.Teams;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Store for the per-channel Graph delta token that makes each Teams channel read incremental.
    /// Extracted so the crawl logic no longer depends on <see cref="CacheConnectionManager"/> directly
    /// and can be tested without Redis. See issue #377.
    ///
    /// Losing a token is not fatal - the next read falls back to a full channel read - but silently
    /// *keeping a stale one* means missed messages, which is why the crawl only ever writes a token
    /// Graph actually handed back.
    /// </summary>
    public interface ITeamChannelDeltaTokenStore
    {
        /// <summary>The stored token for a channel, or <c>null</c> when there isn't one (full read).</summary>
        Task<TeamsRedisManager.TeamChannelDeltaTokenInfo> GetDeltaToken(string teamId, string channelId);

        Task SetDeltaToken(string teamId, string channelId, TeamsRedisManager.TeamChannelDeltaTokenInfo deltaTokenInfo);

        /// <summary>Forget a channel's token, so the next read is a full one.</summary>
        Task RemoveDeltaToken(string teamId, string channelId);
    }

    /// <summary>
    /// Redis-backed <see cref="ITeamChannelDeltaTokenStore"/> - the production implementation. A thin
    /// wrapper over the existing <see cref="TeamsRedisManager"/> extension methods, so the cache keys
    /// and the operator-facing log lines they emit are unchanged.
    /// </summary>
    public class RedisTeamChannelDeltaTokenStore : ITeamChannelDeltaTokenStore
    {
        private readonly CacheConnectionManager _cacheConnectionManager;
        private readonly ILogger _logger;

        public RedisTeamChannelDeltaTokenStore(CacheConnectionManager cacheConnectionManager, ILogger logger)
        {
            _cacheConnectionManager = cacheConnectionManager ?? throw new ArgumentNullException(nameof(cacheConnectionManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<TeamsRedisManager.TeamChannelDeltaTokenInfo> GetDeltaToken(string teamId, string channelId)
            => _cacheConnectionManager.GetTeamChannelDeltaTokenInfo(teamId, channelId);

        public Task SetDeltaToken(string teamId, string channelId, TeamsRedisManager.TeamChannelDeltaTokenInfo deltaTokenInfo)
            => _cacheConnectionManager.SetTeamChannelDeltaTokenInfo(teamId, channelId, deltaTokenInfo, _logger);

        public Task RemoveDeltaToken(string teamId, string channelId)
            => _cacheConnectionManager.RemoveTeamChannelDeltaToken(teamId, channelId, _logger);
    }

    /// <summary>
    /// In-memory <see cref="ITeamChannelDeltaTokenStore"/> for tests.
    ///
    /// NOT a production fallback: when Redis isn't configured the importer deliberately reads no
    /// channel messages at all (there is no refresh token to impersonate a user with), so substituting
    /// this in production would not make deep Teams analytics work - it would only hide that.
    /// </summary>
    public class InMemoryTeamChannelDeltaTokenStore : ITeamChannelDeltaTokenStore
    {
        private readonly ConcurrentDictionary<string, TeamsRedisManager.TeamChannelDeltaTokenInfo> _tokens
            = new ConcurrentDictionary<string, TeamsRedisManager.TeamChannelDeltaTokenInfo>(StringComparer.Ordinal);

        private static string Key(string teamId, string channelId) => $"{teamId}-{channelId}";

        public Task<TeamsRedisManager.TeamChannelDeltaTokenInfo> GetDeltaToken(string teamId, string channelId)
        {
            _tokens.TryGetValue(Key(teamId, channelId), out var info);
            return Task.FromResult(info);
        }

        public Task SetDeltaToken(string teamId, string channelId, TeamsRedisManager.TeamChannelDeltaTokenInfo deltaTokenInfo)
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
