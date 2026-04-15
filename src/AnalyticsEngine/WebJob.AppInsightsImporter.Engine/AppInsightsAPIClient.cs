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
    public class AppInsightsAPIClient : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _appInsightsId;
        private readonly TokenCredential _credential;
        private static readonly string[] AppInsightsScope = new[] { "https://api.applicationinsights.io/.default" };
        private AccessToken? _cachedToken;
        private readonly SemaphoreSlim _tokenLock = new SemaphoreSlim(1, 1);

        internal const int MaxRetries = 5;
        internal const int MaxBackoffSeconds = 60;

        #region Constructors

        /// <summary>
        /// Creates a new App Insights API client that authenticates using Entra ID credentials.
        /// </summary>
        /// <param name="appInsightsConnectionString">The Application Insights connection string. The ApplicationId is parsed from it for use in the query URL.</param>
        /// <param name="credential">A TokenCredential (e.g. ClientSecretCredential) for Entra ID authentication.</param>
        /// <param name="debugTracer">Logger instance.</param>
        public AppInsightsAPIClient(string appInsightsConnectionString, TokenCredential credential, ILogger debugTracer)
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

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.Timeout = TimeSpan.FromMinutes(10);

            _logger = debugTracer;
        }

        #endregion

        #region Props

        private HttpClient client = new HttpClient();

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
                    await Task.Delay(TimeSpan.FromSeconds(backoff));
                }
                catch (Exception ex) when (IsTransientException(ex) && attempt < MaxRetries)
                {
                    var backoff = Math.Min((int)Math.Pow(2, attempt), MaxBackoffSeconds);
                    _logger.LogWarning($"Transient error calling App Insights API: {ex.Message}. Retrying in {backoff}s (attempt {attempt + 1}/{MaxRetries})...");
                    await Task.Delay(TimeSpan.FromSeconds(backoff));
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
        /// Load page-views
        /// </summary>
        public async Task<PageViewCollection> GetPageViewsFromAppInsights(DateTime forDate, bool saveRestResponses)
        {
            await SetBearerToken();

            // Only from the last hit timestamp
            var adxQuery = $"pageViews | where " + GetWhereString(forDate) +
                $" | order by timestamp asc";

            // API Doc: https://docs.microsoft.com/en-us/rest/api/application-insights/query/get
            var req = $"https://api.applicationinsights.io/v1/apps/{_appInsightsId}/query?query={adxQuery}";
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
            var req = $"https://api.applicationinsights.io/v1/apps/{_appInsightsId}/query?query={adxQuery}";

            var resultsResponse = await GetWithRetry(req);

            var result = await HandleResponse<AppInsightsQueryResult>(resultsResponse, saveRestResponses, "event");

            return new CustomEventsResultCollection(result.DefaultTable, forDate, _logger);
        }

        string GetWhereString(DateTime forDate)
        {
            return $"timestamp >= todatetime('{forDate.ToString("yyyy-MM-dd HH:mm:ss")}') and timestamp < todatetime('{forDate.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss")}')";
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
            var fileTitle = $"{DateTime.Now.ToString("yyyy-dd-M_HH-mm-ss")}.json";
            var fileName = Path.Combine(dir, fileTitle);

            object responseDebug = null;
            try
            {
                responseDebug = Newtonsoft.Json.Linq.JObject.Parse(responseBody);
            }
            catch (FormatException)
            {
                // Don't care
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
