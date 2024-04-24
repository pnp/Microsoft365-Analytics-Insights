using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.UsageReports
{
    public abstract class BaseAggregateWeeklyStats : AbstractEFEntity
    {
        [Column("week_ending")]
        public DateTime? ForWeekEnding { get; set; }
    }


    [Table("sharepoint_sites_file_stats_log")]
    public class SharePointSitesFileWeeklyStats : BaseAggregateWeeklyStats
    {
        [Column("site_id")]
        public int SiteId { get; set; }

        [ForeignKey(nameof(SiteId))]
        public Site Site { get; set; }

        [Column("external_sharing")]
        public bool ExternalSharing { get; set; }

        [Column("file_count")]
        public int FileCount { get; set; }

        [Column("active_file_count")]
        public int ActiveFileCount { get; set; }

        [Column("page_view_count")]
        public int PageViewCount { get; set; }

        [Column("visited_page_count")]
        public int VisitedPageCount { get; set; }

        [Column("storage_used_bytes")]
        public long StorageUsedInBytes { get; set; }

        [Column("storage_allocated_bytes")]
        public long StorageAllocatedInBytes { get; set; }

        [Column("anonymous_link_count")]
        public int AnonymousLinkCount { get; set; }

        [Column("company_link_count")]
        public int CompanyLinkCount { get; set; }

        [Column("secure_link_for_guest_count")]
        public int SecureLinkForGuestCount { get; set; }

        [Column("secure_link_for_member_count")]
        public int SecureLinkForMemberCount { get; set; }
    }
}
