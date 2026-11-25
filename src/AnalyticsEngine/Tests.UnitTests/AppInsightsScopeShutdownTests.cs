using Common.Entities.Config;
using Common.Entities.UserScope;
using DataUtils;
using DataUtils.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Tests.UnitTests
{
    [TestClass]
    public class AppInsightsScopeShutdownTests
    {
        private const string Group = "00000000-0000-0000-0000-000000000001";
        private const string Members = "/v1.0/groups/" + Group + "/members/microsoft.graph.user";
        private static readonly TimeSpan EntryDeadline = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(5);

        [DataTestMethod]
        [DataRow("credential")]
        [DataRow("page")]
        [DataRow("retry-after")]
        public async Task StopDuringScopeRefresh_ExitsBeforeImportOrCycleOutcome(string boundary)
        {
            using (var stop = new CancellationTokenSource())
            using (var cleanup = new CancellationTokenSource())
            {
                var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                var stopped = false;
                async Task Block(CancellationToken token)
                {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cleanup.Token))
                    {
                        entered.TrySetResult(token);
                        await Task.Delay(Timeout.Infinite, linked.Token);
                    }
                }

                var logger = new RecordingLogger();
                var clock = new RetryClock();
                clock.OnDelay = (delay, token) =>
                {
                    Assert.AreEqual("retry-after", boundary);
                    Assert.AreEqual(TimeSpan.FromMinutes(1), delay);
                    return Block(token);
                };
                var tokenRequests = 0;
                var graphRequests = 0;
                using (var auth = new SyntheticIdentity(async token =>
                {
                    tokenRequests++;
                    if (boundary == "credential" && !stopped) await Block(token);
                    return TokenResponse();
                }))
                using (var graph = new Handler(async (request, token) =>
                {
                    graphRequests++;
                    Assert.AreEqual("Bearer synthetic", request.Headers.Authorization.ToString());
                    Assert.AreEqual("graph.microsoft.com", request.RequestUri.Host);
                    if (request.RequestUri.AbsolutePath == "/v1.0/groups/" + Group)
                        return Json("{\"id\":\"" + Group + "\",\"displayName\":\"Pilot\"}");
                    Assert.AreEqual(Members, request.RequestUri.AbsolutePath);
                    if (!request.RequestUri.Query.Contains("page=2"))
                        return Json("{\"value\":[{\"userPrincipalName\":\"first@contoso.example\"}],\"@odata.nextLink\":\"https://graph.microsoft.com" + Members + "?page=2\"}");
                    if (!stopped && boundary == "retry-after")
                    {
                        var throttled = new HttpResponseMessage((HttpStatusCode)429);
                        throttled.Headers.Add("Retry-After", "60");
                        return throttled;
                    }
                    if (!stopped && boundary == "page") await Block(token);
                    return Json("{\"value\":[{\"userPrincipalName\":\"second@contoso.example\"}]}");
                }))
                {
                    var provider = UserImportScopeProvider.CreateForGraph(new UserGroupsFilterModel(Group), auth, logger, graph, clock);
                    var cursor = new InMemoryIngestionCursorStore();
                    int imports = 0, outcomes = 0, pauses = 0;
                    var run = Task.Run(() => HostRuntime.RunGuarded("ScopeTest", () => AppInsightsImportLoop.Run(false, () =>
                    {
                        var scope = provider.GetScopeForCycleAsync(stop.Token).GetAwaiter().GetResult();
                        imports++;
                        var importer = new AppInsightsImporter(null, AnalyticsLogger.ConsoleOnlyTracer(),
                            source: new FakeAppInsightsSourceLoader(), siteFilterLoader: new FakeSiteFilterLoader(),
                            hitWatermarkStore: new InMemoryHitWatermarkStore(), persistence: new InMemoryAppInsightsDayPersistenceManager(),
                            userScope: scope, cursorStore: cursor);
                        var result = importer.ImportAndSave(false, null, stop.Token).GetAwaiter().GetResult();
                        outcomes++;
                        return result.Outcome;
                    }, () => pauses++, () => stop.IsCancellationRequested)));
                    try
                    {
                        Assert.AreSame(entered.Task, await Task.WhenAny(entered.Task, run, Task.Delay(EntryDeadline)), "Did not reach " + boundary);
                        stop.Cancel();
                        Assert.AreSame(run, await Task.WhenAny(run, Task.Delay(StopDeadline)), "Scope refresh is still blocked in " + boundary);
                        Assert.IsTrue((await entered.Task).IsCancellationRequested, "Cancellation must reach the held operation itself.");
                        Assert.AreEqual(0, await run, "Only an intentional host shutdown exits successfully.");
                        Assert.AreEqual(0, imports);
                        Assert.AreEqual(0, outcomes);
                        Assert.AreEqual(0, cursor.Writes);
                        Assert.AreEqual(0, pauses);
                        Assert.AreEqual(1, tokenRequests);
                        Assert.AreEqual(boundary == "credential" ? 0 : 3, graphRequests, "No request after the stop.");
                        Assert.IsFalse(logger.Entries.Any(e => e.Level >= LogLevel.Error || e.Message.StartsWith("User import scope for this cycle")));

                        // The shared Retry-After still applies to a later caller. Once it expires, the cancelled
                        // resolution must not masquerade as a cached failure, a partial group, or everyone.
                        stopped = true;
                        clock.UtcNow = clock.UtcNow.AddMinutes(2);
                        var scope = await provider.GetScopeAsync();
                        Assert.IsTrue(scope.IsFiltered);
                        Assert.AreEqual(2, scope.MemberCount);
                    }
                    finally { cleanup.Cancel(); }
                }
            }
        }

        [TestMethod]
        public async Task StopWhileReadingTheDirectoryResponse_DoesNotWaitForTheRestOfTheBody()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var headers = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var stop = new CancellationTokenSource())
                using (var auth = new SyntheticIdentity(_ => Task.FromResult(TokenResponse())))
                using (var loopback = new HttpMessageInvoker(new HttpClientHandler { UseProxy = false }))
                using (var graph = new Handler(async (request, token) =>
                {
                    request.RequestUri = new Uri("http://" + listener.LocalEndpoint + "/");
                    var response = await loopback.SendAsync(request, token);
                    headers.TrySetResult(true);
                    return response;
                }))
                {
                    var provider = UserImportScopeProvider.CreateForGraph(new UserGroupsFilterModel("Pilot"), auth, NullLogger.Instance, graph);
                    var accept = listener.AcceptTcpClientAsync();
                    var cycle = provider.GetScopeForCycleAsync(stop.Token);
                    Assert.AreSame(accept, await Task.WhenAny(accept, cycle, Task.Delay(EntryDeadline)));
                    using (var server = await accept)
                    {
                        var prefix = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 1024\r\n\r\n{");
                        await server.GetStream().WriteAsync(prefix, 0, prefix.Length);
                        Assert.AreSame(headers.Task, await Task.WhenAny(headers.Task, cycle, Task.Delay(EntryDeadline)));
                        stop.Cancel();
                        Assert.AreSame(cycle, await Task.WhenAny(cycle, Task.Delay(StopDeadline)));
                        await Assert.ThrowsAsync<ShutdownRequestedException>(() => cycle);
                    }
                }
            }
            finally { listener.Stop(); }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task LiveHost_DirectoryTimeoutRetries_ButPermissionDenialKeepsExistingPolicy(bool timeout)
        {
            int calls = 0, waits = 0;
            var clock = new RetryClock { OnDelay = (_, __) => { waits++; return Task.CompletedTask; } };
            using (var running = new CancellationTokenSource())
            using (var auth = new SyntheticIdentity(_ => Task.FromResult(TokenResponse())))
            using (var graph = new Handler((request, _) =>
            {
                calls++;
                if (timeout && calls == 1) throw new TaskCanceledException("Synthetic HTTP timeout, not a host stop.");
                return Task.FromResult(timeout ? Json("{\"value\":[]}") : new HttpResponseMessage(HttpStatusCode.Forbidden));
            }))
            {
                var provider = UserImportScopeProvider.CreateForGraph(new UserGroupsFilterModel("Pilot"), auth, NullLogger.Instance, graph, clock);
                var scope = await provider.GetScopeForCycleAsync(running.Token);
                var resolution = await provider.GetResolutionAsync();
                Assert.AreEqual(timeout ? 2 : 1, calls);
                Assert.AreEqual(timeout ? 1 : 0, waits);
                Assert.AreEqual(timeout, scope.IsFiltered, "A 403 with no last good scope still fails open for web traffic.");
                Assert.AreEqual(timeout ? UserImportScopeStatus.Resolved : UserImportScopeStatus.Unavailable, resolution.Status);
                if (!timeout) Assert.AreEqual(403, resolution.FailureHttpStatus);
            }
        }

        [TestMethod]
        public async Task StoppingAWaitingCycle_DoesNotCancelTheRefreshItWasWaitingFor()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            using (var waiter = new CancellationTokenSource())
            using (var auth = new SyntheticIdentity(_ => Task.FromResult(TokenResponse())))
            using (var graph = new Handler(async (_, token) =>
            {
                calls++;
                entered.SetResult(true);
                await release.Task;
                token.ThrowIfCancellationRequested();
                return Json("{\"value\":[]}");
            }))
            {
                var provider = UserImportScopeProvider.CreateForGraph(new UserGroupsFilterModel("Pilot"), auth, NullLogger.Instance, graph);
                var refresh = provider.RefreshAsync();
                try
                {
                    Assert.AreSame(entered.Task, await Task.WhenAny(entered.Task, refresh, Task.Delay(EntryDeadline)));
                    var cycle = provider.GetScopeForCycleAsync(waiter.Token);
                    waiter.Cancel();
                    Assert.AreSame(cycle, await Task.WhenAny(cycle, Task.Delay(StopDeadline)));
                    await Assert.ThrowsAsync<ShutdownRequestedException>(() => cycle);
                    Assert.IsFalse(refresh.IsCompleted);
                }
                finally { release.TrySetResult(true); }
                Assert.AreEqual(UserImportScopeStatus.Resolved, (await refresh).Status);
                Assert.AreEqual(1, calls);
            }
        }

        [TestMethod]
        public async Task UnconfiguredScopeNeedsNoDirectory_AndStillHonoursAHostStop()
        {
            var provider = UserImportScopeProvider.CreateForGraph(null, NullLogger.Instance);
            using (var stop = new CancellationTokenSource())
            {
                Assert.IsFalse((await provider.GetScopeForCycleAsync(stop.Token)).IsFiltered);
                Assert.AreEqual(UserImportScopeStatus.Unfiltered, (await provider.GetResolutionAsync()).Status);
                stop.Cancel();
                await Assert.ThrowsAsync<ShutdownRequestedException>(() => provider.GetScopeForCycleAsync(stop.Token));
            }
        }

        private sealed class RetryClock : IAutoThrottleHttpClientClock
        {
            public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
            public Func<TimeSpan, CancellationToken, Task> OnDelay { get; set; }
            public async Task DelayAsync(TimeSpan delay, CancellationToken token)
            {
                await OnDelay(delay, token);
                UtcNow += delay;
            }
        }

        // Exercise the actual MSAL acquisition, including its cancellation token, with no network transport.
        private sealed class SyntheticIdentity : GraphDirectoryOAuthContext, IMsalHttpClientFactory, IDisposable
        {
            private readonly HttpClient _http;
            public SyntheticIdentity(Func<CancellationToken, Task<HttpResponseMessage>> tokenEndpoint)
                : base(NullLogger.Instance, Group, Group, "synthetic", null, false)
            {
                _http = new HttpClient(new Handler((request, token) =>
                {
                    Assert.AreEqual("login.microsoftonline.com", request.RequestUri.Host);
                    Assert.AreEqual(HttpMethod.Post, request.Method);
                    return tokenEndpoint(token);
                }));
            }
            internal override Task<IConfidentialClientApplication> CreateClientApplicationAsync(CancellationToken cancellationToken) =>
                Task.FromResult(ConfidentialClientApplicationBuilder.Create(Group).WithClientSecret("synthetic")
                    .WithAuthority("https://login.microsoftonline.com/" + Group).WithInstanceDiscovery(false).WithHttpClientFactory(this).Build());
            public HttpClient GetHttpClient() => _http;
            public void Dispose() => _http.Dispose();
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
            public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => _send(request, token);
        }

        private static HttpResponseMessage TokenResponse() => Json("{\"token_type\":\"Bearer\",\"expires_in\":3600,\"access_token\":\"synthetic\"}");
        private static HttpResponseMessage Json(string body) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
