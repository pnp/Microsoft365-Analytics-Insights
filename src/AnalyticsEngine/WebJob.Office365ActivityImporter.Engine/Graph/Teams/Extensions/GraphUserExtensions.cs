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

            // Capture today once - if we re-read DateTime.UtcNow for each Y/M/D component we can
            // straddle a midnight rollover and produce a predicate that matches no rows, so the
            // method writes a duplicate log row. Use UtcNow because EF stores Date in UTC.
            var today = DateTime.UtcNow.Date;
            int todayYear = today.Year, todayMonth = today.Month, todayDay = today.Day;

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
                        t.Date.Year == todayYear &&
                        t.Date.Month == todayMonth &&
                        t.Date.Day == todayDay
                    );
                }
                if (todaysUserLog == null)
                {
                    todaysUserLog = new TeamMembershipLog()
                    {
                        Team = dbTeam,
                        User = user,
                        Date = today
                    };
                    lookupManager.Database.TeamMembershipLogs.Add(todaysUserLog);
                }
            }
        }
    }
}
