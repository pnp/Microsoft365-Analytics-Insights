using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UsageReporting;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    public class WebApiStatsUploader : IStatsUploader, IDisposable
    {
        // Shared HttpClient so the socket pool is reused across instances. Per-instance HttpClient
        // is the classic socket-exhaustion pattern; even though this class is rarely instantiated
        // there is no per-instance state on the client itself.
        private static readonly HttpClient _httpClient = new HttpClient();

        private readonly string _url;
        private readonly string _statsApiSecret;
        private readonly ILogger _logger;
        private readonly HttpClient _client;

        public WebApiStatsUploader(string url, string statsApiSecret, ILogger logger) : this(url, statsApiSecret, logger, _httpClient)
        {
        }

        /// <summary>
        /// Test seam: lets unit tests supply a client with a stub handler so the non-2xx behaviour can be
        /// verified without a live endpoint. Production always uses the shared static client.
        /// </summary>
        internal WebApiStatsUploader(string url, string statsApiSecret, ILogger logger, HttpClient client)
        {
            _url = url;
            _statsApiSecret = statsApiSecret;
            _logger = logger;
            _client = client;
        }

        public void Dispose()
        {
            // _httpClient is intentionally static and shared; do not dispose here.
        }

        public async Task UploadToServer(AnonUsageStatsModel stats)
        {
            if (!string.IsNullOrEmpty(_url) && !(string.IsNullOrEmpty(_statsApiSecret)))
            {
                var body = new TelemetryPayload(stats, _statsApiSecret);
                var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

                var response = await _client.PostAsync(_url, content);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"Uploaded stats to {_url}");
                }
                else
                {
                    // Must throw: the caller only registers the "last uploaded" date - which gates the next
                    // upload for a whole day - when this method completes without error. Returning normally on a
                    // rejected upload would silently drop the report and not retry until the next day.
                    _logger.LogError($"Can't upload stats to API - server returned unexpected response {(int)response.StatusCode} ({response.StatusCode})");
                    response.EnsureSuccessStatusCode();
                }
            }
            else
            {
                _logger.LogInformation($"Can't upload stats to API - invalid API configuration");
                throw new InvalidOperationException("Can't upload stats to API - invalid API configuration");
            }
        }
    }
}
