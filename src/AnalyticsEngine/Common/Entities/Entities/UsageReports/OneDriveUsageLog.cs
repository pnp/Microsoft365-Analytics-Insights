using Common.Entities.ActivityReports;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{

    [Table("onedrive_usage_activity_log")]
    public class OneDriveUsageLog : UserRelatedAbstractUsageActivity
    {

        [Column("storage_used_bytes")]
        public long StorageUsedInBytes { get; set; }

        [Column("active_file_count")]
        public long ActiveFileCount { get; set; }

        [Column("file_count")]
        public long FileCount { get; set; }

    }
}
