using Common.Entities.Config;
using Common.Entities.Redis;
using DataUtils;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    public interface IDeltaValueProvider
    {
        Task<string> GetDeltaToken();
        Task SetDeltaToken(string deltaToken);
        Task ClearDeltaToken();
    }

    public sealed class DeltaTokenUnavailableException : Exception
    {
        public DeltaTokenUnavailableException(string message, Exception innerException) : base(message, innerException) { }
    }

    internal interface IStringValueStore
    {
        Task<string> GetString(string key);
        Task SetString(string key, string value);
        Task DeleteString(string key);
    }

    internal sealed class CacheConnectionStringValueStore : IStringValueStore
    {
        private readonly CacheConnectionManager _cache;

        public CacheConnectionStringValueStore(CacheConnectionManager cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public Task<string> GetString(string key) => _cache.GetString(key);
        public Task SetString(string key, string value) => _cache.SetString(key, value);
        public Task DeleteString(string key) => _cache.DeleteString(key);
    }

    internal sealed class DeltaTokenStoreRetryOptions
    {
        public static readonly DeltaTokenStoreRetryOptions Default = new DeltaTokenStoreRetryOptions(3, TimeSpan.FromSeconds(2));

        public DeltaTokenStoreRetryOptions(int maxAttempts, TimeSpan delay)
        {
            if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
            MaxAttempts = maxAttempts;
            Delay = delay;
        }

        public int MaxAttempts { get; }
        public TimeSpan Delay { get; }
    }

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

    public class RedisProcessDeltaValueProvider : IDeltaValueProvider
    {
        private readonly IStringValueStore _store;
        private readonly AppConfig _appConfig;
        private readonly AnalyticsLogger _logger;
        private readonly DeltaTokenStoreRetryOptions _retryOptions;
        private string _lastKnownCommittedDeltaToken;

        public RedisProcessDeltaValueProvider(AppConfig appConfig, DataUtils.AnalyticsLogger logger)
            : this(appConfig, logger, new CacheConnectionStringValueStore(CacheConnectionManager.GetConnectionManager(appConfig.ConnectionStrings.RedisConnectionString, tenantId: appConfig.TenantGUID.ToString(), clientId: appConfig.ClientID, clientSecret: appConfig.ClientSecret)), DeltaTokenStoreRetryOptions.Default)
        {
        }

        internal RedisProcessDeltaValueProvider(AppConfig appConfig, DataUtils.AnalyticsLogger logger, IStringValueStore store, DeltaTokenStoreRetryOptions retryOptions)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            _logger = logger;
            _retryOptions = retryOptions ?? DeltaTokenStoreRetryOptions.Default;
        }

        public async Task ClearDeltaToken()
        {
            var key = GetRedisUserDeltaCacheKey();
            await ExecuteWithRetry(() => _store.DeleteString(key), "delete");
            _lastKnownCommittedDeltaToken = null;
            _logger.LogWarning($"Cleared persisted user delta token.");
        }

        public async Task<string> GetDeltaToken()
        {
            var key = GetRedisUserDeltaCacheKey();
            try
            {
                var usersQueryDelta = await ExecuteWithRetry(() => _store.GetString(key), "read");
                if (string.IsNullOrEmpty(usersQueryDelta))
                {
                    _logger.LogWarning($"No persisted user delta token found; a confirmed first-run/full-enumeration path will be used.");
                    return null;
                }

                _lastKnownCommittedDeltaToken = usersQueryDelta;
                _logger.LogInformation($"Persisted user delta token found.");
                return usersQueryDelta;
            }
            catch (Exception ex) when (!(ex is DeltaTokenUnavailableException) && !(ex is OperationCanceledException))
            {
                if (!string.IsNullOrEmpty(_lastKnownCommittedDeltaToken))
                {
                    _logger.LogWarning($"User delta token store read failed after {_retryOptions.MaxAttempts:N0} attempt(s); using the last committed in-process checkpoint for this WebJob process. The next successful Redis read will resume normal persisted checkpoint use.");
                    return _lastKnownCommittedDeltaToken;
                }

                throw new DeltaTokenUnavailableException(
                    "User delta token store is unavailable and this process has no last committed checkpoint. Deferring user metadata/licence import rather than treating the outage as a cache miss and starting a full tenant crawl.",
                    ex);
            }
        }

        public async Task SetDeltaToken(string deltaToken)
        {
            var key = GetRedisUserDeltaCacheKey();
            _logger.LogInformation($"Setting persisted user delta token.");
            await ExecuteWithRetry(() => _store.SetString(key, deltaToken), "write");
            _lastKnownCommittedDeltaToken = deltaToken;
        }

        private async Task<T> ExecuteWithRetry<T>(Func<Task<T>> action, string operation)
        {
            Exception last = null;
            for (var attempt = 1; attempt <= _retryOptions.MaxAttempts; attempt++)
            {
                try
                {
                    return await action();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt >= _retryOptions.MaxAttempts)
                    {
                        break;
                    }

                    _logger?.LogWarning($"User delta token store {operation} attempt {attempt:N0}/{_retryOptions.MaxAttempts:N0} failed ({ex.GetType().Name}); retrying.");
                    if (_retryOptions.Delay > TimeSpan.Zero)
                    {
                        await Task.Delay(_retryOptions.Delay);
                    }
                }
            }

            throw last;
        }

        private async Task ExecuteWithRetry(Func<Task> action, string operation)
        {
            await ExecuteWithRetry(async () =>
            {
                await action();
                return true;
            }, operation);
        }

        string GetRedisUserDeltaCacheKey()
        {
            return $"UserDeltaCode-{_appConfig.TenantGUID}";
        }
    }
}


