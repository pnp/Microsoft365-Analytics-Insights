using Common.Entities;
using Common.Entities.Entities.UsageReports;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate
{
    /// <summary>A site row as the weekly SharePoint usage import needs it: just the key and the URL.</summary>
    /// <remarks>
    /// Settable properties and a parameterless constructor on purpose: this is what lets the EF adapter
    /// project straight into it (<c>Select(s =&gt; new StoredSite { ... })</c>) instead of materialising an
    /// anonymous type and then copying every row into a second list. On a tenant with tens of thousands
    /// of sites that copy is pure garbage.
    /// </remarks>
    public sealed class StoredSite
    {
        public StoredSite()
        {
        }

        public StoredSite(int id, string urlBase)
        {
            Id = id;
            UrlBase = urlBase;
        }

        public int Id { get; set; }

        /// <summary>
        /// Nullable, exactly as the column is. The import discards null-URL sites rather than keying on
        /// them - see <c>SharePointSitesWeeklyUsageReportLoader.BeginSaveAsync</c>, and issue #375 part 2,
        /// where collapsing "no URL" into "no row" duplicated site rows.
        /// </summary>
        public string UrlBase { get; set; }
    }

    /// <summary>The most recent stored week for one site.</summary>
    /// <remarks>Projectable for the same reason as <see cref="StoredSite"/>.</remarks>
    public sealed class SiteLatestStoredWeek
    {
        public SiteLatestStoredWeek()
        {
        }

        public SiteLatestStoredWeek(int siteId, DateTime? latestWeekEnding)
        {
            SiteId = siteId;
            LatestWeekEnding = latestWeekEnding;
        }

        public int SiteId { get; set; }

        /// <summary>Nullable because <c>week_ending</c> is - a stored row need not carry a week.</summary>
        public DateTime? LatestWeekEnding { get; set; }
    }

    /// <summary>
    /// Storage for the weekly SharePoint site usage report - the EF reads and writes
    /// <c>SharePointSitesWeeklyUsageReportLoader</c> used to perform against
    /// <c>AnalyticsEntitiesContext</c> inline. See issue #375.
    ///
    /// <para>
    /// Both reads are deliberately whole-table and pre-loaded once: the save loop must do ZERO per-site
    /// round-trips. The previous per-site top-1 query was the dominant cost of this import on a large
    /// tenant, at roughly one query per site.
    /// </para>
    /// </summary>
    public interface ISharePointSiteUsageStore
    {
        /// <summary>Every known site, untracked. Includes sites with a null URL; the caller filters.</summary>
        Task<IReadOnlyList<StoredSite>> GetAllSitesAsync();

        /// <summary>The latest stored week per site, in ONE grouped query rather than one query per site.</summary>
        Task<IReadOnlyList<SiteLatestStoredWeek>> GetLatestStoredWeekPerSiteAsync();

        /// <summary>Queue an insert for a site discovered by this report run.</summary>
        void AddSite(Site site);

        /// <summary>
        /// Queue an insert for a weekly stats row. When <see cref="SharePointSitesFileWeeklyStats.Site"/>
        /// is set instead of <see cref="SharePointSitesFileWeeklyStats.SiteId"/>, the store is responsible
        /// for inserting that site first and filling the FK.
        /// </summary>
        void AddWeeklyStats(SharePointSitesFileWeeklyStats stats);

        /// <summary>Commit everything queued.</summary>
        Task CommitAsync();

        /// <summary>
        /// Enter the bulk-add phase. For the EF adapter this turns change auto-detection off, without
        /// which adding tens of thousands of rows to the context is O(n^2).
        /// </summary>
        void BeginBulkWrite();

        /// <summary>
        /// Leave the bulk-add phase. Called from a <c>finally</c> so it runs even when nothing was saved,
        /// because the context may be reused afterwards.
        /// </summary>
        void EndBulkWrite();
    }

    /// <summary>
    /// EF6 <see cref="ISharePointSiteUsageStore"/>. Both queries, both adds, the commit and the
    /// change-tracking toggle are the ones that were inline in
    /// <c>SharePointSitesWeeklyUsageReportLoader</c>, moved unchanged (issue #375).
    /// </summary>
    public sealed class SqlSharePointSiteUsageStore : ISharePointSiteUsageStore
    {
        private readonly AnalyticsEntitiesContext _db;

        public SqlSharePointSiteUsageStore(AnalyticsEntitiesContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<IReadOnlyList<StoredSite>> GetAllSitesAsync()
        {
            // Lightweight projection, not tracked - the save loop only needs the key and the URL. Projected
            // straight into the DTO so the whole table is materialised ONCE, not copied into a second list.
            return await _db.sites.AsNoTracking()
                .Select(s => new StoredSite { Id = s.ID, UrlBase = s.UrlBase })
                .ToListAsync();
        }

        public async Task<IReadOnlyList<SiteLatestStoredWeek>> GetLatestStoredWeekPerSiteAsync()
        {
            return await _db.SharePointSiteStats
                .GroupBy(s => s.SiteId)
                .Select(g => new SiteLatestStoredWeek { SiteId = g.Key, LatestWeekEnding = g.Max(s => s.ForWeekEnding) })
                .ToListAsync();
        }

        // Added (tracked) and attached via the stats row's navigation property, so EF inserts the site and
        // fills the FK itself - no second round-trip to discover the new key.
        public void AddSite(Site site) => _db.sites.Add(site);

        public void AddWeeklyStats(SharePointSitesFileWeeklyStats stats) => _db.SharePointSiteStats.Add(stats);

        public async Task CommitAsync()
        {
            // Auto-detect is off during the bulk add; run it once here so the pending inserts are picked up.
            _db.ChangeTracker.DetectChanges();
            await _db.SaveChangesAsync();
        }

        public void BeginBulkWrite() => _db.Configuration.AutoDetectChangesEnabled = false;

        /// <summary>
        /// Restores auto-detection to ON rather than to whatever it was before, which is what this loader
        /// has always done. (The daily loaders restore the captured value instead; that difference is
        /// pre-existing and is preserved deliberately - see issue #381, no behavioural change.)
        /// </summary>
        public void EndBulkWrite() => _db.Configuration.AutoDetectChangesEnabled = true;
    }
}
