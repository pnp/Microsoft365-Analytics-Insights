using Newtonsoft.Json;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports
{
    public class OutlookUserActivityUserDetail : AbstractUserActivityUserDetailWithUpn
    {

        [JsonProperty("sendCount")]
        public long SendCount { get; set; }


        [JsonProperty("receiveCount")]
        public long ReceiveCount { get; set; }


        [JsonProperty("readCount")]
        public long ReadCount { get; set; }

        [JsonProperty("meetingCreatedCount")]
        public long MeetingCreated { get; set; }

        [JsonProperty("meetingInteractedCount")]
        public long MeetingInteracted { get; set; }
    }

}
