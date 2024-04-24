using Common.Entities;
using Common.Entities.Entities.UsageReports;
using Common.Entities.LookupCaches.Discrete;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate
{
    /// <summary>
    /// https://learn.microsoft.com/en-us/graph/api/reportroot-getSharePointSiteUsageDetail?view=graph-rest-beta
    /// </summary>
    public class SharePointSitesWeeklyUsageReportLoader : GraphAndSqlAggregateWeeklyUsageReportLoader<SharePointSiteUsageDetail>
    {
        private readonly SPSiteIdToUrlCache _sPSiteIdToUrlCache;
        private readonly SiteCache _siteCache;

        public SharePointSitesWeeklyUsageReportLoader(AnalyticsEntitiesContext db, ManualGraphCallClient client, ILogger telemetry, SPSiteIdToUrlCache sPSiteIdToUrlCache)
            : base(db, client, telemetry)
        {
            _sPSiteIdToUrlCache = sPSiteIdToUrlCache;
            _siteCache = new SiteCache(_context);
        }

        public override async Task<IEnumerable<SharePointSiteUsageDetail>> LoadReportData()
        {
            var usageReports = await base.LoadReportData();
            var filteredReports = new List<SharePointSiteUsageDetail>();
            foreach (var r in usageReports)
            {
                if (!string.IsNullOrEmpty(r.SiteId) && string.IsNullOrEmpty(r.SiteUrl))
                {
                    // No URL in results, despite the clear indication it should be there? Look it up in Graph
                    // Known issue: https://admin.microsoft.com/Adminportal/Home?#/servicehealth/:/alerts/SP676147
                    var urlLookupCache = await _sPSiteIdToUrlCache.GetResourceOrNullIfNotExists(r.SiteId);
                    if (urlLookupCache != null)
                    {
                        r.SiteUrl = urlLookupCache.SiteUrl;
                    }
                }

                // If we have a URL, add to list
                if (!string.IsNullOrEmpty(r.SiteUrl))
                {
                    filteredReports.Add(r);
                }
            }

            return filteredReports;
        }

        public override string ReportGraphURL => "https://graph.microsoft.com/beta/reports/getSharePointSiteUsageDetail(period='D7')?$format=application/json";

        public override string ReportName => "SharePoint Site Usage";

        protected override async Task<DateTime?> GetLastStoredResultFor(SharePointSiteUsageDetail item)
        {
            var latestLog = await _context.SharePointSiteStats.Where(s => s.Site.UrlBase == item.SiteUrl).OrderByDescending(s => s.ForWeekEnding).FirstOrDefaultAsync();
            if (latestLog != null)
            {
                return latestLog.ForWeekEnding;
            }
            return null;
        }

        protected override async Task CommitAllChanges()
        {
            await _context.SaveChangesAsync();
        }

        protected override async Task AddItemToSaveList(SharePointSiteUsageDetail item)
        {
            var site = await _siteCache.GetOrCreateNewResource(item.SiteUrl, new Site { UrlBase = item.SiteUrl });
            var newLog = new SharePointSitesFileWeeklyStats
            {
                Site = site,
                ForWeekEnding = item.ReportRefreshDate,
                ActiveFileCount = item.ActiveFileCount,
                AnonymousLinkCount = item.AnonymousLinkCount,
                ExternalSharing = item.ExternalSharing,
                CompanyLinkCount = item.CompanyLinkCount,
                FileCount = item.FileCount,
                PageViewCount = item.PageViewCount,
                SecureLinkForGuestCount = item.SecureLinkForGuestCount,
                SecureLinkForMemberCount = item.SecureLinkForMemberCount,
                StorageAllocatedInBytes = item.StorageAllocatedInBytes,
                StorageUsedInBytes = item.StorageUsedInBytes,
                VisitedPageCount = item.VisitedPageCount
            };

            _context.SharePointSiteStats.Add(newLog);
        }
    }
}
