using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class UsageLocationCache : DBLookupCacheForEntityWithName<UserUsageLocation>
    {
        public UsageLocationCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<UserUsageLocation> EntityStore => this.DB.UserUsageLocations;
    }
}
