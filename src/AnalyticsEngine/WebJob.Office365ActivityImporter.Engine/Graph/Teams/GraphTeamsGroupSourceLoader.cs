using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Graph implementation of <see cref="ITeamsGroupSourceLoader"/>. Holds the paging that used to
    /// live inside <see cref="TeamsFinder"/> so the finder itself is left with only the selection
    /// rules. See issue #377.
    /// </summary>
    public class GraphTeamsGroupSourceLoader : ITeamsGroupSourceLoader
    {
        private readonly GraphServiceClient _graphServiceClient;
        private readonly ILogger _logger;

        public GraphTeamsGroupSourceLoader(GraphServiceClient graphServiceClient, ILogger logger)
        {
            _graphServiceClient = graphServiceClient ?? throw new ArgumentNullException(nameof(graphServiceClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<List<Group>> LoadGroupsWithProvisioningOptions()
        {
            return GetGroupsAsync(rc =>
            {
                rc.QueryParameters.Select = new[] { "displayName", "id", TeamsCrawlRules.ResourceProvisioningOptionsProperty };
            });
        }

        public Task<List<Group>> LoadGroupsFilteredToTeams()
        {
            // Beta API uses a much cleaner search for groups with a Team
            return GetGroupsAsync(rc =>
            {
                rc.QueryParameters.Filter = "resourceProvisioningOptions/Any(x:x eq 'Team')";
            });
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
                    return TeamsCrawlPagingPolicy.ShouldContinuePaging(loaded, TeamsCrawlPagingPolicy.MaxGroups);
                });

            await iterator.IterateAsync();

            if (iterator.State == PagingState.Paused)
            {
                _logger.LogWarning($"TeamsFinder: hit MAX_GROUPS ({TeamsCrawlPagingPolicy.MaxGroups:N0}) walking groups. Returning partial list of {allGroups.Count:N0}.");
            }

            return allGroups;
        }
    }
}
