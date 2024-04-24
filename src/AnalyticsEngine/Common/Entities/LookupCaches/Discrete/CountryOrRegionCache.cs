using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class CountryOrRegionCache : DBLookupCache<CountryOrRegion>
    {
        public CountryOrRegionCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<CountryOrRegion> EntityStore => this.DB.CountryOrRegions;

        public async override Task<CountryOrRegion> Load(string searchName)
        {
            return await EntityStore.SingleOrDefaultAsync(t => t.Name == searchName);
        }
    }

}
