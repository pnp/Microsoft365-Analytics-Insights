using Newtonsoft.Json;
using System;

namespace Web.AnalyticsWeb.Models
{
    /// <summary>
    /// One row of the install log (the <c>sys_configs</c> table): a configuration applied to the
    /// solution at a point in time. The most recent entry is the current configuration.
    /// </summary>
    public class InstallLogEntryModel
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("dateApplied")]
        public DateTime DateApplied { get; set; }

        [JsonProperty("installedByUser")]
        public string InstalledByUser { get; set; }

        [JsonProperty("messages")]
        public string Messages { get; set; }

        [JsonProperty("configJson")]
        public string ConfigJson { get; set; }

        /// <summary>True for the most recently applied entry (the current configuration).</summary>
        [JsonProperty("isCurrent")]
        public bool IsCurrent { get; set; }
    }
}
