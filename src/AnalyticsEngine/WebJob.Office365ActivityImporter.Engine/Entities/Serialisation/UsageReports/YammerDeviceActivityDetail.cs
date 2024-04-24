using Newtonsoft.Json;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports
{
    /// <summary>
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getyammerdeviceusageuserdetail?view=graph-rest-beta
    /// </summary>
    public class YammerDeviceActivityDetail : AbstractUserActivityUserDetailWithUpn
    {

        [JsonProperty("usedWeb")]
        public bool? UsedWeb { get; set; }

        [JsonProperty("usedWindowsPhone")]
        public bool? UsedWindowsPhone { get; set; }

        [JsonProperty("usedAndroidPhone")]
        public bool? UsedAndroidPhone { get; set; }

        [JsonProperty("usediPad")]
        public bool? UsedIpad { get; set; }

        [JsonProperty("usediPhone")]
        public bool? UsedIphone { get; set; }

        [JsonProperty("usedOthers")]
        public bool? UsedOthers { get; set; }
    }

}
