using Common.Entities.Config;
using Common.Entities.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Web.AnalyticsWeb;

var builder = WebApplication.CreateBuilder(args);

// The Copilot Adoption endpoints take the user filter on the query string (the CSV and Excel exports are
// plain links). The System.Web build lifted the host's query-string limit to 16 KB in Web.Template.config
// so the portal, not the host, explains an over-long filter; Kestrel's request line defaults to 8 KB.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestLineSize = 16 * 1024);

// The product's own configuration object, still read from the App Service / config file exactly as
// it was under System.Web. Constructed once here so a misconfigured deployment fails at startup
// rather than on the first request.
var appConfig = new AppConfig();

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
        // SiteTokenAPI. Nothing is stored server-side: authorising a Team for deep analytics copies the
        // token into the state table explicitly (TeamsAuthAPIController), and only for the Teams the
        // admin chooses.
        OnAuthorizationCodeReceived = async context =>
        {
            var identity = (ClaimsIdentity)context.Principal.Identity;

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

// Global.asax's Application_Start / Application_End, re-homed on the ASP.NET Core host lifetime.
// Resume any user organisation CSV import the previous process was running when it stopped (in the
// background; it never throws), and drain the telemetry pipelines on shutdown - the Copilot Adoption
// HostStopping stage is what separates "recycled under a run" from "the run hung" (issue #441).
app.Lifetime.ApplicationStarted.Register(Web.AnalyticsWeb.Controllers.UserOrgAPIController.ResumeInterruptedImportsAfterStartup);
app.Lifetime.ApplicationStopping.Register(() =>
{
    Web.AnalyticsWeb.Models.LicenceActivity.LicenceActivityTelemetry.Shutdown();
    Web.AnalyticsWeb.Models.UserOrgs.UserOrgImportAppInsights.Shutdown();
    Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionTelemetryHost.Shutdown("ApplicationStopping");
});

app.Run();
