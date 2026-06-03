using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Teams;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
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

            // Capture today once - re-reading DateTime.UtcNow per Y/M/D component would let a midnight
            // rollover produce a predicate that matches no rows and create duplicate per-day rows.
            var today = DateTime.UtcNow.Date;
            int todayYear = today.Year, todayMonth = today.Month, todayDay = today.Day;

            // Pre-fetch all of today's add-on logs for this team in one query to avoid N+1
            // SingleOrDefaultAsync calls (one per installed app). Match in memory below.
            var todaysLogsForTeam = await lookupManager.Database.TeamAddOnLogs
                .Where(t =>
                    t.Team.ID == dbTeam.ID &&
                    t.Date.Year == todayYear &&
                    t.Date.Month == todayMonth &&
                    t.Date.Day == todayDay)
                .ToListAsync();
            var logsByAddOnId = todaysLogsForTeam.ToDictionary(l => l.AddOnID, l => l);

            foreach (var app in apps)
            {
                var appDef = await lookupManager.GetTeamAddOnDefinition(app.TeamsAppDefinition.TeamsAppId, app.TeamsAppDefinition.DisplayName);
                TeamAddOnLog todaysAddOnLog = null;

                if (appDef.IsSavedToDB)
                {
                    logsByAddOnId.TryGetValue(appDef.ID, out todaysAddOnLog);
                }
                if (todaysAddOnLog == null)
                {
                    todaysAddOnLog = new TeamAddOnLog()
                    {
                        AddOn = appDef,
                        Team = dbTeam,
                        Date = today
                    };
                    lookupManager.Database.TeamAddOnLogs.Add(todaysAddOnLog);

                    // Track newly-added so a duplicate installation of the same add-on in
                    // this same call resolves to the same log entity, not a second insert.
                    if (appDef.IsSavedToDB)
                    {
                        logsByAddOnId[appDef.ID] = todaysAddOnLog;
                    }
                }
            }
        }
    }
}
