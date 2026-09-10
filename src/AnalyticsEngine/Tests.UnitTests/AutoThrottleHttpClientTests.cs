using DataUtils;
using DataUtils.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class AutoThrottleHttpClientTests
    {
        [TestMethod]
        public async Task ExecuteHttpCallWithThrottleRetries_HonoursFullRetryAfterWithoutClipping()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = SequencedHandler.FromStatuses(clock, HeaderStatus((HttpStatusCode)429, "Retry-After", "600"), Status(HttpStatusCode.OK));

            using (var client = NewClient(handler, clock, budgetSeconds: 900))
            using (var response = await client.GetAsyncWithThrottleRetries("https://contoso.example/throttled", AnalyticsLogger.ConsoleOnlyTracer()))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            AssertAttemptOffsets(clock, handler, 0, 600);
        }

        [TestMethod]
        public async Task ExecuteHttpCallWithThrottleRetries_DelayShorterThanBudgetStillWaitsFullDelay()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = SequencedHandler.FromStatuses(clock, HeaderStatus((HttpStatusCode)429, "Retry-After", "30"), Status(HttpStatusCode.OK));

            using (var client = NewClient(handler, clock, budgetSeconds: 120))
            using (var response = await client.GetAsyncWithThrottleRetries("https://contoso.example/throttled", AnalyticsLogger.ConsoleOnlyTracer()))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            AssertAttemptOffsets(clock, handler, 0, 30);
        }

        [TestMethod]
        public async Task ExecuteHttpCallWithThrottleRetries_HonoursHttpDateRetryAfter()
        {
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var clock = new FakeRetryClock(start);
            var retryAt = start.AddSeconds(120).UtcDateTime.ToString("r");
            var handler = SequencedHandler.FromStatuses(clock, HeaderStatus((HttpStatusCode)429, "Retry-After", retryAt), Status(HttpStatusCode.OK));

            using (var client = NewClient(handler, clock, budgetSeconds: 300))
            using (var response = await client.GetAsyncWithThrottleRetries("https://contoso.example/throttled", AnalyticsLogger.ConsoleOnlyTracer()))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            AssertAttemptOffsets(clock, handler, 0, 120);
        }

        [TestMethod]
        public async Task ExecuteHttpCallWithThrottleRetries_DelayBeyondBudgetFailsWithoutEarlyRetry()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = SequencedHandler.FromStatuses(clock, HeaderStatus((HttpStatusCode)429, "Retry-After", "600"), Status(HttpStatusCode.OK));

            using (var client = NewClient(handler, clock, budgetSeconds: 180))
            {
                await AssertThrowsAsync<HttpRequestException>(() => client.GetAsyncWithThrottleRetries("https://contoso.example/throttled", AnalyticsLogger.ConsoleOnlyTracer()));
            }

            AssertAttemptOffsets(clock, handler, 0);
            Assert.AreEqual(TimeSpan.Zero, clock.Elapsed, "The client must not shorten the server's deadline to fit the local budget.");
        }

        [TestMethod]
        public async Task ExecuteHttpCallWithThrottleRetries_RepeatedRetryAfterHintsEachSetANewDeadline()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = SequencedHandler.FromStatuses(clock,
                HeaderStatus((HttpStatusCode)429, "Retry-After", "10"),
                HeaderStatus((HttpStatusCode)429, "Retry-After", "20"),
                Status(HttpStatusCode.OK));

            using (var client = NewClient(handler, clock, budgetSeconds: 60))
            using (var response = await client.GetAsyncWithThrottleRetries("https://contoso.example/throttled", AnalyticsLogger.ConsoleOnlyTracer()))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            AssertAttemptOffsets(clock, handler, 0, 10, 30);
        }

        [TestMethod]
        public async Task ExecuteHttpCallWithThrottleRetries_MissingMalformedZeroAndPastRetryAfterUseBoundedFallbacks()
        {
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var clock = new FakeRetryClock(start);
            var past = start.AddSeconds(-10).UtcDateTime.ToString("r");
            var handler = SequencedHandler.FromStatuses(clock,
                Status((HttpStatusCode)429),
                HeaderStatus((HttpStatusCode)429, "Retry-After", "not-a-date"),
                HeaderStatus((HttpStatusCode)429, "Retry-After", "0"),
                HeaderStatus((HttpStatusCode)429, "Retry-After", past),
                Status(HttpStatusCode.OK));

            using (var client = NewClient(handler, clock, budgetSeconds: 60))
            using (var response = await client.GetAsyncWithThrottleRetries("https://contoso.example/throttled", AnalyticsLogger.ConsoleOnlyTracer()))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            AssertAttemptOffsets(clock, handler, 0, 2, 6, 7, 8);
        }

        [TestMethod]
        public async Task ExecuteHttpCallWithThrottleRetries_VeryLargeRetryAfterFailsSafelyWithoutOverflowOrBusyLoop()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = SequencedHandler.FromStatuses(clock, HeaderStatus((HttpStatusCode)429, "Retry-After", "999999999999"), Status(HttpStatusCode.OK));

            using (var client = NewClient(handler, clock, budgetSeconds: 3600))
            {
                await AssertThrowsAsync<HttpRequestException>(() => client.GetAsyncWithThrottleRetries("https://contoso.example/throttled", AnalyticsLogger.ConsoleOnlyTracer()));
            }

            AssertAttemptOffsets(clock, handler, 0);
        }

        [TestMethod]
        public async Task ExecuteHttpCallWithThrottleRetries_CancellationDuringBackoffStopsBeforeNextAttempt()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = SequencedHandler.FromStatuses(clock, HeaderStatus((HttpStatusCode)429, "Retry-After", "600"), Status(HttpStatusCode.OK));
            using (var cts = new CancellationTokenSource())
            {
                clock.BeforeDelay = (delay, token) => cts.Cancel();

                using (var client = NewClient(handler, clock, budgetSeconds: 900))
                {
                    await AssertThrowsAsync<OperationCanceledException>(() => client.GetAsyncWithThrottleRetries("https://contoso.example/throttled", AnalyticsLogger.ConsoleOnlyTracer(), cts.Token));
                }
            }

            AssertAttemptOffsets(clock, handler, 0);
        }

        [TestMethod]
        public async Task GetAsyncWithThrottleRetries_RetriesGatewayResponsesForIdempotentGet()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = SequencedHandler.FromStatuses(clock, Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.OK));

            using (var client = NewClient(handler, clock, budgetSeconds: 30))
            using (var response = await client.GetAsyncWithThrottleRetries("https://contoso.example/gateway", AnalyticsLogger.ConsoleOnlyTracer()))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            AssertAttemptOffsets(clock, handler, 0, 2);
            CollectionAssert.AreEqual(new[] { HttpMethod.Get, HttpMethod.Get }, handler.Requests.Select(r => r.Method).ToArray());
        }

        [TestMethod]
        public async Task GetAsyncWithThrottleRetries_ServiceUnavailableHonoursRetryAfter()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = SequencedHandler.FromStatuses(clock, HeaderStatus(HttpStatusCode.ServiceUnavailable, "Retry-After", "45"), Status(HttpStatusCode.OK));

            using (var client = NewClient(handler, clock, budgetSeconds: 90))
            using (var response = await client.GetAsyncWithThrottleRetries("https://contoso.example/gateway", AnalyticsLogger.ConsoleOnlyTracer()))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            AssertAttemptOffsets(clock, handler, 0, 45);
        }

        [TestMethod]
        public async Task GetAsyncWithThrottleRetries_RepeatedGatewayFailuresRespectAttemptLimitAndRemainVisible()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = SequencedHandler.FromStatuses(clock, Status(HttpStatusCode.GatewayTimeout), Status(HttpStatusCode.GatewayTimeout), Status(HttpStatusCode.GatewayTimeout), Status(HttpStatusCode.OK));

            using (var client = NewClient(handler, clock, budgetSeconds: 30, maxRetries: 3))
            {
                await AssertThrowsAsync<HttpRequestException>(() => client.GetAsyncWithThrottleRetries("https://contoso.example/gateway", AnalyticsLogger.ConsoleOnlyTracer()));
            }

            AssertAttemptOffsets(clock, handler, 0, 2, 6);
        }

        [TestMethod]
        public async Task GetAsyncWithThrottleRetries_DoesNotRetryPermanentClientErrors()
        {
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
            {
                var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
                var handler = SequencedHandler.FromStatuses(clock, Status(status), Status(HttpStatusCode.OK));

                using (var client = NewClient(handler, clock, budgetSeconds: 30))
                using (var response = await client.GetAsyncWithThrottleRetries("https://contoso.example/client-error", AnalyticsLogger.ConsoleOnlyTracer()))
                {
                    Assert.AreEqual(status, response.StatusCode);
                }

                AssertAttemptOffsets(clock, handler, 0);
            }
        }

        [TestMethod]
        public async Task PostAsyncWithThrottleRetries_DoesNotUseIdempotentGatewayRetryPolicy()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = SequencedHandler.FromStatuses(clock, Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.OK));

            using (var client = NewClient(handler, clock, budgetSeconds: 30))
            using (var response = await client.PostAsyncWithThrottleRetries("https://contoso.example/post", new { value = "synthetic" }, AnalyticsLogger.ConsoleOnlyTracer()))
            {
                Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
            }

            AssertAttemptOffsets(clock, handler, 0);
            Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        }

        [TestMethod]
        public async Task ExecuteHttpCallWithThrottleRetries_RetriesTransientTimeout_ThenSucceeds()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = new TimeoutThenOkHandler(clock, timeoutsBeforeSuccess: 1);

            using (var client = NewClient(handler, clock, budgetSeconds: 30))
            using (var response = await client.ExecuteHttpCallWithThrottleRetries(ct => client.GetAsync("https://contoso.example/timeout", ct), "https://contoso.example/timeout", isReplayableIdempotentGet: true))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "A transient timeout should be retried and then succeed.");
            }

            AssertAttemptOffsets(clock, handler, 0, 2);
        }

        [TestMethod]
        public async Task ExecuteHttpCallWithThrottleRetries_GivesUpAndRethrows_OnPersistentTimeout()
        {
            var clock = new FakeRetryClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var handler = new TimeoutThenOkHandler(clock, timeoutsBeforeSuccess: int.MaxValue);

            using (var client = NewClient(handler, clock, budgetSeconds: 30, maxRetries: 2))
            {
                await AssertThrowsAsync<TaskCanceledException>(() => client.ExecuteHttpCallWithThrottleRetries(ct => client.GetAsync("https://contoso.example/timeout", ct), "https://contoso.example/timeout", isReplayableIdempotentGet: true));
            }

            AssertAttemptOffsets(clock, handler, 0, 2);
        }

        private static AutoThrottleHttpClient NewClient(HttpMessageHandler handler, FakeRetryClock clock, int budgetSeconds, int maxRetries = 10)
        {
            return new AutoThrottleHttpClient(handler, AnalyticsLogger.ConsoleOnlyTracer(), clock)
            {
                MaxRetryAfterWaitSeconds = budgetSeconds,
                MaxRetries = maxRetries
            };
        }

        private static Func<HttpResponseMessage> Status(HttpStatusCode status)
        {
            return () => new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
        }

        private static Func<HttpResponseMessage> HeaderStatus(HttpStatusCode status, string headerName, string headerValue)
        {
            return () =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
                response.Headers.TryAddWithoutValidation(headerName, headerValue);
                return response;
            };
        }

        private static void AssertAttemptOffsets(FakeRetryClock clock, RecordingHandler handler, params int[] expectedSeconds)
        {
            CollectionAssert.AreEqual(expectedSeconds, handler.Requests.Select(r => (int)(r.TimestampUtc - clock.StartUtc).TotalSeconds).ToArray());
        }

        private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
        {
            try
            {
                await action();
            }
            catch (TException ex)
            {
                return ex;
            }

            Assert.Fail($"Expected {typeof(TException).Name}.");
            return null;
        }

        public sealed class FakeRetryClock : IAutoThrottleHttpClientClock
        {
            public FakeRetryClock(DateTimeOffset startUtc)
            {
                StartUtc = startUtc;
                UtcNow = startUtc;
            }

            public DateTimeOffset StartUtc { get; }
            public DateTimeOffset UtcNow { get; private set; }
            public TimeSpan Elapsed => UtcNow - StartUtc;
            public Action<TimeSpan, CancellationToken> BeforeDelay { get; set; }

            public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                BeforeDelay?.Invoke(delay, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                UtcNow = UtcNow.Add(delay);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            }
        }

        private sealed class RequestRecord
        {
            public DateTimeOffset TimestampUtc { get; set; }
            public HttpMethod Method { get; set; }
            public Uri RequestUri { get; set; }
        }

        private abstract class RecordingHandler : HttpMessageHandler
        {
            private readonly FakeRetryClock _clock;
            private readonly List<RequestRecord> _requests = new List<RequestRecord>();

            protected RecordingHandler(FakeRetryClock clock)
            {
                _clock = clock;
            }

            public List<RequestRecord> Requests => _requests;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _requests.Add(new RequestRecord { TimestampUtc = _clock.UtcNow, Method = request.Method, RequestUri = request.RequestUri });
                return SendRecordedAsync(request, cancellationToken);
            }

            protected abstract Task<HttpResponseMessage> SendRecordedAsync(HttpRequestMessage request, CancellationToken cancellationToken);
        }

        private sealed class SequencedHandler : RecordingHandler
        {
            private readonly Queue<Func<HttpResponseMessage>> _responses;

            private SequencedHandler(FakeRetryClock clock, IEnumerable<Func<HttpResponseMessage>> responses) : base(clock)
            {
                _responses = new Queue<Func<HttpResponseMessage>>(responses);
            }

            public static SequencedHandler FromStatuses(FakeRetryClock clock, params Func<HttpResponseMessage>[] responses)
            {
                return new SequencedHandler(clock, responses);
            }

            protected override Task<HttpResponseMessage> SendRecordedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_responses.Count == 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }

                return Task.FromResult(_responses.Dequeue()());
            }
        }

        private sealed class TimeoutThenOkHandler : RecordingHandler
        {
            private int _callCount;
            private readonly int _timeoutsBeforeSuccess;

            public TimeoutThenOkHandler(FakeRetryClock clock, int timeoutsBeforeSuccess) : base(clock)
            {
                _timeoutsBeforeSuccess = timeoutsBeforeSuccess;
            }

            protected override Task<HttpResponseMessage> SendRecordedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _callCount++;
                if (_callCount <= _timeoutsBeforeSuccess)
                {
                    return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated HTTP timeout."));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
