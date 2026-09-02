using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public class TeamsFinder : AbstractApiLoader
    {
        private readonly ITeamsGroupSourceLoader _groupSource;

        /// <summary>
        /// Production constructor: reads groups straight from Graph.
        /// </summary>
        public TeamsFinder(AnalyticsLogger logger, AppConfig settings, GraphServiceClient graphServiceClient)
            : this(logger, settings, new GraphTeamsGroupSourceLoader(graphServiceClient, logger)) { }

        /// <summary>
        /// Constructor taking the group source as a port, so the crawl-selection rules can be exercised
        /// without Graph. See issue #377.
        /// </summary>
        public TeamsFinder(AnalyticsLogger logger, AppConfig settings, ITeamsGroupSourceLoader groupSource) : base(logger, settings)
        {
            _groupSource = groupSource ?? throw new ArgumentNullException(nameof(groupSource));
        }

        public async Task<List<Group>> FindGroupsWithTeamToCrawl(TeamsCrawlConfig filterConfig)
        {
            if (filterConfig is null)
            {
                throw new ArgumentNullException(nameof(filterConfig));
            }

            // For now we're using the V1 endpoint, but leaving the beta-endpoint code in anyway for when it becomes RTM
            bool legacyAPIMode = true;

            var allGroupsWithTeams = new List<Group>();
            _logger.LogInformation($"Searching for groups with a team attached...");

            if (legacyAPIMode)
            {
                var v1Groups = await _groupSource.LoadGroupsWithProvisioningOptions();

                // Filter v1 groups by those that have a Team
                var teamPartition = TeamsCrawlRules.PartitionGroupsWithTeams(v1Groups);
                allGroupsWithTeams = teamPartition.WithTeam;

                foreach (var group in teamPartition.WithoutTeam)
                {
                    _logger.LogInformation($"Group name '{group.DisplayName}' has no Team associated.");
                }
            }
            else
            {
                // Beta API uses a much cleaner search for groups with a Team
                allGroupsWithTeams = await _groupSource.LoadGroupsFilteredToTeams();
            }

            // Do the needful
            _logger.LogInformation($"Searching for groups with a team attached...");

            var crawlPartition = TeamsCrawlRules.PartitionByCrawlConfig(allGroupsWithTeams, filterConfig);
            foreach (var g in crawlPartition.Excluded)
            {
                _logger.LogInformation($"Excluding group '{g.DisplayName}' from crawl due to crawl configuration");
            }

            return crawlPartition.ToCrawl;
        }
    }
}
