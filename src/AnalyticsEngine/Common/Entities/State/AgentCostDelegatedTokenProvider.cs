using Azure.Core;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>MSAL owns redemption, refresh, account selection and token-cache serialization.</summary>
    public sealed class AgentCostDelegatedTokenProvider
    {
        // The entitlement REST reference documents .default, not a Licensing.Read permission.
        public const string Scope = "https://api.powerplatform.com/.default";
        public const string CallbackPath = "/signin-agent-costs";
        private readonly AgentCostConnectionStore _store;
        private readonly Func<Task<IConfidentialClientApplication>> _client;
        private readonly Func<Task<AgentCostTokenProtection>> _protection;

        public AgentCostDelegatedTokenProvider(AgentCostConnectionStore store,
            Func<Task<IConfidentialClientApplication>> client, Func<Task<AgentCostTokenProtection>> protection)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        }

        public static AgentCostDelegatedTokenProvider Create(AppConfig config, AgentCostConnectionStore store) =>
            new AgentCostDelegatedTokenProvider(store,
                async () =>
                {
                    var builder = ConfidentialClientApplicationBuilder.Create(config.ClientID)
                        .WithAuthority("https://login.microsoftonline.com/" + config.TenantGUID)
                        .WithRedirectUri(RedirectUri(config));
                    if (config.UseClientCertificate)
                        builder.WithCertificate(await AuthHelper.RetrieveKeyVaultCertificate(
                            AuthHelper.CertificateName, config.KeyVaultUrl, NullLogger.Instance));
                    else
                        builder.WithClientSecret(config.ClientSecret);
                    return builder.Build();
                },
                async () => new AgentCostTokenProtection(config.ClientSecret, config.UseClientCertificate
                    ? await AuthHelper.RetrieveKeyVaultCertificate(AuthHelper.CertificateName, config.KeyVaultUrl, NullLogger.Instance)
                    : null));

        public static string RedirectUri(AppConfig config) => config.WebAppURL.TrimEnd('/') + CallbackPath;

        public async Task ConnectAsync(string code, string redirectUri, string expectedTenant, string expectedObjectId,
            Func<string, Task> verifyAccess)
        {
            var app = await _client().ConfigureAwait(false);
            if (!string.Equals(app.AppConfig.RedirectUri, redirectUri, StringComparison.Ordinal))
                throw new AgentCostConnectionException("failed");
            var cache = new CacheSession(app);
            AuthenticationResult result;
            try
            {
                result = await app.AcquireTokenByAuthorizationCode(new[] { Scope }, code)
                    .ExecuteAsync().ConfigureAwait(false);
            }
            catch (MsalException)
            {
                // Do not propagate token-endpoint messages, claims challenges, or raw response bodies.
                throw new AgentCostConnectionException("consentOrPolicy");
            }
            if (!string.Equals(result.TenantId, expectedTenant, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(result.UniqueId, expectedObjectId, StringComparison.OrdinalIgnoreCase)
                || result.Account == null)
                throw new AgentCostConnectionException("identityMismatch");

            // A usable access token alone is not a durable connection. Prove MSAL can renew it before publishing.
            try
            {
                result = await app.AcquireTokenSilent(new[] { Scope }, result.Account)
                    .WithForceRefresh(true).ExecuteAsync().ConfigureAwait(false);
            }
            catch (MsalException)
            {
                throw new AgentCostConnectionException("consentOrPolicy");
            }
            await verifyAccess(result.AccessToken).ConfigureAwait(false);
            var protection = await _protection().ConfigureAwait(false);
            await _store.ConnectAsync(Serialize(cache, result.Account.HomeAccountId.Identifier, protection)).ConfigureAwait(false);
        }

        public async Task<AccessToken> AcquireAsync(string version)
        {
            if (await _store.NeedsReconnectAsync(version).ConfigureAwait(false))
                throw new AgentCostConnectionException("reconnectNeeded");
            var value = await _store.GetCacheAsync(version).ConfigureAwait(false);
            try
            {
                if (value == null) throw new AgentCostConnectionException("reconnectNeeded");
                var protection = await _protection().ConfigureAwait(false);
                var payload = JsonConvert.DeserializeObject<CachePayload>(Encoding.UTF8.GetString(protection.Unprotect(value)));
                var app = await _client().ConfigureAwait(false);
                var cache = new CacheSession(app, Convert.FromBase64String(payload.Cache));
                var account = await app.GetAccountAsync(payload.Account).ConfigureAwait(false);
                if (account == null) throw new AgentCostConnectionException("reconnectNeeded");
                var result = await app.AcquireTokenSilent(new[] { Scope }, account).ExecuteAsync().ConfigureAwait(false);
                await _store.SaveCacheAsync(version, Serialize(cache, payload.Account, protection)).ConfigureAwait(false);
                var current = await _store.GetHeadAsync().ConfigureAwait(false);
                if (current?.Connected != true || current.Version != version)
                    throw new AgentCostConnectionException("disconnected");
                return new AccessToken(result.AccessToken, result.ExpiresOn);
            }
            catch (MsalUiRequiredException)
            {
                await _store.RequireReconnectAsync(version).ConfigureAwait(false);
                throw new AgentCostConnectionException("reconnectNeeded");
            }
            catch (Exception ex) when (ex is CryptographicException || ex is JsonException
                || ex is FormatException || ex is AgentCostConnectionException)
            {
                await _store.RequireReconnectAsync(version).ConfigureAwait(false);
                throw new AgentCostConnectionException("reconnectNeeded");
            }
            catch (MsalException)
            {
                throw new AgentCostConnectionException("tokenUnavailable");
            }
        }

        private static string Serialize(CacheSession cache, string account, AgentCostTokenProtection protection) =>
            protection.Protect(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CachePayload
            {
                Account = account,
                Cache = Convert.ToBase64String(cache.Bytes),
            })));

        private sealed class CacheSession
        {
            public byte[] Bytes { get; private set; }
            public CacheSession(IConfidentialClientApplication app, byte[] bytes = null)
            {
                Bytes = bytes;
                app.UserTokenCache.SetBeforeAccess(args =>
                {
                    if (Bytes != null) args.TokenCache.DeserializeMsalV3(Bytes, shouldClearExistingCache: true);
                });
                app.UserTokenCache.SetAfterAccess(args =>
                {
                    if (args.HasStateChanged) Bytes = args.TokenCache.SerializeMsalV3();
                });
            }
        }

        private sealed class CachePayload
        {
            public string Account { get; set; }
            public string Cache { get; set; }
        }
    }

    /// <summary>Stable, secret-free outcome; never retains the original MSAL exception.</summary>
    public sealed class AgentCostConnectionException : Exception
    {
        public AgentCostConnectionException(string code) : base("Copilot Studio billing connection: " + code) { Code = code; }
        public string Code { get; }
    }
}
