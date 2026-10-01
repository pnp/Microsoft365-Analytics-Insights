using Common.Entities.Config;
using Common.Entities.Models;
using DataUtils;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Owin;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.OpenIdConnect;
using Owin;
using System;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb
{
    public partial class Startup
    {
        /// <summary>
        /// Response header set on the 401 that replaces the sign-in redirect for API calls, so the SPA can tell
        /// "your session has expired, re-authenticate" apart from an authenticated-but-unauthorised 401 (e.g.
        /// SiteTokenAPI reporting that it has no Graph refresh token for this session).
        /// </summary>
        public const string SessionExpiredHeader = "X-Auth-Session-Expired";

        /// <summary>Application Insights operation name for Teams connection failures.</summary>
        internal const string TeamsConnectionTelemetryContext = "Web Teams connection";

        public void ConfigureAuth(IAppBuilder app)
        {
            var config = new AppConfig();

            // One logger for the life of the app: each AnalyticsLogger builds its own TelemetryClient.
            var teamsConnectionLog = new Lazy<AnalyticsLogger>(
                () => new AnalyticsLogger(config.AppInsightsConnectionString, TeamsConnectionTelemetryContext));

            var tokenCapture = new DelegatedGraphTokenCapture(
                redeemCode: (code, scopes) => RefreshOAuthToken.GetAccessToken(code, scopes, config),
                report: (message, exception) => ReportTeamsConnectionFailure(teamsConnectionLog.Value, message, exception));

            ConfigureAuth(app, CreateOpenIdConnectOptions(
                config.ClientID, config.Authority, config.WebAppURL, tokenCapture, app.GetDefaultCookieManager()));
        }

        /// <summary>
        /// Adds the cookie and OIDC middleware. Separate from <see cref="ConfigureAuth(IAppBuilder)"/> so a test can
        /// run the real middleware in-process with options it controls.
        /// </summary>
        internal static void ConfigureAuth(IAppBuilder app, OpenIdConnectAuthenticationOptions openIdConnectOptions)
        {
            app.SetDefaultSignInAsAuthenticationType(CookieAuthenticationDefaults.AuthenticationType);

            app.UseCookieAuthentication(new CookieAuthenticationOptions());

            app.UseOpenIdConnectAuthentication(openIdConnectOptions);
        }

        internal static OpenIdConnectAuthenticationOptions CreateOpenIdConnectOptions(
            string clientId,
            string authority,
            string webAppUrl,
            DelegatedGraphTokenCapture tokenCapture,
            ICookieManager cookieManager)
        {
            return new OpenIdConnectAuthenticationOptions
            {
                ClientId = clientId,
                Authority = authority,
                PostLogoutRedirectUri = webAppUrl,
                RedirectUri = webAppUrl,

                // Signing in asks for OpenID Connect's own scopes and nothing else, so an optional feature's
                // permissions can never stop anyone using the portal (issue #670). The delegated Teams
                // permissions are asked for only by the Teams connection: see RedirectToIdentityProvider.
                Scope = DelegatedGraphConsent.SignInScopes,
                ResponseType = "code id_token",
                TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateIssuer = true
                },
                // Stops the cancelled API challenges below leaving orphan nonce cookies behind.
                CookieManager = new ApiSafeCookieManager(cookieManager),
                Notifications = new OpenIdConnectAuthenticationNotifications()
                {
                    RedirectToIdentityProvider = context =>
                    {
                        if (context.ProtocolMessage.RequestType != OpenIdConnectRequestType.Authentication)
                        {
                            return Task.CompletedTask;
                        }

                        // An expired session must not turn an API call into a sign-in redirect.
                        //
                        // The OIDC middleware runs in Active mode, so it converts the 401 from an [Authorize]'d
                        // controller into a 302 to login.microsoftonline.com. A top-level navigation handles that
                        // fine, but the SPA's fetch() follows the redirect cross-origin, the login page carries no
                        // CORS headers, and the call rejects with an opaque "TypeError: Failed to fetch" - so the
                        // portal just breaks with no hint that the user simply needs to sign in again.
                        //
                        // For API requests we therefore suppress the redirect and leave the plain 401 in place.
                        if (IsApiRequest(context.OwinContext.Request))
                        {
                            context.HandleResponse();
                            context.OwinContext.Response.StatusCode = 401;

                            // Only flag it as an expired session when there is genuinely no signed-in user.
                            // A 401 raised by a controller while the user IS signed in (SiteTokenAPI having
                            // no Graph refresh token) must not bounce them through a pointless sign-in.
                            var signedIn = context.OwinContext.Authentication?.User?.Identity?.IsAuthenticated == true;
                            if (!signedIn)
                            {
                                context.OwinContext.Response.Headers[SessionExpiredHeader] = "true";
                            }

                            return Task.CompletedTask;
                        }

                        // The Teams connection (AccountController.ConnectTeams) is the only request that asks
                        // for the delegated Teams permissions. Its challenge is marked, and the same marker
                        // comes back on the callback inside the protected state.
                        var challenge = context.OwinContext.Authentication?.AuthenticationResponseChallenge;
                        if (DelegatedGraphConsent.IsTeamsConnect(challenge?.Properties))
                        {
                            context.ProtocolMessage.Scope = DelegatedGraphConsent.TeamsConnectScopes;

                            var loginHint = DelegatedGraphConsent.LoginHintFor(context.OwinContext.Authentication.User);
                            if (loginHint != null)
                            {
                                context.ProtocolMessage.LoginHint = loginHint;
                            }
                        }

                        return Task.CompletedTask;
                    },

                    // A sign-in redeems nothing. A Teams connection redeems the code for the Teams scopes and
                    // keeps the refresh token in the encrypted, httpOnly auth cookie. TeamsAuthAPI copies it
                    // into TeamsTokenStore only for Teams the admin selects. Neither path can throw here:
                    // see DelegatedGraphTokenCapture.
                    AuthorizationCodeReceived = context =>
                        tokenCapture.OnAuthorizationCodeReceivedAsync(context.AuthenticationTicket, context.Code),

                    // Entra ID refusing a Teams connection (consent missing, prompt declined) sends the admin
                    // back to the Teams permissions page, still signed in. Any other failure keeps Katana's
                    // default handling.
                    AuthenticationFailed = context =>
                    {
                        var returnUri = tokenCapture.OnAuthenticationFailed(
                            context.ProtocolMessage, context.Exception, context.Options.StateDataFormat);
                        if (returnUri != null)
                        {
                            context.HandleResponse();
                            context.Response.Redirect(returnUri);
                        }

                        return Task.CompletedTask;
                    },
                }
            };
        }

        /// <summary>
        /// Records a failed Teams connection. A warning rather than an error: the portal kept working, and the
        /// likeliest cause is a tenant decision (no consent for an optional feature) rather than a fault.
        /// </summary>
        private static void ReportTeamsConnectionFailure(AnalyticsLogger logger, string message, Exception exception)
        {
            logger.LogWarning(message);
            if (exception != null)
            {
                logger.TrackException(exception);
            }
        }

        /// <summary>
        /// True for calls the SPA makes with fetch/XHR rather than a top-level navigation. Those must get a
        /// status code they can act on, not a redirect to the identity provider they cannot follow.
        /// </summary>
        private static bool IsApiRequest(IOwinRequest request)
        {
            if (request == null) return false;

            // The portal's apiFetch always sends this and a browser navigation never does, so it is the one
            // unambiguous signal that a script is waiting for a status code.
            if (string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Otherwise fall back to the path - but NOT for a top-level navigation. The Copilot adoption CSV
            // and workbook exports are plain <a href> links to [Authorize]'d /api routes, and they must still
            // redirect to sign-in: the browser IS navigating, so it can follow the redirect and then deliver
            // the download. Answering those with a bare 401 would just show the user a blank error page.
            if (IsDocumentNavigation(request)) return false;

            return request.Path.StartsWithSegments(new PathString("/api"));
        }

        /// <summary>
        /// True when the browser is navigating the top-level document (a link, address bar or form post)
        /// rather than making a background request.
        /// </summary>
        /// <remarks>
        /// Uses the Fetch Metadata request headers, which every current browser sends. On a browser old
        /// enough to omit them this returns false and the <c>/api</c> path rule applies as before.
        /// </remarks>
        private static bool IsDocumentNavigation(IOwinRequest request)
        {
            return string.Equals(request.Headers["Sec-Fetch-Mode"], "navigate",
                       System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(request.Headers["Sec-Fetch-Dest"], "document",
                       System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Cookie manager for the OIDC middleware that suppresses response cookies on API requests.
        /// </summary>
        /// <remarks>
        /// Katana writes the OIDC nonce cookie in <c>AddNonceToMessage</c>, which runs BEFORE the
        /// <c>RedirectToIdentityProvider</c> notification - so <c>HandleResponse()</c> cancels the redirect but
        /// cannot un-write the cookie. Without this, every suppressed API challenge would leave an orphan
        /// <c>OpenIdConnect.nonce.*</c> cookie behind that nothing ever consumes (they live ~15 minutes).
        /// A page firing several parallel API calls would drop several per attempt, and because the auth cookie
        /// on this site can also carry a Graph refresh token (once an admin connects Microsoft Teams), the
        /// accumulated Cookie header can grow past IIS/proxy limits - turning a recoverable "please sign in
        /// again" into a 400 Request Too Large that
        /// the user cannot get out of without clearing cookies.
        ///
        /// The only cookie the middleware writes on a challenge is that nonce, and for API requests the
        /// challenge is cancelled, so dropping the write is exactly right. Reads and deletes are untouched, and
        /// so is every non-API request - the sign-in redirect and its callback are top-level navigations, which
        /// still get a real nonce and validate it normally.
        ///
        /// It decorates the host's own manager rather than constructing one: Katana does
        /// <c>Options.CookieManager ??= app.GetDefaultCookieManager()</c>, and under
        /// <c>Microsoft.Owin.Host.SystemWeb</c> that default integrates with System.Web's cookie collection.
        /// Hard-coding a replacement would quietly change behaviour on the successful sign-in path.
        /// </remarks>
        private sealed class ApiSafeCookieManager : ICookieManager
        {
            private readonly ICookieManager _inner;

            public ApiSafeCookieManager(ICookieManager inner)
            {
                _inner = inner ?? new CookieManager();
            }

            public string GetRequestCookie(IOwinContext context, string key)
                => _inner.GetRequestCookie(context, key);

            public void AppendResponseCookie(IOwinContext context, string key, string value, CookieOptions options)
            {
                if (IsApiRequest(context?.Request))
                {
                    return;
                }

                _inner.AppendResponseCookie(context, key, value, options);
            }

            public void DeleteCookie(IOwinContext context, string key, CookieOptions options)
                => _inner.DeleteCookie(context, key, options);
        }
    }
}
