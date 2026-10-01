using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>
    /// Builds an Azure Table <see cref="TableClient"/> the way the solution authenticates to every Azure service that takes
    /// either a key or Entra ID: use the connection string as it is when it carries credentials; if the storage account
    /// denies them, retry with RBAC; and when it carries none, use RBAC directly. Shared by the runtime state store
    /// (<see cref="StateStore"/>), the audit-import blob checkpoint and the user-organisation change log.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item><description><b>Credentials in the connection string</b> - an <c>AccountKey</c> (what the installer writes) or a
    /// <c>SharedAccessSignature</c> - are used as they are.</description></item>
    /// <item><description><b>Access denied</b> - any HTTP 401/403, whatever the reason: shared-key access disabled on the
    /// account (<c>KeyBasedAuthenticationNotPermitted</c>, increasingly the default under enterprise governance policy), a
    /// rotated or wrong key (<c>AuthenticationFailed</c>), an expired SAS - is retried with RBAC. So are credentials that
    /// can't be parsed.</description></item>
    /// <item><description><b>No credentials in the connection string</b> (only <c>AccountName</c> or <c>TableEndpoint</c>):
    /// RBAC directly.</description></item>
    /// </list>
    /// RBAC means the runtime service principal, authenticated with its configured client certificate or client secret -
    /// never the host's managed identity. Anything that is not an access problem (DNS, timeouts, 5xx) is thrown as it is:
    /// other credentials would not help. The storage emulator (<c>UseDevelopmentStorage=true</c>) has no Entra ID, so it
    /// only ever uses the connection string.
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
        /// Builds a <see cref="TableClient"/> for <paramref name="tableName"/> and ensures the table exists: with the
        /// connection string's own credentials when it has them, falling back to the runtime service principal when the
        /// account denies them, and with the service principal directly when it has none. Throws when no usable
        /// authentication is available or the service can't be reached.
        /// </summary>
        /// <param name="purpose">What the table is for, e.g. "blob checkpoint table". Used in log and error messages only.</param>
        public static TableClient CreateAndEnsureTable(string storageConnectionString, string tableName,
            string tenantId, string clientId, string clientSecret, ILogger logger, string purpose,
            TableClientOptions clientOptions = null)
        {
            return CreateAndEnsureTableAsync(storageConnectionString, tableName, tenantId, clientId, clientSecret,
                    null, false, logger, purpose, CancellationToken.None, true, clientOptions, null)
                .GetAwaiter().GetResult();
        }

        /// <inheritdoc cref="CreateAndEnsureTable"/>
        public static Task<TableClient> CreateAndEnsureTableAsync(string storageConnectionString, string tableName,
            string tenantId, string clientId, string clientSecret, ILogger logger, string purpose,
            CancellationToken cancellationToken = default)
        {
            return CreateAndEnsureTableAsync(storageConnectionString, tableName, tenantId, clientId, clientSecret,
                null, false, logger, purpose, cancellationToken, false, null, null);
        }

        /// <summary>
        /// Opens a table with the runtime identity in <paramref name="config"/>, honoring either certificate or
        /// client-secret authentication when RBAC is needed.
        /// </summary>
        public static TableClient CreateAndEnsureTable(string storageConnectionString, string tableName,
            AppConfig config, ILogger logger, string purpose, TableClientOptions clientOptions = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var tenantId = config.TenantGUID == Guid.Empty ? null : config.TenantGUID.ToString();
            return CreateAndEnsureTableAsync(storageConnectionString, tableName, tenantId, config.ClientID,
                    config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate, logger, purpose,
                    CancellationToken.None, true, clientOptions, null)
                .GetAwaiter().GetResult();
        }

        /// <inheritdoc cref="CreateAndEnsureTable(string, string, AppConfig, ILogger, string, TableClientOptions)"/>
        public static Task<TableClient> CreateAndEnsureTableAsync(string storageConnectionString, string tableName,
            AppConfig config, ILogger logger, string purpose, CancellationToken cancellationToken = default)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var tenantId = config.TenantGUID == Guid.Empty ? null : config.TenantGUID.ToString();
            return CreateAndEnsureTableAsync(storageConnectionString, tableName, tenantId, config.ClientID,
                config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate, logger, purpose,
                cancellationToken, false, null, null);
        }

        /// <summary>
        /// <see cref="CreateAndEnsureTableAsync(string, string, string, string, string, ILogger, string, CancellationToken)"/>
        /// with the HTTP pipeline options and the RBAC credential supplied by the caller, so tests can see which credential
        /// each request carried without a network or a tenant. <c>null</c> means the production defaults.
        /// </summary>
        internal static async Task<TableClient> CreateAndEnsureTableAsync(string storageConnectionString, string tableName,
            string tenantId, string clientId, string clientSecret, string keyVaultUrl, bool useClientCertificate,
            ILogger logger, string purpose,
            CancellationToken cancellationToken, bool synchronous, TableClientOptions clientOptions,
            Func<string, string, string, string, bool, Task<TokenCredential>> createCredential)
        {
            if (string.IsNullOrWhiteSpace(storageConnectionString))
                throw new ArgumentException("A storage connection string is required.", nameof(storageConnectionString));
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("A table name is required.", nameof(tableName));

            purpose = string.IsNullOrWhiteSpace(purpose) ? $"table '{tableName}'" : purpose;

            // Azurite / the storage emulator has no Entra ID identity at all, so there is nothing to fall back to.
            if (IsDevelopmentStorage(storageConnectionString))
                return await CreateFromConnectionStringAsync(storageConnectionString, tableName, clientOptions, cancellationToken, synchronous).ConfigureAwait(false);

            var endpoint = GetTableEndpoint(storageConnectionString);
            var canUseRbac = endpoint != null
                && !string.IsNullOrWhiteSpace(tenantId)
                && !string.IsNullOrWhiteSpace(clientId)
                && (useClientCertificate
                    ? !string.IsNullOrWhiteSpace(keyVaultUrl)
                    : !string.IsNullOrWhiteSpace(clientSecret));

            if (HasCredentials(storageConnectionString))
            {
                try
                {
                    return await CreateFromConnectionStringAsync(storageConnectionString, tableName, clientOptions, cancellationToken, synchronous).ConfigureAwait(false);
                }
                catch (RequestFailedException ex) when (IsAccessDenied(ex) && canUseRbac)
                {
                    var denial = $"HTTP {ex.Status} {ex.ErrorCode ?? "UnknownError"}";
                    if (IsKeyAuthDisabled(ex))
                    {
                        logger?.LogInformation($"Azure Table storage ({purpose}): shared-key access is disabled on the storage account ({denial}); " +
                            "using RBAC/Entra ID with the runtime service principal.");
                    }
                    else
                    {
                        logger?.LogWarning($"Azure Table storage ({purpose}): the storage account denied the Storage connection string's credentials ({denial}); " +
                            "retrying with RBAC/Entra ID using the runtime service principal. If the account key was rotated, update the Storage connection string.");
                    }
                }
                catch (Exception ex) when (IsUnusableConnectionString(ex) && canUseRbac)
                {
                    // The type only: the message of a parse failure can quote the connection string, key included.
                    logger?.LogWarning($"Azure Table storage ({purpose}): the credentials in the Storage connection string can't be used ({ex.GetType().Name}); " +
                        "using RBAC/Entra ID with the runtime service principal.");
                }
            }

            if (!canUseRbac)
            {
                throw new InvalidOperationException(BuildNoRbacMessage(
                    endpoint, storageConnectionString, purpose, useClientCertificate));
            }

            var credential = createCredential != null
                ? await createCredential(tenantId, clientId, clientSecret, keyVaultUrl, useClientCertificate).ConfigureAwait(false)
                : await CreateRuntimeCredentialAsync(
                    tenantId, clientId, clientSecret, keyVaultUrl, useClientCertificate, logger).ConfigureAwait(false);
            var rbacClient = clientOptions == null
                ? new TableClient(endpoint, tableName, credential)
                : new TableClient(endpoint, tableName, credential, clientOptions);
            await EnsureTableAsync(rbacClient, cancellationToken, synchronous).ConfigureAwait(false);
            return rbacClient;
        }

        /// <summary>
        /// True when the storage account refused the request's credentials - HTTP 401 or 403, whatever the error code - which
        /// different credentials (RBAC) may get past.
        /// </summary>
        public static bool IsAccessDenied(RequestFailedException ex)
        {
            return ex != null && (ex.Status == 401 || ex.Status == 403);
        }

        /// <summary>True when the storage account rejected the request because account-key auth is turned off.</summary>
        public static bool IsKeyAuthDisabled(RequestFailedException ex)
        {
            if (!IsAccessDenied(ex)) return false;
            return ex.ErrorCode != null && KeyAuthDisabledCodes.Contains(ex.ErrorCode);
        }

        /// <summary>True when the connection string carries a usable shared key.</summary>
        public static bool HasAccountKey(string storageConnectionString)
        {
            var parts = Parse(storageConnectionString);
            return parts.TryGetValue("AccountKey", out var key) && !string.IsNullOrWhiteSpace(key);
        }

        /// <summary>
        /// True when the connection string carries credentials of its own - an <c>AccountKey</c> or a
        /// <c>SharedAccessSignature</c> - and so is tried as it is before RBAC.
        /// </summary>
        public static bool HasCredentials(string storageConnectionString)
        {
            if (HasAccountKey(storageConnectionString)) return true;

            var parts = Parse(storageConnectionString);
            return parts.TryGetValue("SharedAccessSignature", out var sas) && !string.IsNullOrWhiteSpace(sas);
        }

        /// <summary>
        /// Failures that mean the connection string itself can't be used - e.g. an <c>AccountKey</c> that is not base64 - as
        /// opposed to a service that refused it.
        /// </summary>
        private static bool IsUnusableConnectionString(Exception ex)
        {
            return ex is FormatException || ex is ArgumentException || ex is InvalidOperationException;
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
            TableClientOptions clientOptions, CancellationToken cancellationToken, bool synchronous)
        {
            var client = clientOptions == null
                ? new TableClient(storageConnectionString, tableName)
                : new TableClient(storageConnectionString, tableName, clientOptions);
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

        private static async Task<TokenCredential> CreateRuntimeCredentialAsync(
            string tenantId, string clientId, string clientSecret, string keyVaultUrl,
            bool useClientCertificate, ILogger logger)
        {
            if (!useClientCertificate)
            {
                return new ClientSecretCredential(tenantId, clientId, clientSecret);
            }

            var certificate = await AuthHelper.RetrieveKeyVaultCertificate(
                AuthHelper.CertificateName, keyVaultUrl, logger ?? AnalyticsLogger.ConsoleOnlyTracer()).ConfigureAwait(false);
            return new ClientCertificateCredential(tenantId, clientId, certificate);
        }

        private static string BuildNoRbacMessage(
            Uri endpoint, string storageConnectionString, string purpose, bool useClientCertificate)
        {
            if (!HasCredentials(storageConnectionString) && endpoint == null)
                return "The storage connection string contains neither credentials (an AccountKey or a SharedAccessSignature) nor an AccountName/TableEndpoint, " +
                       $"so neither the connection string nor RBAC can be used for the {purpose}.";

            var identitySettings = useClientCertificate
                ? "tenant id / client id / Key Vault URL for client-certificate authentication"
                : "tenant id / client id / client secret";
            return "The storage account can't be reached with the Storage connection string's own credentials and the runtime service principal " +
                   $"({identitySettings}) is not configured, so the {purpose} cannot " +
                   "be authenticated. Configure the runtime account, or give the Storage connection string working credentials.";
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
