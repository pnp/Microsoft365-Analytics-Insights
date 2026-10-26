using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// For where there's no support for a Graph call resource in SDK
    /// </summary>
    public class ManualGraphCallClient : ConfidentialClientApplicationThrottledHttpClient
    {
        #region Constructors

        public ManualGraphCallClient(HttpMessageHandler server, ILogger logger) : base(server, logger)
        {
        }
        public ManualGraphCallClient(ImportAppIndentityOAuthContext appIndentity, ILogger logger) : base(appIndentity, false, logger)
        {
        }
        #endregion

        public async Task<T> GetAsyncWithThrottleRetries<T>(string url)
        {
            return await this.GetAsyncWithThrottleRetries<T>(url, null);
        }
        public async Task<string> GetStringAsyncWithThrottleRetries(string url)
        {
            using (var callResponse = await this.GetAsyncWithThrottleRetries(url, base._logger))
            {
                var callResponseBody = await callResponse.Content.ReadAsStringAsync();

                try
                {
                    callResponse.EnsureSuccessStatusCode();
                }
                catch (HttpRequestException ex)
                {
                    throw FailureFor(callResponse, url, callResponseBody, ex);
                }

                return callResponseBody;
            }
        }

        public async Task<T> GetStreamAsyncWithThrottleRetries<T>(string url, Func<Stream, Task<T>> streamReader)
        {
            if (streamReader == null) throw new ArgumentNullException(nameof(streamReader));

            using (var callResponse = await this.GetAsyncWithThrottleRetries(url, HttpCompletionOption.ResponseHeadersRead, base._logger))
            {
                if (!callResponse.IsSuccessStatusCode)
                {
                    var callResponseBody = await callResponse.Content.ReadAsStringAsync();

                    try
                    {
                        callResponse.EnsureSuccessStatusCode();
                    }
                    catch (HttpRequestException ex)
                    {
                        throw FailureFor(callResponse, url, callResponseBody, ex);
                    }
                }

                using (var stream = await callResponse.Content.ReadAsStreamAsync())
                {
                    return await streamReader(stream);
                }
            }
        }

        public async Task<T> GetAsyncWithThrottleRetries<T>(string url, Action<string> jsonStringAction)
        {
            // 'using' so the underlying socket is released promptly rather than waiting for finalisation.
            using (var callResponse = await this.GetAsyncWithThrottleRetries(url, base._logger))
            {
                var callResponseBody = await callResponse.Content.ReadAsStringAsync();

                try
                {
                    callResponse.EnsureSuccessStatusCode();
                }
                catch (HttpRequestException ex)
                {
                    throw FailureFor(callResponse, url, callResponseBody, ex);
                }

                // Get call
                T dto = default(T);
                try
                {
                    dto = JsonConvert.DeserializeObject<T>(callResponseBody);
                }
                catch (JsonReaderException ex)
                {
                    // Deliberately neither the body nor the exception: a 200 that isn't the JSON we expected can still hold message
                    // subjects and bodies, and the URL holds a UPN and sync tokens.
                    _logger.LogError($"GraphCallMalformedResponse: {DescribeCall(callResponse, url, callResponseBody)}; parseError={ex.GetType().Name}; line={ex.LineNumber}; position={ex.LinePosition}. The response could not be parsed, so the caller sees no result.");
                }

                jsonStringAction?.Invoke(callResponseBody);
                return dto;
            }
        }

        /// <summary>
        /// Logs a failed call and returns the exception to throw. A 404 means the resource simply isn't there (typically a user
        /// with no Exchange mailbox, or a guest account). That's a permanent, expected tenant state - not an application error -
        /// so it is logged at debug and surfaces as a dedicated type that callers can skip on. Logging it as an error would push
        /// an exception to Application Insights on every single import cycle for a condition that never clears.
        /// </summary>
        private Exception FailureFor(HttpResponseMessage callResponse, string url, string callResponseBody, HttpRequestException ex)
        {
            if (callResponse.StatusCode == HttpStatusCode.NotFound)
            {
                var notFound = new GraphResourceNotFoundException(url, callResponseBody, ex);
                _logger.LogDebug($"GraphCallNotFound: {DescribeCall(callResponse, url, callResponseBody)}");
                return notFound;
            }

            var graphException = CreateGraphHttpException(callResponse, url, callResponseBody, ex);
            _logger.LogError(graphException, $"GraphCallFailed: {DescribeCall(callResponse, url, callResponseBody)}");
            return graphException;
        }

        /// <summary>
        /// What is safe to log about a call: the endpoint shape (path with identifiers replaced, no query string), the status,
        /// Graph's request ids and error code, and how big the payload was. Never the URL - it carries the UPN and the delta or
        /// skip token - and never the body, which can hold message subjects and bodies.
        /// </summary>
        internal static string DescribeCall(HttpResponseMessage response, string url, string body)
        {
            return $"endpoint={GraphEndpointTemplate.FromUrl("GET", url)}; status={(int)response.StatusCode}; " +
                $"graphErrorCode={GraphHttpException.ExtractGraphErrorCode(body) ?? "none"}; " +
                $"requestId={GetGraphRequestId(response) ?? "none"}; clientRequestId={GetHeader(response, "client-request-id") ?? "none"}; " +
                $"payloadChars={body?.Length ?? 0}";
        }

        private static GraphHttpException CreateGraphHttpException(HttpResponseMessage response, string url, string responseBody, Exception innerException)
        {
            return new GraphHttpException(response.StatusCode, url, responseBody, innerException, "GET", GetGraphRequestId(response));
        }

        private static string GetGraphRequestId(HttpResponseMessage response) => GetHeader(response, "request-id");

        private static string GetHeader(HttpResponseMessage response, string name)
        {
            if (response == null)
            {
                return null;
            }

            return response.Headers.TryGetValues(name, out var values)
                ? values.FirstOrDefault()
                : null;
        }
    }
}
