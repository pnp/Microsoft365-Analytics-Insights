using Common.Entities.ActivityReports;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    /// <summary>
    /// What a user has been upto in Teams overrall, on a given date
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getteamsuseractivityuserdetail?view=graph-rest-beta#response
    /// </summary>
    [Table("teams_user_activity_log")]
    public class GlobalTeamsUserUsageLog : UserRelatedAbstractUsageActivity
    {

        [Column("private_chat_count")]
        public long PrivateChatMessageCount { get; set; }

        [Column("team_chat_count")]
        public long TeamChatMessageCount { get; set; }

        [Column("calls_count")]
        public long CallCount { get; set; }

        [Column("meetings_count")]
        public long MeetingCount { get; set; }

        [Column("adhoc_meetings_attended_count")]
        public long AdHocMeetingsAttendedCount { get; set; }

        [Column("adhoc_meetings_organized_count")]
        public long AdHocMeetingsOrganizedCount { get; set; }

        [Column("meetings_attended_count")]
        public long MeetingsAttendedCount { get; set; }

        [Column("meetings_organized_count")]
        public long MeetingsOrganizedCount { get; set; }

        [Column("scheduled_onetime_meetings_attended_count")]
        public long ScheduledOneTimeMeetingsAttendedCount { get; set; }

        [Column("scheduled_onetime_meetings_organized_count")]
        public long ScheduledOneTimeMeetingsOrganizedCount { get; set; }

        [Column("scheduled_recurring_meetings_attended_count")]
        public long ScheduledRecurringMeetingsAttendedCount { get; set; }

        [Column("scheduled_recurring_meetings_organized_count")]
        public long ScheduledRecurringMeetingsOrganizedCount { get; set; }

        [Column("audio_duration_seconds")]
        public int AudioDurationSeconds { get; set; }

        [Column("video_duration_seconds")]
        public int VideoDurationSeconds { get; set; }

        [Column("screenshare_duration_seconds")]
        public int ScreenShareDurationSeconds { get; set; }

        /// <summary>
        /// The number of post messages in all channels during the specified time period.
        /// </summary>
        [Column("post_messages")]
        public long PostMessages { get; set; }

        [Column("reply_messages")]
        public long ReplyMessages { get; set; }

        [Column("urgent_messages")]
        public long UrgentMessages { get; set; }
    }

    /// <summary>
    /// What a user has been upto in Teams overrall, on a given date
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getteamsuseractivityuserdetail?view=graph-rest-beta#response
    /// </summary>
    [Table("teams_user_device_usage_log")]
    public class GlobalTeamsUserDeviceUsageLog : UserRelatedAbstractUsageActivity
    {

        [Column("used_web")]
        public bool? UsedWeb { get; set; }

        [Column("used_win_phone")]
        public bool? UsedWindowsPhone { get; set; }


        [Column("used_linux")]
        public bool? UsedLinux { get; set; }


        [Column("used_chrome_os")]
        public bool? UsedChromeOS { get; set; }

        [Column("used_ios")]
        public bool? UsedIOS { get; set; }

        [Column("used_android")]
        public bool? UsedAndroidPhone { get; set; }

        [Column("used_mac")]
        public bool? UsedMac { get; set; }

        [Column("used_windows")]
        public bool? UsedWindows { get; set; }
    }
}
