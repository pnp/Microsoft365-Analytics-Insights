extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using Common.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
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
    /// The portal page as the ASP.NET Core host actually serves it: through the same
    /// <see cref="PortalHosting"/> pipeline Program.cs uses, over HTTP.
    /// </summary>
    /// <remarks>
    /// The printed report's footer names the build from <c>window.o365AnalyticsBuildLabel</c>, which only
    /// exists because HomeController substitutes <see cref="BuildConstants.BuildLabel"/> into index.html
    /// as it serves it (aaba1ee9). Serve the file any other way and nothing fails: the page loads, prints,
    /// and the footer reads "(development build)" whatever is running. PortalBuildLabelTests pin the
    /// substitution as a function; these pin that every route to the page on this host reaches it.
    /// </remarks>
    [TestClass]
    public class PortalHostingTests
    {
        private const string SignedInHeader = "X-Synthetic-Signed-In";
        private const string TestScheme = "synthetic-portal-host";

        private const string PortalPage =
            "<!doctype html><html><head><script>window.o365AnalyticsBuildLabel = \"__BuildLabel__\";</script>"
            + "</head><body><div id=\"root\"></div></body></html>";

        private static string _webRoot;
        private static WebApplication _app;
        private static HttpClient _client;

        public TestContext TestContext { get; set; }

        [ClassInitialize]
        public static async Task StartHost(TestContext _)
        {
            _webRoot = Path.Combine(Path.GetTempPath(), "portal-hosting-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
            File.WriteAllText(Path.Combine(_webRoot, "index.html"), PortalPage);
            File.WriteAllText(Path.Combine(_webRoot, "assets", "app.js"), "console.log('synthetic asset');");

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
                .AddControllersWithViews()
                .AddWebApiCompatibleJson()
                // HomeController only: the Web assembly's other controllers reach for configuration and
                // databases this test has deliberately not set up.
                .ConfigureApplicationPartManager(parts =>
                {
                    parts.ApplicationParts.Clear();
                    parts.ApplicationParts.Add(new AssemblyPart(typeof(HomeController).Assembly));
                    parts.FeatureProviders.Clear();
                    parts.FeatureProviders.Add(new OnlyHomeController());
                });
            builder.Services.AddAuthentication(TestScheme)
                .AddScheme<AuthenticationSchemeOptions, HeaderSignInHandler>(TestScheme, null);
            builder.Services.AddAuthorization();

            _app = builder.Build();

            // Program.cs's order, less CORS (no cross-origin caller here).
            _app.UsePortalStaticFiles();
            _app.UseRouting();
            _app.UseAuthentication();
            _app.UseAuthorization();
            _app.MapPortalRoutes();

            await _app.StartAsync();
            _client = _app.GetTestClient();
        }

        [ClassCleanup]
        public static async Task StopHost()
        {
            _client?.Dispose();
            if (_app != null) await _app.DisposeAsync();
            MemoryCache.Default.Remove(HomeController.PortalIndexCacheKey);
            try { Directory.Delete(_webRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [TestMethod]
        public async Task TheRoot_ServesThePageStampedWithTheRunningBuild()
        {
            await AssertStampedPage("/");
        }

        [TestMethod]
        public async Task ThePagesFileName_GoesThroughHomeController_NotStaticFiles()
        {
            // UseStaticFiles would otherwise serve wwwroot/index.html raw - placeholder and all, to anyone.
            await AssertStampedPage("/index.html");
            await AssertStampedPage("/INDEX.HTML");
        }

        [TestMethod]
        public async Task AnUnmatchedPath_FallsBackToThePageThroughHomeController()
        {
            // Was MapFallbackToFile("index.html"): the raw file, unstamped and unauthenticated.
            await AssertStampedPage("/a/deep/link");
        }

        [TestMethod]
        public async Task StaticAssets_AreStillServedFromWwwroot()
        {
            using (var response = await _client.GetAsync("/assets/app.js"))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                StringAssert.Contains(await response.Content.ReadAsStringAsync(), "synthetic asset");
            }
        }

        [TestMethod]
        public async Task ThePage_IsNeverServedToAnAnonymousCaller()
        {
            foreach (var path in new[] { "/", "/index.html", "/a/deep/link" })
            {
                using (var response = await _client.GetAsync(path))
                {
                    Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
                        $"{path} must go through HomeController's [Authorize], as it does on the Web API 2 build.");
                }
            }
        }

        private async Task AssertStampedPage(string path)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, path))
            {
                request.Headers.Add(SignedInHeader, "true");

                using (var response = await _client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
                    Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType, path);
                    Assert.IsTrue(response.Headers.CacheControl?.NoStore == true,
                        $"{path}: index.html names content-hashed chunks and must always be revalidated.");

                    var html = await response.Content.ReadAsStringAsync();
                    Assert.IsFalse(html.Contains(HomeController.BuildLabelPlaceholder),
                        $"{path} served the page unstamped: the printed footer would say '(development build)'.");
                    StringAssert.Contains(html,
                        "window.o365AnalyticsBuildLabel = \""
                        + System.Web.HttpUtility.JavaScriptStringEncode(BuildConstants.BuildLabel) + "\";",
                        path);

                    TestContext.WriteLine($"{path} -> window.o365AnalyticsBuildLabel = \"{BuildConstants.BuildLabel}\"");
                }
            }
        }

        private sealed class OnlyHomeController : ControllerFeatureProvider
        {
            protected override bool IsController(TypeInfo typeInfo) =>
                typeInfo.AsType() == typeof(HomeController);
        }

        /// <summary>Signed in when the request carries <see cref="SignedInHeader"/>; otherwise anonymous, and challenged with a 401.</summary>
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
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
            }
        }
    }
}
