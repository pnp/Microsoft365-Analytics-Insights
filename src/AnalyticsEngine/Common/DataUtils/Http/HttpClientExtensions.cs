using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Common.DataUtils.Http
{
    public static class HttpClientExtensions
    {
        public static async Task<HttpResponseMessage> GetAsyncWithThrottleRetries(this AutoThrottleHttpClient httpClient, string url, ILogger debugTracer)
        {
            // Default to return when full content is read
            return await httpClient.GetAsyncWithThrottleRetries(url, HttpCompletionOption.ResponseContentRead, debugTracer);
        }
        public static async Task<HttpResponseMessage> GetAsyncWithThrottleRetries(this AutoThrottleHttpClient httpClient, string url, HttpCompletionOption completionOption, ILogger debugTracer)
        {
            if (httpClient is null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException($"'{nameof(url)}' cannot be null or empty.", nameof(url));
            }

            if (debugTracer is null)
            {
                throw new ArgumentNullException(nameof(debugTracer));
            }

            var response = await httpClient.ExecuteHttpCallWithThrottleRetries(async () => await httpClient.GetAsync(url, completionOption), url);


            return response;
        }

        public static async Task<HttpResponseMessage> PostAsyncWithThrottleRetries(this AutoThrottleHttpClient httpClient, string url, object body, ILogger debugTracer)
        {
            if (httpClient is null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException($"'{nameof(url)}' cannot be null or empty.", nameof(url));
            }

            if (debugTracer is null)
            {
                throw new ArgumentNullException(nameof(debugTracer));
            }

            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(body);
            var httpContent = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.ExecuteHttpCallWithThrottleRetries(async () => await httpClient.PostAsync(url, httpContent), url);

            return response;
        }


        public static async Task<HttpResponseMessage> PostAsyncWithThrottleRetries(this ConfidentialClientApplicationThrottledHttpClient httpClient, string url, string bodyContent, string mimeType, string boundary, ILogger logger)
        {
            if (httpClient is null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException($"'{nameof(url)}' cannot be null or empty.", nameof(url));
            }

            if (logger is null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            var body = new StringContent(bodyContent);
            var header = new MediaTypeHeaderValue(mimeType);
            header.Parameters.Add(new NameValueHeaderValue("boundary", boundary));
            body.Headers.ContentType = header;

            var response = await httpClient.ExecuteHttpCallWithThrottleRetries(async () => await httpClient.PostAsync(url, body), url);

            return response;
        }

        public static int? GetRetryAfterHeaderSeconds(this HttpResponseMessage response)
        {
            int responseWaitVal = 0;
            response.Headers.TryGetValues("Retry-After", out var r);

            if (r != null)
                foreach (var retryAfterHeaderVal in r)
                {
                    if (int.TryParse(retryAfterHeaderVal, out responseWaitVal))
                    {
                        return responseWaitVal;
                    }
                }

            return null;
        }
    }
}
