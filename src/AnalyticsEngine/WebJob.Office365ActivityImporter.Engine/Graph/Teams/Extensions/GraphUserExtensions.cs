using Common.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public static class GraphUserExtensions
    {
        public static async Task SaveStatsForToday(this List<BaseUser> members, O365Team team, TeamsAndCallsDBLookupManager lookupManager)
        {
            using (var store = new SqlTeamsPersistenceStore(lookupManager))
            {
                await store.SaveMembers(members, team);
            }
        }
    }
}
