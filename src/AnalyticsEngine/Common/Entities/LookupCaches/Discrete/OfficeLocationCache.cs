using System.Data.Entity;

namespace Common.Entities.LookupCaches
{
    public class OfficeLocationCache : DBLookupCacheForEntityWithName<UserOfficeLocation>
    {
        public OfficeLocationCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<UserOfficeLocation> EntityStore => this.DB.UserOfficeLocations;
    }
}
