using Common.Entities.Teams;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    internal class TeamsChannelCache : DBLookupCache<TeamChannel>
    {
        public TeamsChannelCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<TeamChannel> EntityStore => this.DB.TeamChannels;

        public async override Task<TeamChannel> Load(string searchKeyChannelID)
        {
            return await DB.TeamChannels.SingleOrDefaultAsync(t => t.GraphID == searchKeyChannelID);
        }
    }
}
