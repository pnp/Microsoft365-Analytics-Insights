using Common.Entities.Entities;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class YammerGroupCache : DBLookupCache<YammerGroup>
    {
        public YammerGroupCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<YammerGroup> EntityStore => this.DB.YammerGroups;

        public async override Task<YammerGroup> Load(string searchName)
        {
            return await EntityStore.SingleOrDefaultAsync(t => t.Name == searchName);
        }
    }
}
