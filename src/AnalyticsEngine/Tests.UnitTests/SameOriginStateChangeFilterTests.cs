extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Web API's central cross-site request forgery defence (<see cref="SameOriginStateChangeFilter"/>): every
    /// state-changing call must come from the portal's own scripts, GETs are untouched, and only the deliberately
    /// anonymous endpoints that other systems call cross-site are exempt.
    /// </summary>
    [TestClass]
    public class SameOriginStateChangeFilterTests
    {
        [Authorize]
        public sealed class CsrfProbeController : ApiController
        {
            [HttpGet, Route("api/CsrfProbe")]
            public IHttpActionResult Read() => Ok("read");

            [HttpPut, Route("api/CsrfProbe")]
            public IHttpActionResult Replace() => Ok("replaced");

            [HttpPost, Route("api/CsrfProbe")]
            public IHttpActionResult Create() => Ok("created");

            [HttpDelete, Route("api/CsrfProbe")]
            public IHttpActionResult Remove() => Ok("removed");

            [AcceptVerbs("PATCH"), Route("api/CsrfProbe")]
            public IHttpActionResult Amend() => Ok("amended");
        }

        public sealed class CsrfAnonymousProbeController : ApiController
        {
            [HttpPost, Route("api/CsrfAnonymousProbe")]
            [AllowCrossSiteRequests("Synthetic: another system posts here.")]
            public IHttpActionResult Notify() => Ok("notified");
        }

        [Authorize]
        public sealed class CsrfSignedInOptOutProbeController : ApiController
        {
            [HttpPost, Route("api/CsrfSignedInOptOutProbe")]
            [AllowCrossSiteRequests("Synthetic: an opt-out that must be ignored.")]
            public IHttpActionResult Change() => Ok("changed");
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
            using (var config = new HttpConfiguration())
            {
                WebApiConfig.Register(config);
                WebApiConfig.AddCrossSiteRequestDefence(config);

                Assert.AreEqual(1, config.Filters.Count(f => f.Instance is SameOriginStateChangeFilter));
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
                        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{method} without the portal's header ran.");

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
                foreach (var method in new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Delete })
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
                using (var forged = await host.Client.SendAsync(Request(HttpMethod.Put, "api/TeamsAuthAPI", portalHeader: false, fetchSite: "cross-site")))
                    Assert.AreEqual(HttpStatusCode.Forbidden, forged.StatusCode);

                // The portal's own call still reaches the action: with no body it answers 404 without touching storage.
                using (var genuine = await host.Client.SendAsync(Request(HttpMethod.Put, "api/TeamsAuthAPI", portalHeader: true, fetchSite: "same-origin")))
                    Assert.AreEqual(HttpStatusCode.NotFound, genuine.StatusCode);
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
        public async Task TheCorsPolicy_NeverAllowsCredentials()
        {
            // The header check rests on this: a page on an org_urls origin may send the header after a preflight, but
            // without credentials, so its request can't carry the signed-in admin's cookie.
            var cors = new AllowCorsForOrgUrlsAttribute(() => Task.FromResult(new List<string> { "https://contoso.sharepoint.com", "*" }));

            using (var request = new HttpRequestMessage(HttpMethod.Options, "https://contoso.example/api/ImportConfig"))
            {
                var policy = await cors.GetCorsPolicyAsync(request, CancellationToken.None);
                Assert.IsFalse(policy.SupportsCredentials);
            }
        }
    }
}
