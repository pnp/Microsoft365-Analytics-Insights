using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models
{
    /// <summary>A named record count for one SQL table, shown in the home page overview.</summary>
    public class NamedCountModel
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }

        public NamedCountModel() { }

        public NamedCountModel(string name, int count)
        {
            Name = name;
            Count = count;
        }
    }

    /// <summary>
    /// JSON shape returned by api/SystemStatus and rendered by the SPA's Home page. Mirrors the
    /// fields the old server-rendered home page showed (kept deliberately flat / display-oriented).
    /// </summary>
    public class SystemStatusApiModel
    {
        [JsonProperty("buildLabel")]
        public string BuildLabel { get; set; }

        [JsonProperty("hasValidConfig")]
        public bool HasValidConfig { get; set; }

        /// <summary>Record counts for the main / interesting tables (home page overview).</summary>
        [JsonProperty("dataCounts")]
        public List<NamedCountModel> DataCounts { get; set; } = new List<NamedCountModel>();

        [JsonProperty("webhookEndpointUrl")]
        public string WebhookEndpointUrl { get; set; }

        [JsonProperty("callsImportEnabled")]
        public bool CallsImportEnabled { get; set; }

        /// <summary>Webhook subscription state as a string: Disabled | Active | Missing | Error.</summary>
        [JsonProperty("callWebhookState")]
        public string CallWebhookState { get; set; }

        [JsonProperty("callWebhookExpiry")]
        public DateTimeOffset? CallWebhookExpiry { get; set; }

        [JsonProperty("callWebhookStatusDetail")]
        public string CallWebhookStatusDetail { get; set; }

        [JsonProperty("webAppConfigSQL")]
        public string WebAppConfigSQL { get; set; }

        [JsonProperty("webAppConfigRedis")]
        public string WebAppConfigRedis { get; set; }

        [JsonProperty("webAppConfigCognitive")]
        public string WebAppConfigCognitive { get; set; }

        [JsonProperty("cognitiveServiceEnabled")]
        public bool CognitiveServiceEnabled { get; set; }

        [JsonProperty("webAppConfigServiceBus")]
        public string WebAppConfigServiceBus { get; set; }
    }
}
