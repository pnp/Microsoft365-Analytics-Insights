using Common.Entities.Entities.Teams;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    internal class TeamsTabCache : DBLookupCache<TeamTabDefinition>
    {
        public TeamsTabCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<TeamTabDefinition> EntityStore => this.DB.TeamTabDefinitions;

        public async override Task<TeamTabDefinition> Load(string searchKeyTabID)
        {
            return await DB.TeamTabDefinitions.SingleOrDefaultAsync(t => t.GraphID == searchKeyTabID);
        }
    }
}
