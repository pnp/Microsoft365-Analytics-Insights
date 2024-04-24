using Newtonsoft.Json;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports
{
    public class TeamsUserActivityUserDetail : AbstractUserActivityUserDetailWithUpn
    {
        [JsonProperty("teamChatMessageCount")]
        public long TeamChatMessageCount { get; set; }

        [JsonProperty("privateChatMessageCount")]
        public long PrivateChatMessageCount { get; set; }

        [JsonProperty("callCount")]
        public long CallCount { get; set; }

        [JsonProperty("meetingCount")]
        public long MeetingCount { get; set; }

        [JsonProperty("postMessages")]
        public long PostMessages { get; set; }

        [JsonProperty("replyMessages")]
        public long ReplyMessages { get; set; }

        [JsonProperty("urgentMessages")]
        public long UrgentMessages { get; set; }

        [JsonProperty("meetingsOrganizedCount")]
        public long MeetingsOrganizedCount { get; set; }

        [JsonProperty("meetingsAttendedCount")]
        public long MeetingsAttendedCount { get; set; }

        [JsonProperty("adHocMeetingsOrganizedCount")]
        public long AdHocMeetingsOrganizedCount { get; set; }

        [JsonProperty("adHocMeetingsAttendedCount")]
        public long AdHocMeetingsAttendedCount { get; set; }

        [JsonProperty("scheduledOneTimeMeetingsOrganizedCount")]
        public long ScheduledOneTimeMeetingsOrganizedCount { get; set; }

        [JsonProperty("scheduledOneTimeMeetingsAttendedCount")]
        public long ScheduledOneTimeMeetingsAttendedCount { get; set; }

        [JsonProperty("scheduledRecurringMeetingsOrganizedCount")]
        public long ScheduledRecurringMeetingsOrganizedCount { get; set; }

        [JsonProperty("scheduledRecurringMeetingsAttendedCount")]
        public long ScheduledRecurringMeetingsAttendedCount { get; set; }

        [JsonProperty("audioDuration")]
        public string AudioDuration { get; set; }

        [JsonProperty("videoDuration")]
        public string VideoDuration { get; set; }

        [JsonProperty("screenShareDuration")]
        public string ScreenShareDuration { get; set; }
    }
}
