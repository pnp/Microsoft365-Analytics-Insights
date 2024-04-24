using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches.Discrete
{
    public class SiteCache : DBLookupCache<Site>
    {
        public SiteCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<Site> EntityStore => this.DB.sites;

        public async override Task<Site> Load(string searchName)
        {
            return await EntityStore.SingleOrDefaultAsync(t => t.UrlBase == searchName);
        }
    }
}
