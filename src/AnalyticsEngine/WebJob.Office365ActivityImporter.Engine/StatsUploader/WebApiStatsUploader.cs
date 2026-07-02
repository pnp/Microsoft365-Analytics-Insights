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

        public WebApiStatsUploader(string url, string statsApiSecret, ILogger logger)
        {
            _url = url;
            _statsApiSecret = statsApiSecret;
            _logger = logger;
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

                var response = await _httpClient.PostAsync(_url, content);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"Uploaded stats to {_url}");
                }
                else
                {
                    _logger.LogError($"Can't upload stats to API - server returned unexpected response {response.StatusCode}");
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
