using Common.Entities;
using Common.Entities.Config;
using Common.Entities.State;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserImport
{
    /// <summary>The storage operations the User import page needs, as a port so its rules can be tested without Azure Storage.</summary>
    internal interface IUserImportCheckpointStore
    {
        Task<bool> KeyExistsAsync(string key);

        Task<string> GetStringAsync(string key);

        /// <returns>True when the key existed and was deleted.</returns>
        Task<bool> DeleteKeyAsync(string key);
    }

    /// <summary>
    /// The User import page's keys in the runtime state table: the delta token in the
    /// <see cref="StatePartitions.UserImport"/> partition, the last-completed stamp in
    /// <see cref="StatePartitions.ImportSchedule"/> - the same partitions the importer writes them to.
    /// </summary>
    internal sealed class StateTableUserImportCheckpointStore : IUserImportCheckpointStore
    {
        private readonly IKeyValueStore _checkpoints;
        private readonly IKeyValueStore _schedule;

        internal StateTableUserImportCheckpointStore(IKeyValueStore checkpoints, IKeyValueStore schedule)
        {
            _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
            _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        }

        public Task<bool> KeyExistsAsync(string key) => StoreFor(key).ExistsAsync(key);

        public Task<string> GetStringAsync(string key) => StoreFor(key).GetStringAsync(key);

        public Task<bool> DeleteKeyAsync(string key) => StoreFor(key).DeleteAsync(key);

        private IKeyValueStore StoreFor(string key)
            => string.Equals(key, UserImportCheckpointKeys.LastCompleted, StringComparison.Ordinal) ? _schedule : _checkpoints;
    }

    /// <summary>Storage is configured but could not be reached, or failed the request.</summary>
    public sealed class UserImportCheckpointUnavailableException : Exception
    {
        public UserImportCheckpointUnavailableException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// What the portal's Administration &gt; User import page shows and does: whether the Graph user import has a
    /// stored checkpoint (its <c>/users/delta</c> token), and clearing it so the next run reads every user again.
    /// </summary>
    /// <remarks>
    /// This is the in-product version of the manual workaround for issue #664 - deleting the stored token by hand.
    /// The key names come from <see cref="UserImportCheckpointKeys"/>, the same definitions the importer uses, so
    /// the page cannot clear a key the importer no longer reads. The token itself is never read, only tested for
    /// existence, so it cannot reach the browser or a log.
    ///
    /// <para>
    /// A clear that lands while a user import is running is not undone by that run: the importer checks the
    /// checkpoint is still there before saving a new one (<c>GraphUserLoader.CommitDeltaTokenAsync</c>).
    /// </para>
    /// </remarks>
    internal sealed class UserImportCheckpointService
    {
        private const string LogContext = "UserImportCheckpoint";

        private static readonly Lazy<AnalyticsLogger> ProductionLogger = new Lazy<AnalyticsLogger>(
            () => new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, LogContext));

        private readonly Guid _tenantId;
        private readonly Func<IUserImportCheckpointStore> _openStore;
        private readonly ImportTaskSettings _importSettings;
        private readonly int _intervalHours;
        private readonly ILogger _logger;

        /// <param name="openStore">Opens the state store; null when no Storage connection string is configured. May throw when storage can't be reached.</param>
        internal UserImportCheckpointService(Guid tenantId, Func<IUserImportCheckpointStore> openStore, ImportTaskSettings importSettings, int intervalHours, ILogger logger)
        {
            _tenantId = tenantId;
            _openStore = openStore;
            _importSettings = importSettings;
            _intervalHours = intervalHours;
            _logger = logger;
        }

        internal bool StorageConfigured => _openStore != null;

        /// <summary>The service for this deployment, from the same app settings the importer reads.</summary>
        internal static UserImportCheckpointService ForThisDeployment()
        {
            var config = new AppConfig();

            Func<IUserImportCheckpointStore> openStore = null;
            if (StateStore.IsConfigured(config))
            {
                openStore = () => new StateTableUserImportCheckpointStore(
                    StateStore.TryOpen(config, StatePartitions.UserImport),
                    StateStore.TryOpen(config, StatePartitions.ImportSchedule));
            }

            return new UserImportCheckpointService(config.TenantGUID, openStore, config.ImportJobSettings,
                config.GraphMetadataImportIntervalHours, ProductionLogger.Value);
        }

        internal async Task<UserImportCheckpointStatus> GetStatusAsync()
        {
            var status = new UserImportCheckpointStatus
            {
                StorageConfigured = StorageConfigured,
                UserImportEnabled = _importSettings?.GraphUsersMetadata,
                CheckpointTable = StateStore.TableName,
                CheckpointPartition = StatePartitions.UserImport,
                CheckpointKey = UserImportCheckpointKeys.DeltaToken(_tenantId),
                IntervalHours = _intervalHours,
            };

            if (!StorageConfigured)
            {
                return status;
            }

            await WithStore("read", async store =>
            {
                status.CheckpointStored = await store.KeyExistsAsync(status.CheckpointKey);
                status.LastCompletedUtc = ParseLastCompleted(await store.GetStringAsync(UserImportCheckpointKeys.LastCompleted));
            });

            return status;
        }

        /// <summary>
        /// Deletes the stored checkpoint, so the next user import reads every user. With
        /// <paramref name="runOnNextCycle"/>, also deletes the last-completed stamp so that run happens on the next
        /// import cycle rather than once the interval has passed.
        /// </summary>
        internal async Task<UserImportCheckpointClearResult> ClearAsync(bool runOnNextCycle)
        {
            if (!StorageConfigured)
            {
                throw new InvalidOperationException("Azure Storage is not configured, so the user import never stores a checkpoint and there is nothing to clear.");
            }

            var result = new UserImportCheckpointClearResult();
            await WithStore("clear", async store =>
            {
                result.CheckpointCleared = await store.DeleteKeyAsync(UserImportCheckpointKeys.DeltaToken(_tenantId));
                if (runOnNextCycle)
                {
                    result.LastCompletedCleared = await store.DeleteKeyAsync(UserImportCheckpointKeys.LastCompleted);
                }
            });

            var checkpoint = result.CheckpointCleared
                ? "the stored /users/delta checkpoint was deleted"
                : "there was no stored /users/delta checkpoint to delete";
            var schedule = !runOnNextCycle
                ? "it runs when its interval next allows"
                : result.LastCompletedCleared
                    ? "the last-completed stamp was deleted too, so it runs on the next import cycle"
                    : "no last-completed stamp was recorded, so it is already due on the next import cycle";
            _logger?.LogWarning($"User import - checkpoint cleared from the web portal (Administration > User import): {checkpoint}. The next user import reads the full user list; {schedule}.");

            return result;
        }

        private async Task WithStore(string operation, Func<IUserImportCheckpointStore, Task> action)
        {
            try
            {
                await action(_openStore());
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.LogError(ex, $"User import checkpoint - couldn't {operation} it in Azure Table storage ('{StateStore.TableName}' table): {ex.Message}");
                throw new UserImportCheckpointUnavailableException($"Couldn't {operation} the user import checkpoint in Azure Table storage.", ex);
            }
        }

        /// <summary>
        /// Reads the importer's last-completed stamp with the same rules as its own reader
        /// (<c>PersistedImportLastRunStore.GetLastRunUtc</c>): round-trip format, returned as UTC; anything else is "not recorded".
        /// </summary>
        internal static DateTime? ParseLastCompleted(string raw)
        {
            if (!string.IsNullOrEmpty(raw)
                && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            return null;
        }
    }
}
