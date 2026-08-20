using Common.Entities.Config;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory
{
    /// <summary>
    /// Resolves the pilot group(s) named by <c>UserGroupsFilter</c> to the set of member UPNs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists rather than reusing <see cref="User.UserGroupsCache"/>.</b> That cache answers
    /// "which groups is this user in?", one Graph call per user. Asking it about every user in the database
    /// to find a pilot group would cost one call per tenant user - at the ~200k-user design target that is
    /// 200,000 Graph calls spent *deciding who to import*, before a single interaction is read. It would
    /// defeat the entire point of scoping the feature.
    /// </para>
    /// <para>
    /// Going group-first inverts the cost: enumerate groups once, keep the ones whose display name matches
    /// the filter, then page their members. That is O(groups + pilot members) instead of O(tenant users),
    /// so a 50-person pilot costs roughly a handful of calls no matter how large the tenant is.
    /// </para>
    /// <para>
    /// It also fixes a correctness problem: the per-user cache reads only the first page of a user's
    /// <c>memberOf</c>, so a user in many groups could be wrongly judged "not in the pilot group". Reading
    /// membership from the group side pages properly.
    /// </para>
    /// </remarks>
    public interface IPilotGroupMemberResolver
    {
        /// <summary>
        /// Member UPNs of every group whose display name matches <paramref name="filter"/>. Returns an empty
        /// set when nothing matches. Comparison is case-insensitive so it lines up with SQL Server's default
        /// collation when the results are matched against the users table.
        /// </summary>
        Task<HashSet<string>> GetMemberUpnsAsync(UserGroupsFilterModel filter);
    }

    /// <summary>Microsoft Graph implementation of <see cref="IPilotGroupMemberResolver"/>.</summary>
    public class GraphPilotGroupMemberResolver : IPilotGroupMemberResolver
    {
        /// <summary>Graph's maximum page size for directory objects.</summary>
        private const int GraphPageSize = 999;

        /// <summary>
        /// Safety cap on group pages. Filtering happens client-side on display name (so that the '*'
        /// wildcards the rest of the product uses keep working), which means the group list is enumerated;
        /// this bounds that on a very large directory.
        /// </summary>
        private const int MaxGroupPages = 50;

        /// <summary>Safety cap on member pages per group, so one enormous group can't dominate a cycle.</summary>
        private const int MaxMemberPagesPerGroup = 50;

        private readonly ManualGraphCallClient _httpClient;
        private readonly ILogger _logger;

        public GraphPilotGroupMemberResolver(ManualGraphCallClient httpClient, ILogger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger;
        }

        public async Task<HashSet<string>> GetMemberUpnsAsync(UserGroupsFilterModel filter)
        {
            var upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (filter == null || filter.Patterns.Count == 0)
                return upns;

            var groups = await LoadMatchingGroupsAsync(filter);
            if (groups.Count == 0)
            {
                _logger.LogWarning(
                    $"Copilot interaction history: no Entra ID group matched UserGroupsFilter " +
                    $"('{string.Join(";", filter.Patterns)}'). The filter matches group *display names* and " +
                    "supports '*' wildcards.");
                return upns;
            }

            foreach (var group in groups)
            {
                var before = upns.Count;
                await AddGroupMembersAsync(group, upns);
                _logger.LogInformation(
                    $"Copilot interaction history: group '{group.DisplayName}' contributed {upns.Count - before} member(s) to the pilot scope.");
            }

            return upns;
        }

        private async Task<List<GraphGroup>> LoadMatchingGroupsAsync(UserGroupsFilterModel filter)
        {
            var matched = new List<GraphGroup>();
            var url = $"https://graph.microsoft.com/v1.0/groups?$select=id,displayName&$top={GraphPageSize}";
            var pages = 0;

            while (!string.IsNullOrEmpty(url))
            {
                if (++pages > MaxGroupPages)
                {
                    _logger.LogWarning(
                        $"Copilot interaction history: stopped enumerating Entra ID groups after {MaxGroupPages} pages. " +
                        "If the pilot group wasn't found, narrow UserGroupsFilter or raise this limit.");
                    break;
                }

                var page = await _httpClient.GetAsyncWithThrottleRetries<PageableGraphResponse<GraphGroup>>(url);
                if (page?.PageResults == null)
                    break;

                foreach (var group in page.PageResults)
                {
                    if (!string.IsNullOrEmpty(group.DisplayName) && filter.Matches(group.DisplayName))
                        matched.Add(group);
                }

                url = page.OdataNextLink;
            }

            return matched;
        }

        private async Task AddGroupMembersAsync(GraphGroup group, HashSet<string> upns)
        {
            // Restrict to user members: a group can also contain nested groups, devices and service
            // principals, none of which have Copilot interaction history.
            var url = $"https://graph.microsoft.com/v1.0/groups/{Uri.EscapeDataString(group.Id)}" +
                      $"/members/microsoft.graph.user?$select=id,userPrincipalName&$top={GraphPageSize}";
            var pages = 0;

            while (!string.IsNullOrEmpty(url))
            {
                if (++pages > MaxMemberPagesPerGroup)
                {
                    _logger.LogWarning(
                        $"Copilot interaction history: stopped reading members of '{group.DisplayName}' after " +
                        $"{MaxMemberPagesPerGroup} pages. This group looks too large to be a pilot group.");
                    return;
                }

                PageableGraphResponse<GraphGroupMember> page;
                try
                {
                    page = await _httpClient.GetAsyncWithThrottleRetries<PageableGraphResponse<GraphGroupMember>>(url);
                }
                catch (Exception ex)
                {
                    // A group we can't read shouldn't abort the whole import; the others still resolve.
                    _logger.LogWarning(
                        $"Copilot interaction history: could not read members of group '{group.DisplayName}' " +
                        $"({ex.GetType().Name}). Skipping that group for this cycle.");
                    return;
                }

                if (page?.PageResults == null)
                    return;

                foreach (var member in page.PageResults)
                {
                    if (!string.IsNullOrWhiteSpace(member.UserPrincipalName))
                        upns.Add(member.UserPrincipalName.Trim());
                }

                url = page.OdataNextLink;
            }
        }

        private class GraphGroup
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("displayName")]
            public string DisplayName { get; set; }
        }

        private class GraphGroupMember
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("userPrincipalName")]
            public string UserPrincipalName { get; set; }
        }
    }
}
