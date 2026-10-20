using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtils.Health
{
    public static class RuntimeCredentialHealth
    {
        public static async Task<RuntimeHealthResult> CheckAsync(string tenantId, string clientId,
            string secret, bool useCertificate, string keyVaultUrl, CancellationToken cancellationToken)
        {
            DateTime? expiryUtc = null;
            TokenCredential credential;
            if (useCertificate)
            {
                // The same certificate cache the importer uses: report the credential actually in use,
                // not a newly rotated certificate the running importer has not loaded.
                var certificate = await AuthHelper.RetrieveKeyVaultCertificate(AuthHelper.CertificateName,
                    keyVaultUrl, NullLogger.Instance, cancellationToken).ConfigureAwait(false);
                expiryUtc = certificate.NotAfter.ToUniversalTime();
                credential = new ClientCertificateCredential(tenantId, clientId, certificate);
            }
            else
                credential = new ClientSecretCredential(tenantId, clientId, secret);

            // Fresh credential per check deliberately bypasses the importer's cached access token.
            // Token issuance proves credential validity, NOT Graph/Activity API permissions.
            return await CheckTokenAsync(credential, expiryUtc, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<RuntimeHealthResult> CheckTokenAsync(TokenCredential credential,
            DateTime? expiryUtc, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token.Token) || token.ExpiresOn <= nowUtc)
                return new RuntimeHealthResult(HealthStatus.Unhealthy, "Runtime credential did not acquire a usable token.", reasonKey: "defaultHealth.tokenUnavailable");
            if (!expiryUtc.HasValue)
                return new RuntimeHealthResult(HealthStatus.Degraded,
                    "Runtime token acquired. Client-secret expiry is unavailable; track it in the app registration.", reasonKey: "defaultHealth.secretExpiryUnknown");
            var days = (int)Math.Floor((expiryUtc.Value - nowUtc).TotalDays);
            return new RuntimeHealthResult(days < 3 ? HealthStatus.Unhealthy : days < 14 ? HealthStatus.Degraded : HealthStatus.Healthy,
                days < 14 ? "Runtime certificate expires soon; renew it and restart the importers."
                    : "Runtime token acquired; certificate expiry checked.", days,
                days < 14 ? "defaultHealth.certificateExpiring" : "defaultHealth.certificateValid");
        }
    }
}
