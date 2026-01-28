using System.Data.Entity;

namespace Common.Entities.LookupCaches
{
    public class CompanyNameCache : DBLookupCacheForEntityWithName<CompanyName>
    {
        public CompanyNameCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<CompanyName> EntityStore => this.DB.CompanyNames;
    }
}
