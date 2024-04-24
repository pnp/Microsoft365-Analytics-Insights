using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class LicenseTypeCache : DBLookupCache<LicenseType>
    {
        public LicenseTypeCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<LicenseType> EntityStore => this.DB.LicenseTypes;

        public async override Task<LicenseType> Load(string searchName)
        {
            return await EntityStore.SingleOrDefaultAsync(t => t.Name == searchName);
        }
    }
}
