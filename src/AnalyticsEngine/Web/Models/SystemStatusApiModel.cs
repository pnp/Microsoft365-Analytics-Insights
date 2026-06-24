using Newtonsoft.Json;
using System;

namespace Web.AnalyticsWeb.Models
{
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

        [JsonProperty("hitCount")]
        public int HitCount { get; set; }

        [JsonProperty("activityCount")]
        public int ActivityCount { get; set; }

        [JsonProperty("teamsCount")]
        public int TeamsCount { get; set; }

        [JsonProperty("teamsBeingTrackedCount")]
        public int TeamsBeingTrackedCount { get; set; }

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

        [JsonProperty("configJson")]
        public string ConfigJson { get; set; }
    }
}
