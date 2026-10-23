using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Entities.CopilotCosts;

namespace Web.AnalyticsWeb.Models.CopilotCosts
{
    public sealed class CostMsalIdentity : ICostIdentity
    {
        private readonly AppConfig _config;
        private readonly string _callback;
        private readonly ConfigurationManager<OpenIdConnectConfiguration> _metadata;

        public CostMsalIdentity(AppConfig config, string callback)
        {
            _config = config;
            _callback = callback;
            _metadata = new ConfigurationManager<OpenIdConnectConfiguration>(
                $"https://login.microsoftonline.com/{config.TenantGUID}/v2.0/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever());
        }

        public async Task<CostToken> RedeemAsync(string code, string verifier, string nonce, CancellationToken ct)
        {
            try
            {
                var app = await CreateAsync();
                var result = await app.Client.AcquireTokenByAuthorizationCode(CostScopes.Graph, code)
                    .WithPkceCodeVerifier(verifier).ExecuteAsync(ct);
                await ValidateIdentityAsync(result.IdToken, nonce, ct);
                return Export(app, result);
            }
            catch (MsalException) { throw new CostConnectionException("reconnectRequired"); }
            catch (SecurityTokenException) { throw new CostConnectionException("invalidCallback"); }
            catch (ArgumentException) { throw new CostConnectionException("invalidCallback"); }
        }

        public async Task<CostToken> RefreshAsync(string accountId, byte[] cache, CancellationToken ct)
        {
            try
            {
                var app = await CreateAsync(cache);
                var account = await app.Client.GetAccountAsync(accountId);
                if (account == null) throw new CostConnectionException("reconnectRequired");
                // A deliberate feasibility slice proves silent refresh, rather than only reusing an
                // unexpired access token. Ordinary portal status requests never acquire a token.
                var result = await app.Client.AcquireTokenSilent(CostScopes.Graph, account).WithForceRefresh(true).ExecuteAsync(ct);
                return Export(app, result);
            }
            catch (MsalUiRequiredException) { throw new CostConnectionException("reconnectRequired"); }
            catch (MsalException) { throw new CostConnectionException("identityUnavailable"); }
        }

        private async Task<MsalSession> CreateAsync(byte[] cache = null)
        {
            IConfidentialClientApplication client;
            try
            {
                client = await AuthHelper.GetNewClientApp(_config.TenantGUID.ToString(), _config.ClientID,
                    _config.ClientSecret, _config.KeyVaultUrl, _config.UseClientCertificate, NullLogger.Instance, _callback);
            }
            catch (Azure.RequestFailedException) { throw new CostConnectionException("identityUnavailable"); }
            catch (Azure.Identity.AuthenticationFailedException) { throw new CostConnectionException("identityUnavailable"); }
            var session = new MsalSession
            {
                Client = client,
                Cache = cache
            };
            session.Client.UserTokenCache.SetBeforeAccess(args =>
            {
                if (session.Cache != null) args.TokenCache.DeserializeMsalV3(session.Cache, shouldClearExistingCache: true);
            });
            session.Client.UserTokenCache.SetAfterAccess(args => session.Cache = args.TokenCache.SerializeMsalV3());
            return session;
        }

        private sealed class MsalSession
        {
            public IConfidentialClientApplication Client { get; set; }
            public byte[] Cache { get; set; }
        }

        private async Task ValidateIdentityAsync(string idToken, string nonce, CancellationToken ct)
        {
            OpenIdConnectConfiguration metadata;
            try { metadata = await _metadata.GetConfigurationAsync(ct); }
            catch (InvalidOperationException) { throw new CostConnectionException("identityUnavailable"); }
            try { ValidateIdentityToken(idToken, nonce, _config.TenantGUID.ToString(), _config.ClientID, metadata.SigningKeys); }
            catch (SecurityTokenSignatureKeyNotFoundException)
            {
                _metadata.RequestRefresh();
                throw;
            }
        }

        public static void ValidateIdentityToken(string idToken, string nonce, string tenant, string client, IEnumerable<SecurityKey> signingKeys)
        {
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = $"https://login.microsoftonline.com/{tenant}/v2.0",
                ValidateAudience = true, ValidAudience = client,
                ValidateLifetime = true, RequireExpirationTime = true,
                RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                IssuerSigningKeys = signingKeys, ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
                ClockSkew = TimeSpan.FromMinutes(1)
            };
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(idToken, parameters, out _);
            if (string.IsNullOrEmpty(nonce) || principal.FindFirst("nonce")?.Value != nonce
                || principal.FindFirst("tid")?.Value != tenant
                || !Guid.TryParse(principal.FindFirst("oid")?.Value, out _))
                throw new CostConnectionException("invalidCallback");
        }

        private static CostToken Export(MsalSession app, AuthenticationResult result)
        {
            if (CostScopes.Graph.Any(scope => !result.Scopes.Any(granted =>
                string.Equals(granted.Replace("https://graph.microsoft.com/", ""), scope.Replace("https://graph.microsoft.com/", ""),
                    StringComparison.OrdinalIgnoreCase))))
                throw new CostConnectionException("permissionDenied");
            return new CostToken
            {
                AccessToken = result.AccessToken, IdToken = result.IdToken,
                AccountId = result.Account.HomeAccountId.Identifier,
                TenantId = result.TenantId,
                Cache = app.Cache
            };
        }
    }
}
