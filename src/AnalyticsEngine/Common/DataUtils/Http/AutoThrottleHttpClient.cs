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

                // Get response but don't buffer full content (which will buffer overlflow for large files)
                response = await httpAction();

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
                    var waitValue = response.GetRetryAfterHeaderSeconds();
                    if (!ignoreRetryHeader && waitValue.HasValue)
                    {
                        secondsToWait = waitValue.Value;
                        _logger.LogInformation($"{THROTTLE_ERROR} for {url}. Waiting to retry for attempt #{retries}, {secondsToWait} seconds (from 'retry-after' header)...");
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
