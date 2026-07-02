using Common.Entities.Config;
using Common.Entities.Redis;
using DataUtils;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Interface for delta token provider
    /// </summary>
    public interface IDeltaValueProvider
    {
        Task<string> GetDeltaToken();
        Task SetDeltaToken(string deltaToken);
        Task ClearDeltaToken();
    }

    /// <summary>
    /// In-process delta token provider. Used when no Redis connection string is provided.
    /// </summary>
    public class InProcessDeltaValueProvider : IDeltaValueProvider
    {
        private readonly AnalyticsLogger _logger;
        private string _deltaToken;
        public InProcessDeltaValueProvider(DataUtils.AnalyticsLogger logger)
        {
            _logger = logger;
        }

        public Task ClearDeltaToken()
        {
            _deltaToken = null;
            _logger.LogWarning($"Cleared in-memory delta token for tenant.");
            return Task.CompletedTask;
        }

        public Task<string> GetDeltaToken()
        {
            if (string.IsNullOrEmpty(_deltaToken))
            {
                _logger.LogWarning($"No in-memory delta token found.");
            }
            else
            {
                _logger.LogInformation($"In-memory delta token found.");
            }
            return Task.FromResult(_deltaToken);
        }

        public Task SetDeltaToken(string deltaToken)
        {
            _logger.LogInformation($"Setting in-memory delta token.");
            _deltaToken = deltaToken;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Redis-based delta token provider. Used when Redis connection string is provided.
    /// </summary>
    public class RedisProcessDeltaValueProvider : IDeltaValueProvider
    {
        private readonly CacheConnectionManager _cacheConnectionManager;
        private readonly AppConfig _appConfig;
        private readonly AnalyticsLogger _logger;

        public RedisProcessDeltaValueProvider(AppConfig appConfig, DataUtils.AnalyticsLogger logger)
        {
            _cacheConnectionManager = CacheConnectionManager.GetConnectionManager(appConfig.ConnectionStrings.RedisConnectionString, tenantId: appConfig.TenantGUID.ToString(), clientId: appConfig.ClientID, clientSecret: appConfig.ClientSecret);
            _appConfig = appConfig;
            _logger = logger;
        }

        public async Task ClearDeltaToken()
        {
            var REDIS_USER_DELTA_KEY = GetRedisUserDeltaCacheKey();
            await _cacheConnectionManager.DeleteString(REDIS_USER_DELTA_KEY);
            _logger.LogWarning($"Cleared delta token for tenant {_appConfig.TenantGUID}.");
        }

        public async Task<string> GetDeltaToken()
        {
            var REDIS_USER_DELTA_KEY = GetRedisUserDeltaCacheKey();
            var usersQueryDelta = await _cacheConnectionManager.GetString(REDIS_USER_DELTA_KEY);
            if (string.IsNullOrEmpty(usersQueryDelta))
            {
                _logger.LogWarning($"No delta token found for tenant {_appConfig.TenantGUID}.");
            }
            else
            {
                _logger.LogInformation($"Delta token found for tenant {_appConfig.TenantGUID}.");
            }
            return usersQueryDelta;
        }

        public async Task SetDeltaToken(string deltaToken)
        {
            var REDIS_USER_DELTA_KEY = GetRedisUserDeltaCacheKey();
            _logger.LogInformation($"Setting delta token for tenant {_appConfig.TenantGUID}.");
            await _cacheConnectionManager.SetString(REDIS_USER_DELTA_KEY, deltaToken);
        }

        string GetRedisUserDeltaCacheKey()
        {
            var REDIS_USER_DELTA_KEY = $"UserDeltaCode-{_appConfig.TenantGUID}";
            return REDIS_USER_DELTA_KEY;
        }
    }
}
