using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class LicenseTypeCache : DBLookupCacheForEntityWithName<LicenseType>
    {
        public LicenseTypeCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<LicenseType> EntityStore => this.DB.LicenseTypes;

    }
}
