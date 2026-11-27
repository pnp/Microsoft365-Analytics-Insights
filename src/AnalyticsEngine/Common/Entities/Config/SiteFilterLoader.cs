using DataUtils;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.Config
{
    public class SiteFilterLoader
    {
        /// <summary>
        /// Load from the DB our SharePoint filter config
        /// </summary>
        public static Task<List<FilterUrlConfig>> Load(AnalyticsEntitiesContext db) => Load(db, CancellationToken.None);

        public static async Task<List<FilterUrlConfig>> Load(AnalyticsEntitiesContext db, CancellationToken cancellationToken)
        {
            var allOrgURLs = from urls in db.org_urls
                             select urls;

            var orgUrlCache = await allOrgURLs.ToListAsync(cancellationToken);
#if DEBUG
            if (orgUrlCache.Count == 0)
            {
                db.org_urls.Add(new OrgUrl() { UrlBase = "https://" });
                db.org_urls.Add(new OrgUrl() { UrlBase = "https://DEVBOXSHAREPOINT" });
                await db.SaveChangesAsync(cancellationToken);
                orgUrlCache = await db.org_urls.ToListAsync(cancellationToken);
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
