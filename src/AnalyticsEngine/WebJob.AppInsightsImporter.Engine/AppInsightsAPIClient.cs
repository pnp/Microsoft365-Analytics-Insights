using Azure.Core;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// HTTP client for App Insights calls, authenticated via Entra ID (OAuth).
    /// </summary>
    public class AppInsightsAPIClient : IAppInsightsSourceLoader, IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _appInsightsId;
        private readonly TokenCredential _credential;
        private static readonly string[] AppInsightsScope = new[] { "https://api.applicationinsights.io/.default" };
        private AccessToken? _cachedToken;
        private readonly SemaphoreSlim _tokenLock = new SemaphoreSlim(1, 1);

        internal const int MaxRetries = 5;
        internal const int MaxBackoffSeconds = 60;

        /// <summary>
        /// How the client waits between retries. Replaceable by tests so the retry and back-off rules can be
        /// asserted on the delays actually <b>requested</b>, rather than inferred from elapsed wall-clock
        /// time - which measures scheduling, GC and deserialisation as well, and is therefore both flaky
        /// under load and capable of passing after a no-back-off regression. See issue #374.
        /// </summary>
        internal Func<TimeSpan, Task> RetryDelay { get; set; } = Task.Delay;

        #region Constructors

        /// <summary>
        /// Creates a new App Insights API client that authenticates using Entra ID credentials.
        /// </summary>
        /// <param name="appInsightsConnectionString">The Application Insights connection string. The ApplicationId is parsed from it for use in the query URL.</param>
        /// <param name="credential">A TokenCredential (e.g. ClientSecretCredential) for Entra ID authentication.</param>
        /// <param name="logger">Logger instance.</param>
        public AppInsightsAPIClient(string appInsightsConnectionString, TokenCredential credential, ILogger logger)
            : this(appInsightsConnectionString, credential, logger, null)
        {
        }

        /// <summary>
        /// As above, but with the HTTP transport supplied by the caller. Used by the tests to drive the
        /// retry, back-off and transient-classification logic against a stub handler instead of the real
        /// App Insights endpoint (issue #374). A <c>null</c> handler means the production transport.
        /// </summary>
        public AppInsightsAPIClient(string appInsightsConnectionString, TokenCredential credential, ILogger logger, HttpMessageHandler messageHandler)
        {
            if (string.IsNullOrEmpty(appInsightsConnectionString))
            {
                throw new ArgumentException($"'{nameof(appInsightsConnectionString)}' cannot be null or empty.", nameof(appInsightsConnectionString));
            }

            _credential = credential ?? throw new ArgumentNullException(nameof(credential));

            _appInsightsId = ParseConnectionStringValue(appInsightsConnectionString, "ApplicationId");
            if (string.IsNullOrEmpty(_appInsightsId))
            {
                throw new ArgumentException("Could not parse ApplicationId from the provided connection string.", nameof(appInsightsConnectionString));
            }

            client = messageHandler == null
                ? new HttpClient { Timeout = TimeSpan.FromMinutes(10) }
                : new HttpClient(messageHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(10) };

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _logger = logger;
        }

        #endregion

        #region Props

        // Per-instance HttpClient. Cannot be made static because callers set the
        // Authorization header on DefaultRequestHeaders per token refresh; sharing
        // would race between instances. Lifetime is bounded by the importer run.
        private readonly HttpClient client;

        #endregion

        /// <summary>
        /// Parses a named value from an App Insights connection string.
        /// </summary>
        public static string ParseConnectionStringValue(string connectionString, string keyName)
        {
            if (string.IsNullOrEmpty(connectionString)) return null;
            foreach (var part in connectionString.Split(';'))
            {
                var separatorIndex = part.IndexOf('=');
                if (separatorIndex > 0)
                {
                    var key = part.Substring(0, separatorIndex).Trim();
                    var value = part.Substring(separatorIndex + 1).Trim();
                    if (key.Equals(keyName, StringComparison.OrdinalIgnoreCase))
                        return value;
                }
            }
            return null;
        }

        private async Task SetBearerToken()
        {
            // https://learn.microsoft.com/en-us/azure/azure-monitor/app/azure-ad-authentication?tabs=net
            // Only refresh the token when it is missing or within 5 minutes of expiry to
            // avoid redundant Entra ID calls on every HTTP request.
            // Guarded by semaphore because parallel API calls share this client instance.
            await _tokenLock.WaitAsync();
            try
            {
                if (!_cachedToken.HasValue || _cachedToken.Value.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
                {
                    var tokenRequestContext = new TokenRequestContext(AppInsightsScope);
                    _cachedToken = await _credential.GetTokenAsync(tokenRequestContext, CancellationToken.None);
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken.Value.Token);
                    _logger.LogInformation("Refreshed App Insights bearer token.");
                }
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        /// <summary>
        /// Executes an HTTP GET with retry logic for transient failures (429, 5xx, network errors).
        /// </summary>
        async Task<HttpResponseMessage> GetWithRetry(string url)
        {
            for (int attempt = 0; ; attempt++)
            {
                HttpResponseMessage response = null;
                try
                {
                    response = await client.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }

                    if (!IsTransientStatusCode(response.StatusCode) || attempt >= MaxRetries)
                    {
                        return response;
                    }

                    var retryAfterSeconds = GetRetryAfterSeconds(response);
                    var backoff = retryAfterSeconds ?? Math.Min((int)Math.Pow(2, attempt), MaxBackoffSeconds);
                    _logger.LogWarning($"Transient HTTP {(int)response.StatusCode} from App Insights API. Retrying in {backoff}s (attempt {attempt + 1}/{MaxRetries})...");

                    response.Dispose();
                    await RetryDelay(TimeSpan.FromSeconds(backoff));
                }
                catch (Exception ex) when (IsTransientException(ex) && attempt < MaxRetries)
                {
                    var backoff = Math.Min((int)Math.Pow(2, attempt), MaxBackoffSeconds);
                    _logger.LogWarning($"Transient error calling App Insights API: {ex.Message}. Retrying in {backoff}s (attempt {attempt + 1}/{MaxRetries})...");
                    await RetryDelay(TimeSpan.FromSeconds(backoff));
                }
            }
        }

        static bool IsTransientStatusCode(HttpStatusCode statusCode)
        {
            return statusCode == (HttpStatusCode)429
                || statusCode == HttpStatusCode.BadGateway
                || statusCode == HttpStatusCode.ServiceUnavailable
                || statusCode == HttpStatusCode.GatewayTimeout
                || statusCode == HttpStatusCode.RequestTimeout;
        }

        static bool IsTransientException(Exception ex)
        {
            return ex is HttpRequestException || ex is TaskCanceledException;
        }

        static int? GetRetryAfterSeconds(HttpResponseMessage response)
        {
            response.Headers.TryGetValues("Retry-After", out var values);
            if (values != null)
            {
                foreach (var val in values)
                {
                    if (int.TryParse(val, out var seconds))
                    {
                        return seconds;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// <see cref="IAppInsightsSourceLoader"/>. Kept as a thin alias over the original method name so no
        /// existing call site or test changes.
        /// </summary>
        public Task<PageViewCollection> GetPageViewsAsync(DateTime forDateUtc, bool saveRestResponses)
            => GetPageViewsFromAppInsights(forDateUtc, saveRestResponses);

        /// <summary>
        /// <see cref="IAppInsightsSourceLoader"/>. Kept as a thin alias over the original method name so no
        /// existing call site or test changes.
        /// </summary>
        public Task<CustomEventsResultCollection> GetCustomEventsAsync(DateTime forDateUtc, bool saveRestResponses)
            => GetCustomEventsFromAppInsights(forDateUtc, saveRestResponses);

        /// <summary>
        /// Load page-views
        /// </summary>
        public async Task<PageViewCollection> GetPageViewsFromAppInsights(DateTime forDate, bool saveRestResponses)
        {
            await SetBearerToken();

            // Only from the last hit timestamp
            var adxQuery = $"pageViews | where " + GetWhereString(forDate) +
                $" | order by timestamp asc";

            // API Doc: https://docs.microsoft.com/en-us/rest/api/application-insights/query/get
            var req = $"https://api.applicationinsights.io/v1/apps/{_appInsightsId}/query?query={Uri.EscapeDataString(adxQuery)}";
            var response = await GetWithRetry(req);

            var result = await HandleResponse<AppInsightsQueryResult>(response, saveRestResponses, "pageview");

            return new PageViewCollection(result.DefaultTable, forDate, this._logger);
        }

        /// <summary>
        /// Load & process events into specific events we can use
        /// </summary>
        public async Task<CustomEventsResultCollection> GetCustomEventsFromAppInsights(DateTime forDate, bool saveRestResponses)
        {
            await SetBearerToken();

            // Only from the last hit timestamp
            var adxQuery = $"customEvents | where " + GetWhereString(forDate) + " | order by timestamp asc";

            // Doc: https://dev.applicationinsights.io/reference/get-events
            var req = $"https://api.applicationinsights.io/v1/apps/{_appInsightsId}/query?query={Uri.EscapeDataString(adxQuery)}";

            var resultsResponse = await GetWithRetry(req);

            var result = await HandleResponse<AppInsightsQueryResult>(resultsResponse, saveRestResponses, "event");

            return new CustomEventsResultCollection(result.DefaultTable, forDate, _logger);
        }

        /// <summary>
        /// The KQL time window for one UTC day.
        ///
        /// Formatted with <see cref="System.Globalization.CultureInfo.InvariantCulture"/> deliberately: the
        /// current culture selects the calendar, so on a host whose culture uses a non-Gregorian calendar
        /// this rendered a completely different date - "2569-05-30" under th-TH (Buddhist), "1447-12-13"
        /// under ar-SA (Umm al-Qura) - and App Insights then returned no rows for the window, silently and
        /// without an error. KQL <c>todatetime()</c> only understands Gregorian ISO dates.
        /// </summary>
        internal string GetWhereString(DateTime forDate)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return $"timestamp >= todatetime('{forDate.ToString("yyyy-MM-dd HH:mm:ss", culture)}') and timestamp < todatetime('{forDate.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss", culture)}')";
        }

        async Task<T> HandleResponse<T>(HttpResponseMessage response, bool saveRestResponses, string operationType)
        {
            if (saveRestResponses)
            {
                // When saving debug responses we need the full string
                return await HandleResponseWithStringBody<T>(response, operationType);
            }

            // Stream directly from the HTTP response to avoid LOH string allocations for large payloads
            if (response.IsSuccessStatusCode)
            {
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var streamReader = new StreamReader(stream))
                using (var jsonReader = new JsonTextReader(streamReader))
                {
                    var serializer = new JsonSerializer();
                    return serializer.Deserialize<T>(jsonReader);
                }
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Unexpected response: {responseBody}");
            }
        }

        async Task<T> HandleResponseWithStringBody<T>(HttpResponseMessage response, string operationType)
        {
            var responseBody = await response.Content.ReadAsStringAsync();

            var dir = Path.Combine(Path.GetTempPath(), "AppInsightsImporter", "REST", operationType);
            Directory.CreateDirectory(dir);
            // UTC + zero-padded month so files sort lexicographically by time. The previous
            // "yyyy-dd-M_HH-mm-ss" format used non-padded month which collides for files with
            // matching numeric reps (e.g. 2026-10-1 vs 2026-1-10) and sorts incorrectly.
            var fileTitle = $"{DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture)}.json";
            var fileName = Path.Combine(dir, fileTitle);

            object responseDebug = null;
            try
            {
                responseDebug = Newtonsoft.Json.Linq.JObject.Parse(responseBody);
            }
            catch (JsonReaderException)
            {
                // Body wasn't valid JSON; fall back to raw string output below
            }

            if (responseDebug == null)
            {
                responseDebug = responseBody;
            }

            var fileOut = new { Response = response, Body = responseDebug, Request = response.RequestMessage };

            File.WriteAllText(fileName, JsonConvert.SerializeObject(fileOut));
            Console.WriteLine($"--DEBUG: Wrote {fileName}");

            if (response.IsSuccessStatusCode)
            {
                var responeObj = JsonConvert.DeserializeObject<T>(responseBody);
                return responeObj;
            }
            else
            {
                throw new HttpRequestException($"Unexpected response: {responseBody}");
            }
        }

        public void Dispose()
        {
            client.Dispose();
            _tokenLock.Dispose();
        }
    }
}
