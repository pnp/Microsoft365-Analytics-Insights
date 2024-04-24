using Common.Entities.Entities;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class TeamsCache : DBLookupCache<TeamDefinition>
    {
        public TeamsCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<TeamDefinition> EntityStore => this.DB.Teams;

        public async override Task<TeamDefinition> Load(string searchKey)
        {
            return await DB.Teams.SingleOrDefaultAsync(t => t.GraphID == searchKey);
        }
    }

}
