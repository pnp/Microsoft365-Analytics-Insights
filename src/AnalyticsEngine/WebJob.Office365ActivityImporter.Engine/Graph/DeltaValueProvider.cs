using Common.Entities.Config;
using Common.Entities.State;
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
    }

    public sealed class DeltaTokenUnavailableException : Exception
    {
        public DeltaTokenUnavailableException(string message, Exception innerException) : base(message, innerException) { }
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
    /// In-process delta token provider. Used when no Storage connection string is configured.
    /// </summary>
    public class InProcessDeltaValueProvider : IDeltaValueProvider
    {
        private readonly AnalyticsLogger _logger;
        private string _deltaToken;
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
    }

    /// <summary>
    /// Durable delta token provider over the runtime state store (the <see cref="StatePartitions.UserImport"/> partition
    /// of the <see cref="StateStore.TableName"/> Azure Table). Used when a Storage connection string is configured.
    /// </summary>
    /// <remarks>
    /// A confirmed miss (no row) returns null - the deliberate first-run / full-enumeration path. A store that can't be
    /// read is NOT a miss: after bounded retries it falls back to the last token this process committed, and with none
    /// it throws <see cref="DeltaTokenUnavailableException"/> so the user import is deferred rather than turned into a
    /// full tenant crawl by a storage blip.
    /// </remarks>
    public class PersistedDeltaValueProvider : IDeltaValueProvider
    {
        private readonly IKeyValueStore _store;
        private readonly AppConfig _appConfig;
        private readonly AnalyticsLogger _logger;
        private readonly DeltaTokenStoreRetryOptions _retryOptions;
        private string _lastKnownCommittedDeltaToken;

        public PersistedDeltaValueProvider(AppConfig appConfig, DataUtils.AnalyticsLogger logger, IKeyValueStore store)
            : this(appConfig, logger, store, DeltaTokenStoreRetryOptions.Default)
        {
        }

        internal PersistedDeltaValueProvider(AppConfig appConfig, DataUtils.AnalyticsLogger logger, IKeyValueStore store, DeltaTokenStoreRetryOptions retryOptions)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            _logger = logger;
            _retryOptions = retryOptions ?? DeltaTokenStoreRetryOptions.Default;
        }

        public async Task ClearDeltaToken(CancellationToken cancellationToken = default)
        {
            var key = GetUserDeltaTokenKey();
            await ExecuteWithRetry(() => _store.DeleteAsync(key, cancellationToken), "delete", cancellationToken);
            _lastKnownCommittedDeltaToken = null;
            _logger.LogWarning($"Cleared persisted user delta token.");
        }

        public async Task<string> GetDeltaToken(CancellationToken cancellationToken = default)
        {
            var key = GetUserDeltaTokenKey();
            try
            {
                var usersQueryDelta = await ExecuteWithRetry(() => _store.GetStringAsync(key, cancellationToken), "read", cancellationToken);
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
                    _logger.LogWarning($"User delta token store read failed after {_retryOptions.MaxAttempts:N0} attempt(s); using the last committed in-process checkpoint for this WebJob process. The next successful read from the state store will resume normal persisted checkpoint use.");
                    return _lastKnownCommittedDeltaToken;
                }

                throw new DeltaTokenUnavailableException(
                    "User delta token store is unavailable and this process has no last committed checkpoint. Deferring user metadata/licence import rather than treating the outage as a cache miss and starting a full tenant crawl.",
                    ex);
            }
        }

        public async Task SetDeltaToken(string deltaToken, CancellationToken cancellationToken = default)
        {
            var key = GetUserDeltaTokenKey();
            _logger.LogInformation($"Setting persisted user delta token.");
            await ExecuteWithRetry(() => _store.SetStringAsync(key, deltaToken, cancellationToken: cancellationToken), "write", cancellationToken);
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
        /// Row key of this tenant's stored user delta token. Defined in <see cref="UserImportCheckpointKeys"/>,
        /// which the web portal's User import page also reads, so the two can never disagree about it.
        /// </summary>
        string GetUserDeltaTokenKey()
        {
            return UserImportCheckpointKeys.DeltaToken(_appConfig.TenantGUID);
        }
    }

    /// <summary>
    /// Remembers which <c>UserGroupsFilter</c> the stored <c>/users/delta</c> checkpoint was taken under, so the user
    /// import can tell that the filter has changed since - even after a restart, which changing it causes. See
    /// <see cref="UserImportCheckpointKeys.DeltaTokenUserScope"/>.
    /// </summary>
    public interface IUserImportScopeMarkerStore
    {
        /// <returns>The stored <see cref="UserGroupsFilterModel.Fingerprint"/>, or null when there is none.</returns>
        Task<string> GetFingerprintAsync();

        /// <summary>Records <paramref name="fingerprint"/>; an empty one (no filter) removes the record.</summary>
        Task SetFingerprintAsync(string fingerprint);
    }

    /// <summary>
    /// The record, kept in the runtime state store beside the delta token it describes (the
    /// <see cref="StatePartitions.UserImport"/> partition). Only needed where the token itself is persisted: an in-process
    /// token does not outlive the import that read it, so it can never be stale.
    /// </summary>
    public sealed class PersistedUserImportScopeMarkerStore : IUserImportScopeMarkerStore
    {
        private readonly IKeyValueStore _store;
        private readonly string _key;

        public PersistedUserImportScopeMarkerStore(IKeyValueStore store, Guid tenantId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _key = UserImportCheckpointKeys.DeltaTokenUserScope(tenantId);
        }

        public Task<string> GetFingerprintAsync() => _store.GetStringAsync(_key);

        public async Task SetFingerprintAsync(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                await _store.DeleteAsync(_key).ConfigureAwait(false);
            }
            else
            {
                await _store.SetStringAsync(_key, fingerprint).ConfigureAwait(false);
            }
        }
    }
}

