using Common.Entities;
using Common.Entities.Config;
using Common.Entities.State;
using Common.Entities.UserOrgs;
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
    /// With Entra-sourced user organisation types configured, the importer keeps its token under a key qualified by
    /// them (<see cref="GraphUserOrgSelection.DeltaKeyQualifier"/>), and resumes from the unqualified key when Graph
    /// rejects them. The page works the qualifier out from the same enabled types, reports a checkpoint when either
    /// key holds one, and clears both.
    /// </para>
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
        private readonly Func<Task<string>> _loadOrgKeyQualifier;

        /// <param name="openStore">Opens the state store; null when no Storage connection string is configured. May throw when storage can't be reached.</param>
        /// <param name="loadOrgKeyQualifier">
        /// The qualifier the importer adds to the checkpoint key for the enabled Entra organisation types; null when there
        /// can be none. May throw, which is taken as "no qualifier", as the importer takes it.
        /// </param>
        internal UserImportCheckpointService(Guid tenantId, Func<IUserImportCheckpointStore> openStore, ImportTaskSettings importSettings, int intervalHours, ILogger logger,
            Func<Task<string>> loadOrgKeyQualifier = null)
        {
            _tenantId = tenantId;
            _openStore = openStore;
            _importSettings = importSettings;
            _intervalHours = intervalHours;
            _logger = logger;
            _loadOrgKeyQualifier = loadOrgKeyQualifier;
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

            var sqlConnectionString = config.ConnectionStrings.SQL;
            return new UserImportCheckpointService(config.TenantGUID, openStore, config.ImportJobSettings,
                config.GraphMetadataImportIntervalHours, ProductionLogger.Value,
                async () => GraphUserOrgSelection.FromTypes(
                    await UserOrgStores.CreateTypeStore(sqlConnectionString).GetEnabledEntraTypesAsync()).DeltaKeyQualifier);
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

            var keys = await ResolveCheckpointKeysAsync();
            status.CheckpointKey = keys[0];

            await WithStore("read", async store =>
            {
                foreach (var key in keys)
                {
                    if (await store.KeyExistsAsync(key))
                    {
                        status.CheckpointStored = true;
                        break;
                    }
                }

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

            var keys = await ResolveCheckpointKeysAsync();
            var result = new UserImportCheckpointClearResult();
            await WithStore("clear", async store =>
            {
                // Every key, not only until one is found: each is a checkpoint a later run could resume from.
                foreach (var key in keys)
                {
                    if (await store.DeleteKeyAsync(key))
                    {
                        result.CheckpointCleared = true;
                    }
                }

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
        /// The keys the importer's next run can resume from: first its checkpoint key, qualified by the enabled Entra
        /// organisation types exactly as the importer qualifies it, then - only when that is qualified - the unqualified
        /// key its without-organisations fallback resumes from.
        /// </summary>
        /// <remarks>
        /// Looking only at the unqualified key would miss the token entirely on a tenant with Entra organisation types:
        /// the page would say nothing is stored, and a clear would not make the next run read every user. Organisation
        /// types that cannot be read - a database not yet upgraded has no such tables - mean no qualifier, which is how
        /// the importer treats the same failure.
        /// </remarks>
        private async Task<string[]> ResolveCheckpointKeysAsync()
        {
            var qualifier = string.Empty;
            if (_loadOrgKeyQualifier != null)
            {
                try
                {
                    qualifier = await _loadOrgKeyQualifier() ?? string.Empty;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger?.LogWarning($"User import checkpoint - couldn't read the configured user organisation types ({ex.GetType().Name}), so the checkpoint key without them is used.");
                }
            }

            var checkpoint = UserImportCheckpointKeys.DeltaToken(_tenantId, qualifier);
            var unqualified = UserImportCheckpointKeys.DeltaToken(_tenantId);
            return string.Equals(checkpoint, unqualified, StringComparison.Ordinal)
                ? new[] { checkpoint }
                : new[] { checkpoint, unqualified };
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
