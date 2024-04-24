using Common.DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// For where there's no support for a Graph call resource in SDK
    /// </summary>
    public class ManualGraphCallClient : ConfidentialClientApplicationThrottledHttpClient
    {
        #region Constructors

        public ManualGraphCallClient(HttpMessageHandler server, ILogger debugTracer) : base(server, debugTracer)
        {
        }
        public ManualGraphCallClient(ImportAppIndentityOAuthContext appIndentity, ILogger debugTracer) : base(appIndentity, false, debugTracer)
        {
        }
        #endregion

        public async Task<T> GetAsyncWithThrottleRetries<T>(string url)
        {
            return await this.GetAsyncWithThrottleRetries<T>(url, null);
        }
        public async Task<T> GetAsyncWithThrottleRetries<T>(string url, Action<string> jsonStringAction)
        {
            var callResponse = await this.GetAsyncWithThrottleRetries(url, base._logger);
            var callResponseBody = await callResponse.Content.ReadAsStringAsync();

            try
            {
                callResponse.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, $"Got HTTP exception calling {url}: {ex.Message}. Response body: {callResponseBody}");
                throw;
            }

            // Get call
            T dto = default(T);
            try
            {
                dto = JsonConvert.DeserializeObject<T>(callResponseBody);
            }
            catch (JsonReaderException ex)
            {
                _logger.LogError(ex, $"Failed to deserialize JSON response from {url} with error '{ex.Message}'. Response body: {callResponseBody}");
            }

            jsonStringAction?.Invoke(callResponseBody);
            return dto;
        }
    }
}
