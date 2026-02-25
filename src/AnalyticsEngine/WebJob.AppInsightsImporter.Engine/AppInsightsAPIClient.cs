using Azure.Core;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
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

        #region Constructors

        /// <summary>
        /// Creates a new App Insights API client that authenticates using Entra ID credentials.
        /// </summary>
        /// <param name="appInsightsConnectionString">The Application Insights connection string. The InstrumentationKey is parsed from it for use in the query URL.</param>
        /// <param name="credential">A TokenCredential (e.g. ClientSecretCredential) for Entra ID authentication.</param>
        /// <param name="debugTracer">Logger instance.</param>
        public AppInsightsAPIClient(string appInsightsConnectionString, TokenCredential credential, ILogger debugTracer)
        {
            if (string.IsNullOrEmpty(appInsightsConnectionString))
            {
                throw new ArgumentException($"'{nameof(appInsightsConnectionString)}' cannot be null or empty.", nameof(appInsightsConnectionString));
            }

            _credential = credential ?? throw new ArgumentNullException(nameof(credential));

            _appInsightsId = ParseInstrumentationKey(appInsightsConnectionString);
            if (string.IsNullOrEmpty(_appInsightsId))
            {
                throw new ArgumentException("Could not parse InstrumentationKey from the provided connection string.", nameof(appInsightsConnectionString));
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
        /// Parses the InstrumentationKey value from an App Insights connection string.
        /// </summary>
        public static string ParseInstrumentationKey(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString)) return null;
            foreach (var part in connectionString.Split(';'))
            {
                var separatorIndex = part.IndexOf('=');
                if (separatorIndex > 0)
                {
                    var key = part.Substring(0, separatorIndex).Trim();
                    var value = part.Substring(separatorIndex + 1).Trim();
                    if (key.Equals("InstrumentationKey", StringComparison.OrdinalIgnoreCase))
                        return value;
                }
            }
            return null;
        }

        private async Task SetBearerToken()
        {
            var tokenRequestContext = new TokenRequestContext(AppInsightsScope);
            var token = await _credential.GetTokenAsync(tokenRequestContext, CancellationToken.None);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
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
            var response = await client.GetAsync(req);

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

            var resultsResponse = await client.GetAsync(req);

            var result = await HandleResponse<AppInsightsQueryResult>(resultsResponse, saveRestResponses, "event");

            return new CustomEventsResultCollection(result.DefaultTable, forDate, _logger);
        }

        string GetWhereString(DateTime forDate)
        {
            return $"timestamp >= todatetime('{forDate.ToString("yyyy-MM-dd HH:mm:ss")}') and timestamp < todatetime('{forDate.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss")}')";
        }

        async Task<T> HandleResponse<T>(HttpResponseMessage response, bool saveRestResponses, string operationType)
        {
            var responseBody = await response.Content.ReadAsStringAsync();

            if (saveRestResponses)
            {
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
            }

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
        }
    }
}
