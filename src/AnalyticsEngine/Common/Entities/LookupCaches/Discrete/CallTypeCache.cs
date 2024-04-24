using Common.Entities.Entities.Teams;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    internal class CallTypeCache : DBLookupCache<CallType>
    {
        public CallTypeCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<CallType> EntityStore => this.DB.CallTypes;

        public async override Task<CallType> Load(string searchKey)
        {
            return await EntityStore.FirstOrDefaultAsync(t => t.Name == searchKey);
        }
    }
}
