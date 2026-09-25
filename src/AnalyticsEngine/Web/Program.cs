using Common.Entities.Config;
using Common.Entities.Models;
using Common.Entities.Redis;
using Common.Entities.Redis.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Web.AnalyticsWeb;

var builder = WebApplication.CreateBuilder(args);

// The product's own configuration object, still read from the App Service / config file exactly as
// it was under System.Web. Constructed once here so a misconfigured deployment fails at startup
// rather than on the first request.
var appConfig = new AppConfig();

// Redis is optional for the web app. When it isn't configured we can't persist the user's refresh
// token, so Teams deep analytics can't be enabled - but sign-in must still work.
// TryGetConnectionManager returns null (instead of throwing) in that case.
var redisConManager = CacheConnectionManager.TryGetConnectionManager(
    appConfig.ConnectionStrings.RedisConnectionString, logger: null,
    tenantId: appConfig.TenantGUID.ToString(), clientId: appConfig.ClientID, clientSecret: appConfig.ClientSecret);

const string GraphScopes = "https://graph.microsoft.com/Team.ReadBasic.All https://graph.microsoft.com/ChannelMessage.Read.All";

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie()
.AddOpenIdConnect(options =>
{
    options.ClientId = appConfig.ClientID;
    options.ClientSecret = appConfig.ClientSecret;
    // Match the v2 token endpoint used by RefreshOAuthToken with v2 discovery and issuer metadata.
    options.Authority = $"{appConfig.Authority}/v2.0";
    options.SignedOutRedirectUri = appConfig.WebAppURL;
    options.ResponseType = OpenIdConnectResponseType.CodeIdToken;
    options.TokenValidationParameters.ValidateIssuer = true;

    options.Scope.Clear();
    foreach (var scope in $"openid email profile offline_access {GraphScopes}".Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        options.Scope.Add(scope);
    }

    options.Events = new OpenIdConnectEvents
    {
        // An expired session must not turn an API call into a sign-in redirect.
        //
        // The OIDC handler converts the 401 from an [Authorize]'d controller into a 302 to
        // login.microsoftonline.com. A top-level navigation handles that fine, but the SPA's fetch()
        // follows the redirect cross-origin, the login page carries no CORS headers, and the call
        // rejects with an opaque "TypeError: Failed to fetch" - so the portal just breaks with no
        // hint that the user simply needs to sign in again.
        //
        // For API requests we therefore suppress the redirect and leave the plain 401 in place.
        OnRedirectToIdentityProvider = context =>
        {
            if (context.ProtocolMessage.RequestType == OpenIdConnectRequestType.Authentication
                && AuthRequestRules.IsApiRequest(context.Request))
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                // Only flag it as an expired session when there is genuinely no signed-in user.
                // A 401 raised by a controller while the user IS signed in (SiteTokenAPI having no
                // Graph refresh token) must not bounce them through a pointless sign-in.
                var signedIn = context.HttpContext.User?.Identity?.IsAuthenticated == true;
                if (!signedIn)
                {
                    context.Response.Headers[AuthRequestRules.SessionExpiredHeader] = "true";
                }
            }

            return Task.CompletedTask;
        },

        // When Entra redirects back with an auth code, redeem it for tokens and stash the refresh
        // token in the (encrypted, httpOnly) auth cookie so the SPA can get a Graph token via
        // SiteTokenAPI without Redis. When Redis IS configured we also store the token there for the
        // importer's Teams deep-analytics.
        OnAuthorizationCodeReceived = async context =>
        {
            var identity = (ClaimsIdentity)context.Principal.Identity;
            var signedInUser = new ClaimsPrincipal(identity);

            var authToken = await RefreshOAuthToken.GetAccessToken(
                context.ProtocolMessage.Code, $"openid email profile offline_access {GraphScopes}",
                context.TokenEndpointRequest.RedirectUri, appConfig);

            // Persist the refresh token in the auth cookie (claim). SiteTokenAPI uses it to mint
            // fresh access tokens for the SPA. The access token itself isn't stored (it's short-lived
            // and would bloat the cookie).
            if (authToken != null && !string.IsNullOrEmpty(authToken.RefreshToken))
            {
                identity.AddClaim(new Claim(GraphTokenClaims.RefreshToken, authToken.RefreshToken));
            }

            // Teams deep analytics needs the refresh token in Redis for the importer. Without Redis
            // we simply skip this; sign-in and the rest of the app still work.
            if (redisConManager != null && authToken != null)
            {
                await redisConManager.SaveToken(signedInUser, authToken);
            }

            // Supply the redeemed tokens for validation without redeeming the code a second time.
            context.HandleCodeRedemption(authToken.AccessToken, authToken.IdToken);
        }
    };
});

builder.Services.AddAuthorization();

// CORS for the org URLs the tracker is deployed to, replacing AllowCorsForOrgUrlsAttribute.
builder.Services.AddCors(options =>
{
    options.AddPolicy(OrgUrlCorsPolicy.PolicyName, policy => policy
        .SetIsOriginAllowed(OrgUrlCorsPolicy.IsOriginAllowed)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

builder.Services.AddExceptionHandler<AnalyticsWebExceptionHandler>();

// JSON exactly as the Web API 2 build on dev/main writes it - Newtonsoft, names as declared or as
// [JsonProperty] gives them, [JsonIgnore] honoured. The default System.Text.Json formatter ignores
// those attributes and renamed or leaked fields the shared portal reads; see WebApiCompatibleJson.
builder.Services.AddControllersWithViews().AddWebApiCompatibleJson();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

// Static assets from wwwroot - but never index.html itself, which PortalHosting hands on to
// HomeController so the build label is stamped into it and [Authorize] applies.
app.UsePortalStaticFiles();
app.UseRouting();
app.UseCors(OrgUrlCorsPolicy.PolicyName);
app.UseAuthentication();
app.UseAuthorization();

// Attribute-routed API controllers, the Account and Home conventional routes carried across from
// RouteConfig, and a fallback that serves the portal page through HomeController (not from disk,
// which would skip the build-label substitution). See PortalHosting.
app.MapPortalRoutes();

app.Run();
