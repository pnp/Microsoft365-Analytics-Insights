using Azure.Core;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// The exponential back-off for a retry attempt, in seconds: 1, 2, 4, 8 ... capped at
        /// <see cref="MaxBackoffSeconds"/>. Used when the response carries no <c>Retry-After</c> header.
        /// Pure, so the cap is assertable at an attempt number the retry loop itself never reaches
        /// (<see cref="MaxRetries"/> stops at 16 seconds).
        /// </summary>
        internal static int BackoffSecondsFor(int attempt)
        {
            return Math.Min((int)Math.Pow(2, attempt), MaxBackoffSeconds);
        }

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
            : this(appInsightsConnectionString, credential, logger, messageHandler, null)
        {
        }

        /// <summary>
        /// As above, with the windows each day's queries needed remembered in <paramref name="queryWindows"/>, which
        /// outlives this client: the importer creates a client per cycle, and the host keeps one cache for the life of
        /// the process so a day over the API's limits is not split from the whole day again every cycle. See
        /// <see cref="AppInsightsQueryWindowCache"/>. Null keeps the memory for this client only.
        /// </summary>
        public AppInsightsAPIClient(string appInsightsConnectionString, TokenCredential credential, ILogger logger, HttpMessageHandler messageHandler,
            AppInsightsQueryWindowCache queryWindows)
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
            _queryWindows = queryWindows ?? new AppInsightsQueryWindowCache();
        }

        #endregion

        #region Props

        // Which windows each day's queries last needed - see AppInsightsQueryWindowCache.
        private readonly AppInsightsQueryWindowCache _queryWindows;

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
                    var backoff = retryAfterSeconds ?? BackoffSecondsFor(attempt);
                    _logger.LogWarning($"Transient HTTP {(int)response.StatusCode} from App Insights API. Retrying in {backoff}s (attempt {attempt + 1}/{MaxRetries})...");

                    response.Dispose();
                    await RetryDelay(TimeSpan.FromSeconds(backoff));
                }
                catch (Exception ex) when (IsTransientException(ex) && attempt < MaxRetries)
                {
                    var backoff = BackoffSecondsFor(attempt);
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
            var table = await QueryDayAsync(BuildPageViewsQuery, forDate, saveRestResponses, "pageview");
            return new PageViewCollection(table, forDate, this._logger);
        }

        /// <summary>
        /// Load & process events into specific events we can use
        /// </summary>
        public async Task<CustomEventsResultCollection> GetCustomEventsFromAppInsights(DateTime forDate, bool saveRestResponses)
        {
            var table = await QueryDayAsync(BuildCustomEventsQuery, forDate, saveRestResponses, "event");
            return new CustomEventsResultCollection(table, forDate, _logger);
        }

        /// <summary>
        /// Only the columns the result parsers map - the <see cref="AppInsightsQueryResultPropertyAttribute"/>s on
        /// the result classes - are requested. Fetching every column of a whole day is what reaches the API's row
        /// and response-size limits first, and the other columns were thrown away on arrival.
        /// </summary>
        internal static readonly string PageViewColumns = ProjectedColumns(typeof(PageViewAppInsightsQueryResult));

        /// <summary>
        /// As <see cref="PageViewColumns"/>, across every result type <see cref="CustomEventsResultCollection"/>
        /// builds a custom event into - add any new one here too.
        /// </summary>
        internal static readonly string CustomEventColumns = ProjectedColumns(
            typeof(NameOnlyCustomEventAppInsightsQueryResult),
            typeof(PageExitEventAppInsightsQueryResult),
            typeof(SearchEventAppInsightsQueryResult),
            typeof(ClickEventAppInsightsQueryResult),
            typeof(PageUpdateEventAppInsightsQueryResult));

        /// <summary>
        /// The custom events <see cref="CustomEventsResultCollection"/> turns into results; it drops any other, so
        /// they are not downloaded. The App Insights resource also receives this solution's own telemetry events.
        /// </summary>
        private static readonly string ImportedCustomEventNames = string.Join(", ", new[]
        {
            AppInsightsImporterConstants.EVENT_NAME_PAGE_EXIT,
            AppInsightsImporterConstants.EVENT_NAME_USER_SEARCH,
            AppInsightsImporterConstants.EVENT_NAME_CLICK,
            AppInsightsImporterConstants.EVENT_NAME_PAGE_UPDATE,
        }.Select(name => "'" + name + "'"));

        internal string BuildPageViewsQuery(DateTime fromUtc, DateTime toUtc) =>
            $"pageViews | where {GetWhereString(fromUtc, toUtc)} | project {PageViewColumns} | order by timestamp asc";

        internal string BuildCustomEventsQuery(DateTime fromUtc, DateTime toUtc) =>
            $"customEvents | where {GetWhereString(fromUtc, toUtc)} | where name in ({ImportedCustomEventNames}) | project {CustomEventColumns} | order by timestamp asc";

        private static string ProjectedColumns(params Type[] resultTypes)
        {
            var columns = resultTypes
                .SelectMany(t => t.GetProperties())
                .SelectMany(p => p.GetCustomAttributes(true).OfType<AppInsightsQueryResultPropertyAttribute>())
                .Select(a => a.PropName)
                .Distinct()
                .ToList();
            return string.Join(", ", columns);
        }

        /// <summary>
        /// Every row of one UTC day, from as many query windows as the API's limits need. The day is asked for
        /// whole, unless an earlier query for it - in this process - had to be split: then it starts from the windows
        /// that came back complete last time (see <see cref="AppInsightsQueryWindowCache"/>). Either way each window
        /// is read by <see cref="AddCompleteWindowsAsync"/>, which splits it further if its result is partial.
        /// </summary>
        private async Task<AppInsightsTable> QueryDayAsync(Func<DateTime, DateTime, string> queryForWindow, DateTime forDate,
            bool saveRestResponses, string operationType)
        {
            var startWindows = _queryWindows.WindowsFor(operationType, forDate)
                ?? new[] { (FromUtc: forDate, ToUtc: forDate.AddDays(1)) };

            var completeWindows = new List<(DateTime FromUtc, DateTime ToUtc, AppInsightsTable Table)>();
            foreach (var window in startWindows)
            {
                await AddCompleteWindowsAsync(queryForWindow, window.FromUtc, window.ToUtc, saveRestResponses, operationType, completeWindows);
            }

            _queryWindows.Remember(operationType, forDate, completeWindows.Select(w => (w.FromUtc, w.ToUtc)).ToList());
            if (completeWindows.Count > 1)
            {
                _logger.LogInformation($"Read {forDate:yyyy-MM-dd}'s {operationType}s in {completeWindows.Count} windows, as the whole day is over the Application Insights query limits.");
            }
            return AppInsightsTable.Concatenate(completeWindows.Select(w => w.Table).ToList());
        }

        /// <summary>
        /// Queries the half-open window [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) and appends its
        /// result to <paramref name="completeWindows"/>. When App Insights says the result is partial - cut off at
        /// its row or size limit - those rows are discarded and each half of the window is queried instead, first
        /// half first, so the windows (and the rows, each window being in time order) stay in time order. Down to
        /// <see cref="AppInsightsImportWindow.MinimumQueryWindow"/>; a partial result even there fails the import
        /// of the day rather than saving part of it.
        /// </summary>
        private async Task AddCompleteWindowsAsync(Func<DateTime, DateTime, string> queryForWindow, DateTime fromUtc, DateTime toUtc,
            bool saveRestResponses, string operationType, List<(DateTime FromUtc, DateTime ToUtc, AppInsightsTable Table)> completeWindows)
        {
            var table = await QueryCompleteResultAsync(queryForWindow(fromUtc, toUtc), saveRestResponses, operationType);
            if (table != null)
            {
                completeWindows.Add((fromUtc, toUtc, table));
                return;
            }

            if (!AppInsightsImportWindow.TrySplitQueryWindow(fromUtc, toUtc, out var midpointUtc))
            {
                throw new InvalidDataException($"Application Insights returned partial {operationType} results even for the {(toUtc - fromUtc).TotalSeconds:N0}-second window " +
                    $"from {fromUtc:yyyy-MM-dd HH:mm:ss} UTC, the smallest it is split into. The import is halted to avoid losing data.");
            }

            _logger.LogWarning($"Application Insights returned partial {operationType} results for {fromUtc:yyyy-MM-dd HH:mm:ss} to {toUtc:yyyy-MM-dd HH:mm:ss} UTC " +
                "(the query reached its row or size limit). Querying each half of that window instead.");
            await AddCompleteWindowsAsync(queryForWindow, fromUtc, midpointUtc, saveRestResponses, operationType, completeWindows);
            await AddCompleteWindowsAsync(queryForWindow, midpointUtc, toUtc, saveRestResponses, operationType, completeWindows);
        }

        /// <summary>
        /// Runs one query and returns its table, or null when App Insights reports the result as partial. Returning
        /// nothing of a partial result means none of its truncated rows - up to the API's ~104 MB - stay referenced
        /// while the smaller windows are queried. Any other error throws: a smaller window does not help those.
        /// </summary>
        private async Task<AppInsightsTable> QueryCompleteResultAsync(string adxQuery, bool saveRestResponses, string operationType)
        {
            // Per request rather than per day: splitting a large day can take long enough for the token to need refreshing.
            await SetBearerToken();

            // API Doc: https://docs.microsoft.com/en-us/rest/api/application-insights/query/get
            var req = $"https://api.applicationinsights.io/v1/apps/{_appInsightsId}/query?query={Uri.EscapeDataString(adxQuery)}";
            using (var response = await GetWithRetry(req))
            {
                var result = await HandleResponse<AppInsightsQueryResult>(response, saveRestResponses, operationType);
                if (result == null)
                {
                    throw new InvalidDataException("App Insights returned an empty query response.");
                }
                return result.IsPartialResult ? null : result.DefaultTable;
            }
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
            return GetWhereString(forDate, forDate.AddDays(1));
        }

        /// <summary>
        /// The KQL filter for the half-open window [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), so a row on
        /// a boundary between two windows is returned by exactly one of them. Invariant culture, as above.
        /// </summary>
        internal string GetWhereString(DateTime fromUtc, DateTime toUtc)
        {
            return $"timestamp >= todatetime('{FormatKqlTime(fromUtc)}') and timestamp < todatetime('{FormatKqlTime(toUtc)}')";
        }

        private static string FormatKqlTime(DateTime value)
        {
            // Window bounds are whole seconds (see AppInsightsImportWindow.TrySplitQueryWindow); anything finer is kept rather than rounded.
            var format = value.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.fffffff";
            return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
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
