extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class CallRecordWebhookEndpointTests
    {
        [TestMethod]
        public void Post_UsesTheGraphWebhookRouteAndAspNetCoreResults()
        {
            var route = typeof(CallRecordWebhookController).GetCustomAttribute<RouteAttribute>();
            var post = typeof(CallRecordWebhookController).GetMethod(nameof(CallRecordWebhookController.Post));

            Assert.AreEqual("api/CallRecordWebhook", route?.Template);
            Assert.IsNotNull(post);
            Assert.IsNotNull(post.GetCustomAttribute<HttpPostAttribute>());
            Assert.AreEqual(typeof(Task<IActionResult>), post.ReturnType,
                "ASP.NET Core must write the webhook status, headers and plain-text body as an action result.");
            Assert.IsNotNull(post.GetParameters()[1].GetCustomAttribute<FromQueryAttribute>(),
                "Graph's validation token is supplied in the query string.");
        }
    }
}
