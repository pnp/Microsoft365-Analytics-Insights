extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class StableReleaseCorePortTests
    {
        [TestMethod]
        public async Task CorePipelineReturnsOnlyStableErrorCodeAndReferenceWithoutExceptionDetails()
        {
            var failure = new InvalidOperationException("synthetic private detail: contoso-sql");
            using var host = new PortalTestHost(new[] { typeof(ThrowingController) },
                PortalTestHost.SignedIn(), PortalAccessPolicy.NotEnforcing,
                _ => new ThrowingController(failure), productionExceptionHandler: true);
            using var response = await host.Client.GetAsync("api/stable-port-error");
            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            var body = JObject.Parse(text);
            Assert.AreEqual("unexpected", (string)body["code"]);
            Assert.AreEqual(WebErrorReference.For(failure), (string)body["errorId"]);
            Assert.AreEqual(2, body.Count);
            Assert.IsFalse(text.Contains("contoso-sql") || text.Contains("InvalidOperationException"), text);
            Assert.IsTrue(response.Headers.CacheControl.NoStore);
        }

        [TestMethod]
        public async Task RepeatedCachedExceptionKeepsItsOriginalReference()
        {
            var failure = new InvalidOperationException("synthetic cached failure");
            using var host = new PortalTestHost(new[] { typeof(ThrowingController) },
                PortalTestHost.SignedIn(), PortalAccessPolicy.NotEnforcing,
                _ => new ThrowingController(failure), productionExceptionHandler: true);
            async Task<JObject> ReadAsync()
            {
                using var response = await host.Client.GetAsync("api/stable-port-error");
                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                return JObject.Parse(await response.Content.ReadAsStringAsync());
            }
            var first = await ReadAsync();
            var second = await ReadAsync();
            Assert.AreEqual((string)first["errorId"], (string)second["errorId"]);
            Assert.AreNotEqual(WebErrorReference.For(failure), WebErrorReference.For(new Exception("synthetic other")));
        }

        [TestMethod]
        public void CoreHtmlErrorUsesOriginalExceptionFeatureAndNoStore()
        {
            var failure = new InvalidOperationException("synthetic private detail");
            var context = new DefaultHttpContext();
            context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = failure });
            var controller = new HomeController(null)
            {
                ControllerContext = new ControllerContext { HttpContext = context },
            };
            Assert.IsInstanceOfType(controller.Error(), typeof(ViewResult));
            Assert.AreEqual(WebErrorReference.For(failure), (string)controller.ViewBag.ErrorId);
            Assert.AreEqual(500, context.Response.StatusCode);
            Assert.AreEqual("no-store, private", context.Response.Headers.CacheControl.ToString());
        }

        public sealed class ThrowingController : ControllerBase
        {
            private readonly Exception _failure;
            public ThrowingController(Exception failure) { _failure = failure; }
            [HttpGet("api/stable-port-error")]
            public IActionResult Get() => throw _failure;
        }
    }
}
