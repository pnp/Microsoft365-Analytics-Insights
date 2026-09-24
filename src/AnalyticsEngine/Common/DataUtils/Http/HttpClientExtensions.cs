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

        /// <summary>
        /// How long the server asked us to wait, in seconds, or null when it gave no usable hint.
        /// </summary>
        /// <remarks>
        /// Reads the standard <c>Retry-After</c> header and also the Azure resource-provider rate-limit headers
        /// named <c>x-ms-ratelimit-*-retry-after</c>. Microsoft Cost Management throttles with those instead:
        /// its spec says to wait for <c>x-ms-ratelimit-microsoft.consumption-retry-after</c>, and in practice it
        /// sends <c>x-ms-ratelimit-microsoft.costmanagement-{qpu|entity|tenant|client}-retry-after</c>. Without
        /// reading them, a throttled cost query fell back to a few seconds' back-off and retried straight into
        /// the same limit until the retry budget ran out.
        /// <para>When several hints are present the LONGEST wins, because each one names a separate limit and
        /// all of them must have reset before a retry can succeed.</para>
        /// </remarks>
        public static int? GetRetryAfterHeaderSeconds(this HttpResponseMessage response, DateTimeOffset? nowUtc = null)
        {
            if (response == null)
            {
                return null;
            }

            var standard = GetStandardRetryAfterSeconds(response, nowUtc);
            var rateLimit = GetRateLimitRetryAfterSeconds(response);

            if (standard.HasValue && rateLimit.HasValue)
            {
                return Math.Max(standard.Value, rateLimit.Value);
            }

            return standard ?? rateLimit;
        }

        private static int? GetRateLimitRetryAfterSeconds(HttpResponseMessage response)
        {
            int? longest = null;

            foreach (var header in response.Headers)
            {
                if (!header.Key.StartsWith("x-ms-ratelimit-", StringComparison.OrdinalIgnoreCase)
                    || !header.Key.EndsWith("retry-after", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var value in header.Value)
                {
                    if (!long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    {
                        continue;
                    }

                    var clamped = seconds > int.MaxValue ? int.MaxValue : (int)seconds;
                    if (!longest.HasValue || clamped > longest.Value)
                    {
                        longest = clamped;
                    }
                }
            }

            return longest;
        }

        private static int? GetStandardRetryAfterSeconds(HttpResponseMessage response, DateTimeOffset? nowUtc)
        {
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
