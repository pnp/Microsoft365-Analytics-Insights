using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Interface for per-user delta token storage.
    /// </summary>
    public interface IDeltaTokenStore
    {
        Task<string> GetDeltaToken(string key);
        Task SetDeltaToken(string key, string deltaToken);
    }

    /// <summary>
    /// In-memory delta token store. Useful for tests and single-process runs.
    /// </summary>
    public class InMemoryDeltaTokenStore : IDeltaTokenStore
    {
        private readonly Dictionary<string, string> _tokens = new Dictionary<string, string>();

        public Task<string> GetDeltaToken(string key)
        {
            _tokens.TryGetValue(key, out var token);
            return Task.FromResult(token);
        }

        public Task SetDeltaToken(string key, string deltaToken)
        {
            _tokens[key] = deltaToken;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Redis-based delta token store for per-user keys.
    /// </summary>
    public class RedisDeltaTokenStore : IDeltaTokenStore
    {
        private readonly Common.Entities.Redis.CacheConnectionManager _cacheConnectionManager;

        public RedisDeltaTokenStore(string redisConnectionString)
        {
            _cacheConnectionManager = Common.Entities.Redis.CacheConnectionManager.GetConnectionManager(redisConnectionString);
        }

        public async Task<string> GetDeltaToken(string key)
        {
            return await _cacheConnectionManager.GetString(key);
        }

        public async Task SetDeltaToken(string key, string deltaToken)
        {
            await _cacheConnectionManager.SetString(key, deltaToken);
        }
    }
}
