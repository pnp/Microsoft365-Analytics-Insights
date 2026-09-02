using Common.Entities;
using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// The database half of the site-id to URL resolution: the local cache that exists so the importer goes
    /// easy on Graph. Behind a port (issue #375) because the lookup precedence - database first, Graph only
    /// on a miss, then write the answer back - had no test that did not need SQL Server.
    /// </summary>
    public interface ISiteUrlStore
    {
        /// <summary>The stored URL for this site id, or null when it is not cached yet.</summary>
        Task<string> TryGetUrlForSiteIdAsync(string siteId);

        /// <summary>
        /// Record the mapping. A site row already holding this URL is stamped with the id rather than
        /// duplicated - the same URL arriving under a newly-issued site id is the case this cache exists for.
        /// </summary>
        Task SaveSiteUrlAsync(string siteId, string url);
    }

    /// <summary>
    /// EF6 <see cref="ISiteUrlStore"/>. Both queries and the save are the ones that were inline in
    /// <c>SPSiteIdToUrlCache.Load</c>, moved unchanged.
    /// </summary>
    public sealed class SqlSiteUrlStore : ISiteUrlStore
    {
        private readonly AnalyticsEntitiesContext _db;

        public SqlSiteUrlStore(AnalyticsEntitiesContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<string> TryGetUrlForSiteIdAsync(string siteId)
        {
            // Compare directly (no .ToLower()): SQL Server's default collation is case-insensitive,
            // and LOWER() on the column makes the predicate non-SARGable, forcing a full scan of sites.
            var dbRecordBySiteId = await _db.sites.Where(s => s.SiteId == siteId).SingleOrDefaultAsync();
            return dbRecordBySiteId?.UrlBase;
        }

        public async Task SaveSiteUrlAsync(string siteId, string url)
        {
            var dbRecordBySiteUrl = await _db.sites.Where(s => s.UrlBase == url).SingleOrDefaultAsync();
            if (dbRecordBySiteUrl != null)
            {
                dbRecordBySiteUrl.SiteId = siteId;
            }
            else
            {
                _db.sites.Add(new Site
                {
                    SiteId = siteId,
                    UrlBase = url
                });
            }
            await _db.SaveChangesAsync();
        }
    }
}
