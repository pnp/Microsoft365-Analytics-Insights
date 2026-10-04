using DataUtils.Http;
using Common.Entities.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.MessageTracing;

namespace Tests.UnitTests
{
    [TestClass]
    public class MessageTracingTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            HttpMessageTracing.Current = HttpMessageTracing.Disabled;
        }

        [TestMethod]
        public void PatternMatcher_LiteralContains_IsCaseInsensitive()
        {
            Assert.IsTrue(MessageTracePatternMatcher.TryCreate("*00000000-0000-0000-0000-000000000000*", NullLogger.Instance, out var matcher, out _));
            Assert.IsTrue(matcher.TryMatch(Encoding.UTF8.GetBytes(@"{""appId"":""00000000-0000-0000-0000-000000000000""}"), out var matched));
            Assert.AreEqual("*00000000-0000-0000-0000-000000000000*", matched);
        }

        [TestMethod]
        public void PatternMatcher_Wildcards_MatchWholeMessage()
        {
            Assert.IsTrue(MessageTracePatternMatcher.TryCreate("{*contoso?app*}", NullLogger.Instance, out var matcher, out _));
            Assert.IsTrue(matcher.TryMatch(Encoding.UTF8.GetBytes("{\"id\":\"Contoso1App\"}"), out _));
            Assert.IsFalse(matcher.TryMatch(Encoding.UTF8.GetBytes("prefix {\"id\":\"Contoso1App\"} suffix"), out _));
        }

        [DataTestMethod]
        [DataRow("*")]
        [DataRow("????")]
        [DataRow("*abc*")]
        public void PatternMatcher_RefusesWildcardOnlyAndShortLiteralPatterns(string pattern)
        {
            Assert.IsFalse(MessageTracePatternMatcher.TryCreate(pattern, NullLogger.Instance, out _, out var failure));
            Assert.IsFalse(string.IsNullOrWhiteSpace(failure));
        }

        [DataTestMethod]
        [DataRow("https://graph.microsoft.com/v1.0/users", "graph")]
        [DataRow("https://manage.office.com/api/v1.0/contoso/activity/feed", "activity-api")]
        [DataRow("https://management.azure.com/subscriptions/00000000-0000-0000-0000-000000000000", "azure-management")]
        [DataRow("https://api.powerplatform.com/licensing", "api-powerplatform-com")]
        public void ConfidentialClientTraceSource_LabelsKnownHostsAndSanitisesOthers(string url, string expected)
        {
            Assert.AreEqual(expected, ConfidentialClientApplicationHttpHandler.GetTraceSource(new Uri(url)));
        }

        [TestMethod]
        public async Task Handler_Disabled_NoOpsAndCallerCanReadBody()
        {
            HttpMessageTracing.Current = HttpMessageTracing.Disabled;
            var handler = new MessageTraceHandler("graph", new StaticJsonHandler(@"{""id"":""match""}"));
            using (var client = new HttpClient(handler))
            using (var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users", HttpCompletionOption.ResponseHeadersRead))
            {
                Assert.AreEqual(@"{""id"":""match""}", await response.Content.ReadAsStringAsync());
            }
        }

        [TestMethod]
        public async Task Tracer_JsonMatch_EnqueuesAndLeavesBodyReadable()
        {
            var sink = new RecordingSink();
            Assert.IsTrue(MessageTracePatternMatcher.TryCreate("*contoso-app*", NullLogger.Instance, out var matcher, out _));
            HttpMessageTracing.Current = new MessageTraceInspectingTracer(matcher, sink, 1024 * 1024, 500, NullLogger.Instance);

            var handler = new MessageTraceHandler("graph", new StaticJsonHandler(@"{""appId"":""Contoso-App""}"));
            using (var client = new HttpClient(handler))
            using (var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users", HttpCompletionOption.ResponseHeadersRead))
            {
                var body = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(@"{""appId"":""Contoso-App""}", body);
            }

            Assert.AreEqual(1, sink.Envelopes.Count);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(@"{""appId"":""Contoso-App""}"), sink.Envelopes[0].Body);
        }

        [TestMethod]
        public async Task Tracer_NonJsonNoMatchOversizeAndSecretEndpoints_DoNotEnqueue()
        {
            var sink = new RecordingSink();
            Assert.IsTrue(MessageTracePatternMatcher.TryCreate("*contoso-app*", NullLogger.Instance, out var matcher, out _));
            var tracer = new MessageTraceInspectingTracer(matcher, sink, 10, 500, NullLogger.Instance);

            await tracer.TraceAsync("graph", Request("https://graph.microsoft.com/v1.0/users"), Response("text/plain", "contoso-app"), CancellationToken.None);
            await tracer.TraceAsync("graph", Request("https://graph.microsoft.com/v1.0/users"), Response("application/json", "{\"id\":\"other\"}"), CancellationToken.None);
            await tracer.TraceAsync("graph", Request("https://graph.microsoft.com/v1.0/users"), Response("application/json", "{\"id\":\"contoso-app but long\"}"), CancellationToken.None);
            await tracer.TraceAsync("graph", Request("https://login.microsoftonline.com/tenant/oauth2/v2.0/token"), Response("application/json", "{\"id\":\"contoso-app\"}"), CancellationToken.None);
            await tracer.TraceAsync("graph", Request("https://contoso.vault.azure.net/secrets/x"), Response("application/json", "{\"id\":\"contoso-app\"}"), CancellationToken.None);
            await tracer.TraceAsync("graph", Request("https://graph.microsoft.com/beta/copilot/users/1/interactionHistory/getAllEnterpriseInteractions"), Response("application/json", "{\"id\":\"contoso-app\"}"), CancellationToken.None);
            await tracer.TraceAsync("graph", Request("https://graph.microsoft.com/v1.0/users"), Response("application/json", "{\"access_token\":\"contoso-app\"}"), CancellationToken.None);

            Assert.AreEqual(0, sink.Envelopes.Count);
        }

        [TestMethod]
        public async Task Tracer_UnknownLengthOversizeJson_IsSkippedWithoutCorruptingCallerBody()
        {
            var body = "{\"id\":\"contoso-app\",\"payload\":\"" + new string('x', 128) + "\"}";
            var sink = new RecordingSink();
            Assert.IsTrue(MessageTracePatternMatcher.TryCreate("*contoso-app*", NullLogger.Instance, out var matcher, out _));
            HttpMessageTracing.Current = new MessageTraceInspectingTracer(matcher, sink, 10, 500, NullLogger.Instance);

            using (var server = new OneShotChunkedJsonServer(body))
            using (var client = new HttpClient(new MessageTraceHandler("activity-api", new HttpClientHandler())))
            using (var response = await client.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                Assert.AreEqual(body, await response.Content.ReadAsStringAsync());
            }

            Assert.AreEqual(0, sink.Envelopes.Count);
        }

        [TestMethod]
        public async Task AutoThrottle_RetriesWhenTracingBufferingSeesConnectionReset()
        {
            var body = "{\"id\":\"contoso-app\",\"payload\":\"" + new string('x', 4096) + "\"}";
            var sink = new RecordingSink();
            Assert.IsTrue(MessageTracePatternMatcher.TryCreate("*contoso-app*", NullLogger.Instance, out var matcher, out _));
            HttpMessageTracing.Current = new MessageTraceInspectingTracer(matcher, sink, 1024 * 1024, 500, NullLogger.Instance);

            using (var server = new ResetThenSuccessJsonServer(body))
            using (var client = new AutoThrottleHttpClient(new MessageTraceHandler("activity-api", new HttpClientHandler()), NullLogger.Instance) { MaxRetries = 2 })
            using (var response = await client.GetAsyncWithThrottleRetries(server.Url, HttpCompletionOption.ResponseHeadersRead, NullLogger.Instance))
            {
                Assert.AreEqual(body, await response.Content.ReadAsStringAsync());
            }

            Assert.AreEqual(1, sink.Envelopes.Count);
        }

        [TestMethod]
        public async Task Tracer_ExceptionsFromSink_DoNotPropagate()
        {
            Assert.IsTrue(MessageTracePatternMatcher.TryCreate("*contoso-app*", NullLogger.Instance, out var matcher, out _));
            var tracer = new MessageTraceInspectingTracer(matcher, new ThrowingSink(), 1024, 500, NullLogger.Instance);
            await tracer.TraceAsync("graph", Request("https://graph.microsoft.com/v1.0/users"), Response("application/json", "{\"id\":\"contoso-app\"}"), CancellationToken.None);
        }

        [TestMethod]
        public async Task Tracer_HourlyCap_DropsBeyondConfiguredLimit()
        {
            var sink = new RecordingSink();
            Assert.IsTrue(MessageTracePatternMatcher.TryCreate("*contoso-app*", NullLogger.Instance, out var matcher, out _));
            var tracer = new MessageTraceInspectingTracer(matcher, sink, 1024, 1, NullLogger.Instance);
            await tracer.TraceAsync("graph", Request("https://graph.microsoft.com/v1.0/users"), Response("application/json", "{\"id\":\"contoso-app\"}"), CancellationToken.None);
            await tracer.TraceAsync("graph", Request("https://graph.microsoft.com/v1.0/users"), Response("application/json", "{\"id\":\"contoso-app\"}"), CancellationToken.None);
            Assert.AreEqual(1, sink.Envelopes.Count);
            Assert.AreEqual(1, sink.HourlyDrops);
        }

        [TestMethod]
        public async Task Uploader_FullQueueAndUploadFailure_CountsDropsAndFailures()
        {
            using (var uploader = new MessageTraceBlobUploader(
                _ => Task.FromException<Azure.Storage.Blobs.BlobContainerClient>(new InvalidOperationException("offline")),
                NullLogger.Instance, maxQueueMessages: 1, maxQueueBytes: 4))
            {
                Assert.IsFalse(uploader.TryEnqueue(new HttpMessageTraceEnvelope { Body = Encoding.UTF8.GetBytes("large") }));
                Assert.AreEqual(1, uploader.DroppedCount);

                Assert.IsTrue(uploader.TryEnqueue(new HttpMessageTraceEnvelope { Body = Encoding.UTF8.GetBytes("{}"), CapturedUtc = DateTime.UtcNow }));
                await WaitForAsync(() => uploader.FailedCount == 1);
            }
        }

        [TestMethod]
        public async Task Uploader_OpenFailure_ReportsStorageUnavailableAndBacksOffUntilRetry()
        {
            var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            var opens = 0;
            using (var uploader = new MessageTraceBlobUploader(
                _ =>
                {
                    Interlocked.Increment(ref opens);
                    return Task.FromException<Azure.Storage.Blobs.BlobContainerClient>(new InvalidOperationException("denied"));
                },
                NullLogger.Instance, utcNow: () => now))
            {
                Assert.IsFalse(uploader.IsStorageUnavailable);
                Assert.IsTrue(uploader.TryEnqueue(TraceEnvelope()));
                await WaitForAsync(() => uploader.FailedCount == 1);
                Assert.IsTrue(uploader.IsStorageUnavailable);
                Assert.AreEqual(1, Volatile.Read(ref opens));

                // While backing off, traces are refused rather than held in memory, and storage isn't retried.
                Assert.IsFalse(uploader.TryEnqueue(TraceEnvelope()));
                Assert.AreEqual(1, uploader.DroppedCount);
                Assert.AreEqual(0, uploader.QueuedCount);

                now = now + MessageTraceBlobUploader.StorageRetryInterval + TimeSpan.FromSeconds(1);
                Assert.IsTrue(uploader.TryEnqueue(TraceEnvelope()));
                await WaitForAsync(() => uploader.FailedCount == 2);
                Assert.AreEqual(2, Volatile.Read(ref opens));
                Assert.IsTrue(uploader.IsStorageUnavailable);
            }
        }

        [TestMethod]
        public async Task Uploader_SuccessfulOpenAfterFailure_ClearsStorageUnavailable()
        {
            var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            var opens = 0;
            var storage = new StaticBlobResponseHandler(HttpStatusCode.Created);
            using (var uploader = new MessageTraceBlobUploader(
                _ => Interlocked.Increment(ref opens) == 1
                    ? Task.FromException<Azure.Storage.Blobs.BlobContainerClient>(new InvalidOperationException("denied"))
                    : Task.FromResult(FakeContainer(storage)),
                NullLogger.Instance, utcNow: () => now))
            {
                Assert.IsTrue(uploader.TryEnqueue(TraceEnvelope()));
                await WaitForAsync(() => uploader.FailedCount == 1);
                Assert.IsTrue(uploader.IsStorageUnavailable);

                now = now + MessageTraceBlobUploader.StorageRetryInterval + TimeSpan.FromSeconds(1);
                Assert.IsTrue(uploader.TryEnqueue(TraceEnvelope()));
                await WaitForAsync(() => uploader.SavedCount == 1);
                Assert.AreEqual(2, Volatile.Read(ref opens));
                Assert.AreEqual(1, Volatile.Read(ref storage.Requests));
                Assert.IsFalse(uploader.IsStorageUnavailable);
            }
        }

        [TestMethod]
        public async Task Uploader_StorageSideUploadFailureAfterOpen_DropsContainerAndBacksOff()
        {
            var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            var opens = 0;
            var storage = new StaticBlobResponseHandler(HttpStatusCode.Forbidden);
            using (var uploader = new MessageTraceBlobUploader(
                _ =>
                {
                    Interlocked.Increment(ref opens);
                    return Task.FromResult(FakeContainer(storage));
                },
                NullLogger.Instance, utcNow: () => now))
            {
                // The open succeeds, then storage refuses the upload: e.g. shared-key access disabled mid-run.
                Assert.IsTrue(uploader.TryEnqueue(TraceEnvelope()));
                await WaitForAsync(() => uploader.FailedCount == 1);
                Assert.IsTrue(uploader.IsStorageUnavailable);
                Assert.IsFalse(uploader.TryEnqueue(TraceEnvelope()));
                Assert.AreEqual(1, Volatile.Read(ref storage.Requests));

                // After the back-off the open runs again (re-creating the container, redoing the RBAC fallback).
                now = now + MessageTraceBlobUploader.StorageRetryInterval + TimeSpan.FromSeconds(1);
                Assert.IsTrue(uploader.TryEnqueue(TraceEnvelope()));
                await WaitForAsync(() => uploader.FailedCount == 2);
                Assert.AreEqual(2, Volatile.Read(ref opens));
            }
        }

        [TestMethod]
        public void Uploader_ClassifiesStorageSideFailures()
        {
            Assert.IsTrue(MessageTraceBlobUploader.IsStorageSideFailure(new Azure.RequestFailedException(403, "denied")));
            Assert.IsTrue(MessageTraceBlobUploader.IsStorageSideFailure(new Azure.RequestFailedException(404, "ContainerNotFound")));
            Assert.IsTrue(MessageTraceBlobUploader.IsStorageSideFailure(new Azure.RequestFailedException(503, "busy")));
            Assert.IsTrue(MessageTraceBlobUploader.IsStorageSideFailure(new Azure.RequestFailedException("transport", new HttpRequestException("reset"))));
            Assert.IsTrue(MessageTraceBlobUploader.IsStorageSideFailure(new AggregateException(new Azure.RequestFailedException(0, "unreachable"))));
            Assert.IsFalse(MessageTraceBlobUploader.IsStorageSideFailure(new Azure.RequestFailedException(400, "InvalidMetadata")));
            Assert.IsFalse(MessageTraceBlobUploader.IsStorageSideFailure(new InvalidOperationException("back-off")));
        }

        private static Azure.Storage.Blobs.BlobContainerClient FakeContainer(StaticBlobResponseHandler storage)
        {
            return new Azure.Storage.Blobs.BlobContainerClient(
                new Uri("https://contosoanalytics.blob.core.windows.net/message-traces"),
                new Azure.Storage.Blobs.BlobClientOptions
                {
                    Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(storage)),
                    Retry = { MaxRetries = 0 },
                });
        }

        private sealed class StaticBlobResponseHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            public int Requests;

            public StaticBlobResponseHandler(HttpStatusCode status)
            {
                _status = status;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Requests);
                var response = new HttpResponseMessage(_status) { Content = new ByteArrayContent(new byte[0]), RequestMessage = request };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"0x8DCONTOSO\"");
                response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
                return Task.FromResult(response);
            }
        }

        private static HttpMessageTraceEnvelope TraceEnvelope()
        {
            return new HttpMessageTraceEnvelope { Body = Encoding.UTF8.GetBytes("{}"), CapturedUtc = DateTime.UtcNow, Source = "graph" };
        }

        [TestMethod]
        public void Uploader_Create_ValidatesOnlyAndDoesNotOpenStorageAtStartup()
        {
            var config = new AppConfig
            {
                MessageTraceContainer = "message-traces",
                ConnectionStrings = new AppConnectionStrings
                {
                    StorageConnectionString = "DefaultEndpointsProtocol=https;AccountName=contosoanalytics;EndpointSuffix=core.windows.net"
                }
            };

            using (var uploader = MessageTraceBlobUploader.Create(config, NullLogger.Instance))
            {
                Assert.IsNotNull(uploader);
                Assert.AreEqual(0, uploader.FailedCount);
            }
        }

        [TestMethod]
        public void BlobNamingAndMetadata_AreSafeAndSanitiseSecretQueryParameters()
        {
            var url = MessageTraceInspectingTracer.SanitizeUrl(new Uri("https://graph.microsoft.com/v1.0/users?sig=secret&$top=1&token=value"));
            StringAssert.Contains(url.ToString(), "$top=1");
            Assert.IsFalse(url.ToString().Contains("secret"));
            Assert.IsFalse(url.ToString().Contains("token=value"));

            var envelope = new HttpMessageTraceEnvelope
            {
                CapturedUtc = new DateTime(2026, 10, 4, 18, 1, 2, 345, DateTimeKind.Utc),
                Source = "graph",
                Method = "GET",
                RequestUri = url,
                StatusCode = 200,
                ContentType = "application/json; charset=utf-8",
                MatchedPattern = "*0000*",
                Body = Encoding.UTF8.GetBytes("{}")
            };
            StringAssert.Matches(MessageTraceBlobUploader.BuildBlobName(envelope), new System.Text.RegularExpressions.Regex(@"^2026/10/04/180102345-graph-[a-f0-9]{32}\.json$"));
            var metadata = MessageTraceBlobUploader.BuildMetadata(envelope);
            Assert.AreEqual("GET", Uri.UnescapeDataString(metadata["method"]));
            Assert.AreEqual("*0000*", Uri.UnescapeDataString(metadata["matchedPattern"]));
        }

        private static HttpRequestMessage Request(string url) => new HttpRequestMessage(HttpMethod.Get, url);

        private static HttpResponseMessage Response(string mediaType, string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            };
        }

        private static async Task WaitForAsync(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return;
                await Task.Delay(50);
            }
            Assert.Fail("Timed out waiting for condition.");
        }

        private sealed class RecordingSink : IMessageTraceSink
        {
            public List<HttpMessageTraceEnvelope> Envelopes { get; } = new List<HttpMessageTraceEnvelope>();
            public int HourlyDrops { get; private set; }
            public bool TryEnqueue(HttpMessageTraceEnvelope envelope)
            {
                Envelopes.Add(envelope);
                return true;
            }
            public void RecordDroppedByHourlyCap() => HourlyDrops++;
        }

        private sealed class ThrowingSink : IMessageTraceSink
        {
            public bool TryEnqueue(HttpMessageTraceEnvelope envelope) => throw new InvalidOperationException("boom");
            public void RecordDroppedByHourlyCap() => throw new InvalidOperationException("boom");
        }

        private sealed class StaticJsonHandler : HttpMessageHandler
        {
            private readonly string _body;
            public StaticJsonHandler(string body) { _body = body; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(Response("application/json", _body));
        }

        private sealed class OneShotChunkedJsonServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Task _server;
            private readonly string _body;

            public OneShotChunkedJsonServer(string body)
            {
                _body = body;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/trace";
                _server = Task.Run(ServeAsync);
            }

            public string Url { get; }

            private async Task ServeAsync()
            {
                using (var client = await _listener.AcceptTcpClientAsync())
                using (var stream = client.GetStream())
                {
                    await ReadRequestHeadersAsync(stream);
                    var header = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
                    var bodyBytes = Encoding.UTF8.GetBytes(_body);
                    var prefix = Encoding.ASCII.GetBytes(header + bodyBytes.Length.ToString("x") + "\r\n");
                    await stream.WriteAsync(prefix, 0, prefix.Length);
                    await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                    var suffix = Encoding.ASCII.GetBytes("\r\n0\r\n\r\n");
                    await stream.WriteAsync(suffix, 0, suffix.Length);
                }
            }

            public void Dispose()
            {
                _listener.Stop();
                try { _server.Wait(TimeSpan.FromSeconds(2)); } catch { }
            }
        }

        private sealed class ResetThenSuccessJsonServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Task _server;
            private readonly string _body;

            public ResetThenSuccessJsonServer(string body)
            {
                _body = body;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/trace";
                _server = Task.Run(ServeAsync);
            }

            public string Url { get; }

            private async Task ServeAsync()
            {
                using (var first = await _listener.AcceptTcpClientAsync())
                using (var stream = first.GetStream())
                {
                    first.LingerState = new LingerOption(true, 0);
                    await ReadRequestHeadersAsync(stream);
                    var bodyBytes = Encoding.UTF8.GetBytes(_body);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, 0, header.Length);
                    await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length / 2);
                }

                using (var second = await _listener.AcceptTcpClientAsync())
                using (var stream = second.GetStream())
                {
                    await ReadRequestHeadersAsync(stream);
                    var bodyBytes = Encoding.UTF8.GetBytes(_body);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, 0, header.Length);
                    await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                }
            }

            public void Dispose()
            {
                _listener.Stop();
                try { _server.Wait(TimeSpan.FromSeconds(2)); } catch { }
            }
        }

        private static async Task ReadRequestHeadersAsync(NetworkStream stream)
        {
            var buffer = new byte[1024];
            var seen = new StringBuilder();
            while (!seen.ToString().Contains("\r\n\r\n"))
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0) return;
                seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }
    }
}
