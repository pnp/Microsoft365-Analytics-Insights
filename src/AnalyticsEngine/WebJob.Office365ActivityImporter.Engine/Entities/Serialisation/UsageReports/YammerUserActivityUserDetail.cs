using Newtonsoft.Json;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports
{
    /// <summary>
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getyammeractivityuserdetail?view=graph-rest-beta
    /// </summary>
    public class YammerUserActivityUserDetail : AbstractUserActivityUserDetailWithUpn
    {
        [JsonProperty("postedCount")]
        public int? PostedCount { get; set; }

        [JsonProperty("readCount")]
        public int? ReadCount { get; set; }

        [JsonProperty("likedCount")]
        public int? LikedCount { get; set; }
    }
}
