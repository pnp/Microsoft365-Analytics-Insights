using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class CompanyNameCache : DBLookupCache<CompanyName>
    {
        public CompanyNameCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<CompanyName> EntityStore => this.DB.CompanyNames;

        public async override Task<CompanyName> Load(string searchName)
        {
            return await EntityStore.SingleOrDefaultAsync(t => t.Name == searchName);
        }
    }
}
