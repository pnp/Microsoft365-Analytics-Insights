using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary.Extensions;
using System.Linq;
using System.Net.Http;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>A refusal, not an OData diagnosis or evidence that an object should be deleted.</summary>
    internal sealed class NonJsonGraphRefusal : ApiException
    {
        private sealed class PreserveStatusOption : IRequestOption
        {
            internal PreserveStatusOption(bool deletionDecision = false) => DeletionDecision = deletionDecision;
            internal bool DeletionDecision { get; }
        }
        internal static readonly IRequestOption PreserveStatus = new PreserveStatusOption();
        internal static readonly IRequestOption RequireDeletionEvidence = new PreserveStatusOption(deletionDecision: true);

        private NonJsonGraphRefusal(HttpResponseMessage response)
            : base($"HTTP {(int)response.StatusCode} was returned without a JSON Graph error.")
        {
            ResponseStatusCode = (int)response.StatusCode;
            ResponseHeaders = response.Headers.ToDictionary(h => h.Key, h => h.Value);
        }

        internal static NonJsonGraphRefusal FromResponse(HttpRequestMessage request, HttpResponseMessage response, bool unreadableBody = false)
        {
            var status = (int)response.StatusCode;
            if (status < 400 || status >= 500 || status == 429 || status == 401 || status == 407) return null;
            // Only opted-in operations see this distinction. A refused subscription POST can warrant
            // keeping the old subscription alive; a licence read only degrades on a 403/404.
            if (request.Method != HttpMethod.Post && (request.Method != HttpMethod.Get || (status != 403 && status != 404))) return null;
            var content = response.Content;
            var option = request.GetRequestOption<PreserveStatusOption>();
            if (content == null || option == null) return null;
            if (unreadableBody)
                return option.DeletionDecision && (request.Method == HttpMethod.Post ? status == 409 : status == 404)
                    ? new NonJsonGraphRefusal(response) : null;
            var mediaType = content.Headers.ContentType?.MediaType;
            return !GraphTransientErrorHandler.IsJson(mediaType) && (mediaType != null || content.Headers.ContentLength != 0)
                ? new NonJsonGraphRefusal(response) : null;
        }
    }
}
