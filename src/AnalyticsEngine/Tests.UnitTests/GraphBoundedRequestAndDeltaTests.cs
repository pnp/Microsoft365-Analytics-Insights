using Common.Entities.Config;
using Common.Entities.State;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using WebJob.Office365ActivityImporter.Engine.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using ActivityImporter.Engine.ActivityAPI.Copilot;

namespace Tests.UnitTests
{
    [TestClass]
    public class GraphBoundedRequestAndDeltaTests
    {
        [TestMethod]
        public async Task GraphSdk_HtmlServerError_PreservesStatusForDurableEnrichmentRetry()
        {
            var terminal = new SequenceHttpHandler(ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html><body>Temporary gateway failure</body></html>",
                    System.Text.Encoding.UTF8, "text/html")
            }));
            using (var client = GraphServiceClientFactory.CreateWithTimeout(
                new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(5), terminal))
            {
                var ex = await Assert.ThrowsExceptionAsync<ApiException>(() => client.Users["contoso-user"].GetAsync());
                Assert.AreEqual(502, ex.ResponseStatusCode);
                Assert.IsTrue(CopilotAuditEventManager.IsTransientEnrichmentFailure(ex));
                Assert.IsFalse(ex.Message.Contains("<html>"), "A proxy response body must not leak into retry logs.");
            }
            Assert.AreEqual(1, terminal.Attempts);
        }

        [TestMethod]
        public async Task BoundedGraphRequestHandler_RequestTimeoutThenHealthyResponse_RetriesSamePageOnly()
        {
            var terminal = new SequenceHttpHandler(
                async ct => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return new HttpResponseMessage(HttpStatusCode.OK); },
                ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            var client = BuildBoundedClient(terminal, timeoutRetryCount: 1, perRequestMs: 25, totalBudgetMs: 500);

            var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(2, terminal.Attempts, "Only the timed-out page request should be retried; the tenant crawl is not restarted by this handler.");
        }

        [TestMethod]
        public async Task BoundedGraphRequestHandler_RepeatedTimeouts_StopsInsideExplicitBudget()
        {
            var terminal = new SequenceHttpHandler(
                async ct => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return new HttpResponseMessage(HttpStatusCode.OK); },
                async ct => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return new HttpResponseMessage(HttpStatusCode.OK); });
            var client = BuildBoundedClient(terminal, timeoutRetryCount: 1, perRequestMs: 25, totalBudgetMs: 200);
            var sw = Stopwatch.StartNew();

            await Assert.ThrowsExceptionAsync<TimeoutException>(() => client.GetAsync("https://graph.microsoft.com/v1.0/users"));

            Assert.AreEqual(2, terminal.Attempts);
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(2), "The test timeout budget should bound elapsed time deterministically.");
        }

        [TestMethod]
        public async Task BoundedGraphRequestHandler_CallerCancellation_IsNotRetriedAsTransientTimeout()
        {
            var terminal = new SequenceHttpHandler(
                async ct => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return new HttpResponseMessage(HttpStatusCode.OK); });
            var client = BuildBoundedClient(terminal, timeoutRetryCount: 3, perRequestMs: 5000, totalBudgetMs: 6000);
            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(25)))
            {
                await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => client.GetAsync("https://graph.microsoft.com/v1.0/users", cts.Token));
            }

            Assert.AreEqual(1, terminal.Attempts, "Shutdown/caller cancellation must stop promptly and must not consume timeout retry attempts.");
        }

        [TestMethod]
        public async Task BoundedGraphRequestHandler_NonGetRequest_IsDeadlineBoundedButNotRetried()
        {
            var terminal = new SequenceHttpHandler(
                async ct => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return new HttpResponseMessage(HttpStatusCode.OK); });
            var client = BuildBoundedClient(terminal, timeoutRetryCount: 3, perRequestMs: 25, totalBudgetMs: 500);

            await Assert.ThrowsExceptionAsync<TimeoutException>(() => client.PostAsync(
                "https://graph.microsoft.com/v1.0/teams",
                new StringContent("{}")));

            Assert.AreEqual(1, terminal.Attempts, "Non-GET SDK calls must keep a request deadline but must not be retried by the local timeout policy.");
        }

        [TestMethod]
        public async Task GraphImportPipeline_RetryAfterLongerThanAttemptDeadline_IsHonouredByRetryHandler()
        {
            // Retry-After (1 s) is longer than one attempt's deadline (250 ms) and the timeout-retry budget (500 ms).
            var terminal = new SequenceHttpHandler(
                ct => Task.FromResult(JsonResponse((HttpStatusCode)429, retryAfterSeconds: 1)),
                ct => Task.FromResult(JsonResponse(HttpStatusCode.OK)));
            var client = BuildGraphImportPipelineClient(terminal, perRequestMs: 250, timeoutRetryCount: 1, totalBudgetMs: 500);
            var sw = Stopwatch.StartNew();

            var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(2, terminal.Attempts);
            Assert.IsTrue(sw.Elapsed >= TimeSpan.FromMilliseconds(950), $"Retry-After must be waited in full, not cut short by the per-attempt deadline; elapsed {sw.ElapsedMilliseconds} ms.");
            CollectionAssert.AreEqual(new[] { null, "1" }, terminal.RetryAttemptHeaders,
                "The second send must be Kiota's Retry-After retry, not a local timeout re-send.");
        }

        [TestMethod]
        public async Task GraphImportPipeline_RetryAfterHonouredThenStillThrottled_IsNotRetriedLocally()
        {
            var terminal = new SequenceHttpHandler(
                ct => Task.FromResult(JsonResponse((HttpStatusCode)503, retryAfterSeconds: 1)),
                ct => Task.FromResult(JsonResponse((HttpStatusCode)503, retryAfterSeconds: 1)));
            var client = BuildGraphImportPipelineClient(terminal, perRequestMs: 250, timeoutRetryCount: 2, totalBudgetMs: 750, sdkRetryCount: 1);

            await Assert.ThrowsExceptionAsync<AggregateException>(() => client.GetAsync("https://graph.microsoft.com/v1.0/users"));

            Assert.AreEqual(2, terminal.Attempts, "Once the RetryHandler has honoured Retry-After and given up, nothing re-sends the request locally.");
        }

        [TestMethod]
        public async Task GraphImportPipeline_StalledAttempt_StillFailsWithinTheBound()
        {
            var terminal = new SequenceHttpHandler(
                async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return JsonResponse(HttpStatusCode.OK); },
                async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return JsonResponse(HttpStatusCode.OK); });
            var client = BuildGraphImportPipelineClient(terminal, perRequestMs: 100, timeoutRetryCount: 1, totalBudgetMs: 300);
            var sw = Stopwatch.StartNew();

            var ex = await Assert.ThrowsExceptionAsync<TimeoutException>(() => client.GetAsync("https://graph.microsoft.com/v1.0/users"));

            StringAssert.Contains(ex.Message, "Graph import SDK request deadline exhausted");
            Assert.AreEqual(2, terminal.Attempts);
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5), $"A stalled attempt must fail within the timeout budget; elapsed {sw.ElapsedMilliseconds} ms.");
        }

        [TestMethod]
        public void GraphImportPipeline_BoundedHandler_SitsDirectlyInsideRetryHandler()
        {
            var handlers = GraphServiceClientFactory.BuildGraphImportHandlers(GraphRequestBudgetOptions.GraphImportDefault, null);
            var retryIndex = handlers.FindIndex(h => h is Microsoft.Kiota.Http.HttpClientLibrary.Middleware.RetryHandler);

            Assert.IsTrue(retryIndex >= 0);
            Assert.IsInstanceOfType(handlers[retryIndex + 1], typeof(BoundedGraphRequestHandler));
            Assert.AreEqual(1, handlers.Count(h => h is BoundedGraphRequestHandler));
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, int? retryAfterSeconds = null)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? "{}" : "{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"synthetic\"}}",
                    System.Text.Encoding.UTF8, "application/json")
            };
            if (retryAfterSeconds.HasValue)
            {
                response.Headers.Add("Retry-After", retryAfterSeconds.Value.ToString());
            }
            return response;
        }

        private static HttpClient BuildGraphImportPipelineClient(SequenceHttpHandler terminal, int perRequestMs, int timeoutRetryCount, int totalBudgetMs, int sdkRetryCount = 3)
        {
            var budget = new GraphRequestBudgetOptions(
                TimeSpan.FromMilliseconds(perRequestMs),
                timeoutRetryCount,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(totalBudgetMs),
                sdkRetryCount,
                1,
                TimeSpan.FromSeconds(10));
            var authProvider = new Microsoft.Kiota.Authentication.Azure.AzureIdentityAuthenticationProvider(
                new SyntheticTokenCredential(), scopes: new[] { "https://graph.microsoft.com/.default" });
            return GraphServiceClientFactory.CreateHttpClient(authProvider, budget, AnalyticsLogger.ConsoleOnlyTracer(), terminal);
        }

        private sealed class SyntheticTokenCredential : Azure.Core.TokenCredential
        {
            public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new Azure.Core.AccessToken("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));

            public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<Azure.Core.AccessToken>(GetToken(requestContext, cancellationToken));
        }

        [TestMethod]
        public async Task PersistedDeltaProvider_ReadUnavailableWithCommittedToken_UsesSafeFallbackOnlyAfterCommit()
        {
            var store = new FakeStringValueStore();
            var provider = BuildDeltaProvider(store, tenantSeed: 1);
            await provider.SetDeltaToken("committed-token");
            store.ThrowOnGet = true;

            var token = await provider.GetDeltaToken();

            Assert.AreEqual("committed-token", token);
            Assert.AreEqual(3, store.GetAttempts, "The unavailable read is bounded before the last committed in-process checkpoint is used.");
        }

        [TestMethod]
        public async Task PersistedDeltaProvider_ReadUnavailableWithNoSafeFallback_DefersInsteadOfInventingMiss()
        {
            var store = new FakeStringValueStore { ThrowOnGet = true };
            var provider = BuildDeltaProvider(store, tenantSeed: 2);

            await Assert.ThrowsExceptionAsync<DeltaTokenUnavailableException>(() => provider.GetDeltaToken());

            Assert.AreEqual(3, store.GetAttempts);
        }

        [TestMethod]
        public async Task PersistedDeltaProvider_ConfirmedMissingKey_ReturnsNullForExplicitFullEnumerationPath()
        {
            var store = new FakeStringValueStore();
            var provider = BuildDeltaProvider(store, tenantSeed: 3);

            var token = await provider.GetDeltaToken();

            Assert.IsNull(token, "A successful state-store read with no value remains the deliberate first-run/full-load path.");
        }

        [TestMethod]
        public async Task PersistedDeltaProvider_WriteFailureSurfacesAndDoesNotCreateFallbackCheckpoint()
        {
            var store = new FakeStringValueStore { ThrowOnSet = true };
            var provider = BuildDeltaProvider(store, tenantSeed: 4);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => provider.SetDeltaToken("pending-token"));
            store.ThrowOnSet = false;
            store.ThrowOnGet = true;

            await Assert.ThrowsExceptionAsync<DeltaTokenUnavailableException>(() => provider.GetDeltaToken());
        }

        [TestMethod]
        public async Task PersistedDeltaProvider_StoreRecovers_ResumesPersistedCheckpointWithoutOperatorAction()
        {
            var store = new FakeStringValueStore();
            var provider = BuildDeltaProvider(store, tenantSeed: 5);
            await provider.SetDeltaToken("first-committed-token");
            store.ThrowOnGet = true;
            Assert.AreEqual("first-committed-token", await provider.GetDeltaToken());

            store.ThrowOnGet = false;
            await provider.SetDeltaToken("second-committed-token");

            Assert.AreEqual("second-committed-token", await provider.GetDeltaToken());
        }

        [TestMethod]
        public async Task PersistedDeltaProvider_MultipleSyntheticScopes_DoNotReuseCheckpoints()
        {
            var store = new FakeStringValueStore();
            var first = BuildDeltaProvider(store, tenantSeed: 6);
            var second = BuildDeltaProvider(store, tenantSeed: 7);

            await first.SetDeltaToken("contoso-scope-a-token");

            Assert.AreEqual("contoso-scope-a-token", await first.GetDeltaToken());
            Assert.IsNull(await second.GetDeltaToken(), "A token committed for one synthetic tenant/configuration scope must not be reused for another.");
        }

        [TestMethod]
        public async Task PersistedDeltaProvider_CancellationDuringBackoff_StopsWithoutFurtherAttempts()
        {
            var store = new FakeStringValueStore { ThrowOnGet = true };
            var provider = new PersistedDeltaValueProvider(
                BuildConfig(8),
                AnalyticsLogger.ConsoleOnlyTracer(),
                store,
                new DeltaTokenStoreRetryOptions(3, TimeSpan.FromSeconds(5)));

            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(25)))
            {
                await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => provider.GetDeltaToken(cts.Token));
            }

            Assert.AreEqual(1, store.GetAttempts, "Cancellation during state-store retry backoff must stop promptly instead of waiting for the bounded delay.");
        }

        private static HttpClient BuildBoundedClient(SequenceHttpHandler terminal, int timeoutRetryCount, int perRequestMs, int totalBudgetMs)
        {
            var handler = new BoundedGraphRequestHandler(
                new GraphRequestBudgetOptions(
                    TimeSpan.FromMilliseconds(perRequestMs),
                    timeoutRetryCount,
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(totalBudgetMs),
                    0,
                    0,
                    TimeSpan.FromMilliseconds(totalBudgetMs)),
                AnalyticsLogger.ConsoleOnlyTracer())
            {
                InnerHandler = terminal
            };

            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        private static PersistedDeltaValueProvider BuildDeltaProvider(FakeStringValueStore store, int tenantSeed)
            => new PersistedDeltaValueProvider(
                BuildConfig(tenantSeed),
                AnalyticsLogger.ConsoleOnlyTracer(),
                store,
                new DeltaTokenStoreRetryOptions(3, TimeSpan.Zero));

        private static AppConfig BuildConfig(int tenantSeed)
        {
            var config = (AppConfig)FormatterServices.GetUninitializedObject(typeof(AppConfig));
            config.TenantGUID = Guid.Parse($"00000000-0000-0000-0000-00000000000{tenantSeed}");
            config.ConnectionStrings = new AppConnectionStrings();
            return config;
        }

        private sealed class SequenceHttpHandler : HttpMessageHandler
        {
            private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses;
            public int Attempts { get; private set; }
            public List<string> RetryAttemptHeaders { get; } = new List<string>();

            public SequenceHttpHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] responses)
            {
                _responses = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>(responses);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Attempts++;
                RetryAttemptHeaders.Add(request.Headers.TryGetValues("Retry-Attempt", out var values) ? string.Join(",", values) : null);
                var response = _responses.Count == 0
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    : await _responses.Dequeue()(cancellationToken).ConfigureAwait(false);
                // HttpClientHandler sets this; Kiota's RetryHandler clones it to replay a throttled request.
                response.RequestMessage = response.RequestMessage ?? request;
                return response;
            }
        }

        private sealed class FakeStringValueStore : IKeyValueStore
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
            public bool ThrowOnGet { get; set; }
            public bool ThrowOnSet { get; set; }
            public int GetAttempts { get; private set; }

            public string Description => "fake state store";

            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                GetAttempts++;
                if (ThrowOnGet) throw new InvalidOperationException("synthetic state store outage");
                return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
            }

            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                if (ThrowOnSet) throw new InvalidOperationException("synthetic state store write outage");
                _values[key] = value;
                return Task.CompletedTask;
            }

            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_values.Remove(key));
            }

            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_values.ContainsKey(key));
            }
        }
    }
}
