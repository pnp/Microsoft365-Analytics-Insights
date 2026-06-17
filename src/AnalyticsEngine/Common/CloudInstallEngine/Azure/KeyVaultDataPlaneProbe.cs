using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using System;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure
{
    /// <summary>Outcome of a Key Vault data-plane reachability probe.</summary>
    public enum KeyVaultProbeStatus
    {
        /// <summary>The vault data-plane endpoint was reached and the credential is authorized to read.</summary>
        Reachable,

        /// <summary>The vault was reached but the credential was denied (HTTP 403) - missing access policy or a firewall rule.</summary>
        Unauthorized,

        /// <summary>The vault data-plane endpoint could not be reached at all (DNS / network / firewall transport failure).</summary>
        TransportFailure,

        /// <summary>Some other, unexpected error occurred.</summary>
        OtherError
    }

    /// <summary>Result of <see cref="KeyVaultDataPlaneProbe.TryReadAsync"/>.</summary>
    public class KeyVaultProbeResult
    {
        public KeyVaultProbeResult(KeyVaultProbeStatus status, string message)
        {
            Status = status;
            Message = message;
        }

        public KeyVaultProbeStatus Status { get; }
        public string Message { get; }
    }

    /// <summary>
    /// Lightweight pre-flight check that the Key Vault data-plane endpoint
    /// (<c>&lt;name&gt;.vault.azure.net</c>) is actually reachable and readable with a given credential.
    /// This pre-empts the install-time failure where <c>KeyVaultSecretAddTask</c> cannot resolve / reach the
    /// vault, distinguishing a DNS/network problem from an authorization (access policy / firewall) problem.
    /// </summary>
    public static class KeyVaultDataPlaneProbe
    {
        /// <summary>
        /// Sentinel secret name used only to prove reachability + read authorization. It is expected NOT to
        /// exist, so a 404 is the success signal (the call got through and we were authorized to read).
        /// </summary>
        private const string ProbeSecretName = "aitracker-testconfig-connectivity-probe";

        /// <summary>
        /// Attempt a single, read-only data-plane call against the vault and classify the outcome. Never throws.
        /// </summary>
        public static async Task<KeyVaultProbeResult> TryReadAsync(string vaultName, TokenCredential credential)
        {
            if (string.IsNullOrWhiteSpace(vaultName)) throw new ArgumentException($"'{nameof(vaultName)}' cannot be null or empty.", nameof(vaultName));
            if (credential == null) throw new ArgumentNullException(nameof(credential));

            var client = new SecretClient(new Uri($"https://{vaultName.Trim()}.vault.azure.net"), credential);
            try
            {
                await client.GetSecretAsync(ProbeSecretName);
                // The probe secret unexpectedly exists, but the call still proves reachability + Get permission.
                return new KeyVaultProbeResult(KeyVaultProbeStatus.Reachable, "Key Vault data-plane reachable.");
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Reached the vault and were authorized to read; the probe secret simply doesn't exist (expected).
                return new KeyVaultProbeResult(KeyVaultProbeStatus.Reachable, "Key Vault data-plane reachable.");
            }
            catch (RequestFailedException ex) when (ex.Status == 403)
            {
                return new KeyVaultProbeResult(KeyVaultProbeStatus.Unauthorized, ex.Message);
            }
            catch (Exception ex) when (TransportFailureDetector.IsTransportOrDnsFailure(ex, out var leaf))
            {
                return new KeyVaultProbeResult(KeyVaultProbeStatus.TransportFailure, leaf);
            }
            catch (Exception ex)
            {
                return new KeyVaultProbeResult(KeyVaultProbeStatus.OtherError, ex.Message);
            }
        }
    }
}
