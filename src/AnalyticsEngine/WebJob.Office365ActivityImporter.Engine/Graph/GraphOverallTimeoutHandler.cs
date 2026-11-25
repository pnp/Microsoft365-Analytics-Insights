using Microsoft.Kiota.Abstractions;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>One deadline across redirects, SDK retries and response reads; no local replays.</summary>
    internal sealed class GraphOverallTimeoutHandler : DelegatingHandler
    {
        private readonly TimeSpan _timeout;

        public GraphOverallTimeoutHandler(TimeSpan timeout)
        {
            if (timeout != Timeout.InfiniteTimeSpan && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(timeout));
            _timeout = timeout;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(_timeout);
                try
                {
                    var response = await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
                    BoundedGraphResponseBodyHandler.ThrowIfExpired(response, cancellationToken, deadline.Token);
                    return response;
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested &&
                    (ex is OperationCanceledException || ex is ApiException || ex is HttpRequestException || ex is AggregateException))
                {
                    throw new OperationCanceledException("The Graph SDK request was canceled.", ex, cancellationToken);
                }
                catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
                {
                    throw new TaskCanceledException("The Graph SDK request exceeded its timeout.", ex);
                }
            }
        }
    }
}
