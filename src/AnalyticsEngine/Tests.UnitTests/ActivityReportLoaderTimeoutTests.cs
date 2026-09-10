using Common.Entities.Config;
using DataUtils;
using DataUtils.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression tests for the audit-log download loaders and HTTP timeouts.
    /// </summary>
    [TestClass]
    public class ActivityReportLoaderTimeoutTests
    {
        [TestMethod]
        public async Task ActivityReportWebLoader_HttpTimeout_DoesNotCrash_AndCountsError()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            using (var httpClient = new AutoThrottleHttpClient(new TimeoutHttpMessageHandler(), logger))
            {
                httpClient.MaxRetries = 1;

                var loader = new ActivityReportWebLoader(httpClient, logger, Guid.Empty.ToString());
                var metadata = new ActivityReportInfo { ContentUri = new Uri("https://contoso.example/audit/content") };

                var result = await loader.Load(metadata);

                Assert.IsNotNull(result, "A timed-out download should return an (empty) report set, not throw.");
                Assert.AreEqual(0, result.Count, "A timed-out download should yield no reports.");
                Assert.AreEqual(1, loader.ReportDownloadErrorCount, "A timed-out download should be counted as a report download error so it is visible and retried next cycle.");
                Assert.IsFalse(result.DownloadComplete, "Timed-out report downloads must not be checkpointed as complete.");
            }
        }

        [TestMethod]
        public async Task ActivityReportWebLoader_GatewayTimeoutThenOk_RetriesAndKeepsEmptySuccessDistinctFromFailure()
        {
            var clock = NewClock();
            var handler = new SequencedContentHandler(clock, new[]
            {
                Response(HttpStatusCode.GatewayTimeout, string.Empty),
                Response(HttpStatusCode.OK, "[]")
            });

            using (var httpClient = NewAutoClient(handler, clock))
            {
                var loader = new ActivityReportWebLoader(httpClient, AnalyticsLogger.ConsoleOnlyTracer(), Guid.Empty.ToString());
                var result = await loader.Load(new ActivityReportInfo { ContentUri = new Uri("https://contoso.example/audit/content") });

                Assert.AreEqual(0, result.Count, "A valid empty JSON array is genuine emptiness, not a download failure.");
                Assert.IsTrue(result.DownloadComplete, "The successful retry should leave the report eligible for checkpointing.");
                Assert.AreEqual(0, loader.ReportDownloadErrorCount, "The superseded 504 should not be counted once the same report succeeds.");
            }

            CollectionAssert.AreEqual(new[] { 0, 2 }, AttemptOffsets(clock, handler));
        }

        [TestMethod]
        public async Task ActivityReportWebLoader_PermanentHttpError_IsIncompleteNotSuccessfulEmptyData()
        {
            var clock = NewClock();
            var handler = new SequencedContentHandler(clock, new[] { Response(HttpStatusCode.NotFound, "[]") });

            using (var httpClient = NewAutoClient(handler, clock))
            {
                var loader = new ActivityReportWebLoader(httpClient, AnalyticsLogger.ConsoleOnlyTracer(), Guid.Empty.ToString());
                var result = await loader.Load(new ActivityReportInfo { ContentUri = new Uri("https://contoso.example/audit/missing") });

                Assert.AreEqual(0, result.Count);
                Assert.IsFalse(result.DownloadComplete, "401/403/404 responses must not be converted into successful empty reports.");
                Assert.AreEqual(1, loader.ReportDownloadErrorCount, "The failed download remains visible in loader error accounting.");
            }

            CollectionAssert.AreEqual(new[] { 0 }, AttemptOffsets(clock, handler));
        }

        [TestMethod]
        public async Task WebContentMetaDataLoader_GetBadGatewayThenOk_RetriesSameMetadataPage()
        {
            var clock = NewClock();
            var handler = new SequencedContentHandler(clock, new[]
            {
                Response(HttpStatusCode.BadGateway, string.Empty),
                Response(HttpStatusCode.OK, MetadataJson("page-1"))
            });

            using (var httpClient = NewConfidentialClient(handler, clock))
            {
                var loader = new WebContentMetaDataLoader(AnalyticsLogger.ConsoleOnlyTracer(), httpClient, NewConfig());
                var result = await loader.DownloadMetadata("https://contoso.example/metadata?page=1", batchId: 42);

                Assert.AreEqual(1, result.Count);
                Assert.AreEqual("page-1", result[0].ContentId);
                Assert.AreEqual(42, result[0].BatchID);
                Assert.AreEqual(0, loader.MetadataDownloadErrorCount);
            }

            CollectionAssert.AreEqual(new[] { "https://contoso.example/metadata?page=1", "https://contoso.example/metadata?page=1" }, handler.RequestUris.ToArray());
            CollectionAssert.AreEqual(new[] { 0, 2 }, AttemptOffsets(clock, handler));
        }

        [TestMethod]
        public async Task WebContentMetaDataLoader_ServiceUnavailableRetryAfterThenOk_WaitsFullDeadline()
        {
            var clock = NewClock();
            var handler = new SequencedContentHandler(clock, new[]
            {
                Response(HttpStatusCode.ServiceUnavailable, string.Empty, "Retry-After", "45"),
                Response(HttpStatusCode.OK, MetadataJson("page-1"))
            });

            using (var httpClient = NewConfidentialClient(handler, clock, retryBudgetSeconds: 90))
            {
                var loader = new WebContentMetaDataLoader(AnalyticsLogger.ConsoleOnlyTracer(), httpClient, NewConfig());
                var result = await loader.DownloadMetadata("https://contoso.example/metadata?page=1", batchId: 42);

                Assert.AreEqual(1, result.Count);
                Assert.AreEqual(0, loader.MetadataDownloadErrorCount);
            }

            CollectionAssert.AreEqual(new[] { 0, 45 }, AttemptOffsets(clock, handler));
        }

        [TestMethod]
        public async Task WebContentMetaDataLoader_RepeatedGatewayFailures_StopPagingAndCountVisibleFailure()
        {
            var clock = NewClock();
            var handler = new SequencedContentHandler(clock, new[]
            {
                Response(HttpStatusCode.BadGateway, string.Empty),
                Response(HttpStatusCode.BadGateway, string.Empty)
            });

            using (var httpClient = NewConfidentialClient(handler, clock, maxRetries: 2))
            {
                var loader = new WebContentMetaDataLoader(AnalyticsLogger.ConsoleOnlyTracer(), httpClient, NewConfig());
                var result = await loader.DownloadMetadata("https://contoso.example/metadata?page=1", batchId: 42);

                Assert.AreEqual(0, result.Count, "A failed metadata page must not be reported as a complete empty listing.");
                Assert.AreEqual(1, loader.MetadataDownloadErrorCount, "Exhausted gateway retries must use the existing visible metadata error accounting.");
            }

            CollectionAssert.AreEqual(new[] { 0, 2 }, AttemptOffsets(clock, handler));
        }

        [TestMethod]
        public async Task WebContentMetaDataLoader_TransientFailureOnLaterPage_RetriesSamePageAndPreservesOrder()
        {
            var clock = NewClock();
            var page2 = "https://contoso.example/metadata?page=2";
            var handler = new SequencedContentHandler(clock, new[]
            {
                Response(HttpStatusCode.OK, MetadataJson("page-1"), "NextPageUri", page2),
                Response(HttpStatusCode.BadGateway, string.Empty),
                Response(HttpStatusCode.OK, MetadataJson("page-2"))
            });

            using (var httpClient = NewConfidentialClient(handler, clock))
            {
                var loader = new WebContentMetaDataLoader(AnalyticsLogger.ConsoleOnlyTracer(), httpClient, NewConfig());
                var result = await loader.DownloadMetadata("https://contoso.example/metadata?page=1", batchId: 42);

                CollectionAssert.AreEqual(new[] { "page-1", "page-2" }, result.Select(r => r.ContentId).ToArray(), "Retrying a later page must not skip the failed page or reorder the suffix.");
                Assert.AreEqual(0, loader.MetadataDownloadErrorCount);
            }

            Assert.AreEqual("https://contoso.example/metadata?page=1", handler.RequestUris[0]);
            Assert.IsTrue(handler.RequestUris[1].StartsWith(page2, StringComparison.Ordinal));
            Assert.IsTrue(handler.RequestUris[2].StartsWith(page2, StringComparison.Ordinal), "The retry must request the same next-page URI, not skip forward.");
            CollectionAssert.AreEqual(new[] { 0, 0, 2 }, AttemptOffsets(clock, handler));
        }

        private sealed class TimeoutHttpMessageHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated HTTP timeout."));
            }
        }

        private static AutoThrottleHttpClientTests.FakeRetryClock NewClock()
        {
            return new AutoThrottleHttpClientTests.FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        }

        private static AutoThrottleHttpClient NewAutoClient(HttpMessageHandler handler, AutoThrottleHttpClientTests.FakeRetryClock clock, int retryBudgetSeconds = 60, int maxRetries = 10)
        {
            return new AutoThrottleHttpClient(handler, AnalyticsLogger.ConsoleOnlyTracer(), clock)
            {
                MaxTotalRetryBudgetSeconds = retryBudgetSeconds,
                MaxRetries = maxRetries
            };
        }

        private static ConfidentialClientApplicationThrottledHttpClient NewConfidentialClient(HttpMessageHandler handler, AutoThrottleHttpClientTests.FakeRetryClock clock, int retryBudgetSeconds = 60, int maxRetries = 10)
        {
            return new ConfidentialClientApplicationThrottledHttpClient(handler, AnalyticsLogger.ConsoleOnlyTracer(), clock)
            {
                MaxTotalRetryBudgetSeconds = retryBudgetSeconds,
                MaxRetries = maxRetries
            };
        }

        private static AppConfig NewConfig()
        {
            var config = (AppConfig)FormatterServices.GetUninitializedObject(typeof(AppConfig));
            config.TenantGUID = Guid.Empty;
            return config;
        }

        private static Func<HttpResponseMessage> Response(HttpStatusCode status, string content, string headerName = null, string headerValue = null)
        {
            return () =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(content ?? string.Empty) };
                if (!string.IsNullOrEmpty(headerName))
                {
                    response.Headers.TryAddWithoutValidation(headerName, headerValue);
                }
                return response;
            };
        }

        private static string MetadataJson(string contentId)
        {
            return "[{\"contentType\":\"Audit.SharePoint\",\"contentId\":\"" + contentId + "\",\"contentUri\":\"https://contoso.example/audit/" + contentId + "\",\"contentCreated\":\"2026-01-01T00:00:00Z\"}]";
        }

        private static int[] AttemptOffsets(AutoThrottleHttpClientTests.FakeRetryClock clock, SequencedContentHandler handler)
        {
            return handler.AttemptTimes.Select(t => (int)(t - clock.StartUtc).TotalSeconds).ToArray();
        }

        private sealed class SequencedContentHandler : HttpMessageHandler
        {
            private readonly AutoThrottleHttpClientTests.FakeRetryClock _clock;
            private readonly Queue<Func<HttpResponseMessage>> _responses;

            public SequencedContentHandler(AutoThrottleHttpClientTests.FakeRetryClock clock, IEnumerable<Func<HttpResponseMessage>> responses)
            {
                _clock = clock;
                _responses = new Queue<Func<HttpResponseMessage>>(responses);
            }

            public List<DateTimeOffset> AttemptTimes { get; } = new List<DateTimeOffset>();
            public List<string> RequestUris { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                AttemptTimes.Add(_clock.UtcNow);
                RequestUris.Add(request.RequestUri.ToString());
                if (_responses.Count == 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
                }

                return Task.FromResult(_responses.Dequeue()());
            }
        }
    }
}
