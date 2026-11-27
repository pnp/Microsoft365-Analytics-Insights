using Common.Entities.Config;
using Common.Entities.State;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using WebJob.Office365ActivityImporter.Engine.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
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

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GraphFactories_RelativeRedirects_UseEachCurrentHopAndItsOrigin(bool singleTimeout)
        {
            var hops = new List<string>();
            var authenticatedHosts = new List<string>();
            using (var optionalBody = new GatedJsonContent())
            {
                var terminal = new SequenceHttpHandler(
                    ct => Task.FromResult(RelativeRedirect("?part=2", optionalBody)),
                    ct => Task.FromResult(RelativeRedirect("pages/next")),
                    ct => Task.FromResult(RelativeRedirect("/root/page")),
                    ct => Task.FromResult(RelativeRedirect("//download.contoso.invalid/reports/page")),
                    ct => Task.FromResult(RelativeRedirect("?part=3")),
                    ct => Task.FromResult(JsonResponse(HttpStatusCode.OK)))
                {
                    ObserveRequest = r =>
                    {
                        hops.Add(r.RequestUri.AbsoluteUri);
                        if (r.Headers.Authorization != null) authenticatedHosts.Add(r.RequestUri.Host);
                    }
                };
                var credential = new SyntheticTokenCredential();
                using (var graph = singleTimeout
                    ? GraphServiceClientFactory.CreateWithTimeout(GraphServiceClientFactory.CreateAuthenticationProvider(credential), TimeSpan.FromSeconds(1), terminal)
                    : GraphServiceClientFactory.CreateForGraphImport(credential, TestBudget(1000, 1, 3000), null, terminal))
                {
                    Assert.IsNotNull(await WithinWatchdog(graph.Users.GetAsync()));
                    CollectionAssert.AreEqual(new[]
                    {
                        "https://graph.microsoft.com/v1.0/users", "https://graph.microsoft.com/v1.0/users?part=2",
                        "https://graph.microsoft.com/v1.0/pages/next", "https://graph.microsoft.com/root/page",
                        "https://download.contoso.invalid/reports/page", "https://download.contoso.invalid/reports/page?part=3"
                    }, hops);
                    CollectionAssert.AreEqual(Enumerable.Repeat("graph.microsoft.com", 4).ToList(), authenticatedHosts);
                    Assert.IsFalse(optionalBody.Started.Task.IsCompleted);
                    Assert.IsTrue(optionalBody.Disposed);
                }
            }
        }

        [DataTestMethod]
        [DataRow(200)]
        [DataRow(503)]
        public async Task GraphAuditSingleAttempt_FinalBodyDeadlineDoesNotReplay(int status)
        {
            using (var finalBody = new GatedJsonContent())
            {
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(RelativeRedirect("?part=2")),
                    ct => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = finalBody }));
                using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromMilliseconds(250), terminal))
                {
                    var loader = new GraphFileMetadataLoader(graph, AnalyticsLogger.ConsoleOnlyTracer());
                    var elapsed = Stopwatch.StartNew();
                    var lookup = loader.GetUserIdFromUpnSingleAttempt("reader@contoso.com");
                    if (status == 200)
                    {
                        await Assert.ThrowsAsync<OperationCanceledException>(() => WithinWatchdog(lookup));
                    }
                    else
                    {
                        var error = await Assert.ThrowsExceptionAsync<CopilotEnrichmentTransientException>(() => WithinWatchdog(lookup));
                        Assert.AreEqual(status, ((ApiException)error.InnerException).ResponseStatusCode);
                    }
                    Assert.IsTrue(finalBody.Started.Task.IsCompleted && finalBody.Disposed);
                    Assert.AreEqual(2, terminal.Attempts, "Only the original request and the redirected request are allowed under the save lock.");
                    Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"The 250 ms timeout took {elapsed.ElapsedMilliseconds} ms.");
                }
            }
        }

        [TestMethod]
        public async Task GraphSingleTimeout_ServiceRetryAfterIsInsideTheOverallDeadline()
        {
            var terminal = new SequenceHttpHandler(ct => Task.FromResult(JsonResponse((HttpStatusCode)429, retryAfterSeconds: 2)),
                ct => Task.FromResult(JsonResponse(HttpStatusCode.OK)));
            using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromMilliseconds(250), terminal))
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => WithinWatchdog(graph.Users.GetAsync()));
                Assert.AreEqual(1, terminal.Attempts, "The overall timeout ends the SDK wait before it sends again.");
            }
        }

        [TestMethod]
        public async Task GraphSingleTimeout_CallerCancellationOnAnErrorBodyIsNotAStatusResult()
        {
            using (var body = new GatedJsonContent())
            using (var caller = new CancellationTokenSource())
            {
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = body }));
                using (var graph = GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromSeconds(30), terminal))
                {
                    var request = graph.Users.GetAsync(cancellationToken: caller.Token);
                    await WithinWatchdog(body.Started.Task);
                    caller.Cancel();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => WithinWatchdog(request));
                    Assert.IsTrue(body.Disposed);
                    Assert.AreEqual(1, terminal.Attempts);
                }
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task GraphImportPipeline_CustomRedirectDecisionSeesOriginalLocationAndBody(bool follow)
        {
            var decisions = 0;
            var terminal = new SequenceHttpHandler(ct => Task.FromResult(RelativeRedirect("?part=2", new StringContent("Contoso"))),
                ct => Task.FromResult(JsonResponse(HttpStatusCode.OK)));
            using (var http = BuildGraphImportPipelineClient(terminal, 1000, 0, 1000))
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users"))
            {
                var option = new RedirectHandlerOption
                {
                    ShouldRedirect = response =>
                    {
                        decisions++;
                        Assert.AreEqual("?part=2", response.Headers.Location.OriginalString);
                        Assert.AreEqual("Contoso", response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                        return follow;
                    }
                };
                request.Properties[typeof(RedirectHandlerOption).FullName] = option;
                using (var response = await WithinWatchdog(http.SendAsync(request)))
                {
                    Assert.AreEqual(1, decisions);
                    Assert.AreSame(option, response.RequestMessage.Properties[typeof(RedirectHandlerOption).FullName]);
                    if (follow)
                    {
                        CollectionAssert.AreEqual(new[] { "GET /v1.0/users", "GET /v1.0/users?part=2" }, terminal.Requests);
                        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    }
                    else
                    {
                        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
                        Assert.AreEqual("?part=2", response.Headers.Location.OriginalString);
                        Assert.AreEqual("Contoso", await response.Content.ReadAsStringAsync());
                        Assert.AreEqual(1, terminal.Attempts);
                    }
                }
            }
        }

        [DataTestMethod]
        [DataRow(302, true, true)]
        [DataRow(302, false, false)]
        [DataRow(300, false, true)]
        public async Task GraphImportPipeline_UnreadablePassThroughBody_FailsBeforeRedirectDecision(int status, bool disabled, bool oversized)
        {
            var decisions = 0;
            using (var body = new GatedJsonContent { ReadFailure = new IOException("Synthetic body transfer failed.") })
            {
                if (oversized) body.Headers.ContentLength = 64L * 1024 * 1024;
                body.Release();
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(ResponseWithLocation((HttpStatusCode)status, body)));
                using (var http = BuildGraphImportPipelineClient(terminal, 1000, 1, 3000))
                using (var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users"))
                {
                    request.Properties[typeof(RedirectHandlerOption).FullName] = new RedirectHandlerOption
                    {
                        MaxRedirect = disabled ? 0 : 1,
                        ShouldRedirect = _ => { decisions++; return false; }
                    };
                    await Assert.ThrowsAsync<HttpRequestException>(() => WithinWatchdog(http.SendAsync(request)));
                    Assert.AreEqual(!oversized, body.Started.Task.IsCompleted);
                    Assert.IsTrue(body.Disposed);
                    Assert.AreEqual(0, decisions);
                    Assert.AreEqual(1, terminal.Attempts);
                }
            }
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task GraphFactories_StalledCustomRedirectBody_PropagatesCancellationOrDeadline(bool singleTimeout, bool cancelCaller)
        {
            using (var caller = new CancellationTokenSource())
            using (var body = new GatedJsonContent())
            {
                var timeoutMs = cancelCaller ? 30000 : 250;
                var decisions = 0;
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(RelativeRedirect("?part=2", body)));
                using (var graph = singleTimeout
                    ? GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromMilliseconds(timeoutMs), terminal)
                    : GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(), TestBudget(timeoutMs, 0, timeoutMs), null, terminal))
                {
                    var read = graph.Users.GetAsync(rc => rc.Options.Add(new RedirectHandlerOption
                    {
                        ShouldRedirect = _ => { decisions++; return false; }
                    }), caller.Token);
                    await WithinWatchdog(body.Started.Task);
                    if (cancelCaller) caller.Cancel();
                    if (singleTimeout || cancelCaller)
                        await Assert.ThrowsAsync<OperationCanceledException>(() => WithinWatchdog(read));
                    else
                        await Assert.ThrowsExceptionAsync<TimeoutException>(() => WithinWatchdog(read));
                    Assert.AreEqual(0, decisions, "The redirect predicate must never see a fabricated empty body.");
                    Assert.AreEqual(1, terminal.Attempts);
                    Assert.IsTrue(body.Disposed);
                }
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GraphFactories_PassThroughRedirectReturnedAfterDeadline_DoesNotEscapeOuterTimeout(bool singleTimeout)
        {
            var deadline = CancellationToken.None;
            var terminal = new SequenceHttpHandler(ct =>
            {
                deadline = ct;
                return Task.FromResult(RelativeRedirect("?part=2", new StringContent("Contoso")));
            });
            using (var graph = singleTimeout
                ? GraphServiceClientFactory.CreateWithTimeout(new AnonymousAuthenticationProvider(), TimeSpan.FromMilliseconds(250), terminal)
                : GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(), TestBudget(250, 0, 250), null, terminal))
            {
                var read = graph.Users.GetAsync(rc => rc.Options.Add(new RedirectHandlerOption
                {
                    ShouldRedirect = _ =>
                    {
                        Assert.IsTrue(deadline.WaitHandle.WaitOne(TimeSpan.FromSeconds(3)));
                        return false;
                    }
                }));
                if (singleTimeout)
                    await Assert.ThrowsAsync<OperationCanceledException>(() => WithinWatchdog(read));
                else
                    await Assert.ThrowsExceptionAsync<TimeoutException>(() => WithinWatchdog(read));
                Assert.AreEqual(1, terminal.Attempts);
            }
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

        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(false, false)]
        [DataRow(true, true)]
        public async Task GraphImportSdk_StalledBody_AbortsAndRetriesGetThenParsesThePage(bool stopsOnDispose, bool inspectBody)
        {
            var body = new GatedJsonContent(stopsOnDispose);
            var transportToken = CancellationToken.None;
            var terminal = new SequenceHttpHandler(ct =>
            {
                transportToken = ct;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
            }, ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[{\"displayName\":\"Contoso\"}]}", System.Text.Encoding.UTF8, "application/json")
            }));
            using (var client = GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(),
                TestBudget(150, 1, 2000), null, terminal))
            {
                try
                {
                    var users = await WithinWatchdog(client.Users.GetAsync(rc =>
                    {
                        if (inspectBody) rc.Options.Add(new BodyInspectionHandlerOption { InspectResponseBody = true });
                    }));
                    Assert.AreEqual("Contoso", users.Value.Single().DisplayName);
                    Assert.IsTrue(body.Started.Task.IsCompleted && body.Disposed && transportToken.IsCancellationRequested);
                    CollectionAssert.AreEqual(new string[] { null, null }, terminal.RetryAttemptHeaders,
                        "The retry of the interrupted page is local, not a Kiota status retry.");
                }
                finally { body.Release(); }
            }
        }

        [TestMethod]
        public async Task GraphImportSdk_StalledCreatedBody_IsNotPermissionToReplayPost()
        {
            var body = new GatedJsonContent();
            var terminal = new SequenceHttpHandler(ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = body }));
            using (var client = GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(), TestBudget(150, 2, 2000), null, terminal))
            {
                try
                {
                    await Assert.ThrowsExceptionAsync<TimeoutException>(() => WithinWatchdog(client.Users.PostAsync(new Microsoft.Graph.Models.User())));
                    Assert.IsTrue(body.Started.Task.IsCompleted && body.Disposed);
                    Assert.AreEqual(1, terminal.Attempts);
                }
                finally { body.Release(); }
            }
        }

        [TestMethod]
        public async Task GraphImportSdk_CallerCancelsBody_AbortsWithoutRetry()
        {
            var body = new GatedJsonContent();
            var terminal = new SequenceHttpHandler(ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body }));
            using (var caller = new CancellationTokenSource())
            using (var client = GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(), TestBudget(30000, 1, 60000), null, terminal))
            {
                try
                {
                    var request = client.Users.GetAsync(cancellationToken: caller.Token);
                    await WithinWatchdog(body.Started.Task);
                    caller.Cancel();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => WithinWatchdog(request));
                    Assert.IsTrue(body.Disposed);
                    Assert.AreEqual(1, terminal.Attempts);
                }
                finally { body.Release(); }
            }
        }

        [TestMethod]
        public async Task GraphImportSdk_UnreadableForbiddenBody_PreservesStatusWithoutRetry()
        {
            using (var body = new GatedJsonContent())
            {
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = body }));
                using (var client = GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(), TestBudget(150, 2, 2000), null, terminal))
                {
                    var error = await Assert.ThrowsAsync<ApiException>(() => WithinWatchdog(client.Users.GetAsync()));
                    Assert.AreEqual(403, error.ResponseStatusCode);
                    Assert.IsTrue(body.Started.Task.IsCompleted && body.Disposed);
                    Assert.AreEqual(1, terminal.Attempts);
                }
            }
        }

        [DataTestMethod]
        [DataRow(403)]
        [DataRow(404)]
        public async Task GraphUserLoader_UnreadableSubscribedSkus_OnlyForbiddenUsesPerUserFallback(int status)
        {
            using (var body = new GatedJsonContent())
            {
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = body }));
                using (var graph = GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(), TestBudget(250, 1, 2000), null, terminal))
                {
                    var loader = new GraphUserLoader(null, null, AnalyticsLogger.ConsoleOnlyTracer(), graph);
                    if (status == 403)
                    {
                        Assert.IsNull(await WithinWatchdog(loader.LoadTenantSkus()), "Null selects the per-user fallback, not an empty SKU inventory.");
                    }
                    else
                    {
                        var error = await Assert.ThrowsExceptionAsync<ApiException>(() => WithinWatchdog(loader.LoadTenantSkus()));
                        Assert.AreEqual(status, error.ResponseStatusCode);
                    }
                    Assert.IsTrue(body.Started.Task.IsCompleted && body.Disposed);
                    CollectionAssert.AreEqual(new[] { "GET /v1.0/subscribedSkus" }, terminal.Requests);
                }
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ReusedUpn_StalledPreviousObject404IsNotAbsence_AndCallerCancellationStillPropagates(bool stop)
        {
            var current = new GraphUser { Id = Guid.Empty.ToString(), UserPrincipalName = "person@contoso.example", AccountEnabled = true };
            const string previous = "00000000-0000-0000-0000-000000000001";
            var currentJson = "{\"id\":\"" + current.Id + "\",\"userPrincipalName\":\"" + current.UserPrincipalName + "\",\"accountEnabled\":true}";
            using (var body = new GatedJsonContent())
            using (var caller = new CancellationTokenSource())
            {
                var terminal = new SequenceHttpHandler(
                    ct => Task.FromResult(TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK, currentJson)),
                    ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = body }),
                    ct => Task.FromResult(TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK, "{\"value\":[" + currentJson + "]}")));
                using (var graph = GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(), TestBudget(stop ? 30000 : 250, 1, 60000), null, terminal))
                {
                    var loader = new GraphUserLoader(null, null, AnalyticsLogger.ConsoleOnlyTracer(), graph, caller.Token);
                    var request = loader.VerifyReusedUpnAsync(current, previous);
                    await WithinWatchdog(body.Started.Task);
                    if (stop)
                    {
                        caller.Cancel();
                        await Assert.ThrowsAsync<OperationCanceledException>(() => WithinWatchdog(request));
                    }
                    else
                    {
                        Assert.IsFalse(await WithinWatchdog(request));
                    }
                    Assert.AreEqual(2, terminal.Attempts, "An unreadable previous-object response does not permit the directory release check.");
                    Assert.IsTrue(body.Disposed);
                }
            }
        }

        [TestMethod]
        public async Task GraphUserLoader_CancelledSubscribedSkus_DoesNotUsePermissionFallback()
        {
            using (var body = new GatedJsonContent())
            {
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = body }));
                using (var http = BuildGraphImportPipelineClient(terminal, 30000, 1, 60000))
                using (var graph = new Microsoft.Graph.GraphServiceClient(http, new AnonymousAuthenticationProvider()))
                {
                    var load = new GraphUserLoader(null, null, AnalyticsLogger.ConsoleOnlyTracer(), graph).LoadTenantSkus();
                    await WithinWatchdog(body.Started.Task);
                    http.CancelPendingRequests();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => WithinWatchdog(load));
                    Assert.IsTrue(body.Disposed);
                    Assert.AreEqual(1, terminal.Attempts);
                }
            }
        }

        [TestMethod]
        public async Task GraphImportSdk_OversizedPage_RefusesBodyWithoutReadingOrRetrying()
        {
            using (var body = new GatedJsonContent())
            {
                body.Headers.ContentLength = 64L * 1024 * 1024;
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body }));
                using (var client = GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(), TestBudget(5000, 2, 10000), null, terminal))
                {
                    await Assert.ThrowsAsync<HttpRequestException>(() => WithinWatchdog(client.Users.GetAsync()));
                    Assert.IsFalse(body.Started.Task.IsCompleted, "A known oversize page must not allocate a body buffer.");
                    Assert.IsTrue(body.Disposed);
                    Assert.AreEqual(1, terminal.Attempts);
                }
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task GraphImportPipeline_HeadOrNotModified_KeepsResourceLengthWithoutBufferingIt(bool head)
        {
            using (var body = new GatedJsonContent())
            {
                const long resourceLength = 64L * 1024 * 1024;
                body.Headers.ContentLength = resourceLength;
                var status = head ? HttpStatusCode.OK : HttpStatusCode.NotModified;
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(new HttpResponseMessage(status) { Content = body }));
                using (var client = BuildGraphImportPipelineClient(terminal, 500, 0, 500))
                using (var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, "https://graph.microsoft.com/v1.0/users"))
                using (var response = await WithinWatchdog(client.SendAsync(request)))
                {
                    Assert.AreEqual(status, response.StatusCode);
                    Assert.AreEqual(resourceLength, response.Content.Headers.ContentLength);
                    Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length);
                    Assert.IsFalse(body.Started.Task.IsCompleted);
                    Assert.IsTrue(body.Disposed);
                }
            }
        }

        [DataTestMethod]
        [DataRow(301, false)]
        [DataRow(302, false)]
        [DataRow(302, true)]
        [DataRow(303, false)]
        [DataRow(307, false)]
        [DataRow(308, false)]
        public async Task GraphImportSdk_Redirect_FollowsLocationWithoutReadingOptionalBody(int status, bool configureHandlers)
        {
            using (var body = new GatedJsonContent())
            {
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(ResponseWithLocation((HttpStatusCode)status, body)), ct =>
                {
                    Assert.IsTrue(body.Disposed, "Release the redirect transport before requesting the destination.");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"value\":[{\"displayName\":\"Contoso\"}]}", System.Text.Encoding.UTF8, "application/json")
                    });
                });
                using (var client = GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(), TestBudget(500, 1, 2000), null, terminal))
                {
                    var page = await WithinWatchdog(client.Users.GetAsync(rc =>
                    {
                        if (configureHandlers)
                        {
                            rc.Options.Add(new RedirectHandlerOption { MaxRedirect = 1 });
                            rc.Options.Add(new BodyInspectionHandlerOption { InspectResponseBody = true });
                        }
                    }));
                    Assert.AreEqual("Contoso", page.Value.Single().DisplayName);
                    CollectionAssert.AreEqual(new[] { "GET /v1.0/users", "GET /v1.0/users?$select=id" }, terminal.Requests);
                    Assert.IsFalse(body.Started.Task.IsCompleted, "Neither the bounded reader nor Kiota should drain the redirect.");
                }
            }
        }

        [DataTestMethod]
        [DataRow(303, "GET")]
        [DataRow(307, "POST")]
        public async Task GraphImportSdk_RedirectedPost_FinalBodyDeadlineDoesNotReplayWrite(int status, string redirectedMethod)
        {
            using (var redirectBody = new GatedJsonContent())
            using (var finalBody = new GatedJsonContent())
            {
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(ResponseWithLocation((HttpStatusCode)status, redirectBody)),
                    ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = finalBody }));
                using (var client = GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(), TestBudget(250, 2, 2000), null, terminal))
                {
                    await Assert.ThrowsExceptionAsync<TimeoutException>(() => WithinWatchdog(client.Users.PostAsync(new Microsoft.Graph.Models.User())));
                    CollectionAssert.AreEqual(new[] { "POST /v1.0/users", redirectedMethod + " /v1.0/users?$select=id" }, terminal.Requests);
                    Assert.AreEqual(redirectedMethod == "POST", terminal.LastRequestHadContent);
                    Assert.IsFalse(redirectBody.Started.Task.IsCompleted);
                    Assert.IsTrue(redirectBody.Disposed);
                    Assert.IsTrue(finalBody.Started.Task.IsCompleted && finalBody.Disposed, "The deadline must abort the destination body.");
                }
            }
        }

        [TestMethod]
        public void GraphImportTransport_LeavesRedirectsToKiota()
        {
            using (var transport = GraphServiceClientFactory.CreateTransport())
            {
                Assert.IsInstanceOfType(transport, typeof(WinHttpHandler), "The Graph SDK's Windows .NET Framework transport.");
                Assert.IsFalse(((WinHttpHandler)transport).AutomaticRedirection);
            }
        }

        [DataTestMethod]
        [DataRow("https://download.contoso.invalid/page")]
        [DataRow("https://graph.microsoft.us/v1.0/users")]
        [DataRow("https://graph.microsoft.com:8443/v1.0/users")]
        public async Task GraphImportSdk_RedirectTimeout_AuthenticatesOnlyTheConfiguredGraphOrigin(string destination)
        {
            var origin = new Uri("https://graph.microsoft.com/v1.0/users");
            var target = new Uri(destination);
            var hops = new List<Uri>();
            var authorized = new List<Uri>();
            var credential = new SyntheticTokenCredential();
            Func<CancellationToken, Task<HttpResponseMessage>> redirect = ct => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = target } });
            using (var stalled = new GatedJsonContent())
            {
                var terminal = new SequenceHttpHandler(redirect, ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = stalled }),
                    redirect, ct => Task.FromResult(JsonResponse(HttpStatusCode.OK)))
                {
                    ObserveRequest = request =>
                    {
                        hops.Add(request.RequestUri);
                        if (request.Headers.Authorization != null) authorized.Add(request.RequestUri);
                    }
                };
                using (var graph = GraphServiceClientFactory.CreateForGraphImport(credential, TestBudget(500, 1, 3000), null, terminal))
                {
                    Assert.IsNotNull(await WithinWatchdog(graph.Users.GetAsync()));
                    CollectionAssert.AreEqual(new[] { origin, target, origin, target }, hops);
                    CollectionAssert.AreEqual(new[] { origin, origin }, authorized);
                    if (target.Host != origin.Host)
                        Assert.AreEqual(2, credential.Requests, "Only the two requests to the Global Graph host may ask for a token.");
                    Assert.IsTrue(stalled.Started.Task.IsCompleted && stalled.Disposed);
                }
            }
        }

        [TestMethod]
        public async Task GraphImportPipeline_TimeoutThenThrottle_ReplaysOnlyOriginalTargetAndHeaders()
        {
            using (var stalled = new GatedJsonContent())
            {
                var terminal = new SequenceHttpHandler(ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = stalled }),
                    ct => Task.FromResult(JsonResponse((HttpStatusCode)429, retryAfterSeconds: 1)), ct => Task.FromResult(JsonResponse(HttpStatusCode.OK)));
                terminal.ObserveRequest = request =>
                {
                    Assert.AreEqual("/v1.0/users?$top=1", request.RequestUri.PathAndQuery);
                    Assert.AreEqual("eventual", request.Headers.GetValues("ConsistencyLevel").Single());
                    if (terminal.Attempts == 1)
                    {
                        // Model post-send transport mutations with a harmless, same-host URI and header.
                        request.RequestUri = new Uri(request.RequestUri, "/changed-after-send");
                        request.Headers.Remove("ConsistencyLevel");
                    }
                    else
                    {
                        Assert.IsFalse(request.Headers.Contains("Cookie"));
                        Assert.IsNull(request.Headers.Host);
                        Assert.IsNotNull(request.Headers.Authorization, "The SDK authenticates the replay for the original Graph host.");
                    }
                };
                using (var http = BuildGraphImportPipelineClient(terminal, 500, 1, 3000))
                using (var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users?$top=1"))
                {
                    request.Headers.Add("ConsistencyLevel", "eventual");
                    request.Headers.Add("Cookie", "example=synthetic");
                    request.Headers.Host = request.RequestUri.Host;
                    using (var response = await WithinWatchdog(http.SendAsync(request)))
                        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    CollectionAssert.AreEqual(new[] { null, null, "1" }, terminal.RetryAttemptHeaders);
                }
            }
        }

        [DataTestMethod]
        [DataRow(201, false)]
        [DataRow(300, false)]
        [DataRow(302, false)]
        [DataRow(302, true)]
        public async Task GraphImportPipeline_LocationOnPassThroughResponse_PreservesBody(int status, bool disableRedirects)
        {
            var terminal = new SequenceHttpHandler(ct => Task.FromResult(ResponseWithLocation((HttpStatusCode)status, new StringContent("Contoso"))));
            using (var client = BuildGraphImportPipelineClient(terminal, 500, 0, 500))
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users"))
            {
                if (status == 302)
                {
                    request.Properties[typeof(RedirectHandlerOption).FullName] = disableRedirects
                        ? new RedirectHandlerOption { MaxRedirect = 0 }
                        : new RedirectHandlerOption { ShouldRedirect = _ => false };
                }
                using (var response = await WithinWatchdog(client.SendAsync(request)))
                {
                    Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
                    Assert.AreEqual("Contoso", await response.Content.ReadAsStringAsync());
                    Assert.AreEqual(1, terminal.Attempts);
                }
            }
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
        public async Task GraphImportSdk_StalledThrottleBody_PreservesRetryAfterAndSdkRetry()
        {
            var first = new GatedJsonContent();
            var body = new GatedJsonContent();
            var terminal = new SequenceHttpHandler(ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = first }), ct =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)429) { Content = body };
                response.Headers.Add("Retry-After", "1");
                return Task.FromResult(response);
            }, ct => Task.FromResult(JsonResponse(HttpStatusCode.OK)));
            using (var client = GraphServiceClientFactory.CreateForGraphImport(new SyntheticTokenCredential(), TestBudget(150, 2, 750), null, terminal))
            {
                var elapsed = Stopwatch.StartNew();
                try
                {
                    Assert.IsNotNull(await WithinWatchdog(client.Users.GetAsync()));
                    Assert.IsTrue(body.Started.Task.IsCompleted && body.Disposed);
                    Assert.IsTrue(elapsed.Elapsed >= TimeSpan.FromMilliseconds(950));
                    CollectionAssert.AreEqual(new[] { null, null, "1" }, terminal.RetryAttemptHeaders);
                }
                finally { first.Release(); body.Release(); }
            }
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

        private static HttpResponseMessage ResponseWithLocation(HttpStatusCode status, HttpContent body)
            => new HttpResponseMessage(status)
            {
                Headers = { Location = new Uri("https://graph.microsoft.com/v1.0/users?$select=id") },
                Content = body
            };

        private static HttpResponseMessage RelativeRedirect(string location, HttpContent body = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = body };
            response.Headers.TryAddWithoutValidation("Location", location);
            return response;
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

        private static GraphRequestBudgetOptions TestBudget(int perRequestMs, int timeoutRetryCount, int totalBudgetMs, int sdkRetryCount = 3)
            => new GraphRequestBudgetOptions(
                TimeSpan.FromMilliseconds(perRequestMs),
                timeoutRetryCount,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(totalBudgetMs),
                sdkRetryCount,
                1,
                TimeSpan.FromSeconds(10));

        private static HttpClient BuildGraphImportPipelineClient(SequenceHttpHandler terminal, int perRequestMs, int timeoutRetryCount, int totalBudgetMs, int sdkRetryCount = 3)
        {
            var authProvider = GraphServiceClientFactory.CreateAuthenticationProvider(new SyntheticTokenCredential());
            return GraphServiceClientFactory.CreateHttpClient(authProvider, TestBudget(perRequestMs, timeoutRetryCount, totalBudgetMs, sdkRetryCount), AnalyticsLogger.ConsoleOnlyTracer(), terminal);
        }

        internal static async Task<T> WithinWatchdog<T>(Task<T> task)
        {
            Assert.AreSame(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))), "The SDK remained blocked reading the body.");
            return await task;
        }

        internal sealed class GatedJsonContent : HttpContent
        {
            private readonly bool _stopsOnDispose;
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Started { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Disposed { get; private set; }
            public Exception ReadFailure { get; set; }

            public GatedJsonContent(bool stopsOnDispose = true)
            {
                _stopsOnDispose = stopsOnDispose;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }

            public void Release() => _release.TrySetResult(true);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                Started.TrySetResult(true);
                await _release.Task.ConfigureAwait(false);
                if (Disposed) throw new IOException("Synthetic response stream closed.");
                if (ReadFailure != null) throw ReadFailure;
                var bytes = System.Text.Encoding.UTF8.GetBytes("{\"value\":[]}");
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }

            protected override bool TryComputeLength(out long length) { length = 0; return false; }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                if (_stopsOnDispose) Release();
                base.Dispose(disposing);
            }
        }

        internal sealed class SyntheticTokenCredential : Azure.Core.TokenCredential
        {
            public int Requests { get; private set; }

            public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                Requests++;
                return new Azure.Core.AccessToken("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));
            }

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
                InnerHandler = new BoundedGraphResponseBodyHandler { InnerHandler = terminal }
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
            public List<string> Requests { get; } = new List<string>();
            public bool LastRequestHadContent { get; private set; }
            public Action<HttpRequestMessage> ObserveRequest { get; set; }

            public SequenceHttpHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] responses)
            {
                _responses = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>(responses);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Attempts++;
                Requests.Add(request.Method + " " + request.RequestUri.PathAndQuery);
                LastRequestHadContent = request.Content != null;
                RetryAttemptHeaders.Add(request.Headers.TryGetValues("Retry-Attempt", out var values) ? string.Join(",", values) : null);
                ObserveRequest?.Invoke(request);
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
