using Azure;
using Azure.Data.Tables;
using Common.Entities.Config;
using Common.Entities.State;
using System;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models.UserOrgs
{
    /// <summary>
    /// Builds the <see cref="TableClient"/> behind the user organisation change log.
    /// </summary>
    /// <remarks>
    /// The same authentication order as the importer's blob checkpoint
    /// (<c>CheckpointTableClientFactory</c> in the importer engine, which the web app does not
    /// reference): shared key from the <c>Storage</c> connection string when it carries one; the runtime
    /// service principal, using its configured certificate or client secret, when the account has shared-key
    /// access disabled or the connection string has no key. That identity needs <b>Storage Table Data
    /// Contributor</b>; <c>Storage Blob Data Contributor</c> does not cover the Table service.
    /// Retries and timeouts are kept short: this runs on the first request that needs the log, and a
    /// storage account behind a firewall must fail in seconds, not minutes.
    /// </remarks>
    internal static class UserOrgChangeLogTableFactory
    {
        private const string Purpose = "user organisation change log table";

        public static TableClient CreateAndEnsureTable(
            string storageConnectionString,
            string tableName,
            string tenantId,
            string clientId,
            string clientSecret)
        {
            if (string.IsNullOrWhiteSpace(storageConnectionString))
            {
                throw new ArgumentException("A storage connection string is required.", nameof(storageConnectionString));
            }

            var options = new TableClientOptions();
            options.Retry.MaxRetries = 2;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
            return StorageTableClientFactory.CreateAndEnsureTable(
                storageConnectionString, tableName, tenantId, clientId, clientSecret, null, Purpose, options);
        }

        public static TableClient CreateAndEnsureTable(
            string storageConnectionString,
            string tableName,
            AppConfig config)
        {
            var options = new TableClientOptions();
            options.Retry.MaxRetries = 2;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
            return StorageTableClientFactory.CreateAndEnsureTable(
                storageConnectionString, tableName, config, null, Purpose, options);
        }

        internal static bool IsKeyAuthDisabled(RequestFailedException ex)
            => StorageTableClientFactory.IsKeyAuthDisabled(ex);

        /// <summary>The Table endpoint: an explicit <c>TableEndpoint</c>, or one composed from the account name and suffix.</summary>
        internal static Uri GetTableEndpoint(IDictionary<string, string> parts)
        {
            if (parts.TryGetValue("TableEndpoint", out var explicitEndpoint) && !string.IsNullOrWhiteSpace(explicitEndpoint))
            {
                return Uri.TryCreate(explicitEndpoint.Trim(), UriKind.Absolute, out var explicitUri) ? explicitUri : null;
            }

            if (!parts.TryGetValue("AccountName", out var account) || string.IsNullOrWhiteSpace(account))
            {
                return null;
            }

            var suffix = parts.TryGetValue("EndpointSuffix", out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : "core.windows.net";
            var scheme = parts.TryGetValue("DefaultEndpointsProtocol", out var p) && !string.IsNullOrWhiteSpace(p) ? p.Trim() : "https";
            return Uri.TryCreate($"{scheme}://{account.Trim()}.table.{suffix}", UriKind.Absolute, out var uri) ? uri : null;
        }

        /// <summary>
        /// Splits a storage connection string. Only the FIRST <c>=</c> of a segment separates the key: an
        /// account key is base64 and routinely ends in <c>=</c> padding.
        /// </summary>
        internal static IDictionary<string, string> Parse(string storageConnectionString)
        {
            var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in (storageConnectionString ?? string.Empty).Split(';'))
            {
                var separator = segment.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = segment.Substring(0, separator).Trim();
                if (key.Length > 0 && !parts.ContainsKey(key))
                {
                    parts[key] = segment.Substring(separator + 1).Trim();
                }
            }

            return parts;
        }

    }
}
