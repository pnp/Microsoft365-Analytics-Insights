extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.Calls;
using Azure.Core;
using Common.Entities.Calls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The web app reads the Teams calls webhook subscription over Graph's REST API instead of through
    /// the Microsoft Graph SDK, so that the web site package no longer ships the SDK. These pin that the
    /// REST reader answers the way the SDK path did: every page walked, only this deployment's
    /// subscription kept, and Graph's own error text surfaced when it fails.
    /// </summary>
    [TestClass]
    public class GraphRestCallRecordSubscriptionReaderTests
    {
        private static readonly Uri WebhookUrl = new Uri("https://contoso-analytics.example/api/CallRecordWebhook");
        private const string SecondPageUrl = "https://graph.microsoft.com/v1.0/subscriptions?$skiptoken=page2";

        [TestMethod]
        public async Task FindCallRecordSubscriptions_WalksEveryPageAndKeepsOnlyThisDeploymentsSubscription()
        {
            var graph = new ScriptedGraph()
                .Returning(GraphRestCallRecordSubscriptionReader.SubscriptionsUrl, HttpStatusCode.OK, @"{
                    ""value"": [
                        { ""id"": ""other-resource"", ""resource"": ""/users"", ""notificationUrl"": ""https://contoso-analytics.example/api/CallRecordWebhook"", ""expirationDateTime"": ""2026-03-02T09:00:00Z"" },
                        { ""id"": ""other-install"", ""resource"": ""/communications/callRecords"", ""notificationUrl"": ""https://another-install.example/api/CallRecordWebhook"", ""expirationDateTime"": ""2026-03-02T09:00:00Z"" }
                    ],
                    ""@odata.nextLink"": """ + SecondPageUrl + @"""
                }")
                .Returning(SecondPageUrl, HttpStatusCode.OK, @"{
                    ""value"": [
                        { ""id"": ""ours"", ""resource"": ""/communications/callRecords"", ""notificationUrl"": ""https://contoso-analytics.example/api/CallRecordWebhook"", ""expirationDateTime"": ""2026-03-03T09:30:00+01:00"" }
                    ]
                }");
            var credential = new FakeTokenCredential("token-1");

            var found = await new GraphRestCallRecordSubscriptionReader(credential, new HttpClient(graph)).FindCallRecordSubscriptions(WebhookUrl);

            Assert.AreEqual(1, found.Count, "Subscriptions on another resource, or for another deployment, are not ours.");
            Assert.AreEqual("ours", found[0].Id);
            Assert.AreEqual(CallSubscriptionRules.CallRecordsResource, found[0].Resource);
            Assert.AreEqual(WebhookUrl.ToString(), found[0].NotificationUrl);
            Assert.AreEqual(new DateTimeOffset(2026, 3, 3, 8, 30, 0, TimeSpan.Zero), found[0].ExpirationDateTime);

            CollectionAssert.AreEqual(new[] { GraphRestCallRecordSubscriptionReader.SubscriptionsUrl, SecondPageUrl }, graph.RequestedUrls,
                "Our subscription is not necessarily on the first page, so every page must be read.");
            CollectionAssert.AreEqual(new[] { "Bearer token-1", "Bearer token-1" }, graph.AuthorizationHeaders);
            CollectionAssert.AreEqual(new[] { "https://graph.microsoft.com/.default" }, credential.RequestedScopes);
        }

        [TestMethod]
        public async Task CallRecordSubscriptionStatus_OverTheRestReader_ReportsMissingWhenNothingMatches()
        {
            var graph = new ScriptedGraph().Returning(GraphRestCallRecordSubscriptionReader.SubscriptionsUrl, HttpStatusCode.OK, @"{ ""value"": [] }");
            var reader = new GraphRestCallRecordSubscriptionReader(new FakeTokenCredential("t"), new HttpClient(graph));

            var info = await CallRecordSubscriptionStatus.ReadAsync(reader, WebhookUrl);

            Assert.IsFalse(info.Exists);
            Assert.IsNull(info.SubscriptionId);
            Assert.IsNull(info.ExpirationDateTime);
        }

        /// <summary>
        /// #847 (R6-9): a 200 without a readable subscriptions list is a failed read. It used to be read as an empty
        /// page, so Service configuration showed the webhook as "Missing" - and cached that for five minutes - when
        /// whether it was registered was unknown. Equally on a later page: the subscription may be on the page lost.
        /// </summary>
        [DataTestMethod]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("{}")]
        [DataRow(@"{ ""value"": null }")]
        [DataRow("<html>synthetic gateway page</html>")]
        public async Task FindCallRecordSubscriptions_APageWithoutAReadableList_IsAFailedReadNotMissing(string body)
        {
            var firstPage = new ScriptedGraph().Returning(GraphRestCallRecordSubscriptionReader.SubscriptionsUrl, HttpStatusCode.OK, body);
            var first = await Assert.ThrowsExceptionAsync<SubscriptionPageUnreadableException>(() =>
                CallRecordSubscriptionStatus.ReadAsync(new GraphRestCallRecordSubscriptionReader(new FakeTokenCredential("t"), new HttpClient(firstPage)), WebhookUrl));
            Assert.AreEqual(1, first.PageNumber);
            Assert.IsFalse(first.Message.Contains("synthetic gateway page"), "The body never reaches the message.");

            var laterPage = new ScriptedGraph()
                .Returning(GraphRestCallRecordSubscriptionReader.SubscriptionsUrl, HttpStatusCode.OK,
                    @"{ ""value"": [], ""@odata.nextLink"": """ + SecondPageUrl + @""" }")
                .Returning(SecondPageUrl, HttpStatusCode.OK, body);
            var later = await Assert.ThrowsExceptionAsync<SubscriptionPageUnreadableException>(() =>
                new GraphRestCallRecordSubscriptionReader(new FakeTokenCredential("t"), new HttpClient(laterPage)).FindCallRecordSubscriptions(WebhookUrl));
            Assert.AreEqual(2, later.PageNumber);
            Assert.IsFalse(later.Message.Contains("skiptoken"), "The paging link stays out of the message.");
        }

        /// <summary>
        /// The status page shows the exception message. With the SDK that was Graph's own error text, so
        /// an admin missing a permission sees Graph's explanation rather than a bare status code.
        /// </summary>
        [TestMethod]
        public async Task FindCallRecordSubscriptions_WhenGraphRefuses_ThrowsGraphsOwnMessage()
        {
            var graph = new ScriptedGraph().Returning(GraphRestCallRecordSubscriptionReader.SubscriptionsUrl, HttpStatusCode.Forbidden,
                @"{ ""error"": { ""code"": ""Authorization_RequestDenied"", ""message"": ""Insufficient privileges to complete the operation."" } }");
            var reader = new GraphRestCallRecordSubscriptionReader(new FakeTokenCredential("t"), new HttpClient(graph));

            var ex = await Assert.ThrowsExceptionAsync<GraphRequestException>(() => reader.FindCallRecordSubscriptions(WebhookUrl));

            Assert.AreEqual("Insufficient privileges to complete the operation.", ex.Message);
            Assert.AreEqual(HttpStatusCode.Forbidden, ex.StatusCode);
        }

        [TestMethod]
        public async Task FindCallRecordSubscriptions_WhenTheFailureIsNotAGraphError_ThrowsTheStatus()
        {
            var graph = new ScriptedGraph().Returning(GraphRestCallRecordSubscriptionReader.SubscriptionsUrl, HttpStatusCode.ServiceUnavailable, "<html>Service Unavailable</html>");
            var reader = new GraphRestCallRecordSubscriptionReader(new FakeTokenCredential("t"), new HttpClient(graph));

            var ex = await Assert.ThrowsExceptionAsync<GraphRequestException>(() => reader.FindCallRecordSubscriptions(WebhookUrl));

            Assert.AreEqual("503 ServiceUnavailable", ex.Message);
        }

        /// <summary>
        /// The access token must only ever go to Graph. The Graph SDK's authentication provider enforces
        /// that for next-page links too, so dropping the SDK must not drop the check.
        /// </summary>
        [TestMethod]
        public async Task FindCallRecordSubscriptions_NeverSendsTheTokenToANextLinkOutsideGraph()
        {
            var graph = new ScriptedGraph().Returning(GraphRestCallRecordSubscriptionReader.SubscriptionsUrl, HttpStatusCode.OK,
                @"{ ""value"": [], ""@odata.nextLink"": ""https://graph.microsoft.com.contoso.example/v1.0/subscriptions?$skiptoken=x"" }");
            var reader = new GraphRestCallRecordSubscriptionReader(new FakeTokenCredential("t"), new HttpClient(graph));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => reader.FindCallRecordSubscriptions(WebhookUrl));

            CollectionAssert.AreEqual(new[] { GraphRestCallRecordSubscriptionReader.SubscriptionsUrl }, graph.RequestedUrls);
        }

        /// <summary>Answers each URL with a scripted response and records what was asked for.</summary>
        private sealed class ScriptedGraph : HttpMessageHandler
        {
            private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new Dictionary<string, (HttpStatusCode, string)>(StringComparer.Ordinal);

            public List<string> RequestedUrls { get; } = new List<string>();

            public List<string> AuthorizationHeaders { get; } = new List<string>();

            public ScriptedGraph Returning(string url, HttpStatusCode status, string body)
            {
                _responses[url] = (status, body);
                return this;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri.OriginalString;
                RequestedUrls.Add(url);
                AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());

                if (!_responses.TryGetValue(url, out var response))
                {
                    Assert.Fail($"Unexpected request to '{url}'.");
                }

                return Task.FromResult(new HttpResponseMessage(response.Status)
                {
                    Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
                });
            }
        }

        private sealed class FakeTokenCredential : TokenCredential
        {
            private readonly string _token;

            public FakeTokenCredential(string token)
            {
                _token = token;
            }

            public List<string> RequestedScopes { get; } = new List<string>();

            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                RequestedScopes.AddRange(requestContext.Scopes);
                return new AccessToken(_token, DateTimeOffset.UtcNow.AddHours(1));
            }

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                return new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
            }
        }
    }
}
