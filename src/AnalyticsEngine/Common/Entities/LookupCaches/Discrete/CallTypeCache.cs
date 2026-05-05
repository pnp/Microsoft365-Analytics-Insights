using Common.Entities.Entities.Teams;
using System.Data.Entity;

namespace Common.Entities.LookupCaches
{
    internal class CallTypeCache : DBLookupCacheForEntityWithName<CallType>
    {
        public CallTypeCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<CallType> EntityStore => this.DB.CallTypes;
    }
}
