using DataUtils;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities
{
    /// <summary>
    /// The SharePoint webs and site collections already in SQL, by URL, for <see cref="SPWebResolver"/>: read in full once,
    /// then kept current by reading only the rows added since.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The audit import used to read every <c>webs</c> and <c>sites</c> row into each save batch's context, as tracked
    /// entities, and then compare every new site URL with each of them in turn. Since #819 every site collection, OneDrive
    /// and subsite has a web of its own, so at the 200,000-user baseline that is about 400,000 rows read, tracked and
    /// change-detected per batch of 2,000 events, inside the shared write lock. This holds IDs and URLs only - never an EF
    /// entity, so nothing tracked by one batch's context reaches another's - and answers each lookup with one dictionary
    /// probe per path segment.
    /// </para>
    /// <para>
    /// <see cref="RefreshAsync(AnalyticsEntitiesContext)"/> runs before a batch resolves its first web. The first call
    /// reads everything; later ones read only rows whose id is above the highest already read, which picks up the sites
    /// the batch's own staging merge has just inserted, the webs earlier batches saved and anything another writer added.
    /// Rows are never updated or deleted, so nothing read goes stale. A row another writer committed late with a lower id
    /// than one already read is the one thing an incremental read can miss; were a batch then to insert a duplicate, the
    /// unique index refuses it, the batch fails and is retried, and <see cref="Invalidate"/> - called by the failed save -
    /// makes the retry read everything again, as every batch used to.
    /// </para>
    /// <para>
    /// Thread-safe. One instance is shared by every save batch of an import cycle, including concurrent ones.
    /// </para>
    /// </remarks>
    public sealed class SharePointWebIndex
    {
        internal sealed class WebRow
        {
            public int Id { get; set; }
            public int SiteId { get; set; }
            public string Url { get; set; }
        }

        internal sealed class SiteRow
        {
            public int Id { get; set; }
            public string Url { get; set; }
        }

        /// <summary>Where the rows come from: SQL in production, a list in tests.</summary>
        internal interface IRowSource
        {
            /// <summary>Every web with an id above <paramref name="afterId"/>, in id order.</summary>
            Task<List<WebRow>> WebsAfterAsync(int afterId);

            /// <summary>Every site collection with an id above <paramref name="afterId"/>, in id order.</summary>
            Task<List<SiteRow>> SitesAfterAsync(int afterId);
        }

        private readonly object _sync = new object();
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);

        // The first row read for a URL keeps it: rows arrive in id order, so a later row whose URL differs only in case or
        // a trailing slash never moves a URL to a different row part-way through a cycle.
        private readonly UrlPrefixIndex<WebRow> _webs = new UrlPrefixIndex<WebRow>();
        private readonly UrlPrefixIndex<SiteRow> _sites = new UrlPrefixIndex<SiteRow>();
        private int _lastWebId;
        private int _lastSiteId;
        private bool _loaded;
        private int _generation;

        /// <summary>How many times every row has been read. Diagnostics and tests.</summary>
        public int FullLoads { get; private set; }

        public int WebCount
        {
            get { lock (_sync) { return _webs.Count; } }
        }

        public int SiteCount
        {
            get { lock (_sync) { return _sites.Count; } }
        }

        /// <summary>Reads every row the first time, and after <see cref="Invalidate"/>; otherwise only the rows added since.</summary>
        public Task RefreshAsync(AnalyticsEntitiesContext db) => RefreshAsync(new SqlRowSource(db));

        internal async Task RefreshAsync(IRowSource source)
        {
            await _refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                bool full;
                int generation, afterWebId, afterSiteId;
                lock (_sync)
                {
                    full = !_loaded;
                    generation = _generation;
                    afterWebId = full ? 0 : _lastWebId;
                    afterSiteId = full ? 0 : _lastSiteId;
                }

                var webs = await source.WebsAfterAsync(afterWebId).ConfigureAwait(false);
                var sites = await source.SitesAfterAsync(afterSiteId).ConfigureAwait(false);

                lock (_sync)
                {
                    if (full)
                    {
                        _webs.Clear();
                        _sites.Clear();
                        _lastWebId = 0;
                        _lastSiteId = 0;
                        FullLoads++;
                    }

                    foreach (var web in webs)
                    {
                        _webs.TryAdd(web.Url, web);
                        if (web.Id > _lastWebId) _lastWebId = web.Id;
                    }
                    foreach (var site in sites)
                    {
                        _sites.TryAdd(site.Url, site);
                        if (site.Id > _lastSiteId) _lastSiteId = site.Id;
                    }

                    // Invalidated while reading: the next refresh reads everything again.
                    _loaded = generation == _generation;
                }
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        /// <summary>
        /// Makes the next <see cref="RefreshAsync(AnalyticsEntitiesContext)"/> read every row again. For a save that failed,
        /// which may have collided with a row this index had not read.
        /// </summary>
        public void Invalidate()
        {
            lock (_sync)
            {
                _loaded = false;
                _generation++;
            }
        }

        internal bool TryGetWeb(string url, out WebRow row)
        {
            lock (_sync) { return _webs.TryGetExact(url, out row); }
        }

        internal bool TryGetClosestWeb(string url, int shortestMatch, out WebRow row)
        {
            lock (_sync) { return _webs.TryGetClosest(url, shortestMatch, out row); }
        }

        internal bool TryGetClosestSite(string url, int shortestMatch, out SiteRow row)
        {
            lock (_sync) { return _sites.TryGetClosest(url, shortestMatch, out row); }
        }

        /// <summary>IDs and URLs only, untracked: nothing read here is attached to the batch's context.</summary>
        private sealed class SqlRowSource : IRowSource
        {
            private readonly AnalyticsEntitiesContext _db;

            public SqlRowSource(AnalyticsEntitiesContext db)
            {
                _db = db;
            }

            public Task<List<WebRow>> WebsAfterAsync(int afterId) =>
                _db.webs.AsNoTracking()
                    .Where(w => w.ID > afterId)
                    .OrderBy(w => w.ID)
                    .Select(w => new WebRow { Id = w.ID, SiteId = w.site_id, Url = w.url_base })
                    .ToListAsync();

            public Task<List<SiteRow>> SitesAfterAsync(int afterId) =>
                _db.sites.AsNoTracking()
                    .Where(s => s.ID > afterId)
                    .OrderBy(s => s.ID)
                    .Select(s => new SiteRow { Id = s.ID, Url = s.UrlBase })
                    .ToListAsync();
        }
    }
}
