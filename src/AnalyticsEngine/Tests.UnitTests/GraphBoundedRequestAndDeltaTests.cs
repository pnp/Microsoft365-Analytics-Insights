using Common.Entities.Config;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    [TestClass]
    public class GraphBoundedRequestAndDeltaTests
    {
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
        public async Task RedisDeltaProvider_ReadUnavailableWithCommittedToken_UsesSafeFallbackOnlyAfterCommit()
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
        public async Task RedisDeltaProvider_ReadUnavailableWithNoSafeFallback_DefersInsteadOfInventingMiss()
        {
            var store = new FakeStringValueStore { ThrowOnGet = true };
            var provider = BuildDeltaProvider(store, tenantSeed: 2);

            await Assert.ThrowsExceptionAsync<DeltaTokenUnavailableException>(() => provider.GetDeltaToken());

            Assert.AreEqual(3, store.GetAttempts);
        }

        [TestMethod]
        public async Task RedisDeltaProvider_ConfirmedMissingKey_ReturnsNullForExplicitFullEnumerationPath()
        {
            var store = new FakeStringValueStore();
            var provider = BuildDeltaProvider(store, tenantSeed: 3);

            var token = await provider.GetDeltaToken();

            Assert.IsNull(token, "A successful Redis read with no value remains the deliberate first-run/full-load path.");
        }

        [TestMethod]
        public async Task RedisDeltaProvider_WriteFailureSurfacesAndDoesNotCreateFallbackCheckpoint()
        {
            var store = new FakeStringValueStore { ThrowOnSet = true };
            var provider = BuildDeltaProvider(store, tenantSeed: 4);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => provider.SetDeltaToken("pending-token"));
            store.ThrowOnSet = false;
            store.ThrowOnGet = true;

            await Assert.ThrowsExceptionAsync<DeltaTokenUnavailableException>(() => provider.GetDeltaToken());
        }

        [TestMethod]
        public async Task RedisDeltaProvider_StoreRecovers_ResumesPersistedCheckpointWithoutOperatorAction()
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
        public async Task RedisDeltaProvider_MultipleSyntheticScopes_DoNotReuseCheckpoints()
        {
            var store = new FakeStringValueStore();
            var first = BuildDeltaProvider(store, tenantSeed: 6);
            var second = BuildDeltaProvider(store, tenantSeed: 7);

            await first.SetDeltaToken("contoso-scope-a-token");

            Assert.AreEqual("contoso-scope-a-token", await first.GetDeltaToken());
            Assert.IsNull(await second.GetDeltaToken(), "A token committed for one synthetic tenant/configuration scope must not be reused for another.");
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

        private static RedisProcessDeltaValueProvider BuildDeltaProvider(FakeStringValueStore store, int tenantSeed)
        {
            var config = (AppConfig)FormatterServices.GetUninitializedObject(typeof(AppConfig));
            config.TenantGUID = Guid.Parse($"00000000-0000-0000-0000-00000000000{tenantSeed}");
            config.ConnectionStrings = new AppConnectionStrings();

            return new RedisProcessDeltaValueProvider(
                config,
                AnalyticsLogger.ConsoleOnlyTracer(),
                store,
                new DeltaTokenStoreRetryOptions(3, TimeSpan.Zero));
        }

        private sealed class SequenceHttpHandler : HttpMessageHandler
        {
            private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses;
            public int Attempts { get; private set; }

            public SequenceHttpHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] responses)
            {
                _responses = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>(responses);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Attempts++;
                return _responses.Count == 0
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
                    : _responses.Dequeue()(cancellationToken);
            }
        }

        private sealed class FakeStringValueStore : IStringValueStore
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
            public bool ThrowOnGet { get; set; }
            public bool ThrowOnSet { get; set; }
            public int GetAttempts { get; private set; }

            public Task<string> GetString(string key)
            {
                GetAttempts++;
                if (ThrowOnGet) throw new InvalidOperationException("synthetic Redis outage");
                return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
            }

            public Task SetString(string key, string value)
            {
                if (ThrowOnSet) throw new InvalidOperationException("synthetic Redis write outage");
                _values[key] = value;
                return Task.CompletedTask;
            }

            public Task DeleteString(string key)
            {
                _values.Remove(key);
                return Task.CompletedTask;
            }
        }
    }
}

