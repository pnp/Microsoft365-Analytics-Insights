using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary.Extensions;
using System.Linq;
using System.Net.Http;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>A refusal, not an OData diagnosis or evidence that an object should be deleted.</summary>
    internal sealed class NonJsonGraphRefusal : ApiException
    {
        private sealed class PreserveStatusOption : IRequestOption { }
        internal static readonly IRequestOption PreserveStatus = new PreserveStatusOption();

        private NonJsonGraphRefusal(HttpResponseMessage response)
            : base($"HTTP {(int)response.StatusCode} was returned without a JSON Graph error.")
        {
            ResponseStatusCode = (int)response.StatusCode;
            ResponseHeaders = response.Headers.ToDictionary(h => h.Key, h => h.Value);
        }

        internal static void ThrowIfRequested(HttpRequestMessage request, HttpResponseMessage response)
        {
            var status = (int)response.StatusCode;
            if (status < 400 || status >= 500 || status == 429 || status == 401 || status == 407) return;
            // Only opted-in operations see this distinction. A refused subscription POST can warrant
            // keeping the old subscription alive; a licence read only degrades on a 403/404.
            if (request.Method != HttpMethod.Post && status != 403 && status != 404) return;
            var content = response.Content;
            if (content == null || request.GetRequestOption<PreserveStatusOption>() == null) return;
            var mediaType = content.Headers.ContentType?.MediaType;
            if (!GraphTransientErrorHandler.IsJson(mediaType) && (mediaType != null || content.Headers.ContentLength != 0))
                throw new NonJsonGraphRefusal(response);
        }
    }
}
