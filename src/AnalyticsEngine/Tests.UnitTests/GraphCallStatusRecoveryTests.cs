using Common.Entities.Calls;
using DataUtils;
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
        [DataRow(409, true)]
        [DataRow(409, false)]
        [DataRow(403, true)]
        [DataRow(null, true)]
        public async Task Webhook_BodylessConflict_OnlyReplacesAnOwnedSubscription(int? status, bool hasOwned)
        {
            var webhookUrl = new Uri("https://contoso.example/api/CallRecordWebhook");
            var currentUrl = CallSubscriptionRules.CurrentNotificationUrl(webhookUrl).AbsoluteUri;
            var subs = new List<object>
            {
                new { id = "other-url", resource = CallSubscriptionRules.CallRecordsResource, notificationUrl = "https://other.example/webhook" },
                new { id = "other-resource", resource = "/users", notificationUrl = currentUrl }
            };
            if (hasOwned)
                subs.Add(new { id = "owned", resource = CallSubscriptionRules.CallRecordsResource, notificationUrl = currentUrl,
                    expirationDateTime = DateTimeOffset.UtcNow.AddHours(1) });

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
                        return ++creates == 1 ? new HttpResponseMessage((HttpStatusCode)status.Value)
                            : TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.Created, "{\"id\":\"replacement\"}");
                    case "DELETE /v1.0/subscriptions/owned":
                        return new HttpResponseMessage(HttpStatusCode.NotFound); // Already removed between list and delete.
                    default: throw new AssertFailedException("Unexpected subscription operation: " + operation);
                }
            });
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), transport))
            {
                var webhook = new CallWebhook(new GraphCallRecordSubscriptionManager(graph), NullLogger.Instance, SystemClock.Instance);
                var state = CallWebhookClientState.For("synthetic", Guid.Empty.ToString(), Guid.Empty.ToString());
                if (status == 409 && hasOwned)
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
                        var failure = await Assert.ThrowsExceptionAsync<ApiException>(() => webhook.CreateOrUpdateWebhook(webhookUrl, state));
                        Assert.AreEqual(status.Value, failure.ResponseStatusCode);
                    }
                    else
                    {
                        await Assert.ThrowsAsync<OperationCanceledException>(() => webhook.CreateOrUpdateWebhook(webhookUrl, state));
                    }
                    CollectionAssert.AreEqual(new[] { "GET /v1.0/subscriptions", "POST /v1.0/subscriptions" }, operations);
                }
            }
        }
    }
}
