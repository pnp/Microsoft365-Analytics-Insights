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
using System.Threading;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;

namespace Tests.UnitTests
{
    [TestClass]
    public class GraphCallStatusRecoveryTests
    {
        private static readonly Uri WebhookUrl = new Uri("https://contoso-analytics.example/api/CallRecordWebhook");
        private static readonly FixedClock Clock = new FixedClock(new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        private static readonly CallWebhookClientState ClientState = CallWebhookClientState.For("synthetic", Guid.Empty.ToString(), Guid.Empty.ToString());

        private const string DuplicateMessage = "Subscription Id owned already exists for the requested combination.";
        private static HttpResponseMessage Json(int status, string body) =>
            TeamsCrawlTests.StubGraphHandler.Response((HttpStatusCode)status, body);

        private static HttpResponseMessage Redirect() => Redirect(307);

        private static HttpResponseMessage Redirect(int status)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Location = new Uri("/unrelated", UriKind.Relative);
            return response;
        }

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

        private static HttpResponseMessage UnreadableJsonRefusal(int status)
        {
            var response = ErrorResponse(status, "text/html");
            // A JSON content-type isn't a parsed Graph error. The bounded reader rejects the
            // advertised size without allocating it. Only the status survives, not evidence for deletion.
            response.Content.Headers.ContentType.MediaType = "application/json";
            response.Content.Headers.ContentLength = int.MaxValue;
            return response;
        }

        [DataTestMethod]
        [DataRow(403, true, false)]
        [DataRow(403, false, false)]
        [DataRow(403, true, true)]
        [DataRow(403, false, true)]
        [DataRow(401, false, false)]
        [DataRow(500, false, false)]
        public async Task TenantInventory_UnparseableFailure_OnlyForbiddenMeansUnavailable(int status, bool onNextPage, bool advertisedJson)
        {
            var calls = 0;
            var transport = new TeamsCrawlTests.StubGraphHandler().Route("/subscribedSkus", _ =>
                ++calls == 1 && onNextPage
                    ? TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK,
                        "{\"value\":[{}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/subscribedSkus?page=2\"}")
                    : advertisedJson ? Json(status, "{") : ErrorResponse(status, "text/html"));
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
        [DataRow(null, false)]
        [DataRow("{}", false)]
        [DataRow("{\"error\":{}}", false)]
        [DataRow("{\"error\":{\"code\":\" \"}}", false)]
        [DataRow("{\"error\":{\"code\":\"BadRequest\"}}", false)]
        [DataRow("{", false)]
        [DataRow("{\"error\":{\"code\":\"Request_ResourceNotFound\"}}", true)]
        public async Task ReusedUpn_OnlyStructuredAbsenceAllowsTheIndependentOwnerCheck(string body, bool confirmed)
        {
            var current = new GraphUser { Id = Guid.Empty.ToString(), UserPrincipalName = "person@contoso.example", AccountEnabled = true };
            const string previousId = "00000000-0000-0000-0000-000000000001";
            var json = JsonConvert.SerializeObject(new { id = current.Id, userPrincipalName = current.UserPrincipalName, accountEnabled = true });
            var transport = new TeamsCrawlTests.StubGraphHandler().Json("/users/" + current.Id, json)
                .Route("/users/" + previousId, _ => body == null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(404, body))
                .Json("/users", "{\"value\":[" + json + "]}");
            using (var graph = GraphServiceClientFactory.CreateForGraphImport(new GraphBoundedRequestAndDeltaTests.SyntheticTokenCredential(),
                GraphRequestBudgetOptions.GraphImportDefault, null, transport))
            {
                Assert.AreEqual(confirmed, await new GraphUserLoader(null, null, NullLogger.Instance, graph).VerifyReusedUpnAsync(current, previousId));
                Assert.AreEqual(confirmed ? 3 : 2, transport.Requests.Count);
            }
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public async Task ReusedUpn_RedirectInAnyVerificationReadKeepsQuarantine(int redirectedRead)
        {
            var current = new GraphUser { Id = Guid.Empty.ToString(), UserPrincipalName = "person@contoso.example", AccountEnabled = true };
            const string previousId = "00000000-0000-0000-0000-000000000001";
            var json = JsonConvert.SerializeObject(new { id = current.Id, userPrincipalName = current.UserPrincipalName, accountEnabled = true });
            var transport = new TeamsCrawlTests.StubGraphHandler()
                .Route("/users/" + current.Id, _ => redirectedRead == 1 ? Redirect() : Json(200, json))
                .Route("/users/" + previousId, _ => redirectedRead == 2 ? Redirect() : Json(404, TeamsCrawlTests.StubGraphHandler.Error("Request_ResourceNotFound")))
                .Route("/users", _ => Redirect());
            using (var graph = GraphServiceClientFactory.CreateForGraphImport(new GraphBoundedRequestAndDeltaTests.SyntheticTokenCredential(),
                GraphRequestBudgetOptions.GraphImportDefault, null, transport))
            {
                Assert.IsFalse(await new GraphUserLoader(null, null, NullLogger.Instance, graph).VerifyReusedUpnAsync(current, previousId));
                Assert.AreEqual(redirectedRead, transport.Requests.Count, "A verification redirect must not be followed.");
            }
        }

        [DataTestMethod]
        [DataRow("text/html")]
        [DataRow(null)]
        [DataRow("application/json")]
        public async Task ReusedUpn_NonJson404IsNotEvidenceOfDirectoryAbsence(string mediaType)
        {
            var current = new GraphUser { Id = Guid.Empty.ToString(), UserPrincipalName = "person@contoso.example", AccountEnabled = true };
            var previousId = "00000000-0000-0000-0000-000000000001";
            var json = JsonConvert.SerializeObject(new { id = current.Id, userPrincipalName = current.UserPrincipalName, accountEnabled = true });
            var transport = new TeamsCrawlTests.StubGraphHandler()
                .Json("/users/" + current.Id, json)
                .Route("/users/" + previousId, _ =>
                {
                    if (mediaType == "application/json") return UnreadableJsonRefusal(404);
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
                else
                    Assert.AreEqual(status.Value, (await Assert.ThrowsAsync<ApiException>(Read)).ResponseStatusCode);
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
        public async Task Webhook_FailedCreateWithoutDuplicateEvidence_KeepsOwnedSubscription(int? status,
            int? ownedHoursLeft, string mediaType, int renewStatus)
        {
            var operations = new List<string>();
            var transport = SubscriptionTransport(ownedHoursLeft, operations,
                create: () => status.HasValue ? ErrorResponse(status.Value, mediaType) : throw new OperationCanceledException(),
                renew: () => renewStatus == 200 ? TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK, "{\"id\":\"owned\"}")
                    : new HttpResponseMessage((HttpStatusCode)renewStatus));
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var logger = new CallsImportTests.CapturingLogger();
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), logger, Clock);
                if (status.HasValue)
                {
                    var failure = await Assert.ThrowsAsync<ApiException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState));
                    Assert.AreEqual(status.Value, failure.ResponseStatusCode, "Surface the create failure even if the fallback renewal fails.");
                }
                else
                    await Assert.ThrowsAsync<OperationCanceledException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState));
                var expected = new List<string> { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions" };
                if (status.HasValue && ownedHoursLeft < 24) expected.Add("PATCH /v1.0/subscriptions/owned");
                CollectionAssert.AreEqual(expected, operations);
                Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("Synthetic gateway response") || e.Message.Contains("Grant admin consent")),
                    "A bare status or unrelated error code is not a permission or duplicate diagnosis.");
            }
        }

        [DataTestMethod]
        [DataRow(409, "{}")]
        [DataRow(409, "{\"error\":{}}")]
        [DataRow(409, "{\"error\":{\"code\":[]}}")]
        [DataRow(409, "{\"error\":{\"message\":\"" + DuplicateMessage + "\"}}")]
        [DataRow(409, "{\"error\":{\"code\":\"Unrelated\",\"message\":\"" + DuplicateMessage + "\"}}")]
        [DataRow(409, "{\"error\":{\"code\":\"ExtensionError\",\"message\":\"Unrelated conflict\"}}")]
        [DataRow(409, "{\"error\":{\"code\":\"ExtensionError\",\"message\":\"Subscription Id other-url already exists for the requested combination.\"}}")]
        [DataRow(409, "{")]
        [DataRow(403, "<html>Synthetic gateway response</html>")]
        [DataRow(500, "{")]
        public async Task Webhook_InconclusiveCreateFailure_OnlyRenewsTheDueSubscription(int status, string body)
        {
            var operations = new List<string>();
            var transport = SubscriptionTransport(1, operations, () => Json(status, body), () => Json(200, "{\"id\":\"owned\"}"));
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var logger = new CallsImportTests.CapturingLogger();
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), logger, Clock);
                Assert.AreEqual(status, (await Assert.ThrowsAsync<ApiException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState))).ResponseStatusCode);
                Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("Synthetic gateway response") || e.Message.Contains("Grant admin consent")));
            }
            CollectionAssert.AreEqual(new[] { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions", "PATCH /v1.0/subscriptions/owned" }, operations);
        }

        [DataTestMethod]
        [DataRow("Conflict", DuplicateMessage, false)]
        [DataRow("ExtensionError", "Subscription Id <owned> already exists for the requested combination", true)]
        public async Task Webhook_DirectStructuredDuplicateNamesAnOwnedSubscription(string code, string message, bool alreadyGone)
        {
            var operations = new List<string>();
            var posts = 0;
            var transport = SubscriptionTransport(1, operations, () => ++posts == 1
                ? Json(409, JsonConvert.SerializeObject(new { error = new { code, message } }))
                : Json(201, "{\"id\":\"replacement\"}"), () => throw new AssertFailedException(),
                () => alreadyGone ? Json(404, TeamsCrawlTests.StubGraphHandler.Error("ResourceNotFound")) : new HttpResponseMessage(HttpStatusCode.NoContent));
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var result = await new CallWebhook(new GraphCallRecordSubscriptionManager(graph), NullLogger.Instance, Clock)
                    .CreateOrUpdateWebhook(WebhookUrl, ClientState);
                Assert.AreEqual(CallWebhookChange.Replaced, result.Change);
                Assert.AreEqual("replacement", result.SubscriptionId);
            }
            CollectionAssert.AreEqual(new[] { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions", "DELETE /v1.0/subscriptions/owned",
                "POST /v1.0/subscriptions" }, operations);
        }

        [DataTestMethod]
        [DataRow(301)]
        [DataRow(302)]
        [DataRow(307)]
        public async Task Webhook_RedirectedReplacementIsInconclusiveAndStartsNoSubsequentWrite(int status)
        {
            var operations = new List<string>();
            var transport = SubscriptionTransport(1, operations, () => Redirect(status), () => throw new AssertFailedException());
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), NullLogger.Instance, Clock);
                var refusal = await Assert.ThrowsAsync<ApiException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState));
                Assert.AreEqual(status, refusal.ResponseStatusCode);
                StringAssert.Contains(refusal.Message, "not followed");
            }
            CollectionAssert.AreEqual(new[] { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions" }, operations);
        }

        [TestMethod]
        public async Task Webhook_EmptyReplacementResponseStopsBeforeCleanup()
        {
            var operations = new List<string>();
            var transport = SubscriptionTransport(1, operations, () => Json(201, "{}"), () => throw new AssertFailedException());
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), NullLogger.Instance, Clock);
                await Assert.ThrowsAsync<InvalidOperationException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState));
            }
            CollectionAssert.AreEqual(new[] { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions" }, operations);
        }

        [TestMethod]
        public async Task SubscriptionRead_KiotaStatusRetryKeepsTheDirectResponseRequirement()
        {
            var calls = 0;
            var transport = new TeamsCrawlTests.StubGraphHandler().Route("/subscriptions", request =>
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                if (++calls > 1) return Redirect();
                var retry = Json(503, "{");
                retry.Headers.TryAddWithoutValidation("Retry-After", "0");
                return retry;
            });
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(10), transport))
            {
                await Assert.ThrowsAsync<GraphDecisionRedirectException>(() => new GraphCallRecordSubscriptionManager(graph).FindCallRecordSubscriptions(WebhookUrl));
            }
            Assert.AreEqual(2, transport.Requests.Count);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Webhook_RedirectedInventoryPageStopsBeforeMutation(bool secondPage)
        {
            var calls = 0;
            var transport = new TeamsCrawlTests.StubGraphHandler().Route("/subscriptions", request =>
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                return ++calls == 1 && secondPage
                    ? Json(200, "{\"value\":[],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/subscriptions?page=2\"}")
                    : Redirect();
            });
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), NullLogger.Instance, Clock);
                await Assert.ThrowsAsync<GraphDecisionRedirectException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState));
            }
            Assert.AreEqual(secondPage ? 2 : 1, transport.Requests.Count);
        }

        [DataTestMethod]
        [DataRow(404, null)]
        [DataRow(404, "{\"error\":{\"code\":\"BadRequest\"}}")]
        [DataRow(307, null)]
        public async Task Webhook_InconclusiveDeleteDoesNotStartAnotherCreate(int status, string body)
        {
            var operations = new List<string>();
            var transport = SubscriptionTransport(1, operations,
                () => Json(409, JsonConvert.SerializeObject(new { error = new { code = "ExtensionError", message = DuplicateMessage } })),
                () => throw new AssertFailedException(),
                () => status == 307 ? Redirect() : body == null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(status, body));
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), NullLogger.Instance, Clock);
                Assert.AreEqual(status, (await Assert.ThrowsAsync<ApiException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState))).ResponseStatusCode);
            }
            CollectionAssert.AreEqual(new[] { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions", "DELETE /v1.0/subscriptions/owned" }, operations);
        }

        [TestMethod]
        public async Task TenantInventory_MalformedSuccessDoesNotSelectThePermissionFallback()
        {
            var transport = new TeamsCrawlTests.StubGraphHandler().Json("/subscribedSkus", "{");
            using (var graph = GraphServiceClientFactory.CreateForGraphImport(new GraphBoundedRequestAndDeltaTests.SyntheticTokenCredential(),
                GraphRequestBudgetOptions.GraphImportDefault, null, transport))
            {
                var loader = new GraphUserLoader(null, null, NullLogger.Instance, graph);
                await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => loader.LoadTenantSkus());
            }
            Assert.AreEqual(1, transport.Requests.Count);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DirectError_PrebufferedByTheTransportIsStillSizeLimited(bool large)
        {
            var body = TeamsCrawlTests.StubGraphHandler.Error("ResourceNotFound");
            var response = Json(404, large ? body.PadRight(GraphResponseEvidence.MaxErrorBytes + 1) : body);
            await response.Content.LoadIntoBufferAsync();
            var transport = new TeamsCrawlTests.StubGraphHandler().Route("/subscribedSkus", _ => response);
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var error = await Assert.ThrowsAsync<ApiException>(() => graph.SubscribedSkus.GetAsync(rc => rc.Options.Add(GraphResponseEvidence.RequireDirect)));
                Assert.AreEqual(404, error.ResponseStatusCode);
                Assert.AreEqual(!large, GraphResponseEvidence.ConfirmsNotFound(error));
            }
        }

        [TestMethod]
        public async Task TenantInventory_AuthenticationChallengeStillReachesKiota()
        {
            var calls = 0;
            var transport = new TeamsCrawlTests.StubGraphHandler().Route("/subscribedSkus", _ =>
            {
                if (++calls != 1) return Json(200, "{\"value\":[]}");
                var challenge = Json(401, "{");
                challenge.Headers.WwwAuthenticate.ParseAdd("Bearer claims=\"e30=\""); // Base64 for an empty JSON object.
                return challenge;
            });
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var skus = await new GraphUserLoader(null, null, NullLogger.Instance, graph).LoadTenantSkus();
                Assert.AreEqual(0, skus.Count, "The second, successful response is the inventory, not a permission fallback.");
                Assert.AreEqual(2, calls);
            }
        }

        [TestMethod]
        public async Task Webhook_UnreadableConflictBody_DoesNotAuthorizeDeletingAnOwnedSubscription()
        {
            var operations = new List<string>();
            var transport = SubscriptionTransport(1, operations,
                () => UnreadableJsonRefusal(409),
                () => TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK, "{\"id\":\"owned\"}"));
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), NullLogger.Instance, Clock);
                Assert.AreEqual(409, (await Assert.ThrowsAsync<ApiException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState))).ResponseStatusCode);
            }
            CollectionAssert.AreEqual(new[] { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions", "PATCH /v1.0/subscriptions/owned" }, operations);
        }

        [DataTestMethod]
        [DataRow(23)]
        [DataRow(24)]
        [DataRow(null)]
        public async Task Webhook_ExhaustedPostRetries_RethrowsTheSdkAggregateAndRenewsOnlyADueOwnedSubscription(int? ownedHoursLeft)
        {
            var operations = new List<string>();
            var transport = SubscriptionTransport(ownedHoursLeft, operations,
                create: () =>
                {
                    var failure = ErrorResponse(503, "text/html");
                    failure.Headers.TryAddWithoutValidation("Retry-After", "0");
                    return failure;
                },
                renew: () => TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK, "{\"id\":\"owned\"}"));
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(30), transport))
            {
                var logger = new CallsImportTests.CapturingLogger();
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), logger, Clock);
                var failure = await Assert.ThrowsExceptionAsync<AggregateException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState));
                var attempts = failure.Flatten().InnerExceptions;
                Assert.IsTrue(attempts.Count > 0 && attempts.All(e => e is ApiException api && api.ResponseStatusCode == 503));
                var expected = new List<string> { "GET /v1.0/subscriptions" };
                expected.AddRange(Enumerable.Repeat("POST /v1.0/subscriptions", 4)); // SDK retries only; no local POST replay.
                if (ownedHoursLeft < 24) expected.Add("PATCH /v1.0/subscriptions/owned");
                CollectionAssert.AreEqual(expected, operations);
                Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("Synthetic gateway response")));
            }
        }

        [DataTestMethod]
        [DataRow("wrapped-conflict", true)]
        [DataRow("mixed-transport", false)]
        [DataRow("mixed-cancel", false)]
        [DataRow("no-status", false)]
        [DataRow("empty", false)]
        public async Task Webhook_AggregateRequiresKnownHttpFailuresAndNeverEnablesDeletion(string kind, bool renew)
        {
            var http = new ApiException { ResponseStatusCode = 409 };
            var failure = kind == "wrapped-conflict" ? new AggregateException(http)
                : kind == "mixed-transport" ? new AggregateException(http, new HttpRequestException())
                : kind == "mixed-cancel" ? new AggregateException(http, new OperationCanceledException())
                : kind == "no-status" ? new AggregateException(new ApiException())
                : new AggregateException();
            var subscriptions = new FakeCallRecordSubscriptionManager()
                .WithExistingSubscription("owned", Clock.UtcNow.AddHours(1)).FailingCreateWith(failure);
            var webhook = new CallWebhook(subscriptions, NullLogger.Instance, Clock);

            Assert.AreSame(failure, await Assert.ThrowsExceptionAsync<AggregateException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState)));
            Assert.AreEqual(1, subscriptions.Creates.Count);
            Assert.AreEqual(0, subscriptions.Deletes.Count);
            CollectionAssert.AreEqual(renew ? new[] { "owned" } : Array.Empty<string>(), subscriptions.Renewals.Select(r => r.SubscriptionId).ToArray());
        }

        [DataTestMethod]
        [DataRow(403)]
        [DataRow(503)]
        public async Task Webhook_FallbackPatchSdkDeadline_PreservesTheOriginalReplacementFailure(int createStatus)
        {
            var operations = new List<string>();
            using (var body = new GraphBoundedRequestAndDeltaTests.GatedJsonContent())
            {
                var transport = SubscriptionTransport(1, operations, () =>
                {
                    var refusal = ErrorResponse(createStatus, "text/html");
                    refusal.Headers.TryAddWithoutValidation("Retry-After", "0");
                    return refusal;
                }, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
                using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromMilliseconds(500), transport))
                {
                    var logger = new CallsImportTests.CapturingLogger();
                    var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), logger, Clock);
                    var request = webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState);
                    await GraphBoundedRequestAndDeltaTests.WithinWatchdog(body.Started.Task);
                    if (createStatus == 403)
                    {
                        var failure = await Assert.ThrowsAsync<ApiException>(() => GraphBoundedRequestAndDeltaTests.WithinWatchdog(request));
                        Assert.AreEqual(403, failure.ResponseStatusCode);
                    }
                    else
                    {
                        var failure = await Assert.ThrowsExceptionAsync<AggregateException>(() => GraphBoundedRequestAndDeltaTests.WithinWatchdog(request));
                        Assert.IsTrue(failure.Flatten().InnerExceptions.Any(e => e is ApiException api && api.ResponseStatusCode == 503));
                    }
                    var expected = new List<string> { "GET /v1.0/subscriptions" };
                    expected.AddRange(Enumerable.Repeat("POST /v1.0/subscriptions", createStatus == 503 ? 4 : 1));
                    expected.Add("PATCH /v1.0/subscriptions/owned");
                    CollectionAssert.AreEqual(expected, operations);
                    Assert.IsTrue(body.Disposed);
                    Assert.IsTrue(logger.Entries.Any(e => e.Message.Contains("fallback renewal") && e.Message.Contains("deadline")));
                }
            }
        }

        [TestMethod]
        public async Task Webhook_ReplacementPostDeadlineWithoutARefusal_DoesNotStartAnotherMutation()
        {
            var operations = new List<string>();
            using (var body = new GraphBoundedRequestAndDeltaTests.GatedJsonContent())
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromMilliseconds(500),
                SubscriptionTransport(1, operations, () => new HttpResponseMessage(HttpStatusCode.Created) { Content = body }, () => throw new AssertFailedException())))
            {
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), NullLogger.Instance, Clock);
                var request = webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState);
                await GraphBoundedRequestAndDeltaTests.WithinWatchdog(body.Started.Task);
                await Assert.ThrowsAsync<OperationCanceledException>(() => GraphBoundedRequestAndDeltaTests.WithinWatchdog(request));
                Assert.IsTrue(body.Disposed);
                CollectionAssert.AreEqual(new[] { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions" }, operations);
            }
        }

        [TestMethod]
        public async Task Webhook_FallbackPatchCancellation_PreservesAnUnrelatedCallerCancellation()
        {
            using (var stopping = new CancellationTokenSource())
            {
                stopping.Cancel();
                var cancellation = new OperationCanceledException(stopping.Token);
                var subscriptions = new FakeCallRecordSubscriptionManager().WithExistingSubscription("owned", Clock.UtcNow.AddHours(1))
                    .FailingCreateWith(new ApiException { ResponseStatusCode = 403 }).FailingRenewWith(cancellation);
                var webhook = new CallWebhook(subscriptions, NullLogger.Instance, Clock);
                var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState));
                Assert.AreSame(cancellation, failure);
                CollectionAssert.AreEqual(new[] { "owned" }, subscriptions.Renewals.Select(r => r.SubscriptionId).ToArray());
                Assert.AreEqual(0, subscriptions.Deletes.Count);
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task SubscriptionTimer_StoppingDuringFallbackPatch_CancelsTheSdkRequestWithoutSchedulingAFailure(bool hostStops)
        {
            var operations = new List<string>();
            var logger = new CallsImportTests.CapturingLogger();
            using (var host = new CancellationTokenSource())
            using (var body = new GraphBoundedRequestAndDeltaTests.GatedJsonContent())
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(30),
                SubscriptionTransport(1, operations, () => ErrorResponse(403, null), () => new HttpResponseMessage(HttpStatusCode.OK) { Content = body })))
            using (var timer = new CallSubscriptionMaintainer(stopping => new CallWebhook(new GraphCallRecordSubscriptionManager(graph, stopping), logger, Clock),
                WebhookUrl, ClientState, null, logger, Clock, host.Token))
            {
                var check = timer.RunOnceAsync();
                await GraphBoundedRequestAndDeltaTests.WithinWatchdog(body.Started.Task);
                if (hostStops) host.Cancel();
                else timer.Dispose();
                Assert.AreSame(check, await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(5))), "The running SDK request did not stop.");
                await check;
                Assert.IsTrue(body.Disposed);
                Assert.AreEqual(0, timer.ConsecutiveFailures);
                Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("fallback renewal") || e.Message.Contains("Retry pending")));
                await timer.RunOnceAsync();
                CollectionAssert.AreEqual(new[] { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions", "PATCH /v1.0/subscriptions/owned" }, operations);
            }
        }

        [TestMethod]
        public async Task Webhook_FallbackPatchFailure_DoesNotLogTheSecondaryResponseBody()
        {
            const string payload = "synthetic secondary message";
            var logger = new CallsImportTests.CapturingLogger();
            var transport = SubscriptionTransport(1, new List<string>(), () => ErrorResponse(403, null),
                () => TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.BadRequest,
                    JsonConvert.SerializeObject(new { error = new { code = "BadRequest", message = payload } })));
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), logger, Clock);
                Assert.AreEqual(403, (await Assert.ThrowsAsync<ApiException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState))).ResponseStatusCode);
            }
            Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains(payload) || e.Exception?.ToString().Contains(payload) == true));
            Assert.IsTrue(logger.Entries.Any(e => e.Message.Contains("fallback renewal") && e.Message.Contains("HTTP 400")));
        }

        private static TeamsCrawlTests.StubGraphHandler SubscriptionTransport(int? ownedHoursLeft, List<string> operations,
            Func<HttpResponseMessage> create, Func<HttpResponseMessage> renew, Func<HttpResponseMessage> delete = null)
        {
            var currentUrl = CallSubscriptionRules.CurrentNotificationUrl(WebhookUrl).AbsoluteUri;
            var subs = new List<object>
            {
                new { id = "other-url", resource = CallSubscriptionRules.CallRecordsResource, notificationUrl = "https://other.example/webhook" },
                new { id = "other-resource", resource = "/users", notificationUrl = currentUrl }
            };
            if (ownedHoursLeft.HasValue)
                subs.Add(new { id = "owned", resource = CallSubscriptionRules.CallRecordsResource, notificationUrl = currentUrl,
                    expirationDateTime = Clock.UtcNow.AddHours(ownedHoursLeft.Value) });

            return new TeamsCrawlTests.StubGraphHandler().Route("", request =>
            {
                var operation = request.Method + " " + request.RequestUri.AbsolutePath;
                operations.Add(operation);
                switch (operation)
                {
                    case "GET /v1.0/subscriptions":
                        return TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK, JsonConvert.SerializeObject(new { value = subs }));
                    case "POST /v1.0/subscriptions": return create();
                    case "PATCH /v1.0/subscriptions/owned": return renew();
                    case "DELETE /v1.0/subscriptions/owned":
                        return delete == null ? Json(404, TeamsCrawlTests.StubGraphHandler.Error("ResourceNotFound")) : delete();
                    default: throw new AssertFailedException("Unexpected subscription operation: " + operation);
                }
            });
        }
    }
}
