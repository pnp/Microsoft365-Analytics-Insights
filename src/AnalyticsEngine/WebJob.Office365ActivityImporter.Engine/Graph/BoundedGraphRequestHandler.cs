using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
        private static readonly HashSet<string> CredentialAndRoutingHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Authorization", "Proxy-Authorization", "Cookie", "Cookie2", "Host" };

        public BoundedGraphRequestHandler(GraphRequestBudgetOptions options, ILogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            var maxAttempts = IsRetryableRead(request) ? _options.MaxAttempts : 1;
            // Never reconstruct a retry from a request the transport has already seen: both its
            // URI and headers can change. Kiota authenticates each replay for its actual target.
            var replayTemplate = maxAttempts > 1 ? CloneRead(request) : null;
            HttpResponseMessage result = null;
            try
            {
                result = await SendAttemptsAsync(request, replayTemplate, maxAttempts, started, cancellationToken).ConfigureAwait(false);
                return result;
            }
            finally
            {
                if (result?.RequestMessage != replayTemplate) replayTemplate?.Dispose();
            }
        }

        private async Task<HttpResponseMessage> SendAttemptsAsync(HttpRequestMessage request, HttpRequestMessage replayTemplate,
            int maxAttempts, Stopwatch started, CancellationToken cancellationToken)
        {
            OperationCanceledException lastTimeout = null;

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
                using (var retryRequest = attempt == 1 ? null : CloneRead(replayTemplate))
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
                        if (replayTemplate != null && response.RequestMessage == (retryRequest ?? request)
                            && (retryRequest != null || !response.IsSuccessStatusCode))
                        {
                            // Kiota may clone this for a Retry-After. Hand it the original target,
                            // not a mutated wire request or a retry clone about to be disposed.
                            response.RequestMessage = replayTemplate;
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
                if (!CredentialAndRoutingHeaders.Contains(header.Key))
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
