using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure
{
    /// <summary>
    /// Works out the installer host's public IPv4 address, for writing an Azure SQL firewall rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every source here answers a slightly different question, and the differences matter. The most reliable
    /// answer of all is not in this class: it is the address Azure SQL itself reports in its
    /// <c>40615</c> rejection message, which is ground truth for "what does THIS server see me as". That is
    /// used by the self-heal path; this class exists for the up-front, pre-failure check.
    /// </para>
    /// <para>
    /// Sources, in preference order:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Key Vault's <c>x-ms-keyvault-network-info</c> response header</b> - first-party, and returned on the
    /// <c>401</c> of an UNAUTHENTICATED request, so it needs no token, no data-plane permission and no vault
    /// firewall access. Only usable once the vault exists (it is created after the SQL firewall task on a
    /// first install), hence the fallback below.
    /// </description></item>
    /// <item><description>
    /// <b>An HTTPS echo service</b> - last resort. It reports the egress IP of the connection to the echo
    /// service, which is not necessarily the egress IP Azure SQL sees: split-tunnel VPNs, proxies and
    /// multi-address NAT pools can all make them differ, and a NAT pool can hand out a different address per
    /// connection. It can therefore be confidently, silently wrong.
    /// </description></item>
    /// </list>
    /// <para>
    /// The previous implementation used <c>new WebClient().DownloadString("http://icanhazip.com")</c>: plain
    /// HTTP, no timeout, no exception handling, and validated with an unanchored regex so any IP-shaped text
    /// in a captive-portal or error page passed and got written into a customer's SQL firewall.
    /// </para>
    /// </remarks>
    public static class PublicIpResolver
    {
        /// <summary>Header Key Vault returns describing the caller, e.g. <c>conn_type=Ipv4;addr=203.0.113.10;...</c>.</summary>
        internal const string KeyVaultNetworkInfoHeader = "x-ms-keyvault-network-info";

        /// <summary>HTTPS, not HTTP: this value decides what goes into a customer's SQL firewall.</summary>
        internal const string EchoServiceUrl = "https://icanhazip.com";

        /// <summary>
        /// Bounded so a hung or black-holed endpoint cannot stall the install. The old code had no timeout at
        /// all, so an unreachable echo service hung the installer indefinitely.
        /// </summary>
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Parses the caller IP out of Key Vault's <c>x-ms-keyvault-network-info</c> header value.
        /// Returns null when the header is absent, malformed, or reports a non-IPv4 address.
        /// </summary>
        internal static string ParseKeyVaultNetworkInfo(string headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return null;

            foreach (var part in headerValue.Split(';'))
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;

                if (!kv[0].Trim().Equals("addr", StringComparison.OrdinalIgnoreCase)) continue;

                var candidate = kv[1].Trim();
                return SqlFirewallRules.IsValidIPv4(candidate) ? candidate : null;
            }

            return null;
        }

        /// <summary>
        /// Parses an echo-service body into an IPv4 address, or null.
        /// </summary>
        /// <remarks>
        /// Validates the WHOLE trimmed body rather than searching it. The old unanchored
        /// <c>\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b</c> with <c>IsMatch</c> matched a SUBSTRING, so an HTML
        /// error page or captive-portal response containing anything IP-shaped passed validation.
        /// </remarks>
        internal static string ParseEchoServiceBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            var trimmed = body.Trim().Trim('\r', '\n').Trim();

            // A body carrying anything other than the address alone is not an answer we should trust.
            return SqlFirewallRules.IsValidIPv4(trimmed) ? trimmed : null;
        }

        /// <summary>
        /// Best-effort public IPv4 of this host, or null if it could not be determined. Never throws.
        /// </summary>
        /// <param name="keyVaultName">
        /// Configured Key Vault name, enabling the first-party source. Null/empty (or a vault that does not
        /// exist yet) simply falls through to the echo service.
        /// </param>
        public static async Task<string> TryGetPublicIPv4Async(string keyVaultName, ILogger logger)
        {
            var fromVault = await TryGetFromKeyVaultAsync(keyVaultName, logger);
            if (fromVault != null)
            {
                logger?.LogInformation($"Detected this host's public IP address as '{fromVault}' (reported by Azure Key Vault).");
                return fromVault;
            }

            var fromEcho = await TryGetFromEchoServiceAsync(logger);
            if (fromEcho != null)
            {
                logger?.LogInformation(
                    $"Detected this host's public IP address as '{fromEcho}' (reported by {EchoServiceUrl}). " +
                    "Note this is the address seen by that service, which behind a proxy or split-tunnel VPN may differ " +
                    "from the address Azure SQL sees.");
                return fromEcho;
            }

            logger?.LogWarning(
                "Could not determine this host's public IP address, so the Azure SQL firewall rule cannot be checked or " +
                "repaired up-front. If the database step then fails with 'Client with IP address ... is not allowed to " +
                "access the server', the installer will read the address from that error and repair the rule automatically.");
            return null;
        }

        private static async Task<string> TryGetFromKeyVaultAsync(string keyVaultName, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(keyVaultName)) return null;

            // Deliberately unauthenticated: the header comes back on the 401, so this needs no credential and
            // works even when the vault's own firewall would reject us.
            var url = $"https://{keyVaultName.Trim()}.vault.azure.net/secrets/installer-public-ip-probe?api-version=7.4";

            try
            {
                using (var http = new HttpClient { Timeout = RequestTimeout })
                using (var response = await http.GetAsync(url))
                {
                    if (response.Headers.TryGetValues(KeyVaultNetworkInfoHeader, out var values))
                    {
                        foreach (var value in values)
                        {
                            var parsed = ParseKeyVaultNetworkInfo(value);
                            if (parsed != null) return parsed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Expected on a first install (the vault does not exist yet) - not worth alarming about.
                logger?.LogDebug($"Could not read the caller IP from Key Vault '{keyVaultName}': {ex.Message}");
            }

            return null;
        }

        private static async Task<string> TryGetFromEchoServiceAsync(ILogger logger)
        {
            try
            {
                using (var http = new HttpClient { Timeout = RequestTimeout })
                using (var response = await http.GetAsync(EchoServiceUrl))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        logger?.LogWarning($"Public IP lookup at {EchoServiceUrl} returned HTTP {(int)response.StatusCode}.");
                        return null;
                    }

                    return ParseEchoServiceBody(await response.Content.ReadAsStringAsync());
                }
            }
            catch (TaskCanceledException)
            {
                logger?.LogWarning($"Public IP lookup at {EchoServiceUrl} timed out after {RequestTimeout.TotalSeconds:0}s.");
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Public IP lookup at {EchoServiceUrl} failed: {ex.Message}");
            }

            return null;
        }
    }
}
