extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using Common.Entities.Models;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Owin;
using Microsoft.Owin.Builder;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.DataHandler;
using Microsoft.Owin.Security.DataProtection;
using Microsoft.Owin.Security.OpenIdConnect;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Owin;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Issue #670 end to end: the site's own OIDC options, running in the real Katana cookie and OpenID Connect
    /// middleware, in process, against a stand-in identity provider.
    /// </summary>
    /// <remarks>
    /// <see cref="DelegatedGraphConsentTests"/> drives the notification delegates one at a time. This proves the
    /// parts only the middleware can: that a sign-in whose code could not be redeemed for the Teams scopes still
    /// ends signed in, and that <c>DelegatedGraphConsent.ReadStateProperties</c> - which mirrors an internal
    /// Katana format - reads the <c>state</c> Katana really writes.
    /// </remarks>
    [TestClass]
    public class DelegatedGraphConsentPipelineTests
    {
        private const string Issuer = "https://sts.contoso.example/00000000-0000-0000-0000-000000000000/";
        private const string AuthorizeEndpoint = "https://login.contoso.example/authorize";

        private static readonly SymmetricSecurityKey SigningKey =
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('k', 64)));

        [TestMethod]
        public async Task SignIn_AsksEntraOnlyForSignInScopes()
        {
            var site = new Site(RedeemShouldNotHappen);

            var challenge = await site.SendAsync("GET", "/insights");

            Assert.AreEqual(302, challenge.StatusCode);
            StringAssert.StartsWith(challenge.Location, AuthorizeEndpoint + "?");
            Assert.AreEqual("openid email profile", challenge.LocationQuery["scope"]);
            Assert.AreEqual("code id_token", challenge.LocationQuery["response_type"]);
            Assert.IsFalse(challenge.LocationQuery.ContainsKey("login_hint"));
        }

        [TestMethod]
        public async Task TeamsConnect_WhenSignedIn_HintsTheSignedInAccountFromTheAuthCookie()
        {
            var site = new Site(RedeemShouldNotHappen);
            var signIn = await site.CallbackAsync(await site.SendAsync("GET", "/insights"));
            Assert.IsNotNull(signIn.SignedInIdentity());

            var challenge = await site.SendAsync("GET", "/Account/ConnectTeams", cookieHeader: signIn.AuthCookieHeader);

            Assert.AreEqual(302, challenge.StatusCode);
            Assert.AreEqual(DelegatedGraphConsent.TeamsConnectScopes, challenge.LocationQuery["scope"]);
            Assert.AreEqual(DelegatedGraphConsentTests.AdminUpn, challenge.LocationQuery["login_hint"],
                "Entra ID should reuse the admin's session rather than show an account picker.");
        }

        [TestMethod]
        public async Task SignIn_WhenTheTenantHasNotGrantedTheTeamsPermissions_StillSignsIn()
        {
            // The token endpoint answers exactly as it did for the customer. A sign-in must not even ask.
            var site = new Site((code, scopes) => throw DelegatedGraphConsentTests.ConsentRequired());

            var challenge = await site.SendAsync("GET", "/insights");
            var callback = await site.CallbackAsync(challenge);

            Assert.AreEqual(302, callback.StatusCode);
            Assert.AreEqual("https://contoso.example/insights", callback.Location);
            var identity = callback.SignedInIdentity();
            Assert.IsNotNull(identity, "The sign-in must complete and issue the auth cookie.");
            Assert.AreEqual(DelegatedGraphConsentTests.AdminUpn, identity.Name);
            Assert.IsNull(identity.FindFirst(GraphTokenClaims.RefreshToken));
            Assert.AreEqual(0, site.RedeemCalls);
        }

        [TestMethod]
        public async Task TeamsConnect_AsksEntraForTheTeamsPermissions_AndItsStateIsReadable()
        {
            var site = new Site(RedeemShouldNotHappen);

            var challenge = await site.SendAsync("GET", "/Account/ConnectTeams");

            Assert.AreEqual(302, challenge.StatusCode);
            Assert.AreEqual(DelegatedGraphConsent.TeamsConnectScopes, challenge.LocationQuery["scope"]);

            // Katana's own state, read back the way the AuthenticationFailed handler reads it.
            var properties = DelegatedGraphConsent.ReadStateProperties(challenge.LocationQuery["state"], site.Options.StateDataFormat);
            Assert.IsTrue(DelegatedGraphConsent.IsTeamsConnect(properties));
            Assert.AreEqual(DelegatedGraphConsent.TeamsPermissionsRoute, properties.RedirectUri);
        }

        [TestMethod]
        public async Task TeamsConnect_WithoutConsent_EndsSignedIn_BackOnTheTeamsPage()
        {
            var site = new Site((code, scopes) => throw DelegatedGraphConsentTests.ConsentRequired());

            var challenge = await site.SendAsync("GET", "/Account/ConnectTeams");
            var callback = await site.CallbackAsync(challenge);

            Assert.AreEqual(302, callback.StatusCode);
            Assert.AreEqual(
                "/#/admin/teams-permissions?teamsConnect=consent_required&teamsConnectError=AADSTS65001",
                callback.Location);
            Assert.IsNotNull(callback.SignedInIdentity(), "A failed Teams connection must leave the admin signed in.");
            Assert.AreEqual(1, site.RedeemCalls);
        }

        [TestMethod]
        public async Task TeamsConnect_Succeeds_TheRefreshTokenRidesInTheAuthCookie()
        {
            var site = new Site((code, scopes) =>
            {
                Assert.AreEqual(DelegatedGraphConsent.TeamsConnectScopes, scopes);
                return Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-access", RefreshToken = "synthetic-refresh" });
            });

            var challenge = await site.SendAsync("GET", "/Account/ConnectTeams");
            var callback = await site.CallbackAsync(challenge);

            Assert.AreEqual(DelegatedGraphConsent.TeamsPermissionsRoute, callback.Location);
            Assert.AreEqual("synthetic-refresh", callback.SignedInIdentity()?.FindFirst(GraphTokenClaims.RefreshToken)?.Value);
        }

        [TestMethod]
        public async Task TeamsConnect_DeclinedAtEntra_ReturnsToTheTeamsPage()
        {
            var site = new Site(RedeemShouldNotHappen);

            var challenge = await site.SendAsync("GET", "/Account/ConnectTeams");
            var callback = await site.SendAsync("POST", "/", new Dictionary<string, string>
            {
                ["error"] = "access_denied",
                ["error_description"] = "AADSTS65004: User declined to consent to access the app.",
                ["state"] = challenge.LocationQuery["state"],
            });

            Assert.AreEqual(302, callback.StatusCode);
            Assert.AreEqual(
                "/#/admin/teams-permissions?teamsConnect=access_denied&teamsConnectError=AADSTS65004",
                callback.Location);
        }

        [TestMethod]
        public async Task SignIn_DeclinedAtEntra_KeepsKatanasDefaultHandling()
        {
            var site = new Site(RedeemShouldNotHappen);
            var challenge = await site.SendAsync("GET", "/insights");

            Exception thrown = null;
            try
            {
                await site.SendAsync("POST", "/", new Dictionary<string, string>
                {
                    ["error"] = "access_denied",
                    ["error_description"] = "AADSTS65004: User declined to consent to access the app.",
                    ["state"] = challenge.LocationQuery["state"],
                });
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            Assert.IsInstanceOfType(thrown, typeof(OpenIdConnectProtocolException),
                "Only the Teams connection's failures are handled here; a failed sign-in behaves exactly as before.");
        }

        private static Task<RefreshOAuthToken> RedeemShouldNotHappen(string code, string scopes) =>
            throw new AssertFailedException("The authorisation code was not expected to be redeemed.");

        /// <summary>The site's auth pipeline, hosted in memory.</summary>
        private sealed class Site
        {
            private readonly Func<IDictionary<string, object>, Task> _app;

            public Site(Func<string, string, Task<RefreshOAuthToken>> redeem)
            {
                var capture = new DelegatedGraphTokenCapture(
                    (code, scopes) =>
                    {
                        RedeemCalls++;
                        return redeem(code, scopes);
                    },
                    report: (message, ex) => { });

                Options = Startup.CreateOpenIdConnectOptions(
                    DelegatedGraphConsentTests.ClientId,
                    "https://login.contoso.example/00000000-0000-0000-0000-000000000000",
                    DelegatedGraphConsentTests.WebAppUrl,
                    capture,
                    new CookieManager());

                // A static configuration, so nothing is fetched from a metadata endpoint.
                Options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Issuer,
                    AuthorizationEndpoint = AuthorizeEndpoint,
                    TokenEndpoint = "https://login.contoso.example/token",
                    EndSessionEndpoint = "https://login.contoso.example/logout",
                };
                Options.Configuration.SigningKeys.Add(SigningKey);

                var app = new AppBuilder();
                app.SetDataProtectionProvider(new PassThroughProvider());
                Startup.ConfigureAuth(app, Options);
                app.Run(StandInForMvc);
                _app = (Func<IDictionary<string, object>, Task>)app.Build(typeof(Func<IDictionary<string, object>, Task>));
            }

            public OpenIdConnectAuthenticationOptions Options { get; }

            public int RedeemCalls { get; private set; }

            /// <summary>What the MVC side does: <c>AccountController.ConnectTeams</c>, and [Authorize]'d pages.</summary>
            private static Task StandInForMvc(IOwinContext context)
            {
                if (context.Request.Path.Equals(new PathString("/Account/ConnectTeams")))
                {
                    context.Authentication.Challenge(
                        DelegatedGraphConsent.CreateTeamsConnectProperties(),
                        OpenIdConnectAuthenticationDefaults.AuthenticationType);
                    return Task.CompletedTask;
                }

                context.Response.StatusCode = context.Authentication.User?.Identity?.IsAuthenticated == true ? 200 : 401;
                return Task.CompletedTask;
            }

            /// <summary>
            /// Plays the identity provider's side of a successful sign-in: posts back an authorisation code and an
            /// ID token for the challenge, carrying the nonce cookie the challenge set, as a browser would.
            /// </summary>
            public Task<Response> CallbackAsync(Response challenge)
            {
                const string code = "synthetic-code";
                return SendAsync("POST", "/", new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["id_token"] = IdToken(challenge.LocationQuery["nonce"], code),
                    ["state"] = challenge.LocationQuery["state"],
                    ["session_state"] = "synthetic-session",
                }, challenge.CookieHeader);
            }

            public async Task<Response> SendAsync(string method, string path, IDictionary<string, string> form = null, string cookieHeader = null)
            {
                var requestHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Host"] = new[] { "contoso.example" },
                };
                Stream body = Stream.Null;
                if (form != null)
                {
                    var encoded = string.Join("&", form.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
                    body = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
                    requestHeaders["Content-Type"] = new[] { "application/x-www-form-urlencoded" };
                }
                if (cookieHeader != null)
                {
                    requestHeaders["Cookie"] = new[] { cookieHeader };
                }

                var onSendingHeaders = new List<Tuple<Action<object>, object>>();
                var environment = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["owin.Version"] = "1.0",
                    ["owin.CallCancelled"] = CancellationToken.None,
                    ["owin.RequestMethod"] = method,
                    ["owin.RequestScheme"] = "https",
                    ["owin.RequestPathBase"] = string.Empty,
                    ["owin.RequestPath"] = path,
                    ["owin.RequestQueryString"] = string.Empty,
                    ["owin.RequestProtocol"] = "HTTP/1.1",
                    ["owin.RequestHeaders"] = requestHeaders,
                    ["owin.RequestBody"] = body,
                    ["owin.ResponseHeaders"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
                    ["owin.ResponseBody"] = new MemoryStream(),
                    ["server.OnSendingHeaders"] = new Action<Action<object>, object>(
                        (callback, state) => onSendingHeaders.Add(Tuple.Create(callback, state))),
                };

                await _app(environment);

                // As a host does when the response starts: last registered runs first.
                for (var i = onSendingHeaders.Count - 1; i >= 0; i--)
                {
                    onSendingHeaders[i].Item1(onSendingHeaders[i].Item2);
                }

                return new Response(new OwinContext(environment));
            }

            private static string IdToken(string nonce, string code)
            {
                var now = DateTime.UtcNow;
                var token = new JwtSecurityTokenHandler().CreateJwtSecurityToken(
                    issuer: Issuer,
                    audience: DelegatedGraphConsentTests.ClientId,
                    subject: new ClaimsIdentity(new[]
                    {
                        new Claim("sub", "synthetic-subject"),
                        new Claim("unique_name", DelegatedGraphConsentTests.AdminUpn),
                        new Claim("upn", DelegatedGraphConsentTests.AdminUpn),
                        new Claim("nonce", nonce),
                        new Claim("c_hash", CodeHash(code)),
                    }),
                    notBefore: now.AddMinutes(-1),
                    expires: now.AddMinutes(30),
                    issuedAt: now.AddMinutes(-1),
                    signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));

                return new JwtSecurityTokenHandler().WriteToken(token);
            }

            /// <summary>OpenID Connect's <c>c_hash</c>: the left half of the code's SHA-256, base64url-encoded.</summary>
            private static string CodeHash(string code)
            {
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(code));
                    return Base64UrlEncoder.Encode(hash, 0, hash.Length / 2);
                }
            }
        }

        private sealed class Response
        {
            public Response(IOwinContext context)
            {
                StatusCode = context.Response.StatusCode;
                Location = context.Response.Headers["Location"];
                SetCookies = context.Response.Headers.GetValues("Set-Cookie")?.ToList() ?? new List<string>();
            }

            public int StatusCode { get; }

            public string Location { get; }

            public IList<string> SetCookies { get; }

            public IDictionary<string, string> LocationQuery
            {
                get
                {
                    var query = new Uri(Location).Query.TrimStart('?');
                    return query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(pair => pair.Split(new[] { '=' }, 2))
                        .ToDictionary(
                            pair => Uri.UnescapeDataString(pair[0]),
                            pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty);
                }
            }

            /// <summary>The cookies this response set, as the browser would send them back.</summary>
            public string CookieHeader => string.Join("; ", SetCookies.Select(cookie => cookie.Split(';')[0]));

            /// <summary>Just the auth cookie this response issued (every chunk of it), as the browser would send it back.</summary>
            public string AuthCookieHeader => string.Join("; ", SetCookies
                .Select(cookie => cookie.Split(';')[0])
                .Where(pair => pair.StartsWith(".AspNet.Cookies", StringComparison.Ordinal)));

            /// <summary>The identity in the auth cookie this response issued, or <c>null</c> if it didn't sign anyone in.</summary>
            public ClaimsIdentity SignedInIdentity()
            {
                const string prefix = ".AspNet.Cookies=";
                var cookie = SetCookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal));
                if (cookie == null || cookie.Length == prefix.Length)
                {
                    return null;
                }

                var ticket = new TicketDataFormat(new DelegatedGraphConsentTests.PassThroughProtector())
                    .Unprotect(Uri.UnescapeDataString(cookie.Substring(prefix.Length)));
                return ticket?.Identity;
            }
        }

        private sealed class PassThroughProvider : IDataProtectionProvider
        {
            public IDataProtector Create(params string[] purposes) => new DelegatedGraphConsentTests.PassThroughProtector();
        }
    }
}
