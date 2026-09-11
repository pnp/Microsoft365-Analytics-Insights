using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtils.Http
{
    public static class HttpClientExtensions
    {
        public static async Task<HttpResponseMessage> GetAsyncWithThrottleRetries(this AutoThrottleHttpClient httpClient, string url, ILogger logger, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Default to return when full content is read
            return await httpClient.GetAsyncWithThrottleRetries(url, HttpCompletionOption.ResponseContentRead, logger, cancellationToken);
        }
        public static async Task<HttpResponseMessage> GetAsyncWithThrottleRetries(this AutoThrottleHttpClient httpClient, string url, HttpCompletionOption completionOption, ILogger logger, CancellationToken cancellationToken = default(CancellationToken))
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

            var response = await httpClient.ExecuteHttpCallWithThrottleRetries(
                ct => httpClient.GetAsync(url, completionOption, ct),
                url,
                isReplayableIdempotentGet: true,
                cancellationToken: cancellationToken);

            return response;
        }

        public static async Task<HttpResponseMessage> PostAsyncWithThrottleRetries(this AutoThrottleHttpClient httpClient, string url, object body, ILogger logger, CancellationToken cancellationToken = default(CancellationToken))
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

            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(body);
            var response = await httpClient.ExecuteHttpCallWithThrottleRetries(
                ct => httpClient.PostAsync(url, new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), ct),
                url,
                cancellationToken: cancellationToken);

            return response;
        }


        public static async Task<HttpResponseMessage> PostAsyncWithThrottleRetries(this ConfidentialClientApplicationThrottledHttpClient httpClient, string url, string bodyContent, string mimeType, string boundary, ILogger logger, CancellationToken cancellationToken = default(CancellationToken))
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

            var response = await httpClient.ExecuteHttpCallWithThrottleRetries(
                ct =>
                {
                    var body = new StringContent(bodyContent);
                    var header = new MediaTypeHeaderValue(mimeType);
                    header.Parameters.Add(new NameValueHeaderValue("boundary", boundary));
                    body.Headers.ContentType = header;
                    return httpClient.PostAsync(url, body, ct);
                },
                url,
                cancellationToken: cancellationToken);

            return response;
        }

        public static int? GetRetryAfterHeaderSeconds(this HttpResponseMessage response, DateTimeOffset? nowUtc = null)
        {
            if (response == null)
            {
                return null;
            }

            response.Headers.TryGetValues("Retry-After", out var retryAfterHeaderValues);

            if (retryAfterHeaderValues == null)
            {
                return null;
            }

            foreach (var retryAfterHeaderVal in retryAfterHeaderValues)
            {
                if (long.TryParse(retryAfterHeaderVal, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                {
                    if (seconds <= 0)
                    {
                        return 0;
                    }

                    return seconds > int.MaxValue ? int.MaxValue : (int)seconds;
                }

                if (DateTimeOffset.TryParse(retryAfterHeaderVal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var retryAfterDateUtc))
                {
                    var now = nowUtc ?? DateTimeOffset.UtcNow;
                    var delta = retryAfterDateUtc - now.ToUniversalTime();
                    if (delta <= TimeSpan.Zero)
                    {
                        return 0;
                    }

                    return delta.TotalSeconds >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(delta.TotalSeconds);
                }
            }

            return null;
        }
    }
}
