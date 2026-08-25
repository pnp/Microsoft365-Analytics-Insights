using System;
using System.Net;
using System.Net.Http;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Thrown when a Graph call returns HTTP 404.
    ///
    /// A 404 is a <b>terminal, expected outcome</b> rather than an application error: the resource
    /// genuinely does not exist. The common cases are a user with no Exchange Online mailbox
    /// (unlicensed, on-premises or inactive - Graph error <c>MailboxNotEnabledForRESTAPI</c>) and a
    /// guest / external account that has no mailbox in this tenant (<c>Request_ResourceNotFound</c>).
    ///
    /// Neither condition ever resolves by retrying, so callers must treat it as "nothing to import for
    /// this resource" and must <b>not</b> log it via <c>LogError</c> - doing so records an Application
    /// Insights exception on every import cycle, forever, for a perfectly normal tenant state.
    /// </summary>
    /// <remarks>
    /// Derives from <see cref="GraphHttpException"/> (and so from <see cref="HttpRequestException"/>) so
    /// pre-existing <c>catch (HttpRequestException)</c> handlers keep working unchanged; callers that want
    /// to special-case a 404 catch this type first. <c>StatusCode</c>, <c>Url</c>, <c>ResponseBody</c> and
    /// <c>GraphErrorCode</c> are all inherited.
    /// </remarks>
    public class GraphResourceNotFoundException : GraphHttpException
    {
        public GraphResourceNotFoundException(string url, string responseBody, Exception innerException)
            : base(BuildMessage(url, responseBody), HttpStatusCode.NotFound, url, responseBody, innerException)
        {
        }

        private static string BuildMessage(string url, string responseBody)
        {
            var code = ExtractGraphErrorCode(responseBody);
            return string.IsNullOrEmpty(code)
                ? $"Graph returned 404 (Not Found) for {url}."
                : $"Graph returned 404 (Not Found) for {url} with error code '{code}'.";
        }
    }
}
