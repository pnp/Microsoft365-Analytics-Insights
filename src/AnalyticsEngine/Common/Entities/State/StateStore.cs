using Azure.Data.Tables;
using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>
    /// Where the solution keeps its runtime state - the <see cref="TableName"/> table in the solution's own storage
    /// account (the <c>Storage</c> connection string) - and how to open it. This replaced Azure Cache for Redis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table is created on first use. Access follows the same rule as every service that takes either a key or Entra
    /// ID: the Storage connection string's own credentials (the account key the installer writes, or a SAS) are used as
    /// they are; if the account denies them - shared-key access disabled, a rotated key - the runtime service principal is
    /// used instead (RBAC, which the installer grants as <b>Storage Table Data Contributor</b>); and with no credentials in
    /// the connection string, RBAC directly. See <see cref="StorageTableClientFactory"/>. On a private-endpoint deployment
    /// it goes through the storage account's <c>table</c> private endpoint - the same path as the audit-import blob
    /// checkpoint table.
    /// </para>
    /// <para>
    /// Every kind of state has its own partition (<see cref="StatePartitions"/>), so an operator can find, audit or
    /// clear one kind without touching the others.
    /// </para>
    /// </remarks>
    public static class StateStore
    {
        /// <summary>The Azure Table that holds the runtime state.</summary>
        public const string TableName = "AnalyticsState";

        private const string Purpose = "runtime state table '" + TableName + "'";

        /// <summary>One opened table per storage account and identity, shared by every store in the process.</summary>
        private static readonly ConcurrentDictionary<string, LazyTableClient> Tables =
            new ConcurrentDictionary<string, LazyTableClient>(StringComparer.Ordinal);

        /// <summary>Whether a Storage connection string is configured. Without one, callers fall back to in-memory state.</summary>
        public static bool IsConfigured(AppConfig config) => !string.IsNullOrWhiteSpace(config?.ConnectionStrings?.StorageConnectionString);

        /// <summary>
        /// The durable store for <paramref name="partition"/>, or <c>null</c> when no Storage connection string is
        /// configured. Opening is lazy: nothing touches the network until the first read or write, and a failure to
        /// open is thrown from that operation (and retried by the next one), never from here.
        /// </summary>
        public static IKeyValueStore TryOpen(AppConfig config, string partition, ILogger logger = null)
        {
            if (string.IsNullOrEmpty(partition)) throw new ArgumentException("A partition is required.", nameof(partition));
            if (!IsConfigured(config)) return null;

            var connectionString = config.ConnectionStrings.StorageConnectionString;
            var tenantId = config.TenantGUID == Guid.Empty ? null : config.TenantGUID.ToString();
            var clientId = config.ClientID;
            var clientSecret = config.ClientSecret;

            var table = Tables.GetOrAdd(connectionString + "|" + clientId, _ => new LazyTableClient(ct =>
                StorageTableClientFactory.CreateAndEnsureTableAsync(connectionString, TableName, tenantId, clientId, clientSecret, logger, Purpose, ct)));

            return Open(table, partition);
        }

        /// <summary>
        /// The store for <paramref name="partition"/> over <paramref name="table"/>, which drops a client the storage account
        /// refuses so the next operation opens the table again.
        /// </summary>
        internal static AzureTableKeyValueStore Open(LazyTableClient table, string partition)
        {
            return new AzureTableKeyValueStore(table.GetAsync, TableName, partition, onAccessDenied: table.Invalidate);
        }

        /// <summary>
        /// Opens the table once and keeps the client. A failed open is not cached, so a storage blip at start-up heals on
        /// the next operation instead of pinning the process to a dead client. A client the storage account later refuses
        /// (<see cref="Invalidate"/>) is dropped too, so the next operation opens the table again with the same rule - the
        /// connection string's credentials first, RBAC when they are denied.
        /// </summary>
        internal sealed class LazyTableClient
        {
            private readonly Func<CancellationToken, Task<TableClient>> _open;
            private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
            private TableClient _client;

            public LazyTableClient(Func<CancellationToken, Task<TableClient>> open)
            {
                _open = open;
            }

            public async Task<TableClient> GetAsync(CancellationToken cancellationToken)
            {
                var client = Volatile.Read(ref _client);
                if (client != null) return client;

                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_client == null)
                    {
                        Volatile.Write(ref _client, await _open(cancellationToken).ConfigureAwait(false));
                    }
                    return _client;
                }
                finally
                {
                    _gate.Release();
                }
            }

            /// <summary>
            /// Forgets <paramref name="refused"/> if it is still the cached client: the storage account denied it access (a
            /// rotated key, shared-key access switched off, a role assignment removed), so the next operation opens the
            /// table again instead of failing until the process restarts. A client opened since is kept.
            /// </summary>
            public void Invalidate(TableClient refused)
            {
                if (refused != null)
                {
                    Interlocked.CompareExchange(ref _client, null, refused);
                }
            }
        }
    }

    /// <summary>
    /// The partitions of <see cref="StateStore.TableName"/>. Values are part of the stored data's address: renaming one
    /// orphans everything already stored under it.
    /// </summary>
    public static class StatePartitions
    {
        /// <summary>When each interval-gated import last ran (and when a failing usage report may next retry). Round-trip ("o") timestamps.</summary>
        public const string ImportSchedule = "ImportSchedule";

        /// <summary>The Graph user import's <c>/users/delta</c> checkpoint. See <see cref="UserImportCheckpointKeys"/>.</summary>
        public const string UserImport = "UserImport";

        /// <summary>Per-mailbox delta tokens for the sent-email import, plus its list of users with no mailbox.</summary>
        public const string SentEmails = "SentEmails";

        /// <summary>Per-channel delta tokens that make Teams channel-message reads incremental.</summary>
        public const string TeamsChannels = "TeamsChannels";

        /// <summary>
        /// The delegated refresh token authorised for each Team in the portal (Teams deep analytics). Secrets: protected
        /// by the storage account's access control, like every other secret the App Service holds.
        /// </summary>
        public const string TeamsAuth = "TeamsAuth";

        /// <summary>Azure AI Language results for Teams messages, kept for a day so the same text is not analysed twice.</summary>
        public const string CognitiveCache = "CognitiveCache";

        /// <summary>
        /// Purges of data about people outside <c>UserGroupsFilter</c>, from the portal's Administration &gt; User scope page:
        /// each purge's record (progress, counts, who started it), which purge is the latest, and stop requests. A finished
        /// purge's record expires after 90 days. Never the list of people a purge removes, which exists only while it runs.
        /// </summary>
        public const string UserScopePurge = "UserScopePurge";
    }
}
