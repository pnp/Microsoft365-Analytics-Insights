using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public class TeamsImporter : AbstractApiLoader
    {
        private TeamsFinder _teamsFinder;
        private TeamsLoadContext _context;
        private readonly ITeamsCognitiveStatsLoader _cognitiveStatsLoader;

        /// <param name="userScope">The <c>UserGroupsFilter</c> scope for this crawl; null means unfiltered.</param>
        public TeamsImporter(AnalyticsLogger logger, AppConfig settings, GraphServiceClient graphServiceClient,
            Common.Entities.UserScope.UserImportScope userScope = null)
            : this(logger, settings, graphServiceClient, userScope, null)
        {
        }

        public TeamsImporter(AnalyticsLogger logger, AppConfig settings, GraphServiceClient graphServiceClient,
            Common.Entities.UserScope.UserImportScope userScope, ITeamsCognitiveStatsLoader cognitiveStatsLoader) : base(logger, settings)
        {
            if (logger is null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (graphServiceClient is null)
            {
                throw new ArgumentNullException(nameof(graphServiceClient));
            }

            _context = new TeamsLoadContext(graphServiceClient)
            {
                UserScope = userScope ?? Common.Entities.UserScope.UserImportScope.Unfiltered
            };
            _teamsFinder = new TeamsFinder(logger, settings, graphServiceClient);
            _cognitiveStatsLoader = cognitiveStatsLoader ?? new TeamsCognitiveStatsLoader(new AppConfig(), logger);
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
                threads => _logger.LogInformation($"Loading & saving to SQL {targetGroups.Count} groups/teams over {threads} threads..."));
            _logger.LogInformation($"Teams import complete.\n");
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
                team = await O365Team.LoadTeamFull(parentGroup, _context, _logger, _settings, lookupManager.Database);
            }
            catch (ODataError ex)
            {
                _logger.LogError(ex, $"Couldn't load team from Group {parentGroup.DisplayName}: {ex.Message}");
            }


            if (team != null)
            {
                try
                {
                    await team.SaveToSQL(lookupManager, _settings, _logger, _cognitiveStatsLoader);
                }
                catch (SqlException ex)
                {
                    _logger.LogError(ex, $"Couldn't save Group {parentGroup.DisplayName} to SQL: {ex.Message}");
                }
            }
        }
    }
}
