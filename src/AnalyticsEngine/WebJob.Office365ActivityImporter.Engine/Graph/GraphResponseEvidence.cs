using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary.Extensions;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>Opt in only on calls whose direct response guides subscription, identity or licence decisions.</summary>
    internal static class GraphResponseEvidence
    {
        private sealed class DirectResponseOption : IRequestOption { }
        internal static readonly IRequestOption RequireDirect = new DirectResponseOption();
        internal static bool IsRequired(HttpRequestMessage request) => request.GetRequestOption<DirectResponseOption>() != null;

        // Leave authentication challenges intact for the SDK/transport. The HTTP retry middleware
        // still receives every error response, with its original status and headers.
        internal static bool CheckErrorBody(int status) => status >= 400 && status < 600 && status != 401 && status != 407;

        internal const int MaxErrorBytes = 64 * 1024;

        internal static async Task<bool> HasStructuredErrorAsync(HttpContent buffered, CancellationToken cancellationToken)
        {
            // Use the SDK's own error model, not a MIME check or a second, more permissive JSON parser.
            // A small independent buffer leaves the stream and the full valid error intact for Kiota.
            // LoadIntoBufferAsync does not reapply its limit to content already buffered by the transport.
            if (buffered.Headers.ContentLength > MaxErrorBytes) return false;
            var source = await buffered.ReadAsStreamAsync().ConfigureAwait(false);
            if (!source.CanSeek || source.Length > MaxErrorBytes) return false;
            using (var stream = new MemoryStream(await buffered.ReadAsByteArrayAsync().ConfigureAwait(false), writable: false))
            {
                try
                {
                    var mediaType = buffered.Headers.ContentType.MediaType.ToLowerInvariant();
                    var node = await ParseNodeFactoryRegistry.DefaultInstance.GetRootParseNodeAsync(mediaType, stream, cancellationToken).ConfigureAwait(false);
                    var error = node.GetObjectValue(ODataError.CreateFromDiscriminatorValue);
                    return !string.IsNullOrWhiteSpace(error?.Error?.Code);
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is FormatException || ex is OverflowException)
                {
                    return false;
                }
            }
        }

        internal static bool HasResourceNotFoundCode(ApiException ex)
        {
            var code = (ex as ODataError)?.Error?.Code;
            return string.Equals(code, "Request_ResourceNotFound", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "ResourceNotFound", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ConfirmsNotFound(ApiException ex) =>
            ex.ResponseStatusCode == (int)HttpStatusCode.NotFound && HasResourceNotFoundCode(ex);
    }

    internal sealed class GraphDecisionRedirectException : ApiException
    {
        internal GraphDecisionRedirectException(int status)
            : base($"HTTP {status}: a redirect is not an answer from the requested Graph endpoint. It was not followed.")
        {
            ResponseStatusCode = status;
        }
    }
}
