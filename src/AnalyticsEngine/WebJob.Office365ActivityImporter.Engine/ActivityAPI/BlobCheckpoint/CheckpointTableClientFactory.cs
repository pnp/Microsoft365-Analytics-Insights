using Azure;
using Azure.Data.Tables;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using System;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint
{
    /// <summary>
    /// Builds the <see cref="TableClient"/> behind <see cref="AzureTableProcessedBlobStore"/> so the blob
    /// checkpoint works against both classic shared-key storage accounts and accounts that have shared-key
    /// access disabled (<c>allowSharedKeyAccess = false</c>) and therefore require RBAC / Entra ID tokens.
    /// </summary>
    /// <remarks>
    /// A thin facade over <see cref="StorageTableClientFactory"/>, which the runtime state table
    /// (<see cref="StateStore"/>) shares, so both tables authenticate the same way: shared key when the account allows
    /// it, otherwise the runtime service principal via <see cref="Azure.Identity.ClientSecretCredential"/> - never
    /// <c>DefaultAzureCredential</c> or managed identity.
    /// <para>
    /// Data-plane RBAC on the Table service needs the <b>Storage Table Data Contributor</b> role;
    /// <c>Storage Blob Data Contributor</c> does NOT cover Table storage. The installer assigns it in
    /// <c>ResourceSecurityInstallJob</c>.
    /// </para>
    /// </remarks>
    public static class CheckpointTableClientFactory
    {
        private const string Purpose = "blob checkpoint table";

        /// <summary>
        /// Builds a <see cref="TableClient"/> for <paramref name="tableName"/> and ensures the table exists.
        /// Shared key is preferred when the connection string carries an <c>AccountKey</c>; if the account
        /// rejects it because key auth is disabled, the call is retried with the runtime service principal.
        /// Throws when no usable authentication is available, so the caller can fall back to the in-memory store.
        /// </summary>
        public static TableClient CreateAndEnsureTable(string storageConnectionString, string tableName,
            string tenantId, string clientId, string clientSecret, ILogger logger)
            => StorageTableClientFactory.CreateAndEnsureTable(storageConnectionString, tableName, tenantId, clientId, clientSecret, logger, Purpose);

        /// <summary>True when the storage account rejected the request because account-key auth is turned off.</summary>
        public static bool IsKeyAuthDisabled(RequestFailedException ex) => StorageTableClientFactory.IsKeyAuthDisabled(ex);

        /// <summary>True when the connection string carries a usable shared key.</summary>
        public static bool HasAccountKey(string storageConnectionString) => StorageTableClientFactory.HasAccountKey(storageConnectionString);

        /// <summary>True for the local storage emulator, which has no Entra ID identity.</summary>
        public static bool IsDevelopmentStorage(string storageConnectionString) => StorageTableClientFactory.IsDevelopmentStorage(storageConnectionString);

        /// <summary>
        /// Resolves the Table service endpoint a token-authenticated client must target. Prefers an explicit
        /// <c>TableEndpoint</c>, otherwise composes it from <c>AccountName</c> + <c>EndpointSuffix</c> (which
        /// differs in sovereign clouds). Returns <c>null</c> when the connection string names no account.
        /// </summary>
        public static Uri GetTableEndpoint(string storageConnectionString) => StorageTableClientFactory.GetTableEndpoint(storageConnectionString);
    }
}