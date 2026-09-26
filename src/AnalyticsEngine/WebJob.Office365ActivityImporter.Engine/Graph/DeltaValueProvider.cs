using Common.Entities.Config;
using Common.Entities.Redis;
using DataUtils;
using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Interface for delta token provider
    /// </summary>
    public interface IDeltaValueProvider
    {
        Task<string> GetDeltaToken(CancellationToken cancellationToken = default);
        Task SetDeltaToken(string deltaToken, CancellationToken cancellationToken = default);
        Task ClearDeltaToken(CancellationToken cancellationToken = default);

        /// <summary>
        /// Qualifies the cache key so a stored token is only ever reused for the <c>$select</c> it was
        /// minted under.
        /// </summary>
        /// <param name="qualifier">
        /// <see cref="GraphUserOrgSelection.DeltaKeyQualifier"/>. Empty or <c>null</c> restores the
        /// unqualified key, which is what a deployment with no Entra org types uses.
        /// </param>
        /// <remarks>
        /// Set by <see cref="GraphUserLoader"/> alone, from the same
        /// <see cref="GraphUserOrgSelection"/> it builds the request URL from, so the key and the
        /// selection cannot drift apart. Nothing else should call this.
        /// </remarks>
        void SetKeyQualifier(string qualifier);
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

    /// <summary>
    /// In-process delta token provider. Used when no Redis connection string is provided.
    /// </summary>
    public class InProcessDeltaValueProvider : IDeltaValueProvider
    {
        private readonly AnalyticsLogger _logger;
        private string _deltaToken;
        private string _keyQualifier = string.Empty;
        public InProcessDeltaValueProvider(DataUtils.AnalyticsLogger logger)
        {
            _logger = logger;
        }

        public Task ClearDeltaToken(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _deltaToken = null;
            _logger.LogWarning($"Cleared in-memory delta token for tenant.");
            return Task.CompletedTask;
        }

        public Task<string> GetDeltaToken(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        public Task SetDeltaToken(string deltaToken, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation($"Setting in-memory delta token.");
            _deltaToken = deltaToken;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Drops the buffered token when the selection changes.
        /// </summary>
        /// <remarks>
        /// The Redis provider gets this for free by keying on the qualifier, but this one holds a single
        /// token in a field. Without discarding it, a deployment with no Redis would keep reusing a
        /// token minted under the previous <c>$select</c> and the newly configured org attribute would
        /// never arrive for users who did not otherwise change.
        /// </remarks>
        public void SetKeyQualifier(string qualifier)
        {
            var normalised = string.IsNullOrEmpty(qualifier) ? string.Empty : qualifier;
            if (normalised == _keyQualifier)
            {
                return;
            }

            _keyQualifier = normalised;
            if (!string.IsNullOrEmpty(_deltaToken))
            {
                _logger.LogWarning(
                    "User import - the configured org attributes changed, so the in-memory delta token has been discarded. The next import will enumerate every user once so the new attribute is populated.");
                _deltaToken = null;
            }
        }
    }

    /// <summary>
    /// Redis-based delta token provider. Used when Redis connection string is provided.
    /// </summary>
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

        public async Task ClearDeltaToken(CancellationToken cancellationToken = default)
        {
            var key = GetRedisUserDeltaCacheKey();
            await ExecuteWithRetry(() => _store.DeleteString(key), "delete", cancellationToken);
            _lastKnownCommittedDeltaToken = null;
            _logger.LogWarning($"Cleared persisted user delta token.");
        }

        public async Task<string> GetDeltaToken(CancellationToken cancellationToken = default)
        {
            var key = GetRedisUserDeltaCacheKey();
            try
            {
                var usersQueryDelta = await ExecuteWithRetry(() => _store.GetString(key), "read", cancellationToken);
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

        public async Task SetDeltaToken(string deltaToken, CancellationToken cancellationToken = default)
        {
            var key = GetRedisUserDeltaCacheKey();
            _logger.LogInformation($"Setting persisted user delta token.");
            await ExecuteWithRetry(() => _store.SetString(key, deltaToken), "write", cancellationToken);
            _lastKnownCommittedDeltaToken = deltaToken;
        }

        private async Task<T> ExecuteWithRetry<T>(Func<Task<T>> action, string operation, CancellationToken cancellationToken)
        {
            Exception last = null;
            for (var attempt = 1; attempt <= _retryOptions.MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                        await Task.Delay(_retryOptions.Delay, cancellationToken);
                    }
                }
            }

            ExceptionDispatchInfo.Capture(last).Throw();
            throw new InvalidOperationException("Unreachable retry state.");
        }

        private async Task ExecuteWithRetry(Func<Task> action, string operation, CancellationToken cancellationToken)
        {
            await ExecuteWithRetry(async () =>
            {
                await action();
                return true;
            }, operation, cancellationToken);
        }

        /// <summary>
        /// Cache key for this tenant's stored user delta token.
        /// </summary>
        /// <remarks>
        /// Versioned by <see cref="GraphUserDeltaQuery.SelectVersion"/> on purpose. Graph fixes the
        /// <c>$select</c> when a token is minted, so a stored token keeps returning the OLD property set
        /// however the query is edited afterwards. Including the version means a selection change
        /// invalidates the token automatically: the next import falls through to a full enumeration and
        /// the newly selected property is populated for users who have not otherwise changed. Without
        /// this, a new column stays empty forever on every upgraded tenant while looking perfectly
        /// correct on a fresh install - which is close to undetectable.
        /// </remarks>
        string GetRedisUserDeltaCacheKey()
        {
            return $"UserDeltaCode-{_appConfig.TenantGUID}-{GraphUserDeltaQuery.SelectVersion}{_keyQualifier}";
        }

        /// <summary>
        /// Extra qualifier covering the runtime-configured user-org attributes.
        /// </summary>
        /// <remarks>
        /// Empty by default and empty whenever no Entra org types are configured, so the key is
        /// byte-identical to the one this product has always used. That is deliberate: qualifying it
        /// unconditionally would discard every existing customer's delta token on upgrade and make the
        /// next import a full enumeration of the whole tenant, for a feature they may never turn on.
        /// </remarks>
        private string _keyQualifier = string.Empty;

        public void SetKeyQualifier(string qualifier)
        {
            var normalised = string.IsNullOrEmpty(qualifier) ? string.Empty : qualifier;
            if (normalised == _keyQualifier)
            {
                return;
            }

            _keyQualifier = normalised;

            // The in-process safety net holds the last token this process committed, and it is returned
            // when Redis cannot be read. That token was minted under the PREVIOUS selection, so keeping
            // it across a qualifier change would hand a later cycle a token for a different query -
            // exactly what qualifying the key exists to prevent, arriving through the outage path.
            _lastKnownCommittedDeltaToken = null;
        }
    }
}

