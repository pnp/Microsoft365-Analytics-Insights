using Azure;
using Azure.Data.Tables;
using Azure.Identity;
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
    /// service principal (<see cref="ClientSecretCredential"/>) when the account has shared-key access
    /// disabled or the connection string has no key. That identity needs <b>Storage Table Data
    /// Contributor</b>; <c>Storage Blob Data Contributor</c> does not cover the Table service.
    /// Retries and timeouts are kept short: this runs on the first request that needs the log, and a
    /// storage account behind a firewall must fail in seconds, not minutes.
    /// </remarks>
    internal static class UserOrgChangeLogTableFactory
    {
        private static readonly HashSet<string> KeyAuthDisabledCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KeyBasedAuthenticationNotPermitted",
            "AuthenticationTypeDisabled",
        };

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

            var parts = Parse(storageConnectionString);
            bool useDevelopment;
            if (parts.TryGetValue("UseDevelopmentStorage", out var dev) && bool.TryParse(dev, out useDevelopment) && useDevelopment)
            {
                return Ensure(new TableClient(storageConnectionString, tableName, options));
            }

            var endpoint = GetTableEndpoint(parts);
            var canUseRbac = endpoint != null
                && !string.IsNullOrWhiteSpace(tenantId)
                && !string.IsNullOrWhiteSpace(clientId)
                && !string.IsNullOrWhiteSpace(clientSecret);

            if (HasValue(parts, "AccountKey") || HasValue(parts, "SharedAccessSignature"))
            {
                try
                {
                    return Ensure(new TableClient(storageConnectionString, tableName, options));
                }
                catch (RequestFailedException ex) when (IsKeyAuthDisabled(ex) && canUseRbac)
                {
                    // Fall through to the service principal.
                }
            }

            if (!canUseRbac)
            {
                throw new InvalidOperationException(
                    "Shared-key access is unavailable for the storage account and the runtime service principal is not "
                    + "configured, so the change log table cannot be authenticated.");
            }

            return Ensure(new TableClient(endpoint, tableName, new ClientSecretCredential(tenantId, clientId, clientSecret), options));
        }

        internal static bool IsKeyAuthDisabled(RequestFailedException ex)
        {
            return ex != null && (ex.Status == 401 || ex.Status == 403)
                && ex.ErrorCode != null && KeyAuthDisabledCodes.Contains(ex.ErrorCode);
        }

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

        private static bool HasValue(IDictionary<string, string> parts, string key)
        {
            return parts.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
        }

        private static TableClient Ensure(TableClient client)
        {
            client.CreateIfNotExists();
            return client;
        }
    }
}
