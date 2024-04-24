using Newtonsoft.Json;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports
{
    // https://docs.microsoft.com/en-us/graph/api/reportroot-getonedriveusageaccountdetail?view=graph-rest-beta
    public class OneDriveUsageDetail : AbstractUserActivityUserDetail
    {
        [JsonProperty("storageUsedInBytes")]
        public long StorageUsedInBytes { get; set; }

        [JsonProperty("activeFileCount")]
        public long ActiveFileCount { get; set; }

        [JsonProperty("fileCount")]
        public long FileCount { get; set; }

        [JsonProperty("ownerDisplayName")]
        public string OwnerDisplayName { get; set; }

        [JsonProperty("ownerPrincipalName")]
        public string OwnerPrincipalName { get; set; }

        public override string UserEmailFieldVal => this.OwnerPrincipalName;
    }

}
