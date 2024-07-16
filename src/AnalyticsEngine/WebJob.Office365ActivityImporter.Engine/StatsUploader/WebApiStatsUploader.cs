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
        private readonly HttpClient _httpClient;
        private readonly string _url;
        private readonly string _statsApiSecret;
        private readonly ILogger _debugTracer;

        public WebApiStatsUploader(string url, string statsApiSecret, ILogger debugTracer)
        {
            _httpClient = new HttpClient();
            _url = url;
            _statsApiSecret = statsApiSecret;
            _debugTracer = debugTracer;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
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
                    _debugTracer.LogInformation($"Uploaded stats to {_url}");
                }
                else
                {
                    _debugTracer.LogError($"Can't upload stats to API - server returned unexpected response {response.StatusCode}");
                }
            }
            else
            {
                _debugTracer.LogInformation($"Can't upload stats to API - invalid API configuration");
                throw new InvalidOperationException("Can't upload stats to API - invalid API configuration");
            }
        }
    }
}
