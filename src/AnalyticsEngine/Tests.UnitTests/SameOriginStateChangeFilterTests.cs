extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The API's central cross-site request forgery defence (<see cref="SameOriginStateChangeFilter"/>): every
    /// state-changing call must come from the portal's own scripts, GETs are untouched, and only the deliberately
    /// anonymous endpoints that other systems call cross-site are exempt.
    /// </summary>
    /// <remarks>
    /// net10: the Web API tests ported to the ASP.NET Core host, with the filter registered exactly as Program registers
    /// it (<see cref="CrossSiteRequestDefence.AddCrossSiteRequestDefence"/>). Here MVC and API controllers share one
    /// filter pipeline, so these tests also prove the MVC controllers are left alone, as the Web API filter left them.
    /// </remarks>
    [TestClass]
    public class SameOriginStateChangeFilterTests
    {
        [Authorize]
        [Route("api/CsrfProbe")]
        public sealed class CsrfProbeController : ControllerBase
        {
            [HttpGet]
            public IActionResult Read() => Ok("read");

            [HttpPut]
            public IActionResult Replace() => Ok("replaced");

            [HttpPost]
            public IActionResult Create() => Ok("created");

            [HttpDelete]
            public IActionResult Remove() => Ok("removed");

            [HttpPatch]
            public IActionResult Amend() => Ok("amended");
        }

        [Route("api/CsrfAnonymousProbe")]
        public sealed class CsrfAnonymousProbeController : ControllerBase
        {
            [HttpPost]
            [AllowCrossSiteRequests("Synthetic: another system posts here.")]
            public IActionResult Notify() => Ok("notified");
        }

        [Authorize]
        [Route("api/CsrfSignedInOptOutProbe")]
        public sealed class CsrfSignedInOptOutProbeController : ControllerBase
        {
            [HttpPost]
            [AllowCrossSiteRequests("Synthetic: an opt-out that must be ignored.")]
            public IActionResult Change() => Ok("changed");
        }

        /// <summary>An MVC controller, like <c>AccountController</c>: its browser navigations and form posts carry no header.</summary>
        [Authorize]
        [Route("CsrfMvcProbe")]
        public sealed class CsrfMvcProbeController : Controller
        {
            [HttpPost]
            public IActionResult Submit() => Ok("submitted");
        }

        private static PortalTestHost Host(params Type[] controllers) =>
            new PortalTestHost(controllers, PortalTestHost.SignedIn(PortalRoles.Administration), PortalAccessPolicy.Enforcing,
                crossSiteDefence: true);

        private static HttpRequestMessage Request(HttpMethod method, string url, bool portalHeader, string fetchSite = null)
        {
            var request = new HttpRequestMessage(method, url);
            if (portalHeader) request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            if (fetchSite != null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
            return request;
        }

        [TestMethod]
        public void TheProductionConfiguration_RegistersTheFilterOnce()
        {
            // Program's own registration, read from its source as the other Program.cs pins in this project are.
            StringAssert.Contains(File.ReadAllText(WebProjectFile("Program.cs")),
                "AddControllersWithViews(options => options.AddCrossSiteRequestDefence())");

            var services = new ServiceCollection();
            services.AddControllersWithViews(options =>
            {
                options.AddCrossSiteRequestDefence();
                options.AddCrossSiteRequestDefence();
            });
            using (var provider = services.BuildServiceProvider())
            {
                var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;
                Assert.AreEqual(1, filters.Count(f => f is SameOriginStateChangeFilter));
            }
        }

        [TestMethod]
        public async Task AStateChangingCall_FromAnotherSite_IsRefused_AndDoesNotRun()
        {
            using (var host = Host(typeof(CsrfProbeController)))
            {
                foreach (var method in new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Delete, new HttpMethod("PATCH") })
                {
                    // A form or a no-cors fetch: it can't add the header at all.
                    using (var response = await host.Client.SendAsync(Request(method, "api/CsrfProbe", portalHeader: false)))
                    {
                        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{method} without the portal's header ran.");
                        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync(), "A bare 403, from the filter, not the action.");
                    }

                    // A browser says where the request came from, and a page can't change that.
                    foreach (var site in new[] { "cross-site", "same-site", "none" })
                    {
                        using (var response = await host.Client.SendAsync(Request(method, "api/CsrfProbe", portalHeader: true, fetchSite: site)))
                            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{method} from Sec-Fetch-Site '{site}' ran.");
                    }
                }
            }
        }

        [TestMethod]
        public async Task AStateChangingCall_FromThePortal_IsAllowed()
        {
            using (var host = Host(typeof(CsrfProbeController)))
            {
                foreach (var method in new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Delete, new HttpMethod("PATCH") })
                {
                    using (var response = await host.Client.SendAsync(Request(method, "api/CsrfProbe", portalHeader: true, fetchSite: "same-origin")))
                        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"The portal's own {method} was refused.");

                    // An older browser sends no Sec-Fetch-Site; the header alone still proves a same-origin script.
                    using (var response = await host.Client.SendAsync(Request(method, "api/CsrfProbe", portalHeader: true)))
                        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"The portal's own {method} from an older browser was refused.");
                }
            }
        }

        [TestMethod]
        public async Task AGet_IsNotChecked()
        {
            using (var host = Host(typeof(CsrfProbeController)))
            {
                foreach (var fetchSite in new[] { null, "cross-site" })
                {
                    using (var response = await host.Client.SendAsync(Request(HttpMethod.Get, "api/CsrfProbe", portalHeader: false, fetchSite: fetchSite)))
                        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "A GET changes nothing and must not need the portal's header.");
                }
            }
        }

        [TestMethod]
        public async Task TeamsAuthPut_FromAnotherSite_IsRefused_BeforeItTouchesAnyToken()
        {
            using (var host = Host(typeof(TeamsAuthAPIController)))
            {
                // The finding: this stored and deleted per-Team refresh tokens with no same-origin check of its own.
                var forged = Request(HttpMethod.Put, "api/TeamsAuthAPI", portalHeader: false, fetchSite: "cross-site");
                forged.Content = new StringContent("null", Encoding.UTF8, "application/json");
                using (var response = await host.Client.SendAsync(forged))
                    Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);

                // The portal's own call still reaches the action: with no request in the body it answers 404 without
                // touching storage. (A JSON null, as ASP.NET Core answers a body-less PUT with 415 before the action.)
                var genuine = Request(HttpMethod.Put, "api/TeamsAuthAPI", portalHeader: true, fetchSite: "same-origin");
                genuine.Content = new StringContent("null", Encoding.UTF8, "application/json");
                using (var response = await host.Client.SendAsync(genuine))
                    Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            }
        }

        [TestMethod]
        public async Task AnAnonymousEndpointThatOptsOut_TakesCrossSiteCalls_ButASignedInOneCannotOptOut()
        {
            using (var host = Host(typeof(CsrfAnonymousProbeController), typeof(CsrfSignedInOptOutProbeController)))
            {
                using (var response = await host.Client.SendAsync(Request(HttpMethod.Post, "api/CsrfAnonymousProbe", portalHeader: false, fetchSite: "cross-site")))
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Graph and the AITracker call the anonymous endpoints from elsewhere.");

                using (var response = await host.Client.SendAsync(Request(HttpMethod.Post, "api/CsrfSignedInOptOutProbe", portalHeader: false, fetchSite: "cross-site")))
                    Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, "An action that needs sign-in must not be able to opt out.");
            }
        }

        [TestMethod]
        public async Task TheGraphWebhook_StillTakesGraphsCrossSiteValidationPost()
        {
            // net10: the real exempt endpoint, through the real pipeline: Graph sends no portal header and no cookie.
            const string token = "Validation: Testing client application reachability for subscription Request-Id: 00000000-0000-0000-0000-000000000000";
            using (var host = Host(typeof(CallRecordWebhookController)))
            {
                var validation = Request(HttpMethod.Post, "api/CallRecordWebhook?validationToken=" + Uri.EscapeDataString(token),
                    portalHeader: false, fetchSite: "cross-site");
                validation.Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain");
                using (var response = await host.Client.SendAsync(validation))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.AreEqual(token, await response.Content.ReadAsStringAsync());
                }
            }
        }

        [TestMethod]
        public async Task TheTrackersImportConfigPost_IsRouted_AndTakesCrossSiteCalls()
        {
            // net10: the other exempt endpoint, through the real pipeline. The AITracker POSTs here from SharePoint pages -
            // another origin, with neither the portal's header nor its cookie - and the port had left it without a route.
            const string connectionString = "InstrumentationKey=11111111-1111-1111-1111-111111111111;IngestionEndpoint=https://contoso.example/";
            var names = new[] { "AppInsightsConnectionString", "APPSETTING_AppInsightsConnectionString" };
            var original = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
                Environment.SetEnvironmentVariable("AppInsightsConnectionString", connectionString);
                AnalyticsConfig.UseForTesting(AnalyticsConfig.BuildForTesting().AsEnumerable());

                using (var host = new PortalTestHost(new[] { typeof(ImportConfigController) }, PortalTestHost.Anonymous(),
                    PortalAccessPolicy.Enforcing, crossSiteDefence: true))
                {
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(connectionString));
                    var request = Request(HttpMethod.Post, "api/ImportConfig?appInsightsStringEncoded=" + Uri.EscapeDataString(encoded),
                        portalHeader: false, fetchSite: "cross-site");
                    using (var response = await host.Client.SendAsync(request))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
                        Assert.IsNotNull(JObject.Parse(body)["metadataRefreshMinutes"], body);
                    }
                }
            }
            finally
            {
                foreach (var setting in original) Environment.SetEnvironmentVariable(setting.Key, setting.Value);
                AnalyticsConfig.ResetForTesting();
            }
        }

        [TestMethod]
        public async Task AnMvcController_IsNotChecked_AsTheWebApiFilterNeverCheckedOne()
        {
            // net10: sign-in, sign-out and the portal page are AccountController and HomeController - MVC controllers that
            // share this pipeline and are reached by navigations that cannot carry the header.
            using (var host = Host(typeof(CsrfMvcProbeController), typeof(CsrfProbeController)))
            {
                using (var response = await host.Client.SendAsync(Request(HttpMethod.Post, "CsrfMvcProbe", portalHeader: false, fetchSite: "cross-site")))
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "The filter must leave MVC controllers alone.");

                // The same host still checks its API controller.
                using (var response = await host.Client.SendAsync(Request(HttpMethod.Post, "api/CsrfProbe", portalHeader: false, fetchSite: "cross-site")))
                    Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            }

            Assert.IsTrue(typeof(Controller).IsAssignableFrom(typeof(AccountController)), "Sign-in and sign-out stay MVC actions.");
            Assert.IsTrue(typeof(Controller).IsAssignableFrom(typeof(HomeController)), "The portal page stays an MVC action.");
            Assert.IsFalse(PortalTestHost.WebControllers().Any(c => typeof(Controller).IsAssignableFrom(c)),
                "Every API controller is checked: none may derive from the MVC Controller the filter leaves alone.");
        }

        [TestMethod]
        public void OnlyTheGraphWebhookAndTheTrackersImportConfig_OptOut()
        {
            var optedOut = PortalTestHost.WebControllers()
                .SelectMany(controller => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.GetCustomAttribute<AllowCrossSiteRequestsAttribute>() != null
                        || controller.GetCustomAttribute<AllowCrossSiteRequestsAttribute>() != null)
                    .Select(m => new { Controller = controller, Method = m }))
                .ToList();

            CollectionAssert.AreEquivalent(
                new[] { "CallRecordWebhookController.Post", "ImportConfigController.Post" },
                optedOut.Select(a => a.Controller.Name + "." + a.Method.Name).ToArray(),
                "A new cross-site exemption needs a reason the reviewer agrees with, and a line here.");

            foreach (var action in optedOut)
            {
                Assert.IsNull(action.Controller.GetCustomAttribute<AuthorizeAttribute>(true), $"{action.Controller.Name} needs sign-in, so it can't be exempt.");
                Assert.IsNull(action.Method.GetCustomAttribute<AuthorizeAttribute>(true), $"{action.Method.Name} needs sign-in, so it can't be exempt.");
            }
        }

        [TestMethod]
        public void TheCorsPolicy_NeverAllowsCredentials()
        {
            // The header check rests on this: a page on an org_urls origin may send the header after a preflight, but
            // without credentials, so its request can't carry the signed-in admin's cookie.
            var builder = new CorsPolicyBuilder();
            OrgUrlCorsPolicy.Configure(builder);
            Assert.IsFalse(builder.Build().SupportsCredentials);

            // net10: and that is the policy Program registers.
            var program = File.ReadAllText(WebProjectFile("Program.cs"));
            StringAssert.Contains(program, "AddPolicy(OrgUrlCorsPolicy.PolicyName, OrgUrlCorsPolicy.Configure)");
            Assert.IsFalse(program.Contains("AllowCredentials"), "Program must not allow credentials on any CORS policy.");
        }

        private static string WebProjectFile(string name)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "Web", name);
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }

            Assert.Fail(name + " was not found.");
            return null;
        }
    }
}