using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Retries idempotent SDK HTTP reads only when this process' request deadline expires.
    /// Kiota's RetryHandler still owns Graph-directed HTTP retries such as 429/503/504 and
    /// Retry-After. Caller cancellation is never treated as a transient timeout.
    /// </summary>
    internal sealed class BoundedGraphRequestHandler : DelegatingHandler
    {
        private readonly GraphRequestBudgetOptions _options;
        private readonly ILogger _logger;

        public BoundedGraphRequestHandler(GraphRequestBudgetOptions options, ILogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!IsRetryableRead(request))
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var started = Stopwatch.StartNew();
            TaskCanceledException lastTimeout = null;

            for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remaining = _options.TotalTimeoutRetryBudget - started.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException(BuildExhaustedMessage(attempt - 1, started.Elapsed), lastTimeout);
                }

                var timeout = remaining < _options.PerRequestTimeout ? remaining : _options.PerRequestTimeout;
                using (var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    requestDeadline.CancelAfter(timeout);
                    var requestForAttempt = attempt == 1 ? request : await CloneRequestAsync(request).ConfigureAwait(false);
                    try
                    {
                        var response = await base.SendAsync(requestForAttempt, requestDeadline.Token).ConfigureAwait(false);
                        if (attempt > 1)
                        {
                            _logger?.LogInformation($"Graph user import request succeeded on timeout attempt {attempt:N0} after {started.ElapsedMilliseconds:N0} ms.");
                        }
                        return response;
                    }
                    catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && requestDeadline.IsCancellationRequested)
                    {
                        lastTimeout = ex;
                        _logger?.LogWarning($"Graph user import request attempt {attempt:N0}/{_options.MaxAttempts:N0} exceeded its {timeout.TotalSeconds:N0}s deadline after {started.ElapsedMilliseconds:N0} ms.");

                        if (attempt >= _options.MaxAttempts || started.Elapsed >= _options.TotalTimeoutRetryBudget)
                        {
                            throw new TimeoutException(BuildExhaustedMessage(attempt, started.Elapsed), ex);
                        }
                    }
                }

                var delayRemaining = _options.TotalTimeoutRetryBudget - started.Elapsed;
                if (_options.TimeoutRetryDelay > TimeSpan.Zero && delayRemaining > TimeSpan.Zero)
                {
                    var delay = delayRemaining < _options.TimeoutRetryDelay ? delayRemaining : _options.TimeoutRetryDelay;
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new TimeoutException(BuildExhaustedMessage(_options.MaxAttempts, started.Elapsed), lastTimeout);
        }

        private static bool IsRetryableRead(HttpRequestMessage request)
            => request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;

        private string BuildExhaustedMessage(int attempts, TimeSpan elapsed)
            => $"Graph user import request deadline exhausted after {attempts:N0} attempt(s) and {elapsed.TotalSeconds:N1}s. " +
               "The user/licence import is deferred; the previous committed delta checkpoint remains in force.";

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version
            };

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            foreach (var property in request.Properties)
            {
                clone.Properties[property.Key] = property.Value;
            }

            if (request.Content != null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                clone.Content = new ByteArrayContent(bytes);
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }
    }
}
