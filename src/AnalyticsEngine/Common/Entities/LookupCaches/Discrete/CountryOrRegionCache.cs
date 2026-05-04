using System.Data.Entity;

namespace Common.Entities.LookupCaches
{
    public class CountryOrRegionCache : DBLookupCacheForEntityWithName<CountryOrRegion>
    {
        public CountryOrRegionCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<CountryOrRegion> EntityStore => this.DB.CountryOrRegions;
    }
}
