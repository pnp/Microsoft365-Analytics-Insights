using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Common.DataUtils.Http
{
    /// <summary>
    /// Provides OAuth tokens for application-identity (client credentials)
    /// </summary>
    public abstract class ImportAppIndentityOAuthContext
    {
        private AccessToken _accessToken;

        private SemaphoreSlim _getAccessTokenSemaphoreSlim = new SemaphoreSlim(1, 1);
        private readonly string _clientId;
        private readonly string _tenantId;
        private readonly string _clientSecret;
        private readonly string _keyVaultUrl;
        private readonly bool _useClientCertificate;
        private System.Security.Cryptography.X509Certificates.X509Certificate2 _clientAppCert = null;

        public ImportAppIndentityOAuthContext(ILogger telemetry, string clientId, string tenantId, string clientSecret, string keyVaultUrl, bool useClientCertificate)
        {
            if (string.IsNullOrEmpty(clientId))
            {
                throw new ArgumentException($"'{nameof(clientId)}' cannot be null or empty.", nameof(clientId));
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                throw new ArgumentException($"'{nameof(tenantId)}' cannot be null or empty.", nameof(tenantId));
            }

            if (string.IsNullOrEmpty(clientSecret) && !useClientCertificate)
            {
                throw new ArgumentException($"'{nameof(clientSecret)}' cannot be null or empty if no client certificate configured.", nameof(clientSecret));
            }

            _clientId = clientId;
            _tenantId = tenantId;
            _clientSecret = clientSecret;
            _keyVaultUrl = keyVaultUrl;
            _useClientCertificate = useClientCertificate;
            Telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

        }

        public async Task InitClientCredential()
        {
            if (_useClientCertificate)
            {
                if (_clientAppCert == null)
                {
                    _clientAppCert = await AuthHelper.RetrieveKeyVaultCertificate(AuthHelper.CertificateName, _keyVaultUrl, Telemetry);
                    Creds = new ClientCertificateCredential(_tenantId, _clientId, _clientAppCert);
                }
            }
            else
            {
                Creds = new ClientSecretCredential(_tenantId, _clientId, _clientSecret);
            }
        }

        public TokenCredential Creds { get; set; }

        public async Task<AccessToken> GetAccessToken()
        {
            // Threadsafe execution of this instance
            await _getAccessTokenSemaphoreSlim.WaitAsync();

            try
            {
                if (_accessToken.ExpiresOn < DateTime.Now.AddMinutes(5))
                {
                    Console.WriteLine("Generating new OAuth token...");

                    var app = await AuthHelper.GetNewClientApp(_tenantId, _clientId, _clientSecret, _keyVaultUrl, _useClientCertificate, Telemetry);
                    try
                    {
                        var r = await app.AcquireTokenForClient(new string[] { ResourceURL }).ExecuteAsync();
                        _accessToken = new AccessToken(r.AccessToken, r.ExpiresOn);
                    }
                    catch (Exception ex)
                    {
                        Telemetry.LogError(ex, ex.Message);
                        throw;
                    }
                }
            }
            finally
            {
                _getAccessTokenSemaphoreSlim.Release();
            }

            return _accessToken;
        }

        private ILogger Telemetry { get; set; }

        public abstract string ResourceURL { get; }

    }
}
