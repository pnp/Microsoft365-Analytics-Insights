using Newtonsoft.Json;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports
{
    /// <summary>
    /// https://learn.microsoft.com/en-us/graph/api/reportroot-getm365appuserdetail?view=graph-rest-beta
    /// </summary>
    public partial class AppPlatformUserActivityDetail : AbstractUserActivityUserDetailWithUpn
    {
        [JsonProperty("details")]
        public List<AppPlatformUserActivityDetailItems> Details { get; set; }
    }

    public partial class AppPlatformUserActivityDetailItems
    {
        [JsonProperty("windows")]
        public bool? Windows { get; set; }

        [JsonProperty("mac")]
        public bool? Mac { get; set; }

        [JsonProperty("mobile")]
        public bool? Mobile { get; set; }

        [JsonProperty("web")]
        public bool? Web { get; set; }

        [JsonProperty("outlook")]
        public bool? Outlook { get; set; }

        [JsonProperty("word")]
        public bool? Word { get; set; }

        [JsonProperty("excel")]
        public bool? Excel { get; set; }

        [JsonProperty("powerPoint")]
        public bool? PowerPoint { get; set; }

        [JsonProperty("oneNote")]
        public bool? OneNote { get; set; }

        [JsonProperty("teams")]
        public bool? Teams { get; set; }

        [JsonProperty("outlookWindows")]
        public bool? OutlookWindows { get; set; }

        [JsonProperty("wordWindows")]
        public bool? WordWindows { get; set; }

        [JsonProperty("excelWindows")]
        public bool? ExcelWindows { get; set; }

        [JsonProperty("powerPointWindows")]
        public bool? PowerPointWindows { get; set; }

        [JsonProperty("oneNoteWindows")]
        public bool? OneNoteWindows { get; set; }

        [JsonProperty("teamsWindows")]
        public bool? TeamsWindows { get; set; }

        [JsonProperty("outlookMac")]
        public bool? OutlookMac { get; set; }

        [JsonProperty("wordMac")]
        public bool? WordMac { get; set; }

        [JsonProperty("excelMac")]
        public bool? ExcelMac { get; set; }

        [JsonProperty("powerPointMac")]
        public bool? PowerPointMac { get; set; }

        [JsonProperty("oneNoteMac")]
        public bool? OneNoteMac { get; set; }

        [JsonProperty("teamsMac")]
        public bool? TeamsMac { get; set; }

        [JsonProperty("outlookMobile")]
        public bool? OutlookMobile { get; set; }

        [JsonProperty("wordMobile")]
        public bool? WordMobile { get; set; }

        [JsonProperty("excelMobile")]
        public bool? ExcelMobile { get; set; }

        [JsonProperty("powerPointMobile")]
        public bool? PowerPointMobile { get; set; }

        [JsonProperty("oneNoteMobile")]
        public bool? OneNoteMobile { get; set; }

        [JsonProperty("teamsMobile")]
        public bool? TeamsMobile { get; set; }

        [JsonProperty("outlookWeb")]
        public bool? OutlookWeb { get; set; }

        [JsonProperty("wordWeb")]
        public bool? WordWeb { get; set; }

        [JsonProperty("excelWeb")]
        public bool? ExcelWeb { get; set; }

        [JsonProperty("powerPointWeb")]
        public bool? PowerPointWeb { get; set; }

        [JsonProperty("oneNoteWeb")]
        public bool? OneNoteWeb { get; set; }

        [JsonProperty("teamsWeb")]
        public bool? TeamsWeb { get; set; }
    }
}
