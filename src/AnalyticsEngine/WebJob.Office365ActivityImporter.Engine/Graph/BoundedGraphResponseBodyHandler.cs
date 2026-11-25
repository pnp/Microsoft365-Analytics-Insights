using Microsoft.Kiota.Http.HttpClientLibrary.Extensions;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>Closes optional redirect bodies and buffers data-bearing responses under the
    /// attempt's deadline before Kiota or HttpClient can start a tokenless body read.</summary>
    internal sealed class BoundedGraphResponseBodyHandler : DelegatingHandler
    {
        // SDK importer calls return pages, not whole-tenant report downloads.
        private const int MaxPageBytes = 32 * 1024 * 1024;
        private static readonly SemaphoreSlim BodyReadSlots = new SemaphoreSlim(16, 16);
        private static readonly Func<HttpResponseMessage, bool> DefaultShouldRedirect = new RedirectHandlerOption().ShouldRedirect;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = response.Content;
            try
            {
                if (content != null && (request.Method == HttpMethod.Head || response.StatusCode == HttpStatusCode.NotModified))
                {
                    // net48 HttpClient buffers these too. The advertised length describes the resource,
                    // not a body to allocate or read. Keep the metadata on an already-buffered empty body.
                    var empty = new ByteArrayContent(Array.Empty<byte>());
                    await empty.LoadIntoBufferAsync(0).ConfigureAwait(false);
                    empty.Headers.Clear();
                    foreach (var header in content.Headers) empty.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    response.Content = empty;
                    content.Dispose();
                }
                else if (content != null && HasOptionalRedirectBody(request, response))
                {
                    // Kiota drains a redirect before sending its next request. It needs only the
                    // status and Location; close the transport without spending the attempt on it.
                    response.Content = null;
                    content.Dispose();
                }
                else if (content != null)
                {
                    await BodyReadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var read = BufferAndReleaseSlotAsync(content);
                    if (!read.IsCompleted && await Task.WhenAny(read, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false) != read)
                    {
                        // net48 LoadIntoBufferAsync has no cancellation overload. Dispose the transport
                        // below, but don't block shutdown if a producer ignores close. Its read still owns
                        // its slot until it exits; observe any late failure.
                        _ = read.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        throw new OperationCanceledException(cancellationToken);
                    }
                    await read.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested(); // Cancellation can also surface as EOF.
                return response;
            }
            catch (Exception ex)
            {
                if (!response.IsSuccessStatusCode && (cancellationToken.IsCancellationRequested || ex is HttpRequestException))
                {
                    // Kiota reads even a throttling error's body before waiting. Give it the known status
                    // and Retry-After, not a local timeout retry. The outer handler checks caller cancellation.
                    response.Content?.Dispose();
                    response.Content = null;
                    return response;
                }
                response.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        private static bool HasOptionalRedirectBody(HttpRequestMessage request, HttpResponseMessage response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.MovedPermanently:
                case HttpStatusCode.Found:
                case HttpStatusCode.SeeOther:
                case HttpStatusCode.TemporaryRedirect:
                case (HttpStatusCode)308: // Permanent Redirect has no name on net48.
                    if (response.Headers.Location == null) return false;
                    // A custom predicate may inspect the body or return it to the caller. Don't
                    // invoke it speculatively; let Kiota evaluate it once with the bounded body.
                    var option = request.GetRequestOption<RedirectHandlerOption>();
                    return option == null || (option.MaxRedirect > 0 && option.ShouldRedirect == DefaultShouldRedirect);
                default:
                    return false;
            }
        }

        private static async Task BufferAndReleaseSlotAsync(HttpContent content)
        {
            try { await content.LoadIntoBufferAsync(MaxPageBytes).ConfigureAwait(false); }
            finally { BodyReadSlots.Release(); }
        }
    }
}
