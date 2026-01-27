using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class CountryOrRegionCache : DBLookupCacheForEntityWithName<CountryOrRegion>
    {
        public CountryOrRegionCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<CountryOrRegion> EntityStore => this.DB.CountryOrRegions;
    }
}
