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
        // Safety cap on paging at 200k-user scale: a single tenant should not exceed this
        // many groups in any realistic scenario. Trips a warning rather than letting a
        // misbehaving nextLink fill memory indefinitely.
        private const int MAX_GROUPS = 500_000;

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
                var v1Groups = await GetGroupsAsync(rc =>
                {
                    rc.QueryParameters.Select = new[] { "displayName", "id", "resourceProvisioningOptions" };
                });

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
                allGroupsWithTeams = await GetGroupsAsync(rc =>
                {
                    rc.QueryParameters.Filter = "resourceProvisioningOptions/Any(x:x eq 'Team')";
                });
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

        private async Task<List<Group>> GetGroupsAsync(Action<Microsoft.Kiota.Abstractions.RequestConfiguration<Microsoft.Graph.Groups.GroupsRequestBuilder.GroupsRequestBuilderGetQueryParameters>> configure)
        {
            // v5+ replaces .Request().NextPageRequest walking with PageIterator over the
            // typed CollectionResponse.
            var allGroups = new List<Group>();

            var firstPage = await _graphServiceClient.Groups.GetAsync(configure);
            if (firstPage == null) return allGroups;

            int loaded = 0;
            var iterator = PageIterator<Group, GroupCollectionResponse>
                .CreatePageIterator(_graphServiceClient, firstPage, group =>
                {
                    allGroups.Add(group);
                    loaded++;
                    return loaded < MAX_GROUPS;
                });

            await iterator.IterateAsync();

            if (iterator.State == PagingState.Paused)
            {
                _telemetry.LogWarning($"TeamsFinder: hit MAX_GROUPS ({MAX_GROUPS:N0}) walking groups. Returning partial list of {allGroups.Count:N0}.");
            }

            return allGroups;
        }

    }
}
