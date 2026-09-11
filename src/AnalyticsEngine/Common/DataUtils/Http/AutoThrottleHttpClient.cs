using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtils.Http
{
    public interface IAutoThrottleHttpClientClock
    {
        DateTimeOffset UtcNow { get; }
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    public sealed class SystemAutoThrottleHttpClientClock : IAutoThrottleHttpClientClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    public class AutoThrottleHttpClient : HttpClient
    {
        #region Constructor, Props, and Privates

        const string THROTTLE_ERROR = "Throttle error";
        private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromSeconds(1);

        private readonly bool ignoreRetryHeader;
        private readonly IAutoThrottleHttpClientClock _clock;
        protected readonly ILogger _logger;
        private DateTimeOffset? _nextCallEarliestTime = null;
        private int _concurrentCalls = 0, _throttledCalls = 0, _completedCalls = 0;
        private int _maxRetries = 10;
        private int _maxTotalRetryBudgetSeconds = 3600;
        private object _concurrentCallsObj = new object(), _throttledCallsObject = new object(), _completedCallsObject = new object(), _maxRetriesObj = new object();


        public AutoThrottleHttpClient(bool ignoreRetryHeader, ILogger logger, IAutoThrottleHttpClientClock clock = null)
        {
            Timeout = TimeSpan.FromHours(1);
            this.ignoreRetryHeader = ignoreRetryHeader;
            _logger = logger;
            _clock = clock ?? new SystemAutoThrottleHttpClientClock();
        }
        public AutoThrottleHttpClient(bool ignoreRetryHeader, ILogger logger, DelegatingHandler handler, IAutoThrottleHttpClientClock clock = null) : base(handler)
        {
            Timeout = TimeSpan.FromHours(1);
            this.ignoreRetryHeader = ignoreRetryHeader;
            _logger = logger;
            _clock = clock ?? new SystemAutoThrottleHttpClientClock();
        }

        public AutoThrottleHttpClient(HttpMessageHandler handler, ILogger logger, IAutoThrottleHttpClientClock clock = null) : base(handler)
        {
            Timeout = TimeSpan.FromHours(1);
            _logger = logger;
            _clock = clock ?? new SystemAutoThrottleHttpClientClock();
        }


        #endregion

        /// <summary>
        /// Execute a method that returns a HttpResponseMessage, with throttling retry logic.
        /// </summary>
        public Task<HttpResponseMessage> ExecuteHttpCallWithThrottleRetries(Func<Task<HttpResponseMessage>> httpAction, string url, bool isReplayableIdempotentGet = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (httpAction is null)
            {
                throw new ArgumentNullException(nameof(httpAction));
            }

            return ExecuteHttpCallWithThrottleRetries(ct => httpAction(), url, isReplayableIdempotentGet, cancellationToken);
        }

        /// <summary>
        /// Execute a method that returns a HttpResponseMessage, with throttling retry logic.
        /// </summary>
        public async Task<HttpResponseMessage> ExecuteHttpCallWithThrottleRetries(Func<CancellationToken, Task<HttpResponseMessage>> httpAction, string url, bool isReplayableIdempotentGet = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (httpAction is null)
            {
                throw new ArgumentNullException(nameof(httpAction));
            }

            var startedUtc = _clock.UtcNow;
            var attempt = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForSharedRetryDeadline(startedUtc, url, cancellationToken);

                lock (_concurrentCallsObj)
                {
                    _concurrentCalls++;
                }

                HttpResponseMessage response = null;
                try
                {
                    // Get response but don't buffer full content (which will buffer overflow for large files).
                    attempt++;
                    response = await httpAction(cancellationToken);
                }
                catch (Exception ex) when (IsTransientException(ex) && !cancellationToken.IsCancellationRequested)
                {
                    lock (_concurrentCallsObj)
                    {
                        _concurrentCalls--;
                    }

                    if (attempt >= MaxRetries)
                    {
                        _logger.LogError(ex, $"Transient error calling {url}: '{ex.Message}'. Giving up after {MaxRetries} attempts.");
                        throw;
                    }

                    // A client-side HTTP timeout (HttpClient.Timeout elapsed) surfaces as TaskCanceledException,
                    // and a transient socket/DNS blip as HttpRequestException thrown from the send itself before
                    // any response is received. Neither is an HTTP 429, so response-status retry logic never sees
                    // them; without retrying here they would abort a whole import section (or crash in DEBUG).
                    var delay = GetFallbackDelay(attempt);
                    _logger.LogWarning($"Transient error calling {url}: '{ex.Message}'. Waiting {FormatDelay(delay)} before retry (attempt #{attempt} of {MaxRetries})...");
                    await DelayWithinRetryBudget(delay, startedUtc, url, publishSharedDeadline: false, cancellationToken: cancellationToken);
                    continue;
                }

                lock (_concurrentCallsObj)
                {
                    _concurrentCalls--;
                }

                if (!IsRetryableResponse(response, isReplayableIdempotentGet))
                {
                    lock (_completedCallsObject)
                    {
                        _completedCalls++;
                    }

                    return response;
                }

                lock (_throttledCallsObject)
                {
                    _throttledCalls++;
                }

                // Enforce the retry budget on EVERY 429, not just the ones without a 'retry-after'
                // header. Graph almost always sends that header, so the previous shape only counted
                // retries and never stopped: a persistently throttled endpoint looped forever, blocking
                // the calling import section indefinitely.
                if (attempt >= MaxRetries)
                {
                    _logger.LogError($"Retryable HTTP {(int)response.StatusCode} from {url}. Maximum retry attempts {MaxRetries} reached; giving up.");
                    try
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    finally
                    {
                        response.Dispose();
                    }
                }

                var delayNeeded = GetRetryDelay(response, attempt, url);
                try
                {
                    await DelayWithinRetryBudget(delayNeeded.Delay, startedUtc, url, delayNeeded.PublishSharedDeadline, cancellationToken);
                }
                finally
                {
                    // This response is being discarded, so release it before looping or when cancellation stops
                    // the loop. It matters for callers that request HttpCompletionOption.ResponseHeadersRead
                    // (the Copilot CSV report downloads): the body is still unread, so without this each retry
                    // leaks a connection.
                    response.Dispose();
                }
            }
        }

        private async Task WaitForSharedRetryDeadline(DateTimeOffset startedUtc, string url, CancellationToken cancellationToken)
        {
            while (true)
            {
                DateTimeOffset? earliest;
                lock (this)
                {
                    earliest = _nextCallEarliestTime;
                    if (earliest.HasValue && earliest.Value <= _clock.UtcNow)
                    {
                        _nextCallEarliestTime = null;
                        earliest = null;
                    }
                }

                if (!earliest.HasValue)
                {
                    return;
                }

                var delay = earliest.Value - _clock.UtcNow;
                if (delay < MinimumRetryDelay)
                {
                    delay = MinimumRetryDelay;
                }

                lock (_throttledCallsObject)
                {
                    _throttledCalls++;
                }

                await DelayWithinRetryBudget(delay, startedUtc, url, publishSharedDeadline: false, cancellationToken: cancellationToken);
            }
        }

        private RetryDelayDecision GetRetryDelay(HttpResponseMessage response, int attempt, string url)
        {
            int? retryAfterSeconds = null;
            if (!ignoreRetryHeader)
            {
                retryAfterSeconds = response.GetRetryAfterHeaderSeconds(_clock.UtcNow);
            }

            if (retryAfterSeconds.HasValue)
            {
                var delay = TimeSpan.FromSeconds(retryAfterSeconds.Value);
                if (delay < MinimumRetryDelay)
                {
                    _logger.LogInformation($"Retryable HTTP {(int)response.StatusCode} from {url}. 'retry-after' was due now/past; using {FormatDelay(MinimumRetryDelay)} minimum retry delay for attempt #{attempt}.");
                    return new RetryDelayDecision(MinimumRetryDelay, ShouldPublishSharedDeadline(response, hasRetryAfterHeader: true));
                }

                _logger.LogInformation($"Retryable HTTP {(int)response.StatusCode} from {url}. Waiting full 'retry-after' delay of {FormatDelay(delay)} for attempt #{attempt}.");
                return new RetryDelayDecision(delay, ShouldPublishSharedDeadline(response, hasRetryAfterHeader: true));
            }

            var fallback = GetFallbackDelay(attempt);
            _logger.LogInformation($"Retryable HTTP {(int)response.StatusCode} from {url} without a usable 'retry-after'. Waiting {FormatDelay(fallback)} before attempt #{attempt + 1}...");
            return new RetryDelayDecision(fallback, ShouldPublishSharedDeadline(response, hasRetryAfterHeader: false));
        }

        private static TimeSpan GetFallbackDelay(int attempt)
        {
            var seconds = Math.Max(1, attempt * 2);
            return TimeSpan.FromSeconds(seconds);
        }

        private async Task DelayWithinRetryBudget(TimeSpan delay, DateTimeOffset startedUtc, string url, bool publishSharedDeadline, CancellationToken cancellationToken)
        {
            if (delay < MinimumRetryDelay)
            {
                delay = MinimumRetryDelay;
            }

            var now = _clock.UtcNow;
            var elapsed = now - startedUtc;
            var budget = TimeSpan.FromSeconds(Math.Max(0, MaxTotalRetryBudgetSeconds));
            var remaining = budget - elapsed;

            if (remaining < TimeSpan.Zero || delay > remaining)
            {
                _logger.LogError($"Retry deadline for {url} is {FormatDelay(delay)} away, exceeding the remaining retry budget of {FormatDelay(remaining)}. Not retrying before the server's not-before time.");
                throw new HttpRequestException($"Retry deadline for {url} exceeds the remaining retry budget; not retrying before the server's not-before time.");
            }

            DateTimeOffset deadline;
            if (!TryAdd(now, delay, out deadline))
            {
                _logger.LogError($"Retry deadline for {url} is too large to represent. Not retrying before the server's not-before time.");
                throw new HttpRequestException($"Retry deadline for {url} is too large to represent.");
            }

            if (publishSharedDeadline)
            {
                lock (this)
                {
                    if (!_nextCallEarliestTime.HasValue || _nextCallEarliestTime.Value < deadline)
                    {
                        _nextCallEarliestTime = deadline;
                    }
                }
            }

            try
            {
                await _clock.DelayAsync(delay, cancellationToken);
            }
            finally
            {
                if (publishSharedDeadline)
                {
                    lock (this)
                    {
                        if (_nextCallEarliestTime.HasValue && _nextCallEarliestTime.Value <= _clock.UtcNow)
                        {
                            _nextCallEarliestTime = null;
                        }
                    }
                }
            }
        }

        private static bool TryAdd(DateTimeOffset value, TimeSpan delay, out DateTimeOffset result)
        {
            try
            {
                result = value.Add(delay);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                result = DateTimeOffset.MaxValue;
                return false;
            }
        }

        private static bool IsRetryableResponse(HttpResponseMessage response, bool isReplayableIdempotentGet)
        {
            if (response == null)
            {
                return false;
            }

            if ((int)response.StatusCode == 429)
            {
                return true;
            }

            if (!isReplayableIdempotentGet)
            {
                return false;
            }

            return response.StatusCode == HttpStatusCode.BadGateway
                || response.StatusCode == HttpStatusCode.ServiceUnavailable
                || response.StatusCode == HttpStatusCode.GatewayTimeout;
        }

        private static bool ShouldPublishSharedDeadline(HttpResponseMessage response, bool hasRetryAfterHeader)
        {
            if ((int)response.StatusCode == 429)
            {
                return true;
            }

            // A 503 Retry-After is an explicit service not-before signal, so share it across the client. Guessed
            // fallback delays for 502/503/504 are scoped only to the replayed GET: one flaky endpoint should not
            // stall unrelated requests on the same ManualGraphCallClient.
            return response.StatusCode == HttpStatusCode.ServiceUnavailable && hasRetryAfterHeader;
        }

        private struct RetryDelayDecision
        {
            public RetryDelayDecision(TimeSpan delay, bool publishSharedDeadline)
            {
                Delay = delay;
                PublishSharedDeadline = publishSharedDeadline;
            }

            public TimeSpan Delay { get; }
            public bool PublishSharedDeadline { get; }
        }

        private static string FormatDelay(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            if (delay.TotalSeconds >= 1)
            {
                return $"{delay.TotalSeconds:N0}s";
            }

            return $"{delay.TotalMilliseconds:N0}ms";
        }

        /// <summary>
        /// Transient failures worth retrying at the HTTP layer: a client-side timeout (HttpClient.Timeout
        /// elapsed) surfaces as a <see cref="TaskCanceledException"/>, and a transient socket/DNS failure as an
        /// <see cref="HttpRequestException"/> thrown from the send itself. HTTP status codes are handled
        /// separately via the response status code. A caller-requested cancellation also surfaces as
        /// <see cref="TaskCanceledException"/>, so callers must check their own token before treating this as
        /// transient; caller cancellation propagates immediately and does not consume another retry.
        /// </summary>
        private static bool IsTransientException(Exception ex)
        {
            return ex is TaskCanceledException || ex is HttpRequestException;
        }

        #region Props
        public int MaxRetries
        {
            get
            {
                lock (_maxRetriesObj)
                {
                    return _maxRetries;
                }
            }
            set
            {
                lock (_maxRetriesObj)
                {
                    _maxRetries = value;
                }
            }
        }

        /// <summary>
        /// Total retry/back-off execution budget, in seconds. Retry-After is never clipped to this value: when a
        /// server not-before deadline would exceed the remaining budget, the call fails visibly instead of
        /// retrying early. Defaults to 3600 seconds, so the worst-case retry back-off is bounded by this budget
        /// per retryable response cycle and by MaxRetries for total attempts.
        /// </summary>
        public int MaxTotalRetryBudgetSeconds
        {
            get
            {
                lock (_maxRetriesObj)
                {
                    return _maxTotalRetryBudgetSeconds;
                }
            }
            set
            {
                lock (_maxRetriesObj)
                {
                    _maxTotalRetryBudgetSeconds = value;
                }
            }
        }

        [Obsolete("Use MaxTotalRetryBudgetSeconds. Retry-After values are no longer clipped to this property; it now forwards to the total retry budget.")]
        public int MaxRetryAfterWaitSeconds
        {
            get { return MaxTotalRetryBudgetSeconds; }
            set { MaxTotalRetryBudgetSeconds = value; }
        }
        public int ConcurrentCalls
        {
            get
            {
                lock (_concurrentCallsObj)
                {
                    return _concurrentCalls;
                }
            }
        }
        public int ThrottledCalls
        {
            get
            {
                lock (_throttledCallsObject)
                {
                    return _throttledCalls;
                }
            }
        }

        public int CompletedCalls
        {
            get
            {
                lock (_completedCallsObject)
                {
                    return _completedCalls;
                }
            }
        }
        #endregion
    }
}
