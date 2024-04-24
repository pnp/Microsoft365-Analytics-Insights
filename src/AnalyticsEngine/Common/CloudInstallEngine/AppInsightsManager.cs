using Azure.Core;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CloudInstallEngine
{
    /// <summary>
    /// Does things manually for App Insights the SDK doesn't support yet. Manage API access, get app details, etc
    /// </summary>
    public class AppInsightsManager
    {
        private HttpClient _httpClient;
        public AppInsightsManager(TokenCredential creds, string subscriptionID, string resourceGroupName, string aiComponentName)
        {
            if (string.IsNullOrEmpty(subscriptionID))
            {
                throw new ArgumentException($"'{nameof(subscriptionID)}' cannot be null or empty.", nameof(subscriptionID));
            }

            if (string.IsNullOrEmpty(resourceGroupName))
            {
                throw new ArgumentException($"'{nameof(resourceGroupName)}' cannot be null or empty.", nameof(resourceGroupName));
            }

            if (string.IsNullOrEmpty(aiComponentName))
            {
                throw new ArgumentException($"'{nameof(aiComponentName)}' cannot be null or empty.", nameof(aiComponentName));
            }
            Creds = creds;
            SubscriptionID = subscriptionID;
            ResourceGroupName = resourceGroupName;
            AppInsightsComponentName = aiComponentName;
            _httpClient = new HttpClient();
        }

        public TokenCredential Creds { get; }
        public string SubscriptionID { get; set; }
        public string ResourceGroupName { get; set; }
        public string AppInsightsComponentName { get; set; }
        public async Task<AdddedAppInsightsApiResult> EnsureKey(string keyName, ILogger logger)
        {
            var appInsightsInstanceAzureUrl = $"/subscriptions/{SubscriptionID}/resourcegroups/{ResourceGroupName}/providers/Microsoft.insights/components/{AppInsightsComponentName}";
            var appInsightsGetKeysInstanceFullUrl = $"https://management.azure.com{appInsightsInstanceAzureUrl}/apikeys?api-version=2020-02-02";

            // Figure out what keys there are
            var getKeysRequest = new HttpRequestMessage(HttpMethod.Get, appInsightsGetKeysInstanceFullUrl);
            var keysResponse = await ProcessReq(getKeysRequest);
            var keysResponseText = await keysResponse.Content.ReadAsStringAsync();
            var createNewKey = true;

            if (keysResponse.IsSuccessStatusCode)
            {
                var allKeys = JsonConvert.DeserializeObject<AppInsightsApiKeysList>(keysResponseText);
                foreach (var key in allKeys.Keys)
                {
                    if (key.Name == keyName)
                    {
                        var deleteUrl = $"https://management.azure.com{key.Id}?api-version=2020-02-02";
                        var deleteKeyRequest = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
                        var deleteResponse = await ProcessReq(deleteKeyRequest);
                        var deleteResponseText = await deleteResponse.Content.ReadAsStringAsync();
                        if (!deleteResponse.IsSuccessStatusCode)
                        {
                            logger.LogError($"Unexpected server error '{deleteResponse.StatusCode}' deleting old AppInsights API key. Response body: '{deleteResponseText}'");
                            createNewKey = false;
                        }
                        else
                        {
                            logger.LogInformation("Found & deleted old AppInsights API key");
                        }
                    }
                }
            }

            // Add new
            if (createNewKey)
            {
                var addKeyRequest = new HttpRequestMessage(HttpMethod.Post, appInsightsGetKeysInstanceFullUrl);
                string requestBodyContent = @"{""name"":""" + keyName + @""",""linkedReadProperties"":[""" + appInsightsInstanceAzureUrl + @"/api""],""linkedWriteProperties"":[]}";
                addKeyRequest.Content = new StringContent(requestBodyContent, Encoding.UTF8, "application/json");
                var addKeyResponse = await ProcessReq(addKeyRequest);
                var addKeyResponseText = await addKeyResponse.Content.ReadAsStringAsync();

                if (!addKeyResponse.IsSuccessStatusCode)
                {
                    logger.LogError($"Unexpected server error '{addKeyResponse.StatusCode}' adding AppInsights API key. Response body: '{addKeyResponseText}'");
                }
                else
                {
                    logger.LogInformation($"New AppInsights API key created.");
                    var newKeyDetails = JsonConvert.DeserializeObject<AdddedAppInsightsApiResult>(addKeyResponseText);
                    return newKeyDetails;
                }
            }

            return null;
        }

        public async Task<AppInsightsInstanceProperties> GetAppInsightsInstanceProperties(ILogger logger)
        {
            var appInsightsInstanceAzureUrl = $"/subscriptions/{SubscriptionID}/resourcegroups/{ResourceGroupName}/providers/Microsoft.insights/components/{AppInsightsComponentName}";

            var appInsightsGetInstanceFullUrl = $"https://management.azure.com{appInsightsInstanceAzureUrl}?api-version=2020-02-02";
            var getInstanceRequest = new HttpRequestMessage(HttpMethod.Get, appInsightsGetInstanceFullUrl);
            var instanceResponse = await ProcessReq(getInstanceRequest);
            var isntanceResponseText = await instanceResponse.Content.ReadAsStringAsync();

            if (instanceResponse.IsSuccessStatusCode)
            {
                var instanceInfo = JsonConvert.DeserializeObject<AppInsightsInstance>(isntanceResponseText);
                return instanceInfo.Properties;
            }

            logger.LogInformation("Couldn't get AppInsights application info", true);
            instanceResponse.EnsureSuccessStatusCode();
            return null;        // Won't get here if HTTP error
        }

        async Task<HttpResponseMessage> ProcessReq(HttpRequestMessage addKeyRequest)
        {
            var token = await Creds.GetTokenAsync(new TokenRequestContext(new string[] { "https://management.azure.com/.default" }), CancellationToken.None);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            var response = await _httpClient.SendAsync(addKeyRequest, HttpCompletionOption.ResponseHeadersRead, new CancellationToken());
            return response;
        }


        public class AppInsightsApiKey
        {
            [JsonProperty("id")]
            public string Id { get; set; }
            [JsonProperty("name")]
            public string Name { get; set; }
            [JsonProperty("linkedReadProperties")]
            public List<string> LinkedReadProperties { get; set; }
            [JsonProperty("linkedWriteProperties")]
            public List<object> LinkedWriteProperties { get; set; }
            [JsonProperty("createdDate")]
            public DateTime CreatedDate { get; set; }
        }

        public class AppInsightsApiKeysList
        {
            [JsonProperty("value")]
            public List<AppInsightsApiKey> Keys { get; set; }
        }


        public class AppInsightsInstance
        {
            [JsonProperty("properties")]
            public AppInsightsInstanceProperties Properties { get; set; }
        }

        public class AppInsightsInstanceProperties
        {
            [JsonProperty("InstrumentationKey")]
            public string InstrumentationKey { get; set; }


            [JsonProperty("AppId")]
            public string AppId { get; set; }

            [JsonProperty("ConnectionString")]
            public string ConnectionString { get; set; }
        }


        public class AdddedAppInsightsApiResult
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("linkedReadProperties")]
            public List<string> LinkedReadProperties { get; set; }

            [JsonProperty("linkedWriteProperties")]
            public List<object> LinkedWriteProperties { get; set; }

            [JsonProperty("apiKey")]
            public string ApiKey { get; set; }

            [JsonProperty("createdDate")]
            public DateTime createdDate { get; set; }
        }
    }
}
