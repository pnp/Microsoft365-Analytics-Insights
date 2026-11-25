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
            var operations = new List<string>();
            var creates = 0;
            var transport = SubscriptionTransport(ownedHoursLeft, operations,
                create: () =>
                {
                    if (status == null) throw new OperationCanceledException();
                    if (++creates > 1)
                        return TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.Created, "{\"id\":\"replacement\"}");
                    return ErrorResponse(status.Value, mediaType);
                },
                renew: () => renewStatus == 200 ? TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK, "{\"id\":\"owned\"}")
                    : new HttpResponseMessage((HttpStatusCode)renewStatus));
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var logger = new CallsImportTests.CapturingLogger();
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), logger, Clock);
                if (status == 409 && ownedHoursLeft.HasValue && mediaType != "text/html")
                {
                    var result = await webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState);
                    Assert.AreEqual(CallWebhookChange.Replaced, result.Change);
                    Assert.AreEqual("replacement", result.SubscriptionId);
                    CollectionAssert.AreEqual(new[] { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions", "DELETE /v1.0/subscriptions/owned",
                        "POST /v1.0/subscriptions" }, operations);
                }
                else
                {
                    if (status.HasValue)
                    {
                        var failure = await Assert.ThrowsAsync<ApiException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState));
                        Assert.AreEqual(status.Value, failure.ResponseStatusCode, "Surface the create failure even if the fallback renewal fails.");
                    }
                    else
                    {
                        await Assert.ThrowsAsync<OperationCanceledException>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, ClientState));
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
            Func<HttpResponseMessage> create, Func<HttpResponseMessage> renew)
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
                        return new HttpResponseMessage(HttpStatusCode.NotFound); // Removed between list and delete.
                    default: throw new AssertFailedException("Unexpected subscription operation: " + operation);
                }
            });
        }
    }
}
