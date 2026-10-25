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

        /// <summary>
        /// Whether the last <see cref="FindGroupsWithTeamToCrawl"/> read groups but not one reported its provisioned
        /// workloads, so no Team could be identified. The Teams section treats that as a failed run, not an empty tenant.
        /// </summary>
        public bool NoGroupReportedWorkloads { get; private set; }

        /// <summary>Groups Graph returned in the last <see cref="FindGroupsWithTeamToCrawl"/>.</summary>
        public int GroupsScanned { get; private set; }

        /// <summary>Groups that have a Team.</summary>
        public int GroupsWithTeam { get; private set; }

        /// <summary>Groups that reported their provisioned workloads and have no Team: distribution lists, security groups, groups without a Team.</summary>
        public int GroupsWithoutTeam { get; private set; }

        /// <summary>Groups that did not report their provisioned workloads at all.</summary>
        public int GroupsNotReported { get; private set; }

        public async Task<List<Group>> FindGroupsWithTeamToCrawl(TeamsCrawlConfig filterConfig)
        {
            if (filterConfig is null)
            {
                throw new ArgumentNullException(nameof(filterConfig));
            }

            NoGroupReportedWorkloads = false;
            GroupsScanned = GroupsWithTeam = GroupsWithoutTeam = GroupsNotReported = 0;

            // For now we're using the V1 endpoint, but leaving the beta-endpoint code in anyway for when it becomes RTM
            bool legacyAPIMode = true;

            var allGroupsWithTeams = new List<Group>();
            _logger.LogInformation($"Searching for groups with a team attached...");

            if (legacyAPIMode)
            {
                var v1Groups = await _groupSource.LoadGroupsWithProvisioningOptions();
                var workloadsNotReported = 0;
                var noTeam = 0;

                foreach (var group in v1Groups)
                {
                    // Filter v1 groups by those that have a Team. Classified one group at a time, deliberately: partitioning
                    // the whole list first would swallow the classification of earlier groups if a later group's
                    // resourceProvisioningOptions failed to parse.
                    var status = TeamsCrawlRules.ClassifyGroup(group);
                    if (status == GroupTeamStatus.HasTeam)
                    {
                        allGroupsWithTeams.Add(group);
                    }
                    else if (status == GroupTeamStatus.NoTeam)
                    {
                        // Distribution lists, security groups and Microsoft 365 groups without a Team: thousands on a large
                        // tenant, so one summary line below carries the count and the name is Debug only.
                        noTeam++;
                        _logger.LogDebug($"Group name '{group.DisplayName}' has no Team associated.");
                    }
                    else
                    {
                        // GroupTeamStatus.WorkloadsNotReported: neither crawled nor logged, as before.
                        workloadsNotReported++;
                    }
                }

                _logger.LogInformation($"TeamsGroupScan: groups={v1Groups.Count:N0}; withTeam={allGroupsWithTeams.Count:N0}; noTeam={noTeam:N0}; notReported={workloadsNotReported:N0}.");
                GroupsScanned = v1Groups.Count;
                GroupsWithTeam = allGroupsWithTeams.Count;
                GroupsWithoutTeam = noTeam;
                GroupsNotReported = workloadsNotReported;

                // Microsoft 365 groups carry the property, if only as an empty array. If not one group did, the importer is
                // almost certainly not reading it where the SDK puts it - which is how a Graph SDK upgrade once made the
                // crawl import no Teams at all while reporting success. Say so rather than look like a tenant with no Teams.
                if (v1Groups.Count > 0 && workloadsNotReported == v1Groups.Count)
                {
                    NoGroupReportedWorkloads = true;
                    _logger.LogWarning($"Teams import: Graph returned {v1Groups.Count:N0} group(s) but none of them reported its provisioned " +
                        $"workloads ('{TeamsCrawlRules.ResourceProvisioningOptionsProperty}'), so no Team could be identified and none will be imported " +
                        "this cycle. Unless the tenant genuinely has no Microsoft 365 groups, this is an importer fault, not a tenant without Teams.");
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
                _logger.LogDebug($"Excluding group '{g.DisplayName}' from crawl due to crawl configuration");
            }
            if (crawlPartition.Excluded.Count > 0)
            {
                _logger.LogInformation($"TeamsGroupScan: {crawlPartition.Excluded.Count:N0} of {allGroupsWithTeams.Count:N0} Team(s) excluded by the crawl configuration; {crawlPartition.ToCrawl.Count:N0} to crawl.");
            }

            return crawlPartition.ToCrawl;
        }
    }
}
