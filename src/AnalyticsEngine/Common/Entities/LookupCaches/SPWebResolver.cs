using DataUtils;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities
{
    /// <summary>
    /// Finds the web, and the site collection, that a SharePoint audit event belongs to.
    /// </summary>
    /// <remarks>
    /// URLs are compared without case, as the SQL collation of <c>webs.url_base</c> and <c>sites.url_base</c> compares them
    /// (their unique indexes would refuse two rows that differ only in case), and without trailing slashes. A web or site
    /// is only ever matched as a parent on whole path segments, and never across a site collection boundary.
    /// </remarks>
    public class SPWebResolver
    {
        /// <summary>
        /// SharePoint Online's wildcard managed paths. <c>/sites/name</c>, <c>/teams/name</c> and, on the OneDrive host,
        /// <c>/personal/name</c> are each a site collection of their own, never part of the root site collection above them.
        /// </summary>
        private static readonly string[] SiteCollectionManagedPaths = { "sites", "teams", "personal" };

        private readonly Dictionary<string, Site> _siteCache = new Dictionary<string, Site>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Web> _websCache = new Dictionary<string, Web>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The web found for an address inside a web, such as a file's, or null when there was none.</summary>
        private readonly Dictionary<string, Web> _containingWebCache = new Dictionary<string, Web>(StringComparer.OrdinalIgnoreCase);

        private static string CacheKey(string url) => StringUtils.RemoveTrailingSlash(url);

        Site GetOrCreateSite(string siteUrl)
        {
            var key = CacheKey(siteUrl);
            if (!_siteCache.TryGetValue(key, out var site))
            {
                site = new Site() { UrlBase = siteUrl };
                _siteCache.Add(key, site);
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
                        containingWeb = _websCache.Values.GetClosest(key, siteCollectionRootLength);
                        _containingWebCache.Add(key, containingWeb);
                    }
                    return containingWeb;
                }

                if (!_websCache.TryGetValue(key, out var web))
                {
                    // A new web belongs to the nearest site collection we know of that contains it. If there is none, we
                    // assume the web is a site collection of its own: we can't know for sure which site collections exist,
                    // and even an API call could only tell us what is true today.
                    var site = _siteCache.Values.GetClosest(key, siteCollectionRootLength) ?? GetOrCreateSite(url);

                    web = new Web() { url_base = key.ToLowerInvariant(), site = site };
                    _websCache.Add(key, web);

                    // A file looked up before this web existed may be in it.
                    _containingWebCache.Clear();
                }

                return web;
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

        internal async Task PopulateCaches(AnalyticsEntitiesContext database)
        {
            var webs = await database.webs.ToListAsync();
            var sites = await database.sites.ToListAsync();
            foreach (var web in webs)
            {
                CacheWeb(web);
            }
            foreach (var site in sites)
            {
                CacheSite(site);
            }
        }

        public void CacheSite(Site site)
        {
            lock (this)
            {
                _siteCache[CacheKey(site.UrlBase)] = site;
            }
        }

        public void CacheWeb(Web web)
        {
            lock (this)
            {
                _websCache[CacheKey(web.url_base)] = web;
                _containingWebCache.Clear();
            }
        }
    }
}
