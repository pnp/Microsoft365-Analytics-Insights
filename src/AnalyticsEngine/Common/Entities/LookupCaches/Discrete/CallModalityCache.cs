using Common.Entities.Entities.Teams;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    internal class CallModalityCache : DBLookupCacheForEntityWithName<CallModality>
    {
        public CallModalityCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<CallModality> EntityStore => this.DB.CallModalities;
    }
}
