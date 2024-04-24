using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class OfficeLocationCache : DBLookupCache<UserOfficeLocation>
    {
        public OfficeLocationCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<UserOfficeLocation> EntityStore => this.DB.UserOfficeLocations;

        public async override Task<UserOfficeLocation> Load(string searchName)
        {
            return await EntityStore.SingleOrDefaultAsync(t => t.Name == searchName);
        }
    }
}
