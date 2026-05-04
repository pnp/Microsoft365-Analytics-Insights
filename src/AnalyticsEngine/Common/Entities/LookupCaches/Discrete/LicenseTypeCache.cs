using System.Data.Entity;

namespace Common.Entities.LookupCaches
{
    public class LicenseTypeCache : DBLookupCacheForEntityWithName<LicenseType>
    {
        public LicenseTypeCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<LicenseType> EntityStore => this.DB.LicenseTypes;

    }
}
