using Newtonsoft.Json;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate
{
    /// <summary>
    /// https://learn.microsoft.com/en-us/graph/api/reportroot-getsharepointsiteusagedetail?view=graph-rest-beta#response-2
    /// </summary>
    public class SharePointSiteUsageDetail : BaseAggregateItemStats
    {
        [JsonProperty("siteId")]
        public string SiteId { get; set; }

        [JsonProperty("siteUrl")]
        public string SiteUrl { get; set; }

        [JsonProperty("isDeleted")]
        public bool IsDeleted { get; set; }

        [JsonProperty("siteSensitivityLabelId")]
        public string SiteSensitivityLabelId { get; set; }

        [JsonProperty("externalSharing")]
        public bool ExternalSharing { get; set; }

        [JsonProperty("unmanagedDevicePolicy")]
        public string UnmanagedDevicePolicy { get; set; }

        [JsonProperty("geolocation")]
        public string Geolocation { get; set; }

        [JsonProperty("fileCount")]
        public int FileCount { get; set; }

        [JsonProperty("activeFileCount")]
        public int ActiveFileCount { get; set; }

        [JsonProperty("pageViewCount")]
        public int PageViewCount { get; set; }

        [JsonProperty("visitedPageCount")]
        public int VisitedPageCount { get; set; }

        [JsonProperty("storageUsedInBytes")]
        public long StorageUsedInBytes { get; set; }

        [JsonProperty("storageAllocatedInBytes")]
        public long StorageAllocatedInBytes { get; set; }

        [JsonProperty("anonymousLinkCount")]
        public int AnonymousLinkCount { get; set; }

        [JsonProperty("companyLinkCount")]
        public int CompanyLinkCount { get; set; }

        [JsonProperty("secureLinkForGuestCount")]
        public int SecureLinkForGuestCount { get; set; }

        [JsonProperty("secureLinkForMemberCount")]
        public int SecureLinkForMemberCount { get; set; }

        public override string OfficeUniqueIdField => SiteId;
    }
}
