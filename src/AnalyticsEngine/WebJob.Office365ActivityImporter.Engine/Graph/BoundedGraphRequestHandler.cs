using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Applies a deadline to every SDK HTTP attempt and retries idempotent reads only when
    /// this process' request deadline expires, including while receiving the response body.
    /// HTTP error statuses are never retried here. Caller cancellation is not a transient timeout.
    /// <para>
    /// <see cref="GraphServiceClientFactory.BuildGraphImportHandlers"/> places this handler
    /// INSIDE Kiota's RetryHandler, which owns Graph-directed retries and Retry-After waits.
    /// So the deadline bounds each attempt the RetryHandler sends, never the Retry-After wait
    /// between attempts (#846 R8-10).
    /// </para>
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
            var started = Stopwatch.StartNew();
            OperationCanceledException lastTimeout = null;
            var maxAttempts = IsRetryableRead(request) ? _options.MaxAttempts : 1;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remaining = _options.TotalTimeoutRetryBudget - started.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException(BuildExhaustedMessage(attempt - 1, started.Elapsed), lastTimeout);
                }

                var timeout = remaining < _options.PerRequestTimeout ? remaining : _options.PerRequestTimeout;
                using (var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (var retryRequest = attempt == 1 ? null : CloneRead(request))
                {
                    requestDeadline.CancelAfter(timeout);
                    try
                    {
                        var response = await base.SendAsync(retryRequest ?? request, requestDeadline.Token).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            // Even a known error status must not trigger a service retry during shutdown.
                            response.Dispose();
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        if (retryRequest != null && response.RequestMessage == retryRequest)
                        {
                            // Kiota may clone this request after a Retry-After. Don't give it a disposed clone.
                            response.RequestMessage = request;
                        }
                        if (attempt > 1)
                        {
                            _logger?.LogInformation($"Graph import SDK request succeeded on timeout attempt {attempt:N0} after {started.ElapsedMilliseconds:N0} ms.");
                        }
                        return response;
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && requestDeadline.IsCancellationRequested)
                    {
                        lastTimeout = ex;
                        _logger?.LogWarning($"Graph import SDK request attempt {attempt:N0}/{maxAttempts:N0} exceeded its {timeout.TotalSeconds:N0}s deadline after {started.ElapsedMilliseconds:N0} ms.");

                        if (attempt >= maxAttempts || started.Elapsed >= _options.TotalTimeoutRetryBudget)
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

            throw new TimeoutException(BuildExhaustedMessage(maxAttempts, started.Elapsed), lastTimeout);
        }

        private static bool IsRetryableRead(HttpRequestMessage request)
            => request.Content == null && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head);

        private string BuildExhaustedMessage(int attempts, TimeSpan elapsed)
            => $"Graph import SDK request deadline exhausted after {attempts:N0} attempt(s) and {elapsed.TotalSeconds:N1}s. " +
               "The affected Graph import section is deferred; any previous committed checkpoint remains in force.";

        private static HttpRequestMessage CloneRead(HttpRequestMessage request)
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

            return clone;
        }
    }
}
