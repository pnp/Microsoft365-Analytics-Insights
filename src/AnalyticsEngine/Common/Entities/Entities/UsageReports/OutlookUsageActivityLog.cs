using Common.Entities.ActivityReports;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    /// <summary>
    /// What a user has been upto in Outlook overrall, on a given date
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getemailactivityuserdetail?view=graph-rest-beta
    /// </summary>
    [Table("outlook_user_activity_log")]
    public class OutlookUsageActivityLog : UserRelatedAbstractUsageActivity
    {

        [Column("email_send_count")]
        public long SendCount { get; set; }


        [Column("email_receive_count")]
        public long ReceiveCount { get; set; }


        [Column("email_read_count")]
        public long ReadCount { get; set; }

        [Column("meeting_created_count")]
        public long MeetingCreated { get; set; }

        [Column("meeting_interacted_count")]
        public long MeetingInteracted { get; set; }

    }
}
