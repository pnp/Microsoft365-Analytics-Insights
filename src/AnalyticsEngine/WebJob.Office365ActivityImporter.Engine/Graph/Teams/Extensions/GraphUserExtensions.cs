using Common.Entities;
using Common.Entities.Entities;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public static class GraphUserExtensions
    {

        /// <summary>
        /// Save member logs for today
        /// </summary>
        public static async Task SaveStatsForToday(this List<BaseUser> members, O365Team team, TeamsAndCallsDBLookupManager lookupManager)
        {
            TeamDefinition dbTeam = await lookupManager.GetOrCreateTeam(team.Id, team.DisplayName);
            foreach (var member in members)
            {
                var user = await lookupManager.GetOrCreateUser(member.UserPrincipalName, true);
                TeamMembershipLog todaysUserLog = null;
                if (user.IsSavedToDB)
                {
                    todaysUserLog = await lookupManager.Database.TeamMembershipLogs
                        .SingleOrDefaultAsync(t =>
                        t.Team.ID == dbTeam.ID &&
                        t.UserID == user.ID &&
                        t.Date.Year == DateTime.Now.Year &&
                        t.Date.Month == DateTime.Now.Month &&
                        t.Date.Day == DateTime.Now.Day
                    );
                }
                if (todaysUserLog == null)
                {
                    todaysUserLog = new TeamMembershipLog()
                    {
                        Team = dbTeam,
                        User = user,
                        Date = DateTime.Now.Date
                    };
                    lookupManager.Database.TeamMembershipLogs.Add(todaysUserLog);
                }
            }
        }
    }
}
