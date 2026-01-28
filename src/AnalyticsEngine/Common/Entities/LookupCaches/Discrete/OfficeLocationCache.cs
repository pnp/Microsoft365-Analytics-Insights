using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class OfficeLocationCache : DBLookupCacheForEntityWithName<UserOfficeLocation>
    {
        public OfficeLocationCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<UserOfficeLocation> EntityStore => this.DB.UserOfficeLocations;
    }
}
