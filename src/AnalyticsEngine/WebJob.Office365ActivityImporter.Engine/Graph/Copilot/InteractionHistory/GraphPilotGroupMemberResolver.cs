using Common.Entities.Config;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory
{
    /// <summary>
    /// Resolves the pilot group(s) named by <c>UserGroupsFilter</c> to the set of member UPNs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists rather than reusing the per-user groups cache.</b> That cache answers
    /// "which groups is this user in?", one Graph call per user. Asking it about every user in the database
    /// to find a pilot group would cost one call per tenant user - at the ~200k-user design target that is
    /// 200,000 Graph calls spent *deciding who to import*, before a single interaction is read. It would
    /// defeat the entire point of scoping the feature.
    /// </para>
    /// <para>
    /// Going group-first inverts the cost: find the matching groups, then page their members. That is
    /// O(matched groups + pilot members) instead of O(tenant users), so a 50-person pilot costs a handful
    /// of calls no matter how large the tenant is.
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
        /// Member UPNs of every group matching <paramref name="filter"/>, plus whether the answer is
        /// complete. Comparison is case-insensitive so it lines up with SQL Server's default collation
        /// when the results are matched against the users table.
        /// </summary>
        Task<PilotGroupResolution> GetMemberUpnsAsync(UserGroupsFilterModel filter);
    }

    /// <summary>Microsoft Graph implementation of <see cref="IPilotGroupMemberResolver"/>.</summary>
    /// <remarks>
    /// <para>
    /// Patterns are resolved by the cheapest route that can answer them (issue #297):
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// A pattern that is a GUID is a group <b>object id</b> - fetched directly, one call, no ambiguity.
    /// This is the form to prefer: display names are not unique in Entra ID and can be renamed.
    /// </description></item>
    /// <item><description>
    /// A pattern with no <c>*</c> is an <b>exact display name</b> - resolved server-side with
    /// <c>$filter=displayName eq '...'</c>, one call, regardless of directory size.
    /// </description></item>
    /// <item><description>
    /// Only a pattern that genuinely contains a <c>*</c> needs the directory enumerating, because Graph
    /// cannot express the product's wildcard syntax as a server-side filter.
    /// </description></item>
    /// </list>
    /// <para>
    /// That ordering matters at scale. Enumeration was previously the *only* path, so an exactly-named
    /// pilot group on a 200k-user tenant paged the entire group list to find one group - and if the
    /// directory held more groups than the page cap allowed, the group could sit past the cap and never
    /// be found at all, which surfaced as a silent no-op.
    /// </para>
    /// <para>
    /// Every call, of every kind, is drawn from one shared budget. Group discovery and member paging used
    /// to be capped separately, so the caps multiplied: ~50 group pages x ~999 groups x 50 member pages
    /// each is a worst case in the millions of calls. A single total budget cannot be multiplied out.
    /// </para>
    /// </remarks>
    public class GraphPilotGroupMemberResolver : IPilotGroupMemberResolver
    {
        /// <summary>Graph's maximum page size for directory objects.</summary>
        private const int GraphPageSize = 999;

        /// <summary>
        /// Total Graph calls this resolver may make in one cycle, shared across group discovery and member
        /// paging. One budget rather than per-stage caps, so no combination of them can multiply out.
        /// Generous for any realistic pilot: an exactly-named group of 5,000 members costs about 7 calls.
        /// </summary>
        private const int MaxTotalGraphCalls = 200;

        /// <summary>
        /// Safety cap on group pages when a wildcard pattern forces enumeration. Only reached by wildcards;
        /// exact names and object ids never enumerate.
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

        public async Task<PilotGroupResolution> GetMemberUpnsAsync(UserGroupsFilterModel filter)
        {
            var upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (filter == null || filter.Patterns.Count == 0)
                return new PilotGroupResolution(upns);

            // A match-all filter ('*') is not a narrowing, and the caller is expected to have taken the
            // unnarrowed path instead of paying for a directory enumeration that concludes "everyone".
            // Refused rather than honoured so it can never become an expensive silent default.
            if (filter.MatchesEverything)
            {
                return new PilotGroupResolution(upns,
                    "UserGroupsFilter matches every group ('*'), which is not a pilot scope. Remove the filter " +
                    "to import across the directory, or name the pilot group(s).");
            }

            var budget = new CallBudget(MaxTotalGraphCalls);
            var groups = await LoadMatchingGroupsAsync(filter, budget);

            if (groups.Count == 0)
            {
                if (budget.Exhausted)
                {
                    return new PilotGroupResolution(upns, budget.Reason);
                }

                _logger.LogWarning(
                    $"Copilot interaction history: no Entra ID group matched UserGroupsFilter " +
                    $"('{string.Join(";", filter.Patterns)}'). The filter matches group *display names* and " +
                    "supports '*' wildcards; a group object id can be used instead and is matched exactly.");
                return new PilotGroupResolution(upns);
            }

            foreach (var group in groups)
            {
                if (budget.Exhausted)
                    break;

                var before = upns.Count;
                await AddGroupMembersAsync(group, upns, budget);
                _logger.LogInformation(
                    $"Copilot interaction history: group '{group.DisplayName}' contributed {upns.Count - before} member(s) to the pilot scope.");
            }

            return new PilotGroupResolution(upns, budget.Reason);
        }

        private async Task<List<GraphGroup>> LoadMatchingGroupsAsync(UserGroupsFilterModel filter, CallBudget budget)
        {
            // Keyed by object id: the same group can be named by an id and by a display name, and two
            // wildcard patterns can both match it.
            var matched = new Dictionary<string, GraphGroup>(StringComparer.OrdinalIgnoreCase);
            var wildcardPatterns = new List<string>();

            foreach (var pattern in filter.Patterns)
            {
                if (budget.Exhausted)
                    return matched.Values.ToList();

                if (pattern.Contains("*"))
                {
                    wildcardPatterns.Add(pattern);
                }
                else if (Guid.TryParse(pattern, out var groupId))
                {
                    var group = await LoadGroupByIdAsync(groupId, budget);
                    if (group != null)
                        matched[group.Id] = group;
                }
                else
                {
                    foreach (var group in await LoadGroupsByExactNameAsync(pattern, budget))
                        matched[group.Id] = group;
                }
            }

            if (wildcardPatterns.Count > 0 && !budget.Exhausted)
            {
                foreach (var group in await EnumerateGroupsAsync(wildcardPatterns, budget))
                    matched[group.Id] = group;
            }

            return matched.Values.ToList();
        }

        /// <summary>Direct lookup by object id - one call, exact, and immune to a group being renamed.</summary>
        private async Task<GraphGroup> LoadGroupByIdAsync(Guid groupId, CallBudget budget)
        {
            var url = $"https://graph.microsoft.com/v1.0/groups/{groupId}?$select=id,displayName";
            try
            {
                budget.Spend();
                var group = await _httpClient.GetAsyncWithThrottleRetries<GraphGroup>(url);
                if (group == null || string.IsNullOrEmpty(group.Id))
                {
                    _logger.LogWarning(
                        $"Copilot interaction history: no Entra ID group with object id '{groupId}' was found.");
                    return null;
                }
                return group;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Copilot interaction history: could not read the group with object id '{groupId}' " +
                    $"({ex.GetType().Name}). Check the id is a group, and that the runtime identity can read it.");
                return null;
            }
        }

        /// <summary>
        /// Exact display-name lookup, server-side. One call whatever the directory size, so this never
        /// depends on a group appearing before the enumeration cap.
        /// </summary>
        private async Task<List<GraphGroup>> LoadGroupsByExactNameAsync(string displayName, CallBudget budget)
        {
            // OData string literals escape a single quote by doubling it. Without this a group named
            // "Bob's pilot" would produce a malformed filter and a 400.
            var literal = displayName.Replace("'", "''");
            var url = "https://graph.microsoft.com/v1.0/groups" +
                      $"?$filter=displayName eq '{Uri.EscapeDataString(literal)}'" +
                      $"&$select=id,displayName&$top={GraphPageSize}";

            try
            {
                budget.Spend();
                var page = await _httpClient.GetAsyncWithThrottleRetries<PageableGraphResponse<GraphGroup>>(url);
                var results = page?.PageResults?.Where(g => !string.IsNullOrEmpty(g.Id)).ToList()
                    ?? new List<GraphGroup>();

                if (results.Count == 0)
                {
                    _logger.LogWarning(
                        $"Copilot interaction history: no Entra ID group is named '{displayName}'. The match is " +
                        "on the group's display name and is exact unless the pattern contains '*'.");
                }
                else if (results.Count > 1)
                {
                    // Display names are not unique in Entra ID. Say so rather than quietly taking them all.
                    _logger.LogWarning(
                        $"Copilot interaction history: {results.Count} Entra ID groups are named '{displayName}'. " +
                        "All of them are being treated as pilot groups - use the group's object id instead to " +
                        "select exactly one.");
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Copilot interaction history: could not look up the group named '{displayName}' " +
                    $"({ex.GetType().Name}).");
                return new List<GraphGroup>();
            }
        }

        /// <summary>
        /// The fallback for genuine wildcard patterns: page the directory and match client-side, because
        /// Graph has no server-side equivalent of the product's '*' syntax.
        /// </summary>
        private async Task<List<GraphGroup>> EnumerateGroupsAsync(List<string> wildcardPatterns, CallBudget budget)
        {
            var matched = new List<GraphGroup>();
            var matcher = new UserGroupsFilterModel(string.Join(";", wildcardPatterns));
            var url = $"https://graph.microsoft.com/v1.0/groups?$select=id,displayName&$top={GraphPageSize}";
            var pages = 0;

            while (!string.IsNullOrEmpty(url))
            {
                if (++pages > MaxGroupPages)
                {
                    budget.Stop(
                        $"group discovery stopped after {MaxGroupPages} pages while expanding the wildcard " +
                        $"pattern(s) '{string.Join(";", wildcardPatterns)}'. Groups beyond that point were not " +
                        "considered, so the pilot scope may be incomplete. Name the group exactly, or use its " +
                        "object id, to resolve it without enumerating the directory.");
                    break;
                }

                if (budget.Exhausted)
                    break;

                PageableGraphResponse<GraphGroup> page;
                try
                {
                    budget.Spend();
                    page = await _httpClient.GetAsyncWithThrottleRetries<PageableGraphResponse<GraphGroup>>(url);
                }
                catch (Exception ex)
                {
                    budget.Stop($"group discovery failed ({ex.GetType().Name}: {ex.Message}).");
                    break;
                }

                if (page?.PageResults == null)
                    break;

                foreach (var group in page.PageResults)
                {
                    if (!string.IsNullOrEmpty(group.Id) && !string.IsNullOrEmpty(group.DisplayName)
                        && matcher.Matches(group.DisplayName))
                    {
                        matched.Add(group);
                    }
                }

                url = page.OdataNextLink;
            }

            return matched;
        }

        private async Task AddGroupMembersAsync(GraphGroup group, HashSet<string> upns, CallBudget budget)
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
                    budget.Stop(
                        $"reading members of '{group.DisplayName}' stopped after {MaxMemberPagesPerGroup} pages. " +
                        "This group looks too large to be a pilot group.");
                    return;
                }

                if (budget.Exhausted)
                    return;

                PageableGraphResponse<GraphGroupMember> page;
                try
                {
                    budget.Spend();
                    page = await _httpClient.GetAsyncWithThrottleRetries<PageableGraphResponse<GraphGroupMember>>(url);
                }
                catch (Exception ex)
                {
                    // A group we can't read shouldn't abort the whole import; the others still resolve. It
                    // does make the scope incomplete, though, so it is recorded rather than swallowed.
                    budget.Stop(
                        $"members of group '{group.DisplayName}' could not be read ({ex.GetType().Name}), so that " +
                        "group contributed nobody to the pilot scope.");
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

        /// <summary>
        /// One shared allowance for every Graph call made while resolving the scope, plus the reason
        /// resolution stopped early. Separate per-stage caps multiply together; a single total cannot.
        /// </summary>
        private class CallBudget
        {
            private int _remaining;

            public CallBudget(int total)
            {
                _remaining = total;
            }

            /// <summary>Why resolution stopped early, or null while it is still complete.</summary>
            public string Reason { get; private set; }

            public bool Exhausted => Reason != null;

            public void Spend()
            {
                if (--_remaining < 0)
                {
                    Stop($"the resolver's budget of {MaxTotalGraphCalls} Graph calls for working out the pilot " +
                         "scope was used up. Narrow UserGroupsFilter to the pilot group(s), or use group " +
                         "object ids, so the scope resolves in a handful of calls.");
                }
            }

            /// <summary>Records the first reason resolution stopped; later ones are consequences of it.</summary>
            public void Stop(string reason)
            {
                if (Reason == null)
                    Reason = reason;
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
