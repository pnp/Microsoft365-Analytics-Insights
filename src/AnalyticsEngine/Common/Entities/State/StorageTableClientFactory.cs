using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>
    /// Builds an Azure Table <see cref="TableClient"/> that works against both classic shared-key storage accounts and
    /// accounts with shared-key access disabled (<c>allowSharedKeyAccess = false</c>), which require RBAC / Entra ID.
    /// Shared by the runtime state store (<see cref="StateStore"/>) and the audit-import blob checkpoint.
    /// </summary>
    /// <remarks>
    /// A storage account hardened with <c>allowSharedKeyAccess = false</c> - increasingly the default under enterprise
    /// governance policy and the Azure security baseline - rejects a connection-string (shared key) client with
    /// <c>403 KeyBasedAuthenticationNotPermitted</c>. Per the repo convention for Entra ID fallbacks (Azure AI Language,
    /// Service Bus, Azure SQL) we then authenticate with a <see cref="ClientSecretCredential"/> built from the runtime
    /// service principal - never <c>DefaultAzureCredential</c> or managed identity - so behaviour is identical in the
    /// web-jobs, the web app, the installer tests and unit tests.
    /// <para>
    /// Data-plane RBAC on the Table service needs the <b>Storage Table Data Contributor</b> role;
    /// <c>Storage Blob Data Contributor</c> does NOT cover Table storage. The installer assigns it in
    /// <c>ResourceSecurityInstallJob</c>.
    /// </para>
    /// </remarks>
    public static class StorageTableClientFactory
    {
        /// <summary>Error codes a storage account returns when shared-key (account key) auth is switched off.</summary>
        private static readonly HashSet<string> KeyAuthDisabledCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KeyBasedAuthenticationNotPermitted",
            "AuthenticationTypeDisabled",
        };

        /// <summary>
        /// Builds a <see cref="TableClient"/> for <paramref name="tableName"/> and ensures the table exists. Shared key is
        /// preferred when the connection string carries an <c>AccountKey</c>; if the account rejects it because key auth
        /// is disabled, the call is retried with the runtime service principal. Throws when no usable authentication is
        /// available or the service can't be reached.
        /// </summary>
        /// <param name="purpose">What the table is for, e.g. "blob checkpoint table". Used in log and error messages only.</param>
        public static TableClient CreateAndEnsureTable(string storageConnectionString, string tableName,
            string tenantId, string clientId, string clientSecret, ILogger logger, string purpose)
        {
            return CreateAndEnsureTableAsync(storageConnectionString, tableName, tenantId, clientId, clientSecret, logger, purpose,
                    CancellationToken.None, synchronous: true)
                .GetAwaiter().GetResult();
        }

        /// <inheritdoc cref="CreateAndEnsureTable"/>
        public static Task<TableClient> CreateAndEnsureTableAsync(string storageConnectionString, string tableName,
            string tenantId, string clientId, string clientSecret, ILogger logger, string purpose,
            CancellationToken cancellationToken = default)
        {
            return CreateAndEnsureTableAsync(storageConnectionString, tableName, tenantId, clientId, clientSecret, logger, purpose,
                cancellationToken, synchronous: false);
        }

        private static async Task<TableClient> CreateAndEnsureTableAsync(string storageConnectionString, string tableName,
            string tenantId, string clientId, string clientSecret, ILogger logger, string purpose,
            CancellationToken cancellationToken, bool synchronous)
        {
            if (string.IsNullOrWhiteSpace(storageConnectionString))
                throw new ArgumentException("A storage connection string is required.", nameof(storageConnectionString));
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("A table name is required.", nameof(tableName));

            purpose = string.IsNullOrWhiteSpace(purpose) ? $"table '{tableName}'" : purpose;

            // Azurite / the storage emulator has no Entra ID identity at all, so there is nothing to fall back to.
            if (IsDevelopmentStorage(storageConnectionString))
                return await CreateFromConnectionStringAsync(storageConnectionString, tableName, cancellationToken, synchronous).ConfigureAwait(false);

            var endpoint = GetTableEndpoint(storageConnectionString);
            var canUseRbac = endpoint != null
                && !string.IsNullOrWhiteSpace(tenantId)
                && !string.IsNullOrWhiteSpace(clientId)
                && !string.IsNullOrWhiteSpace(clientSecret);

            if (HasAccountKey(storageConnectionString))
            {
                try
                {
                    return await CreateFromConnectionStringAsync(storageConnectionString, tableName, cancellationToken, synchronous).ConfigureAwait(false);
                }
                catch (RequestFailedException ex) when (IsKeyAuthDisabled(ex) && canUseRbac)
                {
                    logger?.LogInformation($"Azure Table storage ({purpose}): shared-key access is disabled on the storage account ({ex.ErrorCode}); " +
                        "retrying with RBAC/Entra ID using the runtime service principal.");
                }
            }

            if (!canUseRbac)
            {
                throw new InvalidOperationException(BuildNoRbacMessage(endpoint, storageConnectionString, purpose));
            }

            var rbacClient = new TableClient(endpoint, tableName, new ClientSecretCredential(tenantId, clientId, clientSecret));
            await EnsureTableAsync(rbacClient, cancellationToken, synchronous).ConfigureAwait(false);
            return rbacClient;
        }

        /// <summary>True when the storage account rejected the request because account-key auth is turned off.</summary>
        public static bool IsKeyAuthDisabled(RequestFailedException ex)
        {
            if (ex == null) return false;
            if (ex.Status != 401 && ex.Status != 403) return false;
            return ex.ErrorCode != null && KeyAuthDisabledCodes.Contains(ex.ErrorCode);
        }

        /// <summary>True when the connection string carries a usable shared key.</summary>
        public static bool HasAccountKey(string storageConnectionString)
        {
            var parts = Parse(storageConnectionString);
            return parts.TryGetValue("AccountKey", out var key) && !string.IsNullOrWhiteSpace(key);
        }

        /// <summary>True for the local storage emulator, which has no Entra ID identity.</summary>
        public static bool IsDevelopmentStorage(string storageConnectionString)
        {
            var parts = Parse(storageConnectionString);
            return parts.TryGetValue("UseDevelopmentStorage", out var v)
                && bool.TryParse(v, out var useDev) && useDev;
        }

        /// <summary>
        /// The storage account name, for display: the connection string's <c>AccountName</c>, else the host of an explicit
        /// <c>TableEndpoint</c>, else <c>devstoreaccount1</c> for the emulator. Null when none can be found. Never returns
        /// the key or any other secret part of the connection string.
        /// </summary>
        public static string GetAccountName(string storageConnectionString)
        {
            var parts = Parse(storageConnectionString);
            if (parts.TryGetValue("AccountName", out var account) && !string.IsNullOrWhiteSpace(account))
                return account.Trim();

            if (IsDevelopmentStorage(storageConnectionString))
                return "devstoreaccount1";

            var endpoint = GetTableEndpoint(storageConnectionString);
            return endpoint?.Host;
        }

        /// <summary>
        /// Resolves the Table service endpoint a token-authenticated client must target. Prefers an explicit
        /// <c>TableEndpoint</c>, otherwise composes it from <c>AccountName</c> + <c>EndpointSuffix</c> (which differs in
        /// sovereign clouds). Returns <c>null</c> when the connection string names no account.
        /// </summary>
        public static Uri GetTableEndpoint(string storageConnectionString)
        {
            var parts = Parse(storageConnectionString);

            if (parts.TryGetValue("TableEndpoint", out var explicitEndpoint) && !string.IsNullOrWhiteSpace(explicitEndpoint))
                return Uri.TryCreate(explicitEndpoint.Trim(), UriKind.Absolute, out var explicitUri) ? explicitUri : null;

            if (!parts.TryGetValue("AccountName", out var account) || string.IsNullOrWhiteSpace(account))
                return null;

            var suffix = parts.TryGetValue("EndpointSuffix", out var s) && !string.IsNullOrWhiteSpace(s)
                ? s.Trim() : "core.windows.net";
            var scheme = parts.TryGetValue("DefaultEndpointsProtocol", out var p) && !string.IsNullOrWhiteSpace(p)
                ? p.Trim() : "https";

            return Uri.TryCreate($"{scheme}://{account.Trim()}.table.{suffix}", UriKind.Absolute, out var uri) ? uri : null;
        }

        private static async Task<TableClient> CreateFromConnectionStringAsync(string storageConnectionString, string tableName,
            CancellationToken cancellationToken, bool synchronous)
        {
            var client = new TableClient(storageConnectionString, tableName);
            await EnsureTableAsync(client, cancellationToken, synchronous).ConfigureAwait(false);
            return client;
        }

        private static async Task EnsureTableAsync(TableClient client, CancellationToken cancellationToken, bool synchronous)
        {
            if (synchronous)
            {
                client.CreateIfNotExists(cancellationToken);
            }
            else
            {
                await client.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static string BuildNoRbacMessage(Uri endpoint, string storageConnectionString, string purpose)
        {
            if (!HasAccountKey(storageConnectionString) && endpoint == null)
                return "The storage connection string contains neither an AccountKey nor an AccountName/TableEndpoint, " +
                       $"so neither shared-key nor RBAC authentication can be used for the {purpose}.";

            return "Shared-key access is unavailable on the storage account and the runtime service principal " +
                   $"(tenant id / client id / client secret) is not configured, so the {purpose} cannot " +
                   "be authenticated. Configure the runtime account, or re-enable shared-key access on the account.";
        }

        /// <summary>
        /// Splits a storage connection string into its key/value parts. Only the FIRST '=' is treated as the separator
        /// because an <c>AccountKey</c> is base64 and routinely ends in '=' padding.
        /// </summary>
        private static IDictionary<string, string> Parse(string storageConnectionString)
        {
            var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(storageConnectionString)) return parts;

            foreach (var segment in storageConnectionString.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(segment)) continue;

                var separator = segment.IndexOf('=');
                if (separator <= 0) continue;

                var key = segment.Substring(0, separator).Trim();
                var value = segment.Substring(separator + 1).Trim();
                if (key.Length > 0 && !parts.ContainsKey(key)) parts[key] = value;
            }

            return parts;
        }
    }
}
