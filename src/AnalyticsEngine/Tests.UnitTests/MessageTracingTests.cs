using DataUtils.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.MessageTracing;

namespace Tests.UnitTests
{
    [TestClass]
    public class MessageTracingTests
    {
        [TestMethod]
        public void PatternMatcher_LiteralContains_IsCaseInsensitive()
        {
            Assert.IsTrue(MessageTracePatternMatcher.TryCreate("*00000000-0000-0000-0000-000000000000*", NullLogger.Instance, out var matcher, out _));
            Assert.IsTrue(matcher.TryMatch(@"{""appId"":""00000000-0000-0000-0000-000000000000""}", out var matched));
            Assert.AreEqual("*00000000-0000-0000-0000-000000000000*", matched);
        }

        [TestMethod]
        public void PatternMatcher_Wildcards_MatchWholeMessage()
        {
            Assert.IsTrue(MessageTracePatternMatcher.TryCreate("{*contoso?app*}", NullLogger.Instance, out var matcher, out _));
            Assert.IsTrue(matcher.TryMatch("{\"id\":\"Contoso1App\"}", out _));
            Assert.IsFalse(matcher.TryMatch("prefix {\"id\":\"Contoso1App\"} suffix", out _));
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
    }
}
