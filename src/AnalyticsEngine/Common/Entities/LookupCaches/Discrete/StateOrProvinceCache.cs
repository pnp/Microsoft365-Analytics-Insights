using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class StateOrProvinceCache : DBLookupCache<StateOrProvince>
    {
        public StateOrProvinceCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<StateOrProvince> EntityStore => this.DB.StateOrProvinces;

        public async override Task<StateOrProvince> Load(string searchName)
        {
            return await EntityStore.SingleOrDefaultAsync(t => t.Name == searchName);
        }
    }
}
