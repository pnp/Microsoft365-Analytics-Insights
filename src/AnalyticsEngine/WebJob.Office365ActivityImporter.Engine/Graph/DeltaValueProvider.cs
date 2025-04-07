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
        private readonly AnalyticsLogger _telemetry;
        private string _deltaToken;
        public InProcessDeltaValueProvider(DataUtils.AnalyticsLogger telemetry)
        {
            _telemetry = telemetry;
        }

        public Task ClearDeltaToken()
        {
            _deltaToken = null;
            _telemetry.LogWarning($"Cleared in-memory delta token for tenant.");
            return Task.CompletedTask;
        }

        public Task<string> GetDeltaToken()
        {
            if (string.IsNullOrEmpty(_deltaToken))
            {
                _telemetry.LogWarning($"No in-memory delta token found.");
            }
            else
            {
                _telemetry.LogInformation($"In-memory delta token found.");
            }
            return Task.FromResult(_deltaToken);
        }

        public Task SetDeltaToken(string deltaToken)
        {
            _telemetry.LogInformation($"Setting in-memory delta token.");
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
        private readonly AnalyticsLogger _telemetry;

        public RedisProcessDeltaValueProvider(AppConfig appConfig, DataUtils.AnalyticsLogger telemetry)
        {
            _cacheConnectionManager = CacheConnectionManager.GetConnectionManager(appConfig.ConnectionStrings.RedisConnectionString);
            _appConfig = appConfig;
            _telemetry = telemetry;
        }

        public async Task ClearDeltaToken()
        {
            var REDIS_USER_DELTA_KEY = GetRedisUserDeltaCacheKey();
            await _cacheConnectionManager.DeleteString(REDIS_USER_DELTA_KEY);
            _telemetry.LogWarning($"Cleared delta token for tenant {_appConfig.TenantGUID}.");
        }

        public async Task<string> GetDeltaToken()
        {
            var REDIS_USER_DELTA_KEY = GetRedisUserDeltaCacheKey();
            var usersQueryDelta = await _cacheConnectionManager.GetString(REDIS_USER_DELTA_KEY);
            if (string.IsNullOrEmpty(usersQueryDelta))
            {
                _telemetry.LogWarning($"No delta token found for tenant {_appConfig.TenantGUID}.");
            }
            else
            {
                _telemetry.LogInformation($"Delta token found for tenant {_appConfig.TenantGUID}.");
            }
            return usersQueryDelta;
        }

        public async Task SetDeltaToken(string deltaToken)
        {
            var REDIS_USER_DELTA_KEY = GetRedisUserDeltaCacheKey();
            _telemetry.LogInformation($"Setting delta token for tenant {_appConfig.TenantGUID}.");
            await _cacheConnectionManager.SetString(REDIS_USER_DELTA_KEY, deltaToken);
        }

        string GetRedisUserDeltaCacheKey()
        {
            var REDIS_USER_DELTA_KEY = $"UserDeltaCode-{_appConfig.TenantGUID}";
            return REDIS_USER_DELTA_KEY;
        }
    }
}
