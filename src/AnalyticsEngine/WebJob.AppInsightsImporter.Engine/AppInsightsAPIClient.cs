using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// HTTP client for App Insights calls
    /// </summary>
    public class AppInsightsAPIClient : IDisposable
    {
        private readonly ILogger _debugTracer;

        #region Constructors

        public AppInsightsAPIClient(string appid, string apikey, ILogger debugTracer)
        {
            if (string.IsNullOrEmpty(appid))
            {
                throw new ArgumentException($"'{nameof(appid)}' cannot be null or empty.", nameof(appid));
            }

            if (string.IsNullOrEmpty(apikey))
            {
                throw new ArgumentException($"'{nameof(apikey)}' cannot be null or empty.", nameof(apikey));
            }

            // Add auth headers to HTTP client
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("x-api-key", apikey);

            client.Timeout = TimeSpan.FromMinutes(10);

            this.ApiKey = apikey;
            _debugTracer = debugTracer;
            this.AppId = appid;
        }

        #endregion

        #region Props

        private HttpClient client = new HttpClient();

        public string AppId { get; set; }
        public string ApiKey { get; set; }

        #endregion

        /// <summary>
        /// Load page-views
        /// </summary>
        public async Task<PageViewCollection> GetPageViewsFromAppInsights(DateTime fromWhen, bool saveRestResponses)
        {
            // Request data from last 14 days to avoid default range. Some calls seem to default to 12 hours, so adding just in case.
            var tsString = GetTimeSpanText(DateTime.Now.AddDays(-14));

            // ...but only from the last hit timestamp
            var adxQuery = $"pageViews | where timestamp > todatetime('{fromWhen.ToString("yyyy-MM-dd HH:mm:ss")}') | order by timestamp asc";

            // API Doc: https://docs.microsoft.com/en-us/rest/api/application-insights/query/get
            var req = $"https://api.applicationinsights.io/v1/apps/{AppId}/query?query={adxQuery}&timespan={tsString}";
            var response = await client.GetAsync(req);

            var result = await HandleResponse<AppInsightsQueryResult>(response, saveRestResponses, "pageview");

            return new PageViewCollection(result.DefaultTable, fromWhen, this._debugTracer);
        }

        /// <summary>
        /// Load & process events into specific events we can use
        /// </summary>
        public async Task<CustomEventsResultCollection> GetCustomEventsFromAppInsights(DateTime fromWhen, bool saveRestResponses)
        {
            // Request data from last 14 days to avoid default range. Some calls seem to default to 12 hours, so adding just in case.
            var tsString = GetTimeSpanText(DateTime.Now.AddDays(-14));

            // ...but only from the last hit timestamp
            var adxQuery = $"customEvents | where timestamp > todatetime('{fromWhen.ToString("yyyy-MM-dd HH:mm:ss")}') | order by timestamp asc";

            // Doc: https://dev.applicationinsights.io/reference/get-events
            var req = $"https://api.applicationinsights.io/v1/apps/{AppId}/query?query={adxQuery}&timespan={tsString}";

            var resultsResponse = await client.GetAsync(req);

            var result = await HandleResponse<AppInsightsQueryResult>(resultsResponse, saveRestResponses, "event");

            return new CustomEventsResultCollection(result.DefaultTable, fromWhen, _debugTracer);
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

        string GetTimeSpanText(DateTime fromWhen)
        {
            if (fromWhen > DateTime.Now)
            {
                throw new ArgumentOutOfRangeException(nameof(fromWhen), "Invalid filter - must be < today");
            }
            var ts = DateTime.Now.Subtract(fromWhen);
            return System.Xml.XmlConvert.ToString(ts);
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}
