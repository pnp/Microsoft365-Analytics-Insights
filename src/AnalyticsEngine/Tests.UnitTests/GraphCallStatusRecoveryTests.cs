using Common.Entities.Calls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;

namespace Tests.UnitTests
{
    [TestClass]
    public class GraphCallStatusRecoveryTests
    {
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
        [DataRow(409, 1, false, 200)]
        [DataRow(409, null, false, 200)]
        [DataRow(403, 23, true, 200)]
        [DataRow(403, 23, false, 200)]
        [DataRow(403, 23, false, 404)]
        [DataRow(403, 23, true, 404)]
        [DataRow(403, 24, false, 200)]
        [DataRow(403, null, false, 200)]
        [DataRow(null, 23, false, 200)]
        public async Task Webhook_FailedCreate_ReplacesOnlyOnConflict_OtherwiseKeepsOwnedSubscription(int? status,
            int? ownedHoursLeft, bool hasErrorBody, int renewStatus)
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
                        return hasErrorBody
                            ? TeamsCrawlTests.StubGraphHandler.Response((HttpStatusCode)status.Value, TeamsCrawlTests.StubGraphHandler.Error("Authorization_RequestDenied"))
                            : new HttpResponseMessage((HttpStatusCode)status.Value);
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
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), NullLogger.Instance, clock);
                var state = CallWebhookClientState.For("synthetic", Guid.Empty.ToString(), Guid.Empty.ToString());
                if (status == 409 && ownedHoursLeft.HasValue)
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
                    if (status == 403 && ownedHoursLeft < 24) expected.Add("PATCH /v1.0/subscriptions/owned");
                    CollectionAssert.AreEqual(expected, operations);
                }
            }
        }
    }
}
