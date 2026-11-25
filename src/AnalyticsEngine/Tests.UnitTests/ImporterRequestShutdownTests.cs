using Azure.Core;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    [TestClass]
    public class ImporterRequestShutdownTests
    {
        private static readonly DateTime Day = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public async Task ScheduledUpdater_ForwardsItsStopToTheSkuClientsCredential()
        {
            using (var stop = new CancellationTokenSource())
            using (var gate = new RequestGate())
            {
                var settings = new AppConfig { ConnectionStrings = new AppConnectionStrings(_ => string.Empty) };
                var updater = new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), settings, new BlockingCredential(gate),
                    null, null, null, null, stop.Token);
                var request = updater.UserLoader.LoadTenantSkus();
                await gate.WaitForEntryAsync();
                stop.Cancel();
                await AssertCancelled(request);
            }
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task UserSkuRead_StopInterruptsTheFirstOrFollowingSdkRequest(bool holders, bool secondPage)
        {
            using (var stop = new CancellationTokenSource())
            using (var gate = new RequestGate())
            {
                var calls = 0;
                var endpoint = holders ? "users" : "subscribedSkus";
                var handler = new Transport(async (request, token) =>
                {
                    Assert.AreEqual("/v1.0/" + endpoint, request.RequestUri.AbsolutePath);
                    if (++calls == 1 && secondPage)
                        return Json(request, "{\"value\":[{}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/" + endpoint + "?page=2\"}");
                    await gate.WaitAsync(token);
                    return Json(request, "{\"value\":[]}");
                });
                using (var graph = GraphServiceClientFactory.CreateForGraphImport(new Credential(),
                    GraphRequestBudgetOptions.GraphImportDefault, null, handler))
                {
                    var loader = new GraphUserLoader(null, null, NullLogger.Instance, graph, stop.Token);
                    Task request = holders ? (Task)loader.LoadUsersBySku(Guid.Empty) : loader.LoadTenantSkus();
                    await gate.WaitForEntryAsync();
                    stop.Cancel();
                    await AssertCancelled(request);
                    Assert.AreEqual(secondPage ? 2 : 1, calls);
                }
            }
        }

        [TestMethod]
        public async Task UserSkuRead_StopInterruptsKiotaRetryAfter_NotJustTheTransportAttempt()
        {
            using (var stop = new CancellationTokenSource())
            {
                var calls = 0;
                var handler = new Transport((request, token) =>
                {
                    var response = Json(request, "{\"value\":[]}");
                    if (++calls == 1)
                    {
                        response.StatusCode = (HttpStatusCode)429;
                        response.Headers.Add("Retry-After", "2");
                    }
                    return Task.FromResult(response);
                });
                using (var graph = GraphServiceClientFactory.CreateForGraphImport(new Credential(),
                    GraphRequestBudgetOptions.GraphImportDefault, null, handler))
                {
                    var request = new GraphUserLoader(null, null, NullLogger.Instance, graph, stop.Token).LoadTenantSkus();
                    Assert.AreEqual(1, calls);
                    Assert.IsFalse(request.IsCompleted, "The first HTTP attempt already returned its 429; Kiota is waiting to retry.");
                    stop.Cancel();
                    await AssertCancelled(request);
                    Assert.AreEqual(1, calls, "No Kiota status retry or local deadline retry on stop.");
                }
            }
        }

        [DataTestMethod]
        [DoNotParallelize]
        [DataRow(false, "pageview")]
        [DataRow(true, "pageview")]
        [DataRow(true, "event")]
        public async Task WebTraffic_StopInterruptsCountSendOrDayRetry_WithoutAnOutcomeOrCheckpoint(bool stopInRetry, string kind)
        {
            using (var stop = new CancellationTokenSource())
            using (var gate = new RequestGate())
            {
                var calls = 0;
                var handler = new Transport(async (request, token) =>
                {
                    calls++;
                    if (!stopInRetry)
                        await gate.WaitAsync(token);
                    if (calls == 1)
                        return Json(request, "{\"tables\":[{\"name\":\"PrimaryResult\",\"columns\":[{\"name\":\"Kind\"},{\"name\":\"Hour\"},{\"name\":\"RowCount\"}],\"rows\":[[\"" + kind + "\",\"2026-01-01T00:00:00Z\",1]]}]}");
                    var throttled = Json(request, "{}");
                    throttled.StatusCode = (HttpStatusCode)429;
                    throttled.Headers.Add("Retry-After", "60");
                    return throttled;
                });
                using (var api = new AppInsightsAPIClient("ApplicationId=" + Guid.Empty, new Credential(), NullLogger.Instance, handler))
                {
                    api.RetryDelay = (delay, token) =>
                    {
                        Assert.AreEqual(TimeSpan.FromSeconds(60), delay);
                        return gate.WaitAsync(token);
                    };
                    var cursor = new InMemoryIngestionCursorStore();
                    var failures = new AppInsightsDayFailureTracker();
                    var persistence = new InMemoryAppInsightsDayPersistenceManager();
                    var importer = new AppInsightsImporter(null, AnalyticsLogger.ConsoleOnlyTracer(), new FixedClock(Day.AddHours(1)),
                        api, new FakeSiteFilterLoader(), new InMemoryHitWatermarkStore(Day), persistence,
                        cursorStore: cursor, dayFailureTracker: failures);
                    var output = await AppInsightsImporterOrchestrationTests.CaptureConsole(async () =>
                    {
                        var import = importer.ImportAndSave(false, null, stop.Token);
                        await gate.WaitForEntryAsync();
                        stop.Cancel();
                        await AssertCancelled(import);
                    });
                    Assert.IsFalse(output.Contains("Outcome=Failed") || output.Contains("New event 'AppInsightsDay'")
                        || output.Contains("New event 'FinishedSectionImport'"));
                    Assert.AreEqual(stopInRetry ? 2 : 1, calls);
                    Assert.AreEqual(0, cursor.Writes);
                    Assert.IsNull(failures.EarliestDayAwaitingRetryUtc);
                    Assert.AreEqual(0, persistence.SavedPageViews.Count);
                    Assert.AreEqual(0, persistence.SavedCustomEvents.Count);
                }
            }
        }

        [TestMethod]
        public async Task WebTraffic_StopDuringARealHttpResponse_DoesNotNeedTheServerToFinishItsBody()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using (var stop = new CancellationTokenSource())
                using (var transport = new LoopbackTransport((IPEndPoint)listener.LocalEndpoint))
                using (var api = new AppInsightsAPIClient("ApplicationId=" + Guid.Empty, new Credential(), NullLogger.Instance, transport))
                {
                    var accept = listener.AcceptTcpClientAsync();
                    var read = api.GetHourlyRowCountsAsync(AppInsightsReadWindow.WholeDay(Day), stop.Token);
                    Assert.AreSame(accept, await Task.WhenAny(accept, Task.Delay(TimeSpan.FromSeconds(10))));
                    using (var service = await accept)
                    {
                        var prefix = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 1024\r\n\r\n{");
                        await service.GetStream().WriteAsync(prefix, 0, prefix.Length);
                        var headers = transport.ReceivedHeaders.Task;
                        Assert.AreSame(headers, await Task.WhenAny(headers, Task.Delay(TimeSpan.FromSeconds(10))));
                        stop.Cancel();
                        await AssertCancelled(read);
                        Assert.AreEqual(1, transport.Calls);
                    }
                }
            }
            finally { listener.Stop(); }
        }

        private static async Task AssertCancelled(Task request)
        {
            Assert.AreSame(request, await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(3))), "The stop did not interrupt the request.");
            await Assert.ThrowsAsync<OperationCanceledException>(() => request);
        }

        private static HttpResponseMessage Json(HttpRequestMessage request, string json) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

        private sealed class Transport : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
            public Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => _send(request, token);
        }

        private sealed class LoopbackTransport : DelegatingHandler
        {
            private readonly Uri _destination;
            public int Calls { get; private set; }
            public readonly TaskCompletionSource<bool> ReceivedHeaders = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public LoopbackTransport(IPEndPoint endpoint) : base(new HttpClientHandler { UseProxy = false }) =>
                _destination = new Uri("http://" + endpoint + "/");
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Calls++;
                request.RequestUri = _destination;
                var response = await base.SendAsync(request, token);
                ReceivedHeaders.TrySetResult(true);
                return response;
            }
        }

        private sealed class Credential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) =>
                new AccessToken("synthetic", DateTimeOffset.UtcNow.AddHours(1));
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) =>
                new ValueTask<AccessToken>(GetToken(context, token));
        }

        private sealed class BlockingCredential : TokenCredential
        {
            private readonly RequestGate _gate;
            public BlockingCredential(RequestGate gate) => _gate = gate;
            public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => throw new NotSupportedException();
            public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token)
            {
                await _gate.WaitAsync(token);
                throw new InvalidOperationException("No credential is issued, even when unwinding a failed test.");
            }
        }

        private sealed class RequestGate : IDisposable
        {
            private readonly TaskCompletionSource<bool> _entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task WaitAsync(CancellationToken token)
            {
                using (token.Register(() => _release.TrySetCanceled()))
                {
                    _entered.TrySetResult(true);
                    await _release.Task;
                }
            }

            public async Task WaitForEntryAsync() =>
                Assert.AreSame(_entered.Task, await Task.WhenAny(_entered.Task, Task.Delay(TimeSpan.FromSeconds(30))), "Did not reach the gated operation.");

            public void Dispose() => _release.TrySetResult(true);
        }
    }
}
