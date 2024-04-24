using Common.Entities.Teams;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    internal class TeamsAddOnCache : DBLookupCache<TeamAddOnDefinition>
    {
        public TeamsAddOnCache(AnalyticsEntitiesContext context) : base(context) { }
        public override DbSet<TeamAddOnDefinition> EntityStore => DB.TeamAddOns;


        public async override Task<TeamAddOnDefinition> Load(string searchKey)
        {
            return await DB.TeamAddOns.SingleOrDefaultAsync(t => t.GraphID == searchKey);
        }

        internal async Task<TeamAddOnDefinition> GetAndUpdateOrCreateNewResource(string addOnId, TeamAddOnDefinition newAddInTemplate)
        {
            return await base.GetResource(addOnId, () =>
            {
                this.EntityStore.Add(newAddInTemplate);

                return Task.FromResult(newAddInTemplate);
            });

        }
    }
}
