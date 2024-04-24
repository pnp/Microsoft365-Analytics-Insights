using Common.Entities.Entities.Teams;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{

    public class TeamsReactionTypeCache : DBLookupCache<TeamsReactionType>
    {
        public TeamsReactionTypeCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<TeamsReactionType> EntityStore => this.DB.TeamsReactionTypes;

        public async override Task<TeamsReactionType> Load(string searchKey)
        {
            return await DB.TeamsReactionTypes.SingleOrDefaultAsync(t => t.Name == searchKey);
        }
    }
}
