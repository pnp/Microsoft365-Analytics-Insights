using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class UsageLocationCache : DBLookupCache<UserUsageLocation>
    {
        public UsageLocationCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<UserUsageLocation> EntityStore => this.DB.UserUsageLocations;

        public async override Task<UserUsageLocation> Load(string searchName)
        {
            return await EntityStore.SingleOrDefaultAsync(t => t.Name == searchName);
        }
    }
}
