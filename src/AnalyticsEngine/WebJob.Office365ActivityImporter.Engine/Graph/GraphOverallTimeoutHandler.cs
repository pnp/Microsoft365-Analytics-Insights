using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary.Extensions;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    internal sealed class GraphSdkDeadlineExceededException : TimeoutException
    {
        internal const string TimeoutMessage = "The Graph SDK request exceeded its timeout.";
        internal GraphSdkDeadlineExceededException(OperationCanceledException cause)
            : base(TimeoutMessage, cause) { }
    }

    /// <summary>One deadline across redirects, SDK retries and response reads; no local replays.</summary>
    internal sealed class GraphOverallTimeoutHandler : DelegatingHandler
    {
        private sealed class DistinguishDeadlineOption : IRequestOption { }
        internal static readonly IRequestOption DistinguishDeadline = new DistinguishDeadlineOption();
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
                    // net48 HttpClient turns an OperationCanceledException subclass into a plain TaskCanceledException.
                    // Only callers that must tell our deadline from a real stop opt into a faulted (not cancelled) task.
                    if (request.GetRequestOption<DistinguishDeadlineOption>() != null)
                        throw new GraphSdkDeadlineExceededException(ex);
                    throw new TaskCanceledException(GraphSdkDeadlineExceededException.TimeoutMessage, ex);
                }
            }
        }
    }
}
