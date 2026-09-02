using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine;

namespace Tests.UnitTests
{
    /// <summary>
    /// The App Insights HTTP client's retry, back-off, transient classification and token-refresh rules,
    /// driven against a stub HttpMessageHandler. Zero network. See issue #374.
    /// </summary>
    [TestClass]
    public class AppInsightsApiClientTests
    {
        private const string ConnectionString =
            "InstrumentationKey=11111111-1111-1111-1111-111111111111;ApplicationId=22222222-2222-2222-2222-222222222222";

        private const string EmptyTableJson =
            "{\"tables\":[{\"name\":\"PrimaryResult\",\"columns\":[{\"name\":\"timestamp\",\"type\":\"datetime\"}],\"rows\":[]}]}";

        /// <summary>Returns a queued sequence of responses and counts the requests it saw.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpResponseMessage>> _responses = new Queue<Func<HttpResponseMessage>>();

            public int RequestCount { get; private set; }
            public List<string> AuthorizationHeaders { get; } = new List<string>();

            public StubHandler Then(Func<HttpResponseMessage> response)
            {
                _responses.Enqueue(response);
                return this;
            }

            /// <summary>Used when the queue runs dry - lets a test say "and then always this".</summary>
            public Func<HttpResponseMessage> Fallback { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                AuthorizationHeaders.Add(request.Headers.Authorization?.Parameter);
                var factory = _responses.Count > 0 ? _responses.Dequeue() : Fallback;
                if (factory == null)
                {
                    throw new InvalidOperationException("Stub handler ran out of queued responses.");
                }
                return Task.FromResult(factory());
            }
        }

        private static HttpResponseMessage Ok() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(EmptyTableJson)
        };

        private static HttpResponseMessage Status(HttpStatusCode code, int? retryAfterSeconds = null)
        {
            var response = new HttpResponseMessage(code) { Content = new StringContent("{}") };
            if (retryAfterSeconds.HasValue)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture));
            }
            return response;
        }

        /// <summary>Hands out tokens with a caller-controlled lifetime, counting how often it was asked.</summary>
        private sealed class CountingCredential : TokenCredential
        {
            private readonly TimeSpan _lifetime;
            private int _issued;

            public CountingCredential(TimeSpan lifetime)
            {
                _lifetime = lifetime;
            }

            public int TokensIssued => _issued;

            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                _issued++;
                return new AccessToken($"token-{_issued}", DateTimeOffset.UtcNow.Add(_lifetime));
            }

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        private static AppInsightsAPIClient NewClient(StubHandler handler, TokenCredential credential = null)
            => new AppInsightsAPIClient(ConnectionString, credential ?? new CountingCredential(TimeSpan.FromHours(1)),
                NullLogger.Instance, handler);

        private static readonly DateTime Day = new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public async Task AppInsightsClient_TransientHttpError_IsRetriedWithBackoff()
        {
            var handler = new StubHandler()
                .Then(() => Status(HttpStatusCode.ServiceUnavailable))
                .Then(Ok);

            using (var client = NewClient(handler))
            {
                var sw = Stopwatch.StartNew();
                var result = await client.GetPageViewsAsync(Day, false);
                sw.Stop();

                Assert.IsNotNull(result);
                Assert.AreEqual(2, handler.RequestCount, "A 503 must be retried.");

                // First retry waits min(2^0, 60) = 1 second. Without a back-off the call would return
                // essentially instantly, so this is what distinguishes "retried" from "retried politely".
                Assert.IsTrue(sw.Elapsed >= TimeSpan.FromMilliseconds(900), $"Expected a ~1s back-off, waited {sw.Elapsed}.");
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_RetryAfterHeader_IsPreferredOverExponentialBackoff()
        {
            // Two 429s that each say "retry immediately". Exponential back-off would have waited 1s then
            // 2s, so completing well inside that proves the server's Retry-After was honoured.
            var handler = new StubHandler()
                .Then(() => Status((HttpStatusCode)429, retryAfterSeconds: 0))
                .Then(() => Status((HttpStatusCode)429, retryAfterSeconds: 0))
                .Then(Ok);

            using (var client = NewClient(handler))
            {
                var sw = Stopwatch.StartNew();
                await client.GetCustomEventsAsync(Day, false);
                sw.Stop();

                Assert.AreEqual(3, handler.RequestCount);
                Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(2),
                    $"Exponential back-off would have taken at least 3s; took {sw.Elapsed}.");
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_NonTransientError_IsNotRetried()
        {
            // A 403 means a missing API permission. Retrying it five times just delays the error the
            // operator needs to see.
            var handler = new StubHandler();
            handler.Fallback = () => Status(HttpStatusCode.Forbidden);

            using (var client = NewClient(handler))
            {
                await Assert.ThrowsExceptionAsync<HttpRequestException>(() => client.GetPageViewsAsync(Day, false));
                Assert.AreEqual(1, handler.RequestCount);
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_TransientErrors_AreCappedAndTheFailureSurfaces()
        {
            // Retry-After: 0 keeps the test fast while still exercising the cap.
            var handler = new StubHandler();
            handler.Fallback = () => Status(HttpStatusCode.BadGateway, retryAfterSeconds: 0);

            using (var client = NewClient(handler))
            {
                await Assert.ThrowsExceptionAsync<HttpRequestException>(() => client.GetPageViewsAsync(Day, false));

                // The initial attempt plus MaxRetries more, then the failure is returned rather than
                // retried forever - an App Insights outage must not hang the web-job indefinitely.
                Assert.AreEqual(AppInsightsAPIClient.MaxRetries + 1, handler.RequestCount);
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_ExpiredToken_IsRefreshedBeforeTheNextCall()
        {
            // The client refreshes when the cached token is within 5 minutes of expiry.
            var shortLived = new CountingCredential(TimeSpan.FromMinutes(1));
            var handler = new StubHandler { Fallback = Ok };

            using (var client = NewClient(handler, shortLived))
            {
                await client.GetPageViewsAsync(Day, false);
                await client.GetPageViewsAsync(Day.AddDays(1), false);

                Assert.AreEqual(2, shortLived.TokensIssued, "A token expiring inside the refresh margin must be re-fetched.");
                Assert.AreEqual("token-1", handler.AuthorizationHeaders[0]);
                Assert.AreEqual("token-2", handler.AuthorizationHeaders[1]);
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_ValidToken_IsReusedRatherThanRefetchedPerRequest()
        {
            var longLived = new CountingCredential(TimeSpan.FromHours(1));
            var handler = new StubHandler { Fallback = Ok };

            using (var client = NewClient(handler, longLived))
            {
                await client.GetPageViewsAsync(Day, false);
                await client.GetCustomEventsAsync(Day, false);
                await client.GetPageViewsAsync(Day.AddDays(1), false);

                Assert.AreEqual(1, longLived.TokensIssued, "A still-valid token must not cost an Entra ID round-trip per request.");
            }
        }

        [TestMethod]
        public void AppInsightsClient_BuildsKqlWindow_ForRequestedDayInUtc_UnderAnyCulture()
        {
            // The current culture picks the CALENDAR, not just the separators: under th-TH the year renders
            // as 2569 and under ar-SA the whole date shifts, and KQL todatetime() then matches nothing -
            // silently, with an empty result set rather than an error. See issue #398.
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                foreach (var cultureName in new[] { "th-TH", "ar-SA", "en-US" })
                {
                    Thread.CurrentThread.CurrentCulture = new CultureInfo(cultureName);
                    using (var client = NewClient(new StubHandler { Fallback = Ok }))
                    {
                        var where = client.GetWhereString(Day);
                        StringAssert.Contains(where, "timestamp >= todatetime('2026-05-30 00:00:00')", $"culture {cultureName}");
                        StringAssert.Contains(where, "timestamp < todatetime('2026-05-31 00:00:00')", $"culture {cultureName}");
                    }
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }
    }
}
