using Common.Entities.Config;
using Common.Entities.Models;
using Common.Entities.Redis;
using Common.Entities.Redis.Auth;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Owin;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.OpenIdConnect;
using Owin;
using System.Security.Claims;
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
                    // Stops the cancelled API challenges below leaving orphan nonce cookies behind.
                    CookieManager = new ApiSafeCookieManager(app.GetDefaultCookieManager()),
                    Notifications = new OpenIdConnectAuthenticationNotifications()
                    {
                        // An expired session must not turn an API call into a sign-in redirect.
                        //
                        // The OIDC middleware runs in Active mode, so it converts the 401 from an [Authorize]'d
                        // controller into a 302 to login.microsoftonline.com. A top-level navigation handles that
                        // fine, but the SPA's fetch() follows the redirect cross-origin, the login page carries no
                        // CORS headers, and the call rejects with an opaque "TypeError: Failed to fetch" - so the
                        // portal just breaks with no hint that the user simply needs to sign in again.
                        //
                        // For API requests we therefore suppress the redirect and leave the plain 401 in place.
                        RedirectToIdentityProvider = context =>
                        {
                            if (context.ProtocolMessage.RequestType == OpenIdConnectRequestType.Authentication
                                && IsApiRequest(context.OwinContext.Request))
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
                            }

                            return Task.CompletedTask;
                        },

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
        /// on this site already carries the Graph refresh token, the accumulated Cookie header can grow past
        /// IIS/proxy limits - turning a recoverable "please sign in again" into a 400 Request Too Large that
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
