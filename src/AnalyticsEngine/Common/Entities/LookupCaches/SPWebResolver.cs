using DataUtils;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;

namespace Common.Entities
{
    /// <summary>
    /// Finds the web, and the site collection, that a SharePoint audit event belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// URLs are compared without case, as the SQL collation of <c>webs.url_base</c> and <c>sites.url_base</c> compares them
    /// (their unique indexes would refuse two rows that differ only in case), and without trailing slashes. A web or site
    /// is only ever matched as a parent on whole path segments, and never across a site collection boundary.
    /// </para>
    /// <para>
    /// One resolver serves one save batch and its context. The rows already in SQL come from a
    /// <see cref="SharePointWebIndex"/> shared by every batch; each lookup is a dictionary probe per path segment of the
    /// address, not a comparison with every web and site.
    /// </para>
    /// </remarks>
    public class SPWebResolver
    {
        /// <summary>
        /// SharePoint Online's wildcard managed paths. <c>/sites/name</c>, <c>/teams/name</c> and, on the OneDrive host,
        /// <c>/personal/name</c> are each a site collection of their own, never part of the root site collection above them.
        /// </summary>
        private static readonly string[] SiteCollectionManagedPaths = { "sites", "teams", "personal" };

        /// <summary>
        /// The webs and site collections already in SQL, or null for a resolver that knows only what it is given through
        /// <see cref="CacheWeb"/> and <see cref="CacheSite"/>.
        /// </summary>
        private readonly SharePointWebIndex _index;
        private readonly AnalyticsEntitiesContext _db;

        // This resolver's own objects: rows from the index once attached to _db, and the webs and sites it has created.
        private readonly UrlPrefixIndex<Site> _siteCache = new UrlPrefixIndex<Site>();
        private readonly UrlPrefixIndex<Web> _websCache = new UrlPrefixIndex<Web>();

        /// <summary>The web found for an address inside a web, such as a file's, or null when there was none.</summary>
        private readonly Dictionary<string, Web> _containingWebCache = new Dictionary<string, Web>(StringComparer.OrdinalIgnoreCase);

        public SPWebResolver()
        {
        }

        /// <param name="index">The webs and site collections in SQL, brought up to date by the caller.</param>
        /// <param name="db">
        /// The context the webs returned are used in. A web or site found in <paramref name="index"/> is attached to it as an
        /// existing, unchanged row - never added - and the same object is returned for it every time.
        /// </param>
        internal SPWebResolver(SharePointWebIndex index, AnalyticsEntitiesContext db)
        {
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        private static string CacheKey(string url) => StringUtils.RemoveTrailingSlash(url);

        Site GetOrCreateSite(string siteUrl)
        {
            if (!_siteCache.TryGetExact(siteUrl, out var site))
            {
                site = new Site() { UrlBase = siteUrl };
                _siteCache.Set(siteUrl, site);
            }

            return site;
        }

        /// <summary>
        /// Gets the web an audit event belongs to, or creates it and its site collection.
        /// </summary>
        /// <param name="url">
        /// With <paramref name="createIfNoneFound"/>, the address of the web itself: an audit record's <c>SiteUrl</c>. Without
        /// it, an address inside a web, such as a file's.
        /// </param>
        /// <param name="createIfNoneFound">
        /// <para>
        /// True for a web's own address. Returns the web with exactly that address, created the first time it is seen. It is
        /// never matched to a parent web: <c>/sites/hr</c> is not part of the root site, and <c>/sites/hrdocs</c> is not
        /// <c>/sites/hr</c>.
        /// </para>
        /// <para>
        /// False for an address inside a web. Returns the nearest web that contains it, in the same site collection, or null.
        /// Nothing is created, because a file's address doesn't say where its web starts.
        /// </para>
        /// </param>
        public Web GetWeb(string url, bool createIfNoneFound)
        {
            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentNullException(nameof(url));
            }

            lock (this)
            {
                var key = CacheKey(url);
                var siteCollectionRootLength = SiteCollectionRootLength(key);

                if (!createIfNoneFound)
                {
                    if (!_containingWebCache.TryGetValue(key, out var containingWeb))
                    {
                        containingWeb = FindContainingWeb(key, siteCollectionRootLength);
                        _containingWebCache.Add(key, containingWeb);
                    }
                    return containingWeb;
                }

                var web = FindWeb(key);
                if (web == null)
                {
                    // A new web belongs to the nearest site collection we know of that contains it. If there is none, we
                    // assume the web is a site collection of its own: we can't know for sure which site collections exist,
                    // and even an API call could only tell us what is true today.
                    var site = FindContainingSite(key, siteCollectionRootLength) ?? GetOrCreateSite(url);

                    web = new Web() { url_base = key.ToLowerInvariant(), site = site };
                    _websCache.Set(key, web);

                    // A file looked up before this web existed may be in it.
                    _containingWebCache.Clear();
                }

                return web;
            }
        }

        /// <summary>The web with exactly this address, or null.</summary>
        private Web FindWeb(string key)
        {
            if (_websCache.TryGetExact(key, out var web))
            {
                return web;
            }
            return _index != null && _index.TryGetWeb(key, out var row) ? Attached(row) : null;
        }

        /// <summary>
        /// The longest web that is <paramref name="key"/> or one of its parents, no shorter than
        /// <paramref name="shortestMatch"/>: this resolver's own, or one in SQL, whichever is longer.
        /// </summary>
        private Web FindContainingWeb(string key, int shortestMatch)
        {
            _websCache.TryGetClosest(key, shortestMatch, out var own);
            if (_index == null || !_index.TryGetClosestWeb(key, shortestMatch, out var row))
            {
                return own;
            }

            // The same length means the same address, which this resolver already holds.
            return own != null && KeyLength(own.url_base) >= KeyLength(row.Url) ? own : Attached(row);
        }

        /// <summary>As <see cref="FindContainingWeb"/>, for site collections.</summary>
        private Site FindContainingSite(string key, int shortestMatch)
        {
            _siteCache.TryGetClosest(key, shortestMatch, out var own);
            if (_index == null || !_index.TryGetClosestSite(key, shortestMatch, out var row))
            {
                return own;
            }

            return own != null && KeyLength(own.UrlBase) >= KeyLength(row.Url) ? own : Attached(row);
        }

        private static int KeyLength(string url) => UrlPrefixIndex<Web>.KeyFor(url)?.Length ?? 0;

        /// <summary>A web read from SQL, as an unchanged row of this resolver's context; the same object every time.</summary>
        private Web Attached(SharePointWebIndex.WebRow row)
        {
            if (!_websCache.TryGetExact(row.Url, out var web))
            {
                web = AttachExisting(_db.webs, new Web { ID = row.Id, site_id = row.SiteId, url_base = row.Url });
                _websCache.Set(row.Url, web);
            }
            return web;
        }

        /// <summary>A site collection read from SQL, as an unchanged row of this resolver's context; the same object every time.</summary>
        private Site Attached(SharePointWebIndex.SiteRow row)
        {
            if (!_siteCache.TryGetExact(row.Url, out var site))
            {
                site = AttachExisting(_db.sites, new Site { ID = row.Id, UrlBase = row.Url });
                _siteCache.Set(row.Url, site);
            }
            return site;
        }

        /// <summary>
        /// Attaches a row that exists in SQL, so saving the context neither inserts it again nor updates it. If the context
        /// already tracks that row - its caller read or saved it - that object is used instead.
        /// </summary>
        private static T AttachExisting<T>(DbSet<T> set, T row) where T : AbstractEFEntity
        {
            try
            {
                return set.Attach(row);
            }
            catch (InvalidOperationException)
            {
                var tracked = set.Local.FirstOrDefault(e => e.ID == row.ID);
                if (tracked == null)
                {
                    throw;
                }
                return tracked;
            }
        }

        /// <summary>
        /// The length of the shortest address that can be the site collection containing <paramref name="url"/>: for an
        /// address under a managed path, such as <c>https://contoso.sharepoint.com/sites/hr/Shared Documents</c>, that site
        /// collection (<c>https://contoso.sharepoint.com/sites/hr</c>); otherwise the host's root site collection. No web or
        /// site shorter than this can contain the address.
        /// </summary>
        internal static int SiteCollectionRootLength(string url)
        {
            var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0)
            {
                return 0;
            }

            var hostEnd = IUrlObjectResolver.EndOfPathSegment(url, schemeEnd + 3);
            if (hostEnd >= url.Length || url[hostEnd] != '/')
            {
                return hostEnd;
            }

            var managedPathEnd = IUrlObjectResolver.EndOfPathSegment(url, hostEnd + 1);
            var managedPath = url.Substring(hostEnd + 1, managedPathEnd - hostEnd - 1);
            if (managedPathEnd < url.Length && url[managedPathEnd] == '/'
                && SiteCollectionManagedPaths.Contains(managedPath, StringComparer.OrdinalIgnoreCase))
            {
                var siteCollectionEnd = IUrlObjectResolver.EndOfPathSegment(url, managedPathEnd + 1);
                if (siteCollectionEnd > managedPathEnd + 1)
                {
                    return siteCollectionEnd;
                }
            }

            return hostEnd;
        }

        public void CacheSite(Site site)
        {
            lock (this)
            {
                _siteCache.Set(site.UrlBase, site);
            }
        }

        public void CacheWeb(Web web)
        {
            lock (this)
            {
                _websCache.Set(web.url_base, web);
                _containingWebCache.Clear();
            }
        }
    }
}
