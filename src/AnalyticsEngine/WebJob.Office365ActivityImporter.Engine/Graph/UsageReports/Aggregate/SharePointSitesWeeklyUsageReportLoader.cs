using Common.Entities;
using Common.Entities.Entities.UsageReports;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate
{
    /// <summary>
    /// https://learn.microsoft.com/en-us/graph/api/reportroot-getSharePointSiteUsageDetail?view=graph-rest-beta
    /// </summary>
    public class SharePointSitesWeeklyUsageReportLoader : GraphAndSqlAggregateWeeklyUsageReportLoader<SharePointSiteUsageDetail>
    {
        private readonly SPSiteIdToUrlCache _sPSiteIdToUrlCache;
        private readonly ISharePointSiteUsageStore _store;

        // Bulk-loaded once in BeginSaveAsync so the per-site save loop needs no DB round-trips:
        private Dictionary<string, DateTime?> _lastStoredWeekByUrl;   // existing latest week_ending per site URL
        private Dictionary<string, int> _existingSiteIdByUrl;         // existing site PK per site URL
        private Dictionary<string, Site> _newSitesByUrl;              // sites created during this run (reused for repeats)

        public SharePointSitesWeeklyUsageReportLoader(AnalyticsEntitiesContext db, ManualGraphCallClient client, ILogger logger, SPSiteIdToUrlCache sPSiteIdToUrlCache)
            : this(new SqlSharePointSiteUsageStore(db), client, logger, sPSiteIdToUrlCache)
        {
        }

        /// <summary>
        /// As above, with storage supplied (issue #375). The context-taking constructor is kept as a
        /// delegating overload so no call site changes.
        /// </summary>
        public SharePointSitesWeeklyUsageReportLoader(ISharePointSiteUsageStore store, ManualGraphCallClient client, ILogger logger, SPSiteIdToUrlCache sPSiteIdToUrlCache)
            : base(client, logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _sPSiteIdToUrlCache = sPSiteIdToUrlCache;
        }

        public override async Task<IEnumerable<SharePointSiteUsageDetail>> LoadReportData()
        {
            var usageReports = await base.LoadReportData();
            var filteredReports = new List<SharePointSiteUsageDetail>();
            foreach (var r in usageReports)
            {
                if (!string.IsNullOrEmpty(r.SiteId) && string.IsNullOrEmpty(r.SiteUrl) && !r.IsDeleted)
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

        /// <summary>
        /// One-time bulk pre-load so the save loop does zero per-site DB round-trips, and disable EF change
        /// auto-detection so adding tens of thousands of rows to the context stays O(n) instead of O(n^2).
        /// </summary>
        protected override async Task BeginSaveAsync(IReadOnlyList<SharePointSiteUsageDetail> allItems)
        {
            // All known sites (lightweight projection, not tracked) keyed by URL.
            var existingSites = await _store.GetAllSitesAsync();

            _existingSiteIdByUrl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var urlBySiteId = new Dictionary<int, string>();
            foreach (var s in existingSites)
            {
                // A site with no URL cannot be keyed by one. Dropping it here is what keeps "no URL"
                // and "no site" from collapsing into the same answer further down.
                if (!string.IsNullOrEmpty(s.UrlBase))
                {
                    _existingSiteIdByUrl[s.UrlBase] = s.Id;
                    urlBySiteId[s.Id] = s.UrlBase;
                }
            }

            // Latest stored week per site in ONE grouped query (replaces the per-site top-1 query that
            // previously ran once per site - the dominant cost of this import at ~1 query/site).
            var latestPerSite = await _store.GetLatestStoredWeekPerSiteAsync();

            _lastStoredWeekByUrl = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in latestPerSite)
            {
                if (urlBySiteId.TryGetValue(row.SiteId, out var url))
                {
                    // Keep the greatest week across any sites resolving to the same URL (matches the previous
                    // OrderByDescending(ForWeekEnding).First() semantics).
                    if (!_lastStoredWeekByUrl.TryGetValue(url, out var existing)
                        || (row.LatestWeekEnding.HasValue && (!existing.HasValue || row.LatestWeekEnding.Value > existing.Value)))
                    {
                        _lastStoredWeekByUrl[url] = row.LatestWeekEnding;
                    }
                }
            }

            _newSitesByUrl = new Dictionary<string, Site>(StringComparer.OrdinalIgnoreCase);
            _store.BeginBulkWrite();
        }

        protected override Task EndSaveAsync()
        {
            // Always restore, even when nothing was saved, since the context may be reused.
            _store.EndBulkWrite();
            return Task.CompletedTask;
        }

        protected override Task<DateTime?> GetLastStoredResultFor(SharePointSiteUsageDetail item)
        {
            DateTime? lastStored = null;
            if (!string.IsNullOrEmpty(item.SiteUrl) && _lastStoredWeekByUrl != null)
            {
                _lastStoredWeekByUrl.TryGetValue(item.SiteUrl, out lastStored);
            }
            return Task.FromResult(lastStored);
        }

        protected override Task CommitAllChanges() => _store.CommitAsync();

        protected override Task AddItemToSaveList(SharePointSiteUsageDetail item)
        {
            var newLog = new SharePointSitesFileWeeklyStats
            {
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

            if (_existingSiteIdByUrl.TryGetValue(item.SiteUrl, out var existingSiteId))
            {
                // Existing site: set the FK directly, no need to load/track the Site entity.
                newLog.SiteId = existingSiteId;
            }
            else if (_newSitesByUrl.TryGetValue(item.SiteUrl, out var newSite))
            {
                // A site we already created earlier in this same run.
                newLog.Site = newSite;
            }
            else
            {
                // Brand-new site: add it (tracked) and attach via navigation so EF inserts it and fills the FK.
                var site = new Site { UrlBase = item.SiteUrl };
                _store.AddSite(site);
                _newSitesByUrl[item.SiteUrl] = site;
                newLog.Site = site;
            }

            _store.AddWeeklyStats(newLog);
            return Task.CompletedTask;
        }
    }
}
