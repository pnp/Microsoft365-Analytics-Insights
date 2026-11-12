extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The two anonymous endpoints - the AITracker's <c>api/ImportConfig</c> and the calls webhook - answer junk with a
    /// 4xx rather than an exception, and don't write a trace per request (#853, R6-6).
    /// </summary>
    [TestClass]
    public class AnonymousEndpointTests
    {
        private const string ConfiguredConnectionString =
            "InstrumentationKey=00000000-0000-0000-0000-000000000853;IngestionEndpoint=https://contoso.invalid/";

        [DataTestMethod]
        [DataRow("")]
        [DataRow("%25%25not-base64%25%25")]
        [DataRow("SW5zdHJ1bWVudGF0aW9uS2V5PW5vdC1hLWd1aWQ=")]   // "InstrumentationKey=not-a-guid"
        [DataRow("QXBwbGljYXRpb25JZD0xMjM=")]                   // "ApplicationId=123": no key at all
        public async Task ImportConfig_MalformedInput_IsABadRequest(string encoded)
        {
            using (var host = ImportConfigHost())
            {
                var response = await host.Client.PostAsync("api/ImportConfig?appInsightsStringEncoded=" + encoded, new StringContent(string.Empty));

                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            }
        }

        [TestMethod]
        public async Task ImportConfig_AnotherResourcesKey_IsForbidden()
        {
            using (var host = ImportConfigHost())
            {
                var response = await host.Client.PostAsync("api/ImportConfig?appInsightsStringEncoded=" +
                    Encoded("InstrumentationKey=00000000-0000-0000-0000-000000000001"), new StringContent(string.Empty));

                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, await response.Content.ReadAsStringAsync());
            }
        }

        [TestMethod]
        public async Task ImportConfig_TheConfiguredKey_GetsTheTrackerConfig()
        {
            using (var host = ImportConfigHost())
            {
                var response = await host.Client.PostAsync("api/ImportConfig?appInsightsStringEncoded=" + Encoded(ConfiguredConnectionString),
                    new StringContent(string.Empty));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                StringAssert.Contains(await response.Content.ReadAsStringAsync(), "\"metadataRefreshMinutes\":42");
            }
        }

        [TestMethod]
        public async Task CallRecordWebhook_ValidationEcho_IsNotSniffed()
        {
            using (var host = WebhookHost())
            {
                var response = await host.Client.PostAsync("api/CallRecordWebhook?validationToken=" + Uri.EscapeDataString("<script>alert(1)</script>"),
                    new StringContent(string.Empty, Encoding.UTF8, "text/plain"));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("text/plain", response.Content.Headers.ContentType.MediaType);
                CollectionAssert.Contains(response.Headers.GetValues("X-Content-Type-Options").ToList(), "nosniff");
            }
        }

        /// <summary>
        /// Graph validates the webhook, and the importer warms it up, many times a day; strangers post junk to it. Each
        /// used to write an Information trace, to the console and to Application Insights.
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public async Task CallRecordWebhook_RepeatedPingsAndBadBodies_AreNotEachTraced()
        {
            string console;
            using (var host = WebhookHost())
            {
                console = await CaptureConsoleAsync(async () =>
                {
                    for (var i = 0; i < 3; i++)
                    {
                        await host.Client.PostAsync("api/CallRecordWebhook?validationToken=ping-" + i, new StringContent(string.Empty, Encoding.UTF8, "text/plain"));
                        await host.Client.PostAsync("api/CallRecordWebhook", new StringContent("{}", Encoding.UTF8, "application/json"));
                    }
                });
            }

            Assert.IsTrue(Regex.Matches(console, "validation request").Count <= 1, console);
            Assert.IsTrue(Regex.Matches(console, "invalid body").Count <= 1, console);
        }

        [TestMethod]
        public void SampledTrace_TracesTheFirstRequest_ThenOncePerIntervalWithTheCount()
        {
            var start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
            var sampled = new CallRecordWebhookController.SampledTrace(TimeSpan.FromMinutes(15));

            Assert.IsTrue(sampled.ShouldTrace(start, out var first, out var firstSince));
            Assert.AreEqual(1, first);
            Assert.AreEqual(start, firstSince);
            Assert.IsFalse(sampled.ShouldTrace(start.AddMinutes(1), out _, out _));
            Assert.IsFalse(sampled.ShouldTrace(start.AddMinutes(14), out _, out _));

            Assert.IsTrue(sampled.ShouldTrace(start.AddMinutes(15), out var next, out var nextSince));
            Assert.AreEqual(3, next, "The two that weren't traced, and this one.");
            Assert.AreEqual(start.AddMinutes(1), nextSince);
        }

        private static PortalTestHost ImportConfigHost() =>
            new PortalTestHost(new[] { typeof(ImportConfigController) }, PortalTestHost.Anonymous(), PortalAccessPolicy.NotEnforcing,
                create: t => new ImportConfigController(() => (ConfiguredConnectionString, 42)),
                productionExceptionHandler: true);

        private static PortalTestHost WebhookHost() =>
            new PortalTestHost(new[] { typeof(CallRecordWebhookController) }, PortalTestHost.Anonymous(), PortalAccessPolicy.NotEnforcing,
                productionExceptionHandler: true);

        private static string Encoded(string connectionString) =>
            Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(connectionString)));

        private static async Task<string> CaptureConsoleAsync(Func<Task> action)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            var captured = new StringWriter();
            Console.SetOut(captured);
            Console.SetError(captured);
            try
            {
                await action();
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
            return captured.ToString();
        }
    }
}
