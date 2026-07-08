using DataUtils;
using DataUtils.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class AutoThrottleHttpClientTests
    {
        /// <summary>
        /// A very large 'retry-after' value must be capped at MaxRetryAfterWaitSeconds rather than sleeping the
        /// calling thread for the whole duration - the fix that prevents a single throttling response from
        /// stalling an import for minutes/hours.
        /// </summary>
        [TestMethod]
        public async Task ExecuteHttpCallWithThrottleRetries_CapsLargeRetryAfter()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var handler = new SequencedThrottleHandler(retryAfterSeconds: 3600); // server asks for a full hour

            using (var client = new AutoThrottleHttpClient(handler, logger))
            {
                client.MaxRetryAfterWaitSeconds = 1; // cap low so the test is fast

                var sw = Stopwatch.StartNew();
                var response = await client.ExecuteHttpCallWithThrottleRetries(
                    () => client.GetAsync("https://example.test/throttled"),
                    "https://example.test/throttled");
                sw.Stop();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, handler.CallCount, "Should have retried exactly once after the capped wait");
                Assert.IsTrue(sw.Elapsed.TotalSeconds < 30,
                    $"Back-off should be capped near 1s (not the 3600s the header asked for) - was {sw.Elapsed.TotalSeconds:F1}s");
            }
        }

        /// <summary>Returns 429 with a Retry-After header on the first call, then 200 OK.</summary>
        private class SequencedThrottleHandler : HttpMessageHandler
        {
            private int _callCount;
            private readonly int _retryAfterSeconds;
            public int CallCount => _callCount;

            public SequencedThrottleHandler(int retryAfterSeconds)
            {
                _retryAfterSeconds = retryAfterSeconds;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref _callCount);
                if (call == 1)
                {
                    var throttled = new HttpResponseMessage((HttpStatusCode)429);
                    throttled.Headers.TryAddWithoutValidation("Retry-After", _retryAfterSeconds.ToString());
                    return Task.FromResult(throttled);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
