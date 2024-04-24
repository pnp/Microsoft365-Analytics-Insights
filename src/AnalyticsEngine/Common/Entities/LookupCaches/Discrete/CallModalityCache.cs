using Common.Entities.Entities.Teams;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    internal class CallModalityCache : DBLookupCache<CallModality>
    {
        public CallModalityCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<CallModality> EntityStore => this.DB.CallModalities;

        public async override Task<CallModality> Load(string searchKey)
        {
            return await EntityStore.SingleOrDefaultAsync(t => t.Name == searchKey);
        }
    }
}
