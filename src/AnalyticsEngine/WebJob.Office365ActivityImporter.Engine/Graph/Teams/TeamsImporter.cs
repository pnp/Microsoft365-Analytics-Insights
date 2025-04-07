using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public class TeamsImporter : AbstractApiLoader
    {
        private TeamsFinder _teamsFinder;
        private TeamsLoadContext _context;

        public TeamsImporter(AnalyticsLogger telemetry, AppConfig settings, GraphServiceClient graphServiceClient) : base(telemetry, settings)
        {
            if (telemetry is null)
            {
                throw new ArgumentNullException(nameof(telemetry));
            }

            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (graphServiceClient is null)
            {
                throw new ArgumentNullException(nameof(graphServiceClient));
            }

            _context = new TeamsLoadContext(graphServiceClient);
            _teamsFinder = new TeamsFinder(telemetry, settings, graphServiceClient);
        }

        /// <summary>
        /// Import with a pre-defined white/blacklist of groups to include/ignore
        /// </summary>
        public async Task RefreshAndSaveAllTeamsData(TeamsCrawlConfig filterConfig)
        {
            var targetGroups = await _teamsFinder.FindGroupsWithTeamToCrawl(filterConfig);

            // Figure out how many threads we'll need
#if DEBUG
            const int MAX_TEAMS_PER_THREAD = 10;
#else
            const int MAX_TEAMS_PER_THREAD = 100;
#endif

            var loader = new ParallelListProcessor<Group>(MAX_TEAMS_PER_THREAD);
            await loader.ProcessListInParallel(targetGroups,
                (threadListChunk, threadIndex) => LoadTeamsChunkThreaded(threadListChunk),
                threads => _telemetry.LogInformation($"Loading & saving to SQL {targetGroups.Count} groups/teams over {threads} threads..."));
            _telemetry.LogInformation($"Teams import complete.\n");
        }

        async Task LoadTeamsChunkThreaded(List<Group> groupsWithTeams)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var lookup = new TeamsAndCallsDBLookupManager(db);

                // Load all the groups with teams
                foreach (var group in groupsWithTeams)
                {
                    await LoadTeamActivityAndSkipIfGraphError(lookup, group);
                }
            }
        }

        async Task LoadTeamActivityAndSkipIfGraphError(TeamsAndCallsDBLookupManager lookupManager, Group parentGroup)
        {
            O365Team team = null;
            try
            {
                team = await O365Team.LoadTeamFull(parentGroup, _context, _telemetry, _settings, lookupManager.Database);
            }
            catch (ServiceException ex)
            {
                _telemetry.LogError(ex, $"Couldn't load team from Group {parentGroup.DisplayName}: {ex.Message}");
            }


            if (team != null)
            {
                try
                {
                    await team.SaveToSQL(lookupManager, _settings, _telemetry);
                }
                catch (SqlException ex)
                {
                    _telemetry.LogError(ex, $"Couldn't save Group {parentGroup.DisplayName} to SQL: {ex.Message}");
                }
            }
        }
    }
}
