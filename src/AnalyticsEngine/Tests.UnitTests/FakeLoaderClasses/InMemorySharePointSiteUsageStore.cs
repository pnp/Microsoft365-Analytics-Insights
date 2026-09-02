using Common.Entities;
using Common.Entities.Entities.UsageReports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="ISharePointSiteUsageStore"/> - the sites and weekly-stats tables replaced by
    /// lists, so the weekly SharePoint save loop (day-of-week gate, "only newer than stored", existing-site
    /// FK reuse, new-site creation and reuse within one run) can be asserted with no SQL Server. See #375.
    ///
    /// <para>
    /// Two things it mirrors on purpose:
    /// </para>
    /// <list type="bullet">
    /// <item>Site keys are assigned ON COMMIT, not on add - so does EF, and a test that read the key
    /// before the commit would be testing the fake rather than the loader.</item>
    /// <item>A stats row added with only its <c>Site</c> navigation set gets its <c>SiteId</c> filled from
    /// that site's newly-assigned key at commit, which is what EF does with the FK.</item>
    /// </list>
    ///
    /// <para>
    /// Note what it does NOT do: it does not match sites by URL. The case-insensitive URL matching lives
    /// in the loader's own dictionaries, so a fake that also matched by URL could hide a loader that had
    /// stopped doing it.
    /// </para>
    /// </summary>
    public class InMemorySharePointSiteUsageStore : ISharePointSiteUsageStore
    {
        private readonly List<Site> _sites = new List<Site>();
        private readonly List<SharePointSitesFileWeeklyStats> _stats = new List<SharePointSitesFileWeeklyStats>();
        private readonly List<Site> _pendingSites = new List<Site>();
        private readonly List<SharePointSitesFileWeeklyStats> _pendingStats = new List<SharePointSitesFileWeeklyStats>();

        private int _nextSiteId = 1;

        /// <summary>Committed sites, in insertion order.</summary>
        public IReadOnlyList<Site> Sites => _sites;

        /// <summary>Committed weekly stats rows, in insertion order.</summary>
        public IReadOnlyList<SharePointSitesFileWeeklyStats> WeeklyStats => _stats;

        /// <summary>How many times the whole-sites read ran. Must be once per save, never once per site.</summary>
        public int SitesReadCount { get; private set; }

        /// <summary>How many times the grouped latest-week read ran. Must be once per save.</summary>
        public int LatestWeekReadCount { get; private set; }

        /// <summary>How many times the loader committed.</summary>
        public int CommitCount { get; private set; }

        public int BulkWriteBegun { get; private set; }
        public int BulkWriteEnded { get; private set; }

        /// <summary>True while a bulk-write scope is open.</summary>
        public bool BulkWriteOpen { get; private set; }

        /// <summary>Seed a committed site and return it (with its assigned key).</summary>
        public Site SeedSite(string urlBase)
        {
            var site = new Site { ID = _nextSiteId++, UrlBase = urlBase };
            _sites.Add(site);
            return site;
        }

        /// <summary>Seed a committed weekly-stats row for an already-seeded site.</summary>
        public InMemorySharePointSiteUsageStore SeedWeek(Site site, DateTime? weekEnding)
        {
            _stats.Add(new SharePointSitesFileWeeklyStats { Site = site, SiteId = site.ID, ForWeekEnding = weekEnding });
            return this;
        }

        public Task<IReadOnlyList<StoredSite>> GetAllSitesAsync()
        {
            SitesReadCount++;
            IReadOnlyList<StoredSite> sites = _sites.Select(s => new StoredSite(s.ID, s.UrlBase)).ToList();
            return Task.FromResult(sites);
        }

        public Task<IReadOnlyList<SiteLatestStoredWeek>> GetLatestStoredWeekPerSiteAsync()
        {
            LatestWeekReadCount++;
            IReadOnlyList<SiteLatestStoredWeek> latest = _stats
                .GroupBy(s => s.SiteId)
                .Select(g => new SiteLatestStoredWeek(g.Key, g.Max(s => s.ForWeekEnding)))
                .ToList();
            return Task.FromResult(latest);
        }

        public void AddSite(Site site) => _pendingSites.Add(site);

        public void AddWeeklyStats(SharePointSitesFileWeeklyStats stats) => _pendingStats.Add(stats);

        public Task CommitAsync()
        {
            CommitCount++;

            foreach (var site in _pendingSites)
            {
                site.ID = _nextSiteId++;
                _sites.Add(site);
            }
            _pendingSites.Clear();

            foreach (var stats in _pendingStats)
            {
                // EF fills the FK from the attached principal; do the same so SiteId is assertable.
                if (stats.Site != null)
                {
                    stats.SiteId = stats.Site.ID;
                }
                _stats.Add(stats);
            }
            _pendingStats.Clear();

            return Task.CompletedTask;
        }

        public void BeginBulkWrite()
        {
            BulkWriteBegun++;
            BulkWriteOpen = true;
        }

        public void EndBulkWrite()
        {
            BulkWriteEnded++;
            BulkWriteOpen = false;
        }
    }
}
