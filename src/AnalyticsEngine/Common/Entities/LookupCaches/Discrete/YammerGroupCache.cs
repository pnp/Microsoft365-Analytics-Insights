using Common.Entities.Entities;
using System.Data.Entity;

namespace Common.Entities.LookupCaches
{
    public class YammerGroupCache : DBLookupCacheForEntityWithName<YammerGroup>
    {
        public YammerGroupCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<YammerGroup> EntityStore => this.DB.YammerGroups;
    }
}
