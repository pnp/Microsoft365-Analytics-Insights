using System.Net.Http;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// A Graph page that answered 200 but carried no <c>value</c> collection, or <c>"value": null</c>. Its rows are
    /// unknown - it is not an empty page - so the shared pager treats it exactly like a page that failed with an HTTP
    /// error.
    /// </summary>
    /// <remarks>
    /// An <see cref="HttpRequestException"/>, so every existing failed-page path handles it unchanged. Deliberately not a
    /// <see cref="GraphHttpException"/>: there is no error status or Graph error code, and nothing that inspects those must
    /// mistake it for one. The message names only the page number, never the URL, which can carry a delta or skip token.
    /// </remarks>
    public class GraphPageWithoutValueException : HttpRequestException
    {
        public GraphPageWithoutValueException(int pageNumber)
            : base($"Graph answered page {pageNumber} with no 'value' collection, so the rows on that page are unknown.")
        {
            PageNumber = pageNumber;
        }

        /// <summary>The page that came back without its rows; 1 is the initial request.</summary>
        public int PageNumber { get; }
    }
}
