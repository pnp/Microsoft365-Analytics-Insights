extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDataLookup;
using Common.Entities.LicenceActivity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests
{
    /// <summary>
    /// net10: the error bodies #635 and #655 gave the portal, read off this host's wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The portal translates a server error from its stable <c>code</c> and keeps the facts beside it
    /// (<c>reference</c>, <c>category</c>, <c>upn</c>); the English <c>message</c> is only the fallback. On
    /// main these controllers are Web API 2, and the portal's serverAuthoredText gates pin the codes by
    /// reading the C#. That proves what the source says, not what an ASP.NET Core controller sends: here the
    /// reply is built differently (<c>StatusCode(...)</c>, an <see cref="ObjectResult"/>) and serialized by
    /// <c>AddWebApiCompatibleJson</c>. A property renamed or dropped on the way would fail nothing else - the
    /// page would quietly fall back to English, or lose the run reference an admin quotes.
    /// </para>
    /// <para>
    /// Licence activity goes through the real pipeline (<see cref="LicenceActivityHttpHost"/>): routing, model
    /// binding and the production serializer. The user lookup controller has no HTTP harness, so its results
    /// are serialized with the settings that same serializer installs.
    /// </para>
    /// </remarks>
    [TestClass]
    public class AspNetCoreErrorBodyContractTests
    {
        [TestMethod]
        public async Task LicenceActivity_EveryErrorBody_CarriesItsCodeBesideTheUnchangedMessage()
        {
            var now = LicenceActivityTests.Now;
            var sources = new LicenceActivitySources { UserMetadata = true, UsageReports = true, NowUtc = now };
            using (var host = new LicenceActivityHttpHost(new SampleStore(), sources, () => now))
            {
                await AssertCodedError(host.Client, "api/LicenceActivity/overview?from=2000-06-29&to=2000-06-30",
                    HttpStatusCode.BadRequest, "dateRange",
                    "Choose 7 to 180 inclusive UTC dates, ending before today. Custom ranges are never rounded.");

                // Rejected by ASP.NET Core's model binding rather than by the query rules - the path that changed
                // shape between Web API 2 and ASP.NET Core - and still worded, and coded, by the controller.
                await AssertCodedError(host.Client, "api/LicenceActivity/overview?departmentId=not-an-integer",
                    HttpStatusCode.BadRequest, "invalidRequest",
                    "That request wasn't valid. Check the selected dates and filters.");

                var overviewId = (string)(await Json(host.Client, "api/LicenceActivity/overview"))["snapshotId"];
                await AssertCodedError(host.Client, $"api/LicenceActivity/users?overviewId={overviewId}&licenceTypeId=1&pageSize=101",
                    HttpStatusCode.BadRequest, "invalidPaging",
                    "Top and pageSize must be 1 to 100; page must be 1 to 10000.");
                await AssertCodedError(host.Client, $"api/LicenceActivity/users?overviewId={overviewId}&licenceTypeId=999",
                    HttpStatusCode.NotFound, "licenceNotOnScreen",
                    "That licence is not part of the figures currently on screen. Refresh the report and try again.");

                var usersId = (string)(await Json(host.Client, $"api/LicenceActivity/users?overviewId={overviewId}&licenceTypeId=1"))["snapshotId"];
                var otherOverviewId = (string)(await Json(host.Client, "api/LicenceActivity/overview?departmentId=7"))["snapshotId"];
                await AssertCodedError(host.Client, $"api/LicenceActivity/export?overviewId={otherOverviewId}&usersId={usersId}",
                    HttpStatusCode.Conflict, "summaryUsersMismatch",
                    "The summary and the user list are no longer from the same set of figures. Refresh the report before exporting.");

                now = now.AddMinutes(6);
                await AssertCodedError(host.Client, $"api/LicenceActivity/export?overviewId={overviewId}",
                    HttpStatusCode.Gone, "figuresExpiredForAction",
                    "These figures are no longer being held. Refresh the report to bring back an up-to-date set before continuing or exporting.");

                sources.UserMetadata = false;
                await AssertCodedError(host.Client, "api/LicenceActivity/overview",
                    HttpStatusCode.PreconditionFailed, "userDetailsImportOff",
                    "This report needs the user details import turned on, so that licences can be matched to the people who hold them.");
            }
        }

        [TestMethod]
        public async Task LicenceActivity_FailedRun_SaysLoadFailedAndCarriesTheRunReference()
        {
            var now = LicenceActivityTests.Now;
            var sources = new LicenceActivitySources { UserMetadata = true, UsageReports = true, NowUtc = now };
            var store = new SampleStore
            {
                Overview = () => Task.FromException<LicenceActivityOverview>(new InvalidOperationException("private filter")),
            };

            using (var host = new LicenceActivityHttpHost(store, sources, () => now))
            {
                var response = await host.Client.GetAsync("api/LicenceActivity/overview");
                var body = await response.Content.ReadAsStringAsync();

                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, body);
                Assert.AreEqual(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta, "A failed run is retryable.");
                var json = JObject.Parse(body);
                CollectionAssert.AreEquivalent(new[] { "code", "message", "reference" }, json.Properties().Select(p => p.Name).ToArray(), body);
                Assert.AreEqual("loadFailed", (string)json["code"]);
                var reference = (string)json["reference"];
                Assert.IsFalse(string.IsNullOrWhiteSpace(reference), "The run reference is the fact an admin quotes to find the failure in the logs.");
                Assert.AreEqual("Licence activity could not be loaded. Retry the request. Reference: " + reference, (string)json["message"],
                    "The message is unchanged; the reference travels beside it for the portal's own sentence.");
                Assert.IsFalse(body.Contains("private filter"), "The underlying exception must not reach the browser.");
            }
        }

        [TestMethod]
        public async Task UserDataLookup_ErrorBodies_CarryTheirCodeAndTheFactsTheSentenceNeeds()
        {
            var controller = new UserDataLookupAPIController(new UserDataLookupService(new InMemoryUserDataLookupQuery()));

            AssertLookupError(await controller.Summary(""), HttpStatusCode.BadRequest,
                "missingUpn", "A 'upn' query parameter is required.", category: null, upn: null);

            // The UPN is the normalised one the service searched for, so "no user found" can name who was looked up.
            AssertLookupError(await controller.Summary("  Nobody@Contoso.com "), HttpStatusCode.NotFound,
                "userNotFound", "No user found with UPN 'Nobody@Contoso.com'.", category: null, upn: "Nobody@Contoso.com");

            AssertLookupError(await controller.Detail("someone@contoso.com", "not-a-category"), HttpStatusCode.BadRequest,
                "unknownCategory", "Unknown category 'not-a-category'.", category: "not-a-category", upn: null);

            var noDrilldown = UserDataLookupRules.Categories.FirstOrDefault(c => !c.SupportsDetail)?.Key;
            Assert.IsNotNull(noDrilldown, "The test needs a category without drill-down.");
            AssertLookupError(await controller.Detail("someone@contoso.com", noDrilldown), HttpStatusCode.BadRequest,
                "categoryNoDrilldown", $"Category '{noDrilldown}' does not support drill-down.", category: noDrilldown, upn: null);
        }

        private static async Task AssertCodedError(HttpClient client, string url, HttpStatusCode status, string code, string message)
        {
            var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(status, response.StatusCode, $"{url}: {body}");
            var json = JObject.Parse(body);
            CollectionAssert.AreEquivalent(new[] { "code", "message" }, json.Properties().Select(p => p.Name).ToArray(), $"{url}: {body}");
            Assert.AreEqual(code, (string)json["code"], url);
            Assert.AreEqual(message, (string)json["message"], url);
            Assert.IsTrue(response.Headers.CacheControl?.NoStore == true, $"{url}: an error reply must not be cached.");
        }

        private static void AssertLookupError(IActionResult result, HttpStatusCode status, string code, string message, string category, string upn)
        {
            var reply = result as ObjectResult;
            Assert.IsNotNull(reply, $"{code}: expected a status-code reply, got {result?.GetType().Name}.");
            Assert.AreEqual((int)status, reply.StatusCode, code);

            var json = JObject.Parse(JsonConvert.SerializeObject(reply.Value, WebApiCompatibleJson.Apply(new JsonSerializerSettings())));
            CollectionAssert.AreEquivalent(new[] { "code", "message", "category", "upn" }, json.Properties().Select(p => p.Name).ToArray(), json.ToString());
            Assert.AreEqual(code, (string)json["code"]);
            Assert.AreEqual(message, (string)json["message"]);
            Assert.AreEqual(category, (string)json["category"], code);
            Assert.AreEqual(upn, (string)json["upn"], code);
        }

        private static async Task<JObject> Json(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{url}: {body}");
            return JObject.Parse(body);
        }

        private sealed class SampleStore : ILicenceActivityStore
        {
            internal Func<Task<LicenceActivityOverview>> Overview = () => Task.FromResult(LicenceActivityTests.SampleOverview());

            public Task<LicenceActivityOverview> LoadOverviewAsync(LicenceActivityQuery query, LicenceActivitySources sources,
                ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken) => Overview();

            public Task<LicenceActivityUsers> LoadUsersAsync(LicenceActivityOverview overview, LicenceActivityQuery query,
                LicenceActivitySources sources, ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken) =>
                Task.FromResult(new LicenceActivityUsers { TotalUsers = 3, RankedUsers = 2 });
        }
    }
}
