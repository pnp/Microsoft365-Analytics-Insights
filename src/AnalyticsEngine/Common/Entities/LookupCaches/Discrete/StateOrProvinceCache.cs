using System.Data.Entity;

namespace Common.Entities.LookupCaches
{
    public class StateOrProvinceCache : DBLookupCacheForEntityWithName<StateOrProvince>
    {
        public StateOrProvinceCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<StateOrProvince> EntityStore => this.DB.StateOrProvinces;
    }
}
