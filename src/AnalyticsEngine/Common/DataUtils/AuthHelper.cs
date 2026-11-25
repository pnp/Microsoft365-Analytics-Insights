using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtils
{
    public class AuthHelper
    {
        public const string CertificateName = "O365AdvancedAnalytics";
        private static X509Certificate2 _cachedCert = null;
        public static Task<IConfidentialClientApplication> GetNewClientApp(
            string tenantId, string clientId, string clientSecret, string keyVaultUrl, bool useClientCertificate, ILogger logger,
            string redirectUri = null)
            => GetNewClientApp(tenantId, clientId, clientSecret, keyVaultUrl, useClientCertificate, logger, redirectUri, CancellationToken.None);

        internal static async Task<IConfidentialClientApplication> GetNewClientApp(
            string tenantId, string clientId, string clientSecret, string keyVaultUrl, bool useClientCertificate, ILogger logger,
            string redirectUri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IConfidentialClientApplication app = null;
            if (useClientCertificate)
            {
                var appRegistrationCert = await RetrieveKeyVaultCertificate(CertificateName, keyVaultUrl, logger, cancellationToken);

                var builder = ConfidentialClientApplicationBuilder.Create(clientId)
                    .WithCertificate(appRegistrationCert)
                    .WithAuthority($"https://login.microsoftonline.com/{tenantId}");
                if (redirectUri != null) builder = builder.WithRedirectUri(redirectUri);
                return builder.Build();
            }
            else
            {
                var builder = ConfidentialClientApplicationBuilder.Create(clientId)
                    .WithClientSecret(clientSecret)
                    .WithAuthority($"https://login.microsoftonline.com/{tenantId}");
                if (redirectUri != null) builder = builder.WithRedirectUri(redirectUri);
                app = builder.Build();
            }
            return app;
        }

        public static async Task<X509Certificate2> RetrieveKeyVaultCertificate(string certName, string keyVaultUrl, ILogger logger,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cachedCert == null)
            {
                logger.LogInformation($"Retrieving certificate {certName} from KeyVault {keyVaultUrl}");
                var client = new CertificateClient(vaultUri: new Uri(keyVaultUrl), credential: new DefaultAzureCredential());

                // Get private key
                var secret = await client.DownloadCertificateAsync(certName, cancellationToken: cancellationToken);
                if (secret.Value != null)
                {
                    _cachedCert = new X509Certificate2(secret.Value);
                }
                else
                {
                    logger.LogCritical($"Certificate {certName} not found in KeyVault {keyVaultUrl}");
                    throw new Exception($"Auth error: Certificate {certName} not found in KeyVault {keyVaultUrl}");
                }
            }
            return _cachedCert;

        }
    }
}
