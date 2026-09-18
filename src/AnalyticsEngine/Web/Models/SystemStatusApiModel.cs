using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models
{
    /// <summary>A named record count for one SQL table, shown in the home page overview.</summary>
    public class NamedCountModel
    {
        /// <summary>
        /// Stable identifier for the figure (e.g. "auditEvents"). The SPA keys its icon off this, so it
        /// must not change with the display name - renaming <see cref="Name"/> is a copy edit, renaming
        /// this is a breaking change.
        /// </summary>
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }

        /// <summary>One short line saying where the number comes from, shown under the figure.</summary>
        [JsonProperty("hint")]
        public string Hint { get; set; }

        public NamedCountModel() { }

        public NamedCountModel(string key, string name, int count, string hint = null)
        {
            Key = key;
            Name = name;
            Count = count;
            Hint = hint;
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

        /// <summary>
        /// Record counts for the main / interesting tables (home page overview). Only the figures that
        /// belong to an import this deployment actually runs are included - a tenant that never turned on
        /// the Teams calls import has no business being shown a permanent "Teams calls: 0".
        /// </summary>
        [JsonProperty("dataCounts")]
        public List<NamedCountModel> DataCounts { get; set; } = new List<NamedCountModel>();

        /// <summary>
        /// Friendly names of the imports switched on for this deployment, in a stable display order.
        /// Same labels as the Health page's Configuration section (they share one map, so they can't drift).
        /// </summary>
        [JsonProperty("enabledImports")]
        public List<string> EnabledImports { get; set; } = new List<string>();

        /// <summary>
        /// False when the deployment's import settings could not be read, in which case
        /// <see cref="DataCounts"/> falls back to every figure rather than silently hiding them all.
        /// </summary>
        [JsonProperty("importSettingsKnown")]
        public bool ImportSettingsKnown { get; set; }

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
