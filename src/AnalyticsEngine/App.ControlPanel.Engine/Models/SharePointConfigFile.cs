// To parse this JSON data, add NuGet 'Newtonsoft.Json' then do:
//
//    using QuickType;
//
//    var configFile = ConfigFile.FromJson(jsonString);

namespace App.ControlPanel.Engine
{

    using System.Globalization;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;

    public partial class SharePointConfigFile
    {
        [JsonProperty("EnvironmentName")]
        public string EnvironmentName { get; set; }

        [JsonProperty("Description")]
        public string Description { get; set; }

        [JsonProperty("AdminUsername")]
        public string AdminUsername { get; set; }

        [JsonProperty("TargetSites")]
        public string[] TargetSites { get; set; }

        [JsonProperty("ApplicationInsightsKey")]
        public string ApplicationInsightsKey { get; set; }

        [JsonProperty("SourceFileRelativeDestination")]
        public string SourceFileRelativeDestination { get; set; }

        [JsonProperty("SPOInsightDocLibTitle")]
        public string SpoInsightDocLibTitle { get; set; }
    }

    public partial class SharePointConfigFile
    {
        public static SharePointConfigFile FromJson(string json) => JsonConvert.DeserializeObject<SharePointConfigFile>(json, Converter.Settings);
    }

    public static class Serialize
    {
        public static string ToJson(this SharePointConfigFile self) => JsonConvert.SerializeObject(self, Converter.Settings);
    }

    internal static class Converter
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            Converters = {
                new IsoDateTimeConverter { DateTimeStyles = DateTimeStyles.AssumeUniversal }
            },
        };
    }
}
