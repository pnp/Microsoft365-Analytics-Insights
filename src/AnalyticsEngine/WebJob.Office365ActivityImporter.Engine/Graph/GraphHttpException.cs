using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.Http;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Thrown when a Graph call returns a non-success HTTP status.
    ///
    /// The framework's own <see cref="HttpRequestException"/> carries no status code on .NET Framework
    /// (<c>StatusCode</c> only exists from .NET 5), so a caller that wanted to tell a 403 apart from a 500
    /// had nothing to test but the exception's English message. That is exactly the distinction an
    /// importer needs: a 403 is a permissions misconfiguration an admin must fix, while a 5xx is
    /// transient. This type keeps the status, the URL, the response body and Graph's machine-readable
    /// error code so the failure can be reported precisely - and so it reaches the Health page as
    /// something an admin can act on rather than "Response status code does not indicate success".
    /// </summary>
    /// <remarks>
    /// Derives from <see cref="HttpRequestException"/> so pre-existing <c>catch (HttpRequestException)</c>
    /// handlers keep working unchanged.
    /// </remarks>
    public class GraphHttpException : HttpRequestException
    {
        public GraphHttpException(HttpStatusCode statusCode, string url, string responseBody, Exception innerException)
            : this(BuildMessage(statusCode, url, responseBody), statusCode, url, responseBody, innerException)
        {
        }

        protected GraphHttpException(string message, HttpStatusCode statusCode, string url, string responseBody, Exception innerException)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            Url = url;
            ResponseBody = responseBody;
            GraphErrorCode = ExtractGraphErrorCode(responseBody);
        }

        /// <summary>The HTTP status Graph returned.</summary>
        public HttpStatusCode StatusCode { get; }

        /// <summary>The URL that failed.</summary>
        public string Url { get; }

        /// <summary>The raw response body, kept for diagnostics.</summary>
        public string ResponseBody { get; }

        /// <summary>
        /// Graph's machine-readable error code (e.g. <c>Authorization_RequestDenied</c>), or null when the
        /// body isn't a parseable Graph error.
        /// </summary>
        public string GraphErrorCode { get; }

        private static string BuildMessage(HttpStatusCode statusCode, string url, string responseBody)
        {
            var code = ExtractGraphErrorCode(responseBody);
            var status = $"{(int)statusCode} ({statusCode})";

            return string.IsNullOrEmpty(code)
                ? $"Graph returned {status} for {url}."
                : $"Graph returned {status} for {url} with error code '{code}'.";
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
