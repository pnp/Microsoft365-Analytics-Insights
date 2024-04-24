using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Teams;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Analytics extensions for Graph Channel
    /// </summary>
    public static class ITeamInstalledAppsCollectionPageExtensions
    {

        /// <summary>
        /// Save add-on logs for today
        /// </summary>
        public static async Task SaveStatsForToday(this IEnumerable<TeamsAppInstallation> apps, TeamsAndCallsDBLookupManager lookupManager, TeamDefinition dbTeam)
        {
            if (apps == null) return;

            foreach (var app in apps)
            {
                var appDef = await lookupManager.GetTeamAddOnDefinition(app.TeamsAppDefinition.TeamsAppId, app.TeamsAppDefinition.DisplayName);
                TeamAddOnLog todaysAddOnLog = null;

                // See if there's already a log for this team/add-on for today
                if (appDef.IsSavedToDB)
                {
                    todaysAddOnLog = await lookupManager.Database.TeamAddOnLogs
                        .SingleOrDefaultAsync(t =>
                        t.Team.ID == dbTeam.ID &&
                        t.AddOnID == appDef.ID &&
                        t.Date.Year == DateTime.Now.Year &&
                        t.Date.Month == DateTime.Now.Month &&
                        t.Date.Day == DateTime.Now.Day
                    );
                }
                if (todaysAddOnLog == null)
                {
                    // No log for combination. Create new
                    todaysAddOnLog = new TeamAddOnLog()
                    {
                        AddOn = appDef,
                        Team = dbTeam,
                        Date = DateTime.Now.Date
                    };
                    lookupManager.Database.TeamAddOnLogs.Add(todaysAddOnLog);
                }
            }
        }
    }
}
