using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public class TeamsFinder : AbstractApiLoader
    {
        private readonly GraphServiceClient _graphServiceClient;

        public TeamsFinder(AnalyticsLogger telemetry, AppConfig settings, GraphServiceClient graphServiceClient) : base(telemetry, settings)
        {
            this._graphServiceClient = graphServiceClient;
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
            _telemetry.LogInformation($"Searching for groups with a team attached...");

            if (legacyAPIMode)
            {
                var v1Groups = await GetGroupsAsync(_graphServiceClient.Groups.Request().Select("displayName,id,resourceProvisioningOptions"), legacyAPIMode);

                foreach (var group in v1Groups)
                {
                    // Filter v1 groups by those that have a Team
                    bool groupHasTeam = false;
                    if (group.AdditionalData.ContainsKey("resourceProvisioningOptions"))
                    {
                        var resourceProvisioningOptions = group.AdditionalData["resourceProvisioningOptions"].ToString();
                        var options = Newtonsoft.Json.Linq.JArray.Parse(resourceProvisioningOptions);
                        foreach (var option in options)
                        {
                            if (option.ToString().ToLower() == "team")
                            {
                                allGroupsWithTeams.Add(group);
                                groupHasTeam = true;
                                break;
                            }
                        }
                        if (!groupHasTeam)
                        {
                            _telemetry.LogInformation($"Group name '{group.DisplayName}' has no Team associated.");
                        }
                    }
                }
            }
            else
            {
                // Beta API uses a much cleaner search for groups with a Team
                allGroupsWithTeams = await GetGroupsAsync(_graphServiceClient.Groups.Request().Filter("resourceProvisioningOptions/Any(x:x eq 'Team')"), legacyAPIMode);
            }

            // Do the needful
            _telemetry.LogInformation($"Searching for groups with a team attached...");

            var filteredTeams = new List<Group>();
            foreach (var g in allGroupsWithTeams)
            {
                if (filterConfig.CrawlGroup(g.Id))
                {
                    filteredTeams.Add(g);
                }
                else
                {
                    _telemetry.LogInformation($"Excluding group '{g.DisplayName}' from crawl due to crawl configuration");
                }
            }

            return filteredTeams;
        }

        private async Task<List<Group>> GetGroupsAsync(IGraphServiceGroupsCollectionRequest pageRequest, bool legacyAPIMode)
        {
            var allGroups = new List<Group>();

            // https://docs.microsoft.com/en-us/graph/teams-list-all-teams#get-a-list-of-groups
            var groups = await pageRequest.GetAsync();
            if (groups.NextPageRequest != null)
            {
#if DEBUG
                Console.WriteLine($"DEBUG: Another page for Groups results...");
#endif
                var nextGroups = await GetGroupsAsync(groups.NextPageRequest, legacyAPIMode);
                allGroups.AddRange(nextGroups);
            }

            allGroups.AddRange(groups);

            return allGroups;
        }

    }
}
