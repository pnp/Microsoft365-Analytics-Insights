using System.Data.Entity;

namespace Common.Entities.LookupCaches
{
    public class UsageLocationCache : DBLookupCacheForEntityWithName<UserUsageLocation>
    {
        public UsageLocationCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<UserUsageLocation> EntityStore => this.DB.UserUsageLocations;
    }
}
