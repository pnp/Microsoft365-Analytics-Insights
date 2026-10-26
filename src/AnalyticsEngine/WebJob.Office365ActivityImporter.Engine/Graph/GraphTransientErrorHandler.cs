using Microsoft.Kiota.Abstractions;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    internal sealed class GraphTransientErrorHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var mediaType = response.Content?.Headers.ContentType?.MediaType;
            var json = string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || (mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ?? false);
            if ((status == 429 || status >= 500 && status <= 599) && !json)
            {
                // Kiota cannot parse an HTML proxy failure as OData and otherwise throws InvalidOperationException,
                // discarding the HTTP status that determines whether enrichment must be durably retried.
                var failure = new ApiException("Microsoft Graph returned a transient HTTP error without a JSON error body.")
                {
                    ResponseStatusCode = status,
                    ResponseHeaders = response.Headers.ToDictionary(h => h.Key, h => h.Value)
                };
                response.Dispose();
                throw failure;
            }
            return response;
        }
    }
}
