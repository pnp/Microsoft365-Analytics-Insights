extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Caching;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Stable build 1860 (#853, R6-8) on the ASP.NET Core host: sign-out and the Teams connection are POST-only, and only from
    /// a page on this site (<see cref="RequireSameOriginFormPostAttribute"/>), through the routes Program.cs maps
    /// (<see cref="PortalHosting.MapPortalRoutes"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things the attribute checks in <c>PortalPermissionTests</c> cannot see. First, a GET: the Web API 2 build answers
    /// 404 because MVC 5 picks the Account route before the action, while ASP.NET Core passes a request whose method no
    /// action takes on to the next endpoint - here the portal fallback, which serves the page with a 200. A link to
    /// <c>/Account/SignOut</c> would then look like it worked and sign nobody out. Second, that the filter really runs in
    /// this host's pipeline, in front of the actions that challenge Entra ID and sign out, and that the site-wide
    /// <see cref="SameOriginStateChangeFilter"/> - which leaves MVC controllers alone - does not stand in for it.
    /// </para>
    /// <para>
    /// Fake "Cookies" and "OpenIdConnect" schemes stand in for the real ones and record what the actions asked of them, so
    /// nothing here reaches Entra ID.
    /// </para>
    /// </remarks>
    [TestClass]
    public class AccountFormPostCoreHostTests
    {
        private const string Host = "portal.contoso.example";
        private const string SignedInHeader = "X-Synthetic-Signed-In";
        private const string RolesHeader = "X-Synthetic-Roles";
        private const string TestScheme = "synthetic-account-host";
        private const string SignedOutHeader = "X-Synthetic-Signed-Out";
        private const string ChallengedHeader = "X-Synthetic-Challenged";

        private const string PortalPage =
            "<!doctype html><html><head><script>window.o365AnalyticsBuildLabel = \"__BuildLabel__\";</script>"
            + "</head><body><div id=\"root\">synthetic account host</div></body></html>";

        private static string _webRoot;
        private static WebApplication _app;
        private static HttpClient _client;

        [ClassInitialize]
        public static async Task StartHost(TestContext _)
        {
            _webRoot = Path.Combine(Path.GetTempPath(), "account-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_webRoot);
            File.WriteAllText(Path.Combine(_webRoot, "index.html"), PortalPage);

            // HomeController caches the stamped page process-wide; start clean so this host's file is served.
            MemoryCache.Default.Remove(HomeController.PortalIndexCacheKey);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = _webRoot,
                WebRootPath = _webRoot,
                EnvironmentName = Environments.Production,
            });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            builder.Services
                // What Program registers, so the site-wide cross-site defence is in the pipeline too.
                .AddControllersWithViews(options => options.AddCrossSiteRequestDefence())
                .AddWebApiCompatibleJson()
                .ConfigureApplicationPartManager(parts =>
                {
                    parts.ApplicationParts.Clear();
                    parts.ApplicationParts.Add(new AssemblyPart(typeof(AccountController).Assembly));
                    parts.FeatureProviders.Clear();
                    parts.FeatureProviders.Add(new OnlyAccountAndHomeControllers());
                });
            builder.Services.AddDataProtection();
            builder.Services.AddAuthentication(TestScheme)
                .AddScheme<AuthenticationSchemeOptions, HeaderSignInHandler>(TestScheme, null)
                .AddScheme<AuthenticationSchemeOptions, RecordingRemoteHandler>(CookieAuthenticationDefaults.AuthenticationScheme, null)
                .AddScheme<AuthenticationSchemeOptions, RecordingRemoteHandler>(OpenIdConnectDefaults.AuthenticationScheme, null);
            builder.Services.AddAuthorization();

            _app = builder.Build();

            // Program.cs's order, less CORS (no cross-origin caller here); role enforcement on, as in production.
            _app.UsePortalStaticFiles();
            _app.UseRouting();
            _app.Use(async (context, next) =>
            {
                context.Items[typeof(PortalAccessPolicy)] = PortalAccessPolicy.Enforcing;
                await next();
            });
            _app.UseAuthentication();
            _app.UseAuthorization();
            _app.MapPortalRoutes();

            await _app.StartAsync();
            _client = _app.GetTestClient();
            _client.BaseAddress = new Uri("https://" + Host + "/");
        }

        [ClassCleanup]
        public static async Task StopHost()
        {
            _client?.Dispose();
            if (_app != null) await _app.DisposeAsync();
            MemoryCache.Default.Remove(HomeController.PortalIndexCacheKey);
            try { Directory.Delete(_webRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [DataTestMethod]
        [DataRow("Account/SignOut")]
        [DataRow("Account/ConnectTeams")]
        public async Task AGet_IsA404_NotThePortalPage(string path)
        {
            // The premise: the portal fallback answers a path no route takes with the page itself.
            using (var deepLink = await SendAsync(HttpMethod.Get, "a/deep/link", signedIn: true))
            {
                Assert.AreEqual(HttpStatusCode.OK, deepLink.StatusCode);
                StringAssert.Contains(await deepLink.Content.ReadAsStringAsync(), "synthetic account host");
            }

            foreach (var signedIn in new[] { true, false })
            using (var response = await SendAsync(HttpMethod.Get, path, signedIn, roles: PortalRoles.Administration, headers: new[] { ("Sec-Fetch-Site", "same-origin") }))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, $"GET /{path} (signed in: {signedIn}), as on the Web API 2 build.");
                Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains("synthetic account host"), "The portal page must not answer it.");
                AssertNothingAskedOfEntra(response);
            }

            // Any other Account path no action takes is a 404 as well, as it is on the Web API 2 build.
            using (var unknown = await SendAsync(HttpMethod.Get, "Account/NoSuchAction", signedIn: true))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
            }
        }

        [TestMethod]
        public async Task SignOut_FromThisSite_SignsOutOfBothSchemes()
        {
            foreach (var sameSite in new[]
            {
                new[] { ("Sec-Fetch-Site", "same-origin") },
                new[] { ("Origin", "https://" + Host) },
                new[] { ("Referer", "https://" + Host + "/#/admin") },
                // The host only: behind App Service's front end the scheme and port the app sees need not be the browser's.
                new[] { ("Origin", "http://" + Host + ":8080") },
            })
            using (var response = await SendAsync(HttpMethod.Post, "Account/SignOut", signedIn: true, headers: sameSite))
            {
                var what = string.Join(", ", sameSite.Select(h => h.Item1 + ": " + h.Item2));
                Assert.AreNotEqual(HttpStatusCode.Forbidden, response.StatusCode, what);
                Assert.IsTrue(response.Headers.TryGetValues(SignedOutHeader, out var schemes), "Signed out: " + what);
                CollectionAssert.AreEquivalent(
                    new[] { CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme },
                    schemes.ToArray(), what);
            }
        }

        [TestMethod]
        public async Task SignOut_FromAnotherSite_OrUnsaid_IsRefused_AndSignsNobodyOut()
        {
            foreach (var crossSite in new[]
            {
                new[] { ("Sec-Fetch-Site", "cross-site"), ("Origin", "https://" + Host) },
                new[] { ("Sec-Fetch-Site", "same-site") },
                new[] { ("Origin", "https://contoso.example") },
                new[] { ("Origin", "null") },
                new[] { ("Referer", "https://" + Host + ".example/") },
                Array.Empty<(string, string)>(),
            })
            using (var response = await SendAsync(HttpMethod.Post, "Account/SignOut", signedIn: true, headers: crossSite))
            {
                var what = crossSite.Length == 0 ? "no Sec-Fetch-Site, Origin or Referer" : string.Join(", ", crossSite.Select(h => h.Item1 + ": " + h.Item2));
                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, what);
                Assert.AreEqual(RequireSameOriginFormPostAttribute.RefusedMessage, await response.Content.ReadAsStringAsync(), what);
                AssertNothingAskedOfEntra(response);
            }
        }

        [TestMethod]
        public async Task ConnectTeams_FromThisSite_ChallengesForAnAdministrator_AndRefusesAnyoneElse()
        {
            using (var admin = await SendAsync(HttpMethod.Post, "Account/ConnectTeams", signedIn: true, roles: PortalRoles.Administration,
                       headers: new[] { ("Sec-Fetch-Site", "same-origin") }))
            {
                Assert.IsTrue(admin.Headers.TryGetValues(ChallengedHeader, out var schemes), "An administrator's own form reaches the OIDC challenge.");
                CollectionAssert.AreEqual(new[] { OpenIdConnectDefaults.AuthenticationScheme }, schemes.ToArray());
            }

            using (var crossSite = await SendAsync(HttpMethod.Post, "Account/ConnectTeams", signedIn: true, roles: PortalRoles.Administration,
                       headers: new[] { ("Sec-Fetch-Site", "cross-site") }))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, crossSite.StatusCode,
                    "With consent already granted Entra ID asks nothing, so another site's form must not start the connection.");
                AssertNothingAskedOfEntra(crossSite);
            }

            using (var notAdmin = await SendAsync(HttpMethod.Post, "Account/ConnectTeams", signedIn: true, roles: PortalRoles.SeePii,
                       headers: new[] { ("Sec-Fetch-Site", "same-origin") }))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, notAdmin.StatusCode, "The connection still needs Administration.");
                AssertNothingAskedOfEntra(notAdmin);
            }

            using (var anonymous = await SendAsync(HttpMethod.Post, "Account/ConnectTeams", signedIn: false,
                       headers: new[] { ("Sec-Fetch-Site", "same-origin") }))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode, "[Authorize] challenges the default scheme first.");
                Assert.IsFalse(anonymous.Headers.Contains(ChallengedHeader), "Not the Teams connection's own challenge.");
            }
        }

        [TestMethod]
        public void TheSiteWideDefence_StillLeavesTheAccountControllerToItsOwnFilter()
        {
            // SameOriginStateChangeFilter checks API controllers only; a form post carries no X-Requested-With. If that ever
            // changed, the portal's own sign-out form would be refused - so the account actions keep their own filter.
            Assert.IsTrue(typeof(Microsoft.AspNetCore.Mvc.Controller).IsAssignableFrom(typeof(AccountController)));
            foreach (var name in new[] { nameof(AccountController.SignOut), nameof(AccountController.ConnectTeams) })
            {
                var action = typeof(AccountController).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                Assert.IsNotNull(action.GetCustomAttribute<RequireSameOriginFormPostAttribute>(), name);
                Assert.IsNotNull(action.GetCustomAttribute<Microsoft.AspNetCore.Mvc.HttpPostAttribute>(), name);
            }
        }

        private static void AssertNothingAskedOfEntra(HttpResponseMessage response)
        {
            Assert.IsFalse(response.Headers.Contains(SignedOutHeader), "Nobody may be signed out.");
            Assert.IsFalse(response.Headers.Contains(ChallengedHeader), "Entra ID must not be asked for anything.");
        }

        private static async Task<HttpResponseMessage> SendAsync(
            HttpMethod method, string path, bool signedIn, string roles = null, params (string Name, string Value)[] headers)
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                if (signedIn) request.Headers.Add(SignedInHeader, "true");
                if (roles != null) request.Headers.Add(RolesHeader, roles);
                foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Name, header.Value);
                // The portal posts an empty form (src/auth/serverPost.ts).
                if (method == HttpMethod.Post) request.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
                return await _client.SendAsync(request);
            }
        }

        private sealed class OnlyAccountAndHomeControllers : ControllerFeatureProvider
        {
            protected override bool IsController(TypeInfo typeInfo) =>
                typeInfo.AsType() == typeof(AccountController) || typeInfo.AsType() == typeof(HomeController);
        }

        /// <summary>Signed in when the request carries <see cref="SignedInHeader"/>, with the roles in <see cref="RolesHeader"/>.</summary>
        private sealed class HeaderSignInHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public HeaderSignInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
                : base(options, logger, encoder)
            {
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                if (!Request.Headers.ContainsKey(SignedInHeader)) return Task.FromResult(AuthenticateResult.NoResult());

                var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin@contoso.com") }, Scheme.Name);
                foreach (var role in Request.Headers[RolesHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    identity.AddClaim(new Claim("roles", role.Trim()));
                }
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
            }
        }

        /// <summary>Stands in for the cookie and OpenID Connect schemes: records a sign-out or a challenge in a response header.</summary>
        private sealed class RecordingRemoteHandler : SignOutAuthenticationHandler<AuthenticationSchemeOptions>
        {
            public RecordingRemoteHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
                : base(options, logger, encoder)
            {
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

            protected override Task HandleSignOutAsync(AuthenticationProperties properties)
            {
                Response.Headers.Append(SignedOutHeader, Scheme.Name);
                return Task.CompletedTask;
            }

            protected override Task HandleChallengeAsync(AuthenticationProperties properties)
            {
                Response.Headers.Append(ChallengedHeader, Scheme.Name);
                Response.Redirect("https://login.contoso.example/authorize");
                return Task.CompletedTask;
            }
        }
    }
}
