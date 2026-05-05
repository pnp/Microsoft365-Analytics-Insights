using Common.Entities.Entities.Teams;
using System.Data.Entity;

namespace Common.Entities.LookupCaches
{
    internal class CallModalityCache : DBLookupCacheForEntityWithName<CallModality>
    {
        public CallModalityCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<CallModality> EntityStore => this.DB.CallModalities;
    }
}
