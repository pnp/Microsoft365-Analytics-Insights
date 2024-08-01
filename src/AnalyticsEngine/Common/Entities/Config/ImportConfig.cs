using Newtonsoft.Json;
using System;

namespace Common.Entities.Config
{
    public class ImportConfig
    {
        [JsonProperty(PropertyName = "expiry")]
        public DateTime Expiry { get; set; } = DateTime.MinValue;

        [JsonProperty(PropertyName = "metadataRefreshMinutes")]
        public int MetadataRefreshMinutes { get; set; }
    }
}
