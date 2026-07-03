using Common.Entities.Config;
using Common.Entities.Models;
using Common.Entities.Redis;
using Common.Entities.Redis.Auth;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.OpenIdConnect;
using Owin;
using System.Security.Claims;

namespace Web.AnalyticsWeb
{
    public partial class Startup
    {
        public void ConfigureAuth(IAppBuilder app)
        {
            var config = new AppConfig();

            // Redis is optional for the web app. When it isn't configured we can't persist the
            // user's refresh token, so Teams deep analytics can't be enabled — but sign-in must
            // still work. TryGetConnectionManager returns null (instead of throwing) in that case.
            var redisConManager = CacheConnectionManager.TryGetConnectionManager(
                config.ConnectionStrings.RedisConnectionString, logger: null,
                tenantId: config.TenantGUID.ToString(), clientId: config.ClientID, clientSecret: config.ClientSecret);
            app.SetDefaultSignInAsAuthenticationType(CookieAuthenticationDefaults.AuthenticationType);

            app.UseCookieAuthentication(new CookieAuthenticationOptions());

            const string graphScopes = "https://graph.microsoft.com/Team.ReadBasic.All https://graph.microsoft.com/ChannelMessage.Read.All";

            app.UseOpenIdConnectAuthentication(
                new OpenIdConnectAuthenticationOptions
                {
                    ClientId = config.ClientID,
                    Authority = config.Authority,
                    PostLogoutRedirectUri = config.WebAppURL,
                    RedirectUri = config.WebAppURL,
                    Scope = $"openid email profile offline_access {graphScopes}",
                    ResponseType = "code id_token",
                    TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                    {
                        ValidateIssuer = true
                    },
                    Notifications = new OpenIdConnectAuthenticationNotifications()
                    {
                        // When AAD redirects back with an auth code, redeem it for tokens and stash
                        // the refresh token in the (encrypted, httpOnly) auth cookie so the SPA can
                        // get a Graph token via SiteTokenAPI without Redis. When Redis IS configured
                        // we also store the token there for the importer's Teams deep-analytics.
                        AuthorizationCodeReceived = async (context) =>
                        {
                            var identity = context.AuthenticationTicket.Identity;
                            var signedInUser = new ClaimsPrincipal(identity);

                            var authToken = await RefreshOAuthToken.GetAccessToken(context.Code, $"openid email profile offline_access {graphScopes}", config);

                            // Persist the refresh token in the auth cookie (claim). SiteTokenAPI uses
                            // it to mint fresh access tokens for the SPA. The access token itself isn't
                            // stored (it's short-lived and would bloat the cookie).
                            if (authToken != null && !string.IsNullOrEmpty(authToken.RefreshToken))
                            {
                                identity.AddClaim(new Claim(GraphTokenClaims.RefreshToken, authToken.RefreshToken));
                            }

                            // Teams deep analytics needs the refresh token in Redis for the importer.
                            // Without Redis we simply skip this; sign-in and the rest of the app still work.
                            if (redisConManager != null && authToken != null)
                            {
                                await redisConManager.SaveToken(signedInUser, authToken);
                            }
                        }
                    }
                });
        }
    }
}
