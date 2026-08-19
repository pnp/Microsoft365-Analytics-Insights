using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtils.Http
{
    public class AutoThrottleHttpClient : HttpClient
    {
        #region Constructor, Props, and Privates

        const string THROTTLE_ERROR = "Throttle error";

        private readonly bool ignoreRetryHeader;
        protected readonly ILogger _logger;
        private DateTime? _nextCallEarliestTime = null;
        private int _concurrentCalls = 0, _throttledCalls = 0, _completedCalls = 0;
        private int _maxRetries = 10;
        private int _maxRetryAfterWaitSeconds = 180;
        private object _concurrentCallsObj = new object(), _throttledCallsObject = new object(), _completedCallsObject = new object(), _maxRetriesObj = new object();


        public AutoThrottleHttpClient(bool ignoreRetryHeader, ILogger logger)
        {
            Timeout = TimeSpan.FromHours(1);
            this.ignoreRetryHeader = ignoreRetryHeader;
            _logger = logger;
        }
        public AutoThrottleHttpClient(bool ignoreRetryHeader, ILogger logger, DelegatingHandler handler) : base(handler)
        {
            Timeout = TimeSpan.FromHours(1);
            this.ignoreRetryHeader = ignoreRetryHeader;
            _logger = logger;
        }

        public AutoThrottleHttpClient(HttpMessageHandler handler, ILogger logger) : base(handler)
        {
            _logger = logger;
        }


        #endregion

        /// <summary>
        /// Execute a method that returns a HttpResponseMessage, with throttling retry logic
        /// </summary>
        public async Task<HttpResponseMessage> ExecuteHttpCallWithThrottleRetries(Func<Task<HttpResponseMessage>> httpAction, string url)
        {
            HttpResponseMessage response = null;
            int retries = 0, secondsToWait = 0;
            bool retryDownload = true;
            while (retryDownload)
            {
                lock (_concurrentCallsObj)
                {
                    _concurrentCalls++;
                }

                // Figure out if we need to wait. Sleep thread outside lock
                TimeSpan? sleepTimeNeeded = null;
                lock (this)
                {
                    if (_nextCallEarliestTime != null && _nextCallEarliestTime > DateTime.Now)
                    {
                        sleepTimeNeeded = _nextCallEarliestTime.Value.Subtract(DateTime.Now);
                    }
                }
                if (sleepTimeNeeded.HasValue)
                {
                    lock (this)
                    {
                        _throttledCalls++;
                    }
                    Thread.Sleep(sleepTimeNeeded.Value);
                    lock (this)
                    {
                        _nextCallEarliestTime = null;
                    }
                }

                // Get response but don't buffer full content (which will buffer overlflow for large files).
                // A client-side timeout (HttpClient.Timeout elapsed) surfaces as a TaskCanceledException, and a
                // transient socket/DNS blip as an HttpRequestException thrown from the call itself (before any
                // response is received). Neither is an HTTP 429, so the status-code retry logic below would
                // never see them - they'd propagate and, in the importers, abort a whole import section (or in
                // DEBUG crash the process). Retry them here with a linear back-off, sharing the MaxRetries
                // budget, then rethrow so a genuinely dead endpoint still surfaces to the caller's own handling.
                try
                {
                    response = await httpAction();
                }
                catch (Exception ex) when (IsTransientException(ex))
                {
                    lock (_concurrentCallsObj)
                    {
                        _concurrentCalls--;
                    }

                    retries++;
                    if (retries >= MaxRetries)
                    {
                        _logger.LogError(ex, $"Transient error calling {url}: '{ex.Message}'. Giving up after {MaxRetries} attempts.");
                        throw;
                    }

                    secondsToWait = retries * 2;
                    _logger.LogWarning($"Transient error calling {url}: '{ex.Message}'. Waiting {secondsToWait}s before retry (attempt #{retries} of {MaxRetries})...");
                    Thread.Sleep(TimeSpan.FromSeconds(secondsToWait));
                    continue;
                }

                lock (_concurrentCallsObj)
                {
                    _concurrentCalls--;
                }

                if (!response.IsSuccessStatusCode && (int)response.StatusCode == 429)
                {
                    retries++;
                    lock (this)
                    {
                        _throttledCalls++;
                    }

                    // Do we have a "retry-after" header & should we use it?
                    var waitValue = response.GetRetryAfterHeaderSeconds();                    if (!ignoreRetryHeader && waitValue.HasValue)
                    {
                        // Honour 'retry-after', but cap it: a single very large (or buggy) value would otherwise
                        // block this thread for that entire duration (we saw multi-minute stalls in usage-report
                        // paging). Capping breaks one huge sleep into shorter, observable waits + retries.
                        var cap = MaxRetryAfterWaitSeconds;
                        secondsToWait = Math.Min(waitValue.Value, cap);
                        if (waitValue.Value > cap)
                        {
                            _logger.LogWarning($"{THROTTLE_ERROR} for {url}. 'retry-after' header asked for {waitValue.Value}s; capping wait at {cap}s for attempt #{retries}.");
                        }
                        else
                        {
                            _logger.LogInformation($"{THROTTLE_ERROR} for {url}. Waiting to retry for attempt #{retries}, {secondsToWait} seconds (from 'retry-after' header)...");
                        }
                    }
                    else
                    {
                        // We have to guess how much to back-off. Loop with ever-increasing wait.
                        if (retries == MaxRetries)
                        {
                            // Don't try forever
                            _logger.LogError($"{THROTTLE_ERROR}. Maximum retry attempts {MaxRetries} has been attempted for {url}.");

                            // Allow normal HTTP exception & abort download
                            response.EnsureSuccessStatusCode();
                        }

                        // We've not reached throttling max retries...keep retrying
                        _logger.LogInformation($"{THROTTLE_ERROR} downloading from REST. Waiting {retries} seconds to try again...");

                        secondsToWait = retries * 2;
                    }

                    // Wait before trying again
                    lock (this)
                    {
                        _nextCallEarliestTime = DateTime.Now.AddSeconds(secondsToWait);
                    }

                    // This response is being discarded, so release it before looping. It matters for callers
                    // that request HttpCompletionOption.ResponseHeadersRead (the Copilot CSV report
                    // downloads): the body is still unread, so without this each retry leaks a connection.
                    response.Dispose();
                    response = null;
                }
                else
                {
                    // Not HTTP 429. Don't bother retrying & let caller handle any error
                    retryDownload = false;

                    lock (_completedCallsObject)
                    {
                        _completedCalls++;
                    }
                }
            }

            return response;
        }

        /// <summary>
        /// Transient failures worth retrying at the HTTP layer: a client-side timeout (HttpClient.Timeout
        /// elapsed) surfaces as a <see cref="TaskCanceledException"/>, and a transient socket/DNS failure as an
        /// <see cref="HttpRequestException"/> thrown from the send itself. HTTP 429 is handled separately via the
        /// response status code, so it is deliberately not included here.
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
        /// Upper bound (seconds) on how long a single 'retry-after' back-off will sleep. A very large or buggy
        /// header value would otherwise stall the calling thread for its whole duration.
        /// </summary>
        public int MaxRetryAfterWaitSeconds
        {
            get
            {
                lock (_maxRetriesObj)
                {
                    return _maxRetryAfterWaitSeconds;
                }
            }
            set
            {
                lock (_maxRetriesObj)
                {
                    _maxRetryAfterWaitSeconds = value;
                }
            }
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
