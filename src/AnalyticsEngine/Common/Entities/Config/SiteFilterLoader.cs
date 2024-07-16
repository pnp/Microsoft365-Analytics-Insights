using DataUtils;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.Config
{
    public class SiteFilterLoader
    {
        /// <summary>
        /// Load from the DB our SharePoint filter config
        /// </summary>
        public static async Task<List<FilterUrlConfig>> Load(AnalyticsEntitiesContext db)
        {
            var allOrgURLs = from urls in db.org_urls
                             select urls;

            var orgUrlCache = await allOrgURLs.ToListAsync();
#if DEBUG
            if (orgUrlCache.Count == 0)
            {
                db.org_urls.Add(new OrgUrl() { UrlBase = "https://" });
                db.org_urls.Add(new OrgUrl() { UrlBase = "https://DEVBOXSHAREPOINT" });
                await db.SaveChangesAsync();
                orgUrlCache = db.org_urls.ToList();
            }
#endif


            return orgUrlCache.Select(u => new FilterUrlConfig
            {
                Url = u.UrlBase,
                ExactSiteMatch = u.ExactMatch.HasValue && u.ExactMatch.Value
            }).ToList();

        }
    }
}
