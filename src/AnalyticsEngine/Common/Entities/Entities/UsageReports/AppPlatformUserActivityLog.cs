using Common.Entities.ActivityReports;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    // https://learn.microsoft.com/en-us/graph/api/reportroot-getm365appuserdetail?view=graph-rest-beta
    [Table("platform_user_activity_log")]
    public class AppPlatformUserActivityLog : UserRelatedAbstractUsageActivity
    {
        [Column("windows")]
        public bool Windows { get; set; }

        [Column("mac")]
        public bool Mac { get; set; }

        [Column("mobile")]
        public bool Mobile { get; set; }

        [Column("web")]
        public bool Web { get; set; }

        [Column("outlook")]
        public bool Outlook { get; set; }

        [Column("word")]
        public bool Word { get; set; }

        [Column("excel")]
        public bool Excel { get; set; }

        [Column("powerpoint")]
        public bool PowerPoint { get; set; }

        [Column("onenote")]
        public bool OneNote { get; set; }

        [Column("teams")]
        public bool Teams { get; set; }

        [Column("outlook_windows")]
        public bool OutlookWindows { get; set; }

        [Column("word_windows")]
        public bool WordWindows { get; set; }

        [Column("excel_windows")]
        public bool ExcelWindows { get; set; }

        [Column("powerpoint_windows")]
        public bool PowerPointWindows { get; set; }

        [Column("onenote_windows")]
        public bool OneNoteWindows { get; set; }

        [Column("teams_windows")]
        public bool TeamsWindows { get; set; }

        [Column("outlook_mac")]
        public bool OutlookMac { get; set; }

        [Column("word_mac")]
        public bool WordMac { get; set; }

        [Column("excel_mac")]
        public bool ExcelMac { get; set; }

        [Column("powerpoint_mac")]
        public bool PowerPointMac { get; set; }

        [Column("onenote_mac")]
        public bool OneNoteMac { get; set; }

        [Column("teams_mac")]
        public bool TeamsMac { get; set; }

        [Column("outlook_mobile")]
        public bool OutlookMobile { get; set; }

        [Column("word_mobile")]
        public bool WordMobile { get; set; }

        [Column("excel_mobile")]
        public bool ExcelMobile { get; set; }

        [Column("powerpoint_mobile")]
        public bool PowerPointMobile { get; set; }

        [Column("onenote_mobile")]
        public bool OneNoteMobile { get; set; }

        [Column("teams_mobile")]
        public bool TeamsMobile { get; set; }

        [Column("outlook_web")]
        public bool OutlookWeb { get; set; }

        [Column("word_web")]
        public bool WordWeb { get; set; }

        [Column("excel_web")]
        public bool ExcelWeb { get; set; }

        [Column("powerpoint_web")]
        public bool PowerPointWeb { get; set; }

        [Column("onenote_web")]
        public bool OneNoteWeb { get; set; }

        [Column("teams_web")]
        public bool TeamsWeb { get; set; }

    }
}
