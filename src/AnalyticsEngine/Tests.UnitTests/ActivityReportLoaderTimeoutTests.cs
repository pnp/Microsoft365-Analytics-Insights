using DataUtils;
using DataUtils.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression tests for the audit-log download loaders and HTTP timeouts.
    ///
    /// A single report/metadata download that timed out threw a <see cref="TaskCanceledException"/> that was
    /// not caught by the loaders (they only handled <see cref="HttpRequestException"/>), so it propagated all
    /// the way up through the parallel processor and crashed the whole Office 365 Activity import WebJob.
    /// These tests assert that a timeout is now handled gracefully - logged, counted, and skipped - so the
    /// import keeps going and the item is retried on the next cycle.
    /// </summary>
    [TestClass]
    public class ActivityReportLoaderTimeoutTests
    {
        /// <summary>
        /// Handler that mimics how <see cref="HttpClient"/> surfaces a timeout on .NET Framework: the request
        /// completes as a cancelled task (<see cref="TaskCanceledException"/>).
        /// </summary>
        private sealed class TimeoutHttpMessageHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated HTTP timeout."));
            }
        }

        [TestMethod]
        public async Task ActivityReportWebLoader_HttpTimeout_DoesNotCrash_AndCountsError()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            using (var httpClient = new AutoThrottleHttpClient(new TimeoutHttpMessageHandler(), logger))
            {
                // Don't retry - we're testing the loader's handling of a download that ultimately times out,
                // not the AutoThrottleHttpClient retry loop (covered by AutoThrottleHttpClientTests). Retrying
                // 10x with back-off would make this test take ~90s.
                httpClient.MaxRetries = 1;

                var loader = new ActivityReportWebLoader(httpClient, logger, Guid.Empty.ToString());
                var metadata = new ActivityReportInfo { ContentUri = new Uri("https://contoso.example/audit/content") };

                // Before the fix this threw TaskCanceledException and crashed the import.
                var result = await loader.Load(metadata);

                Assert.IsNotNull(result, "A timed-out download should return an (empty) report set, not throw.");
                Assert.AreEqual(0, result.Count, "A timed-out download should yield no reports.");
                Assert.AreEqual(1, loader.ReportDownloadErrorCount, "A timed-out download should be counted as a report download error so it is visible and retried next cycle.");
            }
        }
    }
}
