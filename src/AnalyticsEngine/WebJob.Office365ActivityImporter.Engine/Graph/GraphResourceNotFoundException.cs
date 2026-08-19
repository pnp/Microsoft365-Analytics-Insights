using Newtonsoft.Json.Linq;
using System;
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
    /// Derives from <see cref="HttpRequestException"/> so pre-existing <c>catch (HttpRequestException)</c>
    /// handlers keep working unchanged; callers that want to special-case a 404 catch this type first.
    /// </remarks>
    public class GraphResourceNotFoundException : HttpRequestException
    {
        public GraphResourceNotFoundException(string url, string responseBody, Exception innerException)
            : base(BuildMessage(url, responseBody), innerException)
        {
            Url = url;
            ResponseBody = responseBody;
            GraphErrorCode = ExtractGraphErrorCode(responseBody);
        }

        /// <summary>The URL that returned the 404.</summary>
        public string Url { get; }

        /// <summary>The raw response body, kept for diagnostics.</summary>
        public string ResponseBody { get; }

        /// <summary>
        /// Graph's machine-readable error code (e.g. <c>MailboxNotEnabledForRESTAPI</c> or
        /// <c>Request_ResourceNotFound</c>), or null when the body isn't a parseable Graph error.
        /// </summary>
        public string GraphErrorCode { get; }

        private static string BuildMessage(string url, string responseBody)
        {
            var code = ExtractGraphErrorCode(responseBody);
            return string.IsNullOrEmpty(code)
                ? $"Graph returned 404 (Not Found) for {url}."
                : $"Graph returned 404 (Not Found) for {url} with error code '{code}'.";
        }

        /// <summary>
        /// Pulls <c>error.code</c> out of a standard Graph error payload. Never throws - a malformed or
        /// non-JSON body simply yields null, because this is only used for logging and diagnostics.
        /// </summary>
        internal static string ExtractGraphErrorCode(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return null;

            try
            {
                return JObject.Parse(responseBody)["error"]?["code"]?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
