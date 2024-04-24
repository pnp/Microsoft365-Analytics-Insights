using Newtonsoft.Json;
using System;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports
{
    public class OneDriveUserActivityDetail : AbstractUserActivityUserDetailWithUpn
    {

        [JsonProperty("viewedOrEditedFileCount")]
        public long ViewedOrEdited { get; set; }

        [JsonProperty("syncedFileCount")]
        public long Synced { get; set; }

        [JsonProperty("sharedInternallyFileCount")]
        public long SharedInternally { get; set; }

        [JsonProperty("sharedExternallyFileCount")]
        public long SharedExternally { get; set; }

        [JsonProperty("lastActivityDate")]
        public DateTime LastActivityDate { get; set; }
    }

}
