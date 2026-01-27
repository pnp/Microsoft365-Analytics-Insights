using Common.Entities.Entities.Teams;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{

    public class TeamsReactionTypeCache : DBLookupCacheForEntityWithName<TeamsReactionType>
    {
        public TeamsReactionTypeCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<TeamsReactionType> EntityStore => this.DB.TeamsReactionTypes;
    }
}
