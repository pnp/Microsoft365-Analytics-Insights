using Common.DataUtils;
using System.Collections.Generic;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities
{
    public class SPWebResolver
    {
        private Dictionary<string, Site> _siteCache = null;
        private Dictionary<string, Web> _websCache = null;

        public SPWebResolver()
        {
            _siteCache = new Dictionary<string, Site>();
            _websCache = new Dictionary<string, Web>();
        }

        Site GetOrCreateSite(string siteUrl)
        {
            // Populate cache with all sites
            if (!_siteCache.ContainsKey(siteUrl))
            {
                var s = new Site() { UrlBase = siteUrl };
                _siteCache.Add(siteUrl, s);
            }

            return _siteCache[siteUrl];
        }

        /// <summary>
        /// Gets closest web for a URL, optionally or creates new webs + corrsponding site if no matching site found.
        /// Sometimes we don't want to create if not found if the original URL is a file, not a site so we can't with any confidence create a new site from it
        /// </summary>
        public Web GetWeb(string url, bool createIfNoneFound)
        {
            lock (this)
            {
                var key = url.ToLower();
                if (!_websCache.ContainsKey(key))
                {
                    // We need to find a site. 
                    // This gonna be a hack, because we can't garauntee what site-collections there are really in SP.
                    // Even if we could with an API call, who knows if it'll be so in the future.
                    // So if we can't find a site with a base URL of the web, we assume the web URL is the site URL and create that as a "site".

                    // Format URL
                    var keyMinusSlash = StringUtils.RemoveTrailingSlash(key);

                    var bestFitWeb = _websCache.Values.GetClosest(key);
                    if (bestFitWeb == null && createIfNoneFound)
                    {
                        // Find site that matches as much of the web URL as possible
                        var bestFitSite = _siteCache.Values.GetClosest(keyMinusSlash);

                        if (bestFitSite == null)
                        {
                            bestFitSite = GetOrCreateSite(url);
                        }

                        bestFitWeb = new Web() { url_base = keyMinusSlash, site = bestFitSite };
                    }
                    _websCache.Add(key, bestFitWeb);
                }

                return _websCache[key];
            }
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
                if (_siteCache.ContainsKey(site.UrlBase))
                {
                    _siteCache[site.UrlBase] = site;
                }
                else
                {
                    _siteCache.Add(site.UrlBase, site);
                }
            }

        }

        public void CacheWeb(Web web)
        {
            lock (this)
            {
                if (_websCache.ContainsKey(web.url_base))
                {
                    _websCache[web.url_base] = web;
                }
                else
                {
                    _websCache.Add(web.url_base, web);
                }
            }

        }
    }
}
