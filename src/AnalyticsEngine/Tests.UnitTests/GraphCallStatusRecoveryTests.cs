using Common.Entities.Calls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;

namespace Tests.UnitTests
{
    [TestClass]
    public class GraphCallStatusRecoveryTests
    {
        internal static HttpResponseMessage ErrorResponse(int status, string mediaType)
        {
            var result = new HttpResponseMessage((HttpStatusCode)status);
            if (mediaType != null)
            {
                var body = mediaType == "application/json" ? TeamsCrawlTests.StubGraphHandler.Error("synthetic-error")
                    : "<html>Synthetic gateway response</html>";
                result.Content = new StringContent(body, Encoding.UTF8, mediaType);
            }
            return result;
        }

        [DataTestMethod]
        [DataRow(403, true)]
        [DataRow(403, false)]
        [DataRow(401, false)]
        [DataRow(500, false)]
        public async Task TenantInventory_HtmlFailure_OnlyForbiddenMeansUnavailable(int status, bool onNextPage)
        {
            var calls = 0;
            var transport = new TeamsCrawlTests.StubGraphHandler().Route("/subscribedSkus", _ =>
                ++calls == 1 && onNextPage
                    ? TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK,
                        "{\"value\":[{}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/subscribedSkus?page=2\"}")
                    : ErrorResponse(status, "text/html"));
            using (var graph = GraphServiceClientFactory.CreateForGraphImport(new GraphBoundedRequestAndDeltaTests.SyntheticTokenCredential(),
                GraphRequestBudgetOptions.GraphImportDefault, null, transport))
            {
                var loader = new GraphUserLoader(null, null, NullLogger.Instance, graph);
                if (status == 403)
                    Assert.IsNull(await loader.LoadTenantSkus(), "Even a late refusal is not a partial or empty inventory.");
                else if (status == 401)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadTenantSkus(), "Do not intercept an authentication challenge.");
                else
                    Assert.AreEqual(status, (await Assert.ThrowsAsync<ApiException>(() => loader.LoadTenantSkus())).ResponseStatusCode);
                Assert.AreEqual(onNextPage ? 2 : 1, calls);
            }
        }

        [DataTestMethod]
        [DataRow("text/html")]
        [DataRow(null)]
        public async Task ReusedUpn_NonJson404IsNotEvidenceOfDirectoryAbsence(string mediaType)
        {
            var current = new GraphUser { Id = Guid.Empty.ToString(), UserPrincipalName = "person@contoso.example", AccountEnabled = true };
            var previousId = "00000000-0000-0000-0000-000000000001";
            var json = JsonConvert.SerializeObject(new { id = current.Id, userPrincipalName = current.UserPrincipalName, accountEnabled = true });
            var transport = new TeamsCrawlTests.StubGraphHandler()
                .Json("/users/" + current.Id, json)
                .Route("/users/" + previousId, _ =>
                {
                    var response = ErrorResponse(404, "text/html");
                    if (mediaType == null) response.Content.Headers.ContentType = null;
                    return response;
                })
                .Json("/users", "{\"value\":[" + json + "]}");
            using (var graph = GraphServiceClientFactory.CreateForGraphImport(new GraphBoundedRequestAndDeltaTests.SyntheticTokenCredential(),
                GraphRequestBudgetOptions.GraphImportDefault, null, transport))
            {
                var loader = new GraphUserLoader(null, null, NullLogger.Instance, graph);
                Assert.IsFalse(await loader.VerifyReusedUpnAsync(current, previousId));
                Assert.AreEqual(2, transport.Requests.Count, "An unstructured error page is not the object-ID 404 required to retire an identity.");
            }
        }

        [DataTestMethod]
        [DataRow(true, 404, null, true)]
        [DataRow(true, 403, null, false)]
        [DataRow(false, 404, null, true)]
        [DataRow(false, 403, null, false)]
        [DataRow(true, 400, "Request_ResourceNotFound", true)]
        [DataRow(false, 400, "ResourceNotFound", true)]
        [DataRow(true, 400, "BadRequest", false)]
        public async Task UserLookup_OnlyCachesAnExplicit404OrRecognizedODataCode(bool callParticipant, int status, string code, bool missing)
        {
            int requests = 0;
            var transport = new TeamsCrawlTests.StubGraphHandler().Route("/users/person", _ =>
            {
                if (++requests > 1)
                    return TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK, "{\"userPrincipalName\":\"person@contoso.com\"}");
                return code == null ? new HttpResponseMessage((HttpStatusCode)status)
                    : TeamsCrawlTests.StubGraphHandler.Response((HttpStatusCode)status, TeamsCrawlTests.StubGraphHandler.Error(code));
            });
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var context = new TeamsLoadContext(graph);
                async Task<string> Lookup() => callParticipant ? await context.CallUsers.GetUpnAsync("person")
                    : (await context.UserCache.GetUserOrNullIfNotFound("person"))?.UserPrincipalName;
                if (missing)
                {
                    Assert.IsNull(await Lookup());
                    Assert.IsNull(await Lookup());
                    Assert.AreEqual(1, requests);
                }
                else
                {
                    var failure = await Assert.ThrowsAsync<ApiException>(Lookup);
                    Assert.AreEqual(status, failure.ResponseStatusCode);
                    Assert.AreEqual("person@contoso.com", await Lookup(), "A refusal must not poison the cache.");
                    Assert.AreEqual(2, requests);
                }
            }
        }

        [DataTestMethod]
        [DataRow(429, true)]
        [DataRow(429, false)]
        [DataRow(503, true)]
        [DataRow(503, false)]
        [DataRow(null, false)]
        public async Task PerUserLicenceFallback_PreservesTransientAndCancellationHandling(int? status, bool hasErrorBody)
        {
            var transport = new TeamsCrawlTests.StubGraphHandler().Route("/users/person/licenseDetails", _ =>
            {
                if (!status.HasValue) throw new OperationCanceledException();
                return hasErrorBody
                    ? TeamsCrawlTests.StubGraphHandler.Response((HttpStatusCode)status.Value, TeamsCrawlTests.StubGraphHandler.Error("transient"))
                    : new HttpResponseMessage((HttpStatusCode)status.Value);
            });
            using (var http = new HttpClient(transport))
            using (var graph = new Microsoft.Graph.GraphServiceClient(http, new AnonymousAuthenticationProvider(), "https://graph.contoso.invalid/v1.0"))
            {
                // No retry middleware: only the consumer's handling of a final SDK response is under test.
                var loader = new GraphUserLoader(null, null, NullLogger.Instance, graph);
                if (hasErrorBody)
                {
                    Assert.IsNull(await loader.LoadUserLicenseDetails("person"), "As before, a per-user OData error is an unknown assignment.");
                }
                else if (status.HasValue)
                {
                    var failure = await Assert.ThrowsExceptionAsync<ApiException>(() => loader.LoadUserLicenseDetails("person"));
                    Assert.AreEqual(status.Value, failure.ResponseStatusCode);
                }
                else
                {
                    await Assert.ThrowsAsync<OperationCanceledException>(() => loader.LoadUserLicenseDetails("person"));
                }
            }
        }

        [DataTestMethod]
        [DataRow(400)]
        [DataRow(500)]
        [DataRow(null)]
        public async Task PerUserLicenceRead_DoesNotSwallowUnrelatedHtmlErrorsOrCancellation(int? status)
        {
            var transport = new TeamsCrawlTests.StubGraphHandler().Route("/users/person/licenseDetails", _ =>
                status.HasValue ? ErrorResponse(status.Value, "text/html") : throw new OperationCanceledException());
            using (var graph = GraphServiceClientFactory.CreateForGraphImport(new GraphBoundedRequestAndDeltaTests.SyntheticTokenCredential(),
                GraphRequestBudgetOptions.GraphImportDefault, null, transport))
            {
                var loader = new GraphUserLoader(null, null, NullLogger.Instance, graph);
                Task Read() => loader.LoadUserLicenseDetails("person");
                if (!status.HasValue)
                    await Assert.ThrowsAsync<OperationCanceledException>(Read);
                else if (status == 500)
                    Assert.AreEqual(500, (await Assert.ThrowsAsync<ApiException>(Read)).ResponseStatusCode);
                else
                    await Assert.ThrowsAsync<InvalidOperationException>(Read);
                Assert.AreEqual(1, transport.Requests.Count);
            }
        }

        [DataTestMethod]
        [DataRow(409, 1, null, 200)]
        [DataRow(409, null, null, 200)]
        [DataRow(409, 1, "application/json", 200)]
        [DataRow(409, 1, "text/html", 200)]
        [DataRow(400, 23, "text/html", 200)]
        [DataRow(403, 23, "text/html", 200)]
        [DataRow(404, 23, "text/html", 200)]
        [DataRow(403, 23, "application/json", 200)]
        [DataRow(403, 23, null, 200)]
        [DataRow(403, 23, null, 404)]
        [DataRow(403, 23, "application/json", 404)]
        [DataRow(403, 24, null, 200)]
        [DataRow(403, null, null, 200)]
        [DataRow(null, 23, null, 200)]
        public async Task Webhook_FailedCreate_ReplacesOnlyOnConflict_OtherwiseKeepsOwnedSubscription(int? status,
            int? ownedHoursLeft, string mediaType, int renewStatus)
        {
            var webhookUrl = new Uri("https://contoso.example/api/CallRecordWebhook");
            var currentUrl = CallSubscriptionRules.CurrentNotificationUrl(webhookUrl).AbsoluteUri;
            var clock = new FixedClock(new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc));
            var subs = new List<object>
            {
                new { id = "other-url", resource = CallSubscriptionRules.CallRecordsResource, notificationUrl = "https://other.example/webhook" },
                new { id = "other-resource", resource = "/users", notificationUrl = currentUrl }
            };
            if (ownedHoursLeft.HasValue)
                subs.Add(new { id = "owned", resource = CallSubscriptionRules.CallRecordsResource, notificationUrl = currentUrl,
                    expirationDateTime = clock.UtcNow.AddHours(ownedHoursLeft.Value) });

            var operations = new List<string>();
            var creates = 0;
            var transport = new TeamsCrawlTests.StubGraphHandler().Route("", request =>
            {
                var operation = request.Method + " " + request.RequestUri.AbsolutePath;
                operations.Add(operation);
                switch (operation)
                {
                    case "GET /v1.0/subscriptions":
                        return TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK, JsonConvert.SerializeObject(new { value = subs }));
                    case "POST /v1.0/subscriptions":
                        if (status == null) throw new OperationCanceledException();
                        if (++creates > 1)
                            return TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.Created, "{\"id\":\"replacement\"}");
                        return ErrorResponse(status.Value, mediaType);
                    case "DELETE /v1.0/subscriptions/owned":
                        return new HttpResponseMessage(HttpStatusCode.NotFound); // Already removed between list and delete.
                    case "PATCH /v1.0/subscriptions/owned":
                        return renewStatus == 200 ? TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK, "{\"id\":\"owned\"}")
                            : new HttpResponseMessage((HttpStatusCode)renewStatus);
                    default: throw new AssertFailedException("Unexpected subscription operation: " + operation);
                }
            });
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var logger = new CallsImportTests.CapturingLogger();
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), logger, clock);
                var state = CallWebhookClientState.For("synthetic", Guid.Empty.ToString(), Guid.Empty.ToString());
                if (status == 409 && ownedHoursLeft.HasValue && mediaType != "text/html")
                {
                    var result = await webhook.CreateOrUpdateWebhook(webhookUrl, state);
                    Assert.AreEqual(CallWebhookChange.Replaced, result.Change);
                    Assert.AreEqual("replacement", result.SubscriptionId);
                    CollectionAssert.AreEqual(new[] { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions", "DELETE /v1.0/subscriptions/owned",
                        "POST /v1.0/subscriptions" }, operations);
                }
                else
                {
                    if (status.HasValue)
                    {
                        var failure = await Assert.ThrowsAsync<ApiException>(() => webhook.CreateOrUpdateWebhook(webhookUrl, state));
                        Assert.AreEqual(status.Value, failure.ResponseStatusCode, "Surface the create failure even if the fallback renewal fails.");
                    }
                    else
                    {
                        await Assert.ThrowsAsync<OperationCanceledException>(() => webhook.CreateOrUpdateWebhook(webhookUrl, state));
                    }
                    var expected = new List<string> { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions" };
                    if (status.HasValue && ownedHoursLeft < 24) expected.Add("PATCH /v1.0/subscriptions/owned");
                    CollectionAssert.AreEqual(expected, operations);
                    if (mediaType == "text/html" || status == 403 && mediaType == null)
                        Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("Synthetic gateway response") || e.Message.Contains("Grant admin consent")),
                            "A gateway page is not an authoritative Graph diagnosis, nor should its content be logged.");
                }
            }
        }
    }
}
