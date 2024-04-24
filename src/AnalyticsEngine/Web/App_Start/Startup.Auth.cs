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
            var redisConManager = CacheConnectionManager.GetConnectionManager(config.ConnectionStrings.RedisConnectionString);
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
                        // For demo purposes only, see below
                        ValidateIssuer = false

                        // In a real multi-tenant app, you would add logic to determine whether the
                        // issuer was from an authorized tenant
                        //ValidateIssuer = true,
                        //IssuerValidator = (issuer, token, tvp) =>
                        //{
                        //  if (MyCustomTenantValidation(issuer))
                        //  {
                        //    return issuer;
                        //  }
                        //  else
                        //  {
                        //    throw new SecurityTokenInvalidIssuerException("Invalid issuer");
                        //  }
                        //}
                    },
                    Notifications = new OpenIdConnectAuthenticationNotifications()
                    {
                        // If there is a code in the OpenID Connect response, redeem it for an access token and refresh token, and store those away.
                        AuthorizationCodeReceived = async (context) =>
                        {

                            var code = context.Code;

                            var signedInUser = new ClaimsPrincipal(context.AuthenticationTicket.Identity);

                            var authToken = await RefreshOAuthToken.GetAccessToken(context.Code, $"openid email profile offline_access {graphScopes}", config);
                            await redisConManager.SaveToken(signedInUser, authToken);
                        }
                    }
                });
        }

    }
}
