using Common.Entities.ActivityReports;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    /// <summary>
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getyammeractivityuserdetail?view=graph-rest-beta
    /// </summary>
    [Table("yammer_user_activity_log")]
    public class YammerUserActivityLog : UserRelatedAbstractUsageActivity
    {

        [Column("posted_count")]
        public int PostedCount { get; set; }

        [Column("read_count")]
        public int ReadCount { get; set; }

        [Column("liked_count")]
        public int LikedCount { get; set; }
    }

    /// <summary>
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getyammergroupsactivitydetail?view=graph-rest-beta
    /// </summary>
    [Table("yammer_group_activity_log")]
    public class YammerGroupActivityLog : AbstractUsageActivityLog
    {

        [Column("posted_count")]
        public int PostedCount { get; set; }

        [Column("read_count")]
        public int ReadCount { get; set; }

        [Column("liked_count")]
        public int LikedCount { get; set; }

        [Column("member_count")]
        public int MemberCount { get; set; }


        public YammerGroup YammerGroup { get; set; }

        [ForeignKey(nameof(YammerGroup))]
        [Column("yammer_group_id")]
        public int YammerGroupID { get; set; }

        public override int AssociatedLookupId { get => YammerGroupID; set => YammerGroupID = value; }
    }

    /// <summary>
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getyammerdeviceusageuserdetail?view=graph-rest-beta
    /// </summary>
    [Table("yammer_device_activity_log")]
    public class YammerDeviceActivityLog : UserRelatedAbstractUsageActivity
    {

        [Column("used_web")]
        public bool? UsedWeb { get; set; }

        [Column("used_win_phone")]
        public bool? UsedWindowsPhone { get; set; }

        [Column("used_android")]
        public bool? UsedAndroidPhone { get; set; }

        [Column("used_ipad")]
        public bool? UsedIpad { get; set; }


        [Column("used_iphone")]
        public bool? UsedIphone { get; set; }

        [Column("used_others")]
        public bool? UsedOthers { get; set; }

    }
}
