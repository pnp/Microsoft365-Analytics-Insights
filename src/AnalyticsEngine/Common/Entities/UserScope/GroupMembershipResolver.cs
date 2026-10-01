using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.UserScope
{
    /// <summary>Resolves <c>UserGroupsFilter</c> to the people it covers.</summary>
    public interface IGroupMembershipResolver
    {
        /// <summary>Never throws: a failure is reported as <see cref="UserImportScopeStatus.Unavailable"/>.</summary>
        Task<UserImportScopeResolution> ResolveAsync(UserGroupsFilterModel filter);
    }

    /// <summary>
    /// Resolves <c>UserGroupsFilter</c> group-first: find the matching groups, then page their direct user members.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why group-first.</b> The alternative - asking "which groups is this user in?" for every user an import
    /// meets - is one Graph call per user. At the ~200,000-user design target that is 200,000 calls spent deciding
    /// who to import. Group-first costs one call per 999 members plus a few group lookups, whatever the tenant
    /// size, and pages properly; the per-user <c>memberOf</c> read only ever looked at a user's first page of
    /// groups, so someone in many groups could be judged out of scope wrongly.
    /// </para>
    /// <para>
    /// Patterns are resolved by the cheapest route that can answer them (issue #297): a GUID is a group object
    /// id, fetched directly; a pattern without <c>*</c> is an exact display name, resolved server-side; only a
    /// genuine wildcard pages the group list, because Graph cannot express the product's <c>*</c> syntax as a
    /// filter. Every call of every kind is drawn from one shared budget, so no combination of caps multiplies out.
    /// </para>
    /// <para>
    /// <b>A group that does not exist is an answer, a failed read is not.</b> A filter naming no real group
    /// resolves to nobody (and says so), because that is what the filter says. A read that fails - missing
    /// permission, an outage, an exhausted budget - makes the whole resolution
    /// <see cref="UserImportScopeStatus.Unavailable"/>, because treating an unreadable group as an empty one would
    /// silently remove its members from every import.
    /// </para>
    /// </remarks>
    public class GroupMembershipResolver : IGroupMembershipResolver
    {
        /// <summary>
        /// Total Graph calls one resolution may make. Sized for a filter naming a group of a few hundred
        /// thousand people (about one call per 999 members), not just a pilot group.
        /// </summary>
        public const int DefaultMaxTotalGraphCalls = 1000;

        /// <summary>Cap on group-list pages when a wildcard forces enumeration. Exact names and ids never enumerate.</summary>
        public const int DefaultMaxGroupPages = 50;

        /// <summary>Cap on member pages for one group - about 500,000 members.</summary>
        public const int DefaultMaxMemberPagesPerGroup = 500;

        private readonly IGroupDirectoryReader _reader;
        private readonly ILogger _logger;
        private readonly IClock _clock;
        private readonly int _maxTotalGraphCalls;
        private readonly int _maxGroupPages;
        private readonly int _maxMemberPagesPerGroup;

        public GroupMembershipResolver(IGroupDirectoryReader reader, ILogger logger, IClock clock = null,
            int maxTotalGraphCalls = DefaultMaxTotalGraphCalls,
            int maxGroupPages = DefaultMaxGroupPages,
            int maxMemberPagesPerGroup = DefaultMaxMemberPagesPerGroup)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _logger = logger ?? NullLogger.Instance;
            _clock = clock ?? SystemClock.Instance;
            _maxTotalGraphCalls = maxTotalGraphCalls > 0 ? maxTotalGraphCalls : DefaultMaxTotalGraphCalls;
            _maxGroupPages = maxGroupPages > 0 ? maxGroupPages : DefaultMaxGroupPages;
            _maxMemberPagesPerGroup = maxMemberPagesPerGroup > 0 ? maxMemberPagesPerGroup : DefaultMaxMemberPagesPerGroup;
        }

        public async Task<UserImportScopeResolution> ResolveAsync(UserGroupsFilterModel filter)
        {
            // A missing, blank or match-everything filter narrows nothing. Resolving '*' group-first would page
            // the whole directory and every group's membership only to conclude "everyone" (issue #297).
            if (filter == null || !filter.IsNarrowing)
            {
                return UserImportScopeResolution.Unfiltered(_clock.UtcNow);
            }

            var members = new UserScopeMembers();
            var budget = new CallBudget(_maxTotalGraphCalls);
            var unmatchedPatterns = new List<string>();
            var groups = new List<MatchedGroup>();

            try
            {
                groups = await LoadMatchingGroupsAsync(filter, budget, unmatchedPatterns);

                foreach (var group in groups)
                {
                    if (budget.Exhausted)
                    {
                        break;
                    }

                    var before = members.Count;
                    group.UserMemberCount = await AddGroupMembersAsync(group.Group, members, budget);
                    _logger.LogInformation($"User import scope: group '{group.Group.DisplayName}' has {group.UserMemberCount:N0} " +
                        $"direct user member(s), {members.Count - before:N0} not already in scope through another group.");
                }
            }
            catch (Exception ex)
            {
                // Defensive: the loaders below record failures on the budget rather than throwing, so this is
                // only reachable through a bug. It still must not escape, or an import would crash on it.
                return UserImportScopeResolution.Unavailable(members, Summaries(groups), $"{ex.GetType().Name}: {ex.Message}", _clock.UtcNow,
                    UserImportScopeFailureKind.Unexpected);
            }

            if (budget.Exhausted)
            {
                return UserImportScopeResolution.Unavailable(members, Summaries(groups), budget.Reason, _clock.UtcNow, budget.Kind, budget.HttpStatus);
            }

            return UserImportScopeResolution.Resolved(members, Summaries(groups), unmatchedPatterns, _clock.UtcNow);
        }

        private static IReadOnlyList<ResolvedGroup> Summaries(IEnumerable<MatchedGroup> groups)
            => groups.Select(g => new ResolvedGroup(g.Group.Id, g.Group.DisplayName, g.Patterns, g.UserMemberCount)).ToList();

        /// <summary>A matched group, the patterns that selected it and how many user members it has.</summary>
        private sealed class MatchedGroup
        {
            public MatchedGroup(DirectoryGroup group)
            {
                Group = group;
            }

            public DirectoryGroup Group { get; }
            public List<string> Patterns { get; } = new List<string>();
            public int UserMemberCount { get; set; }
        }

        private async Task<List<MatchedGroup>> LoadMatchingGroupsAsync(UserGroupsFilterModel filter, CallBudget budget, List<string> unmatchedPatterns)
        {
            // Keyed by object id: the same group can be named by an id and by a display name, and two wildcard
            // patterns can both match it.
            var matched = new Dictionary<string, MatchedGroup>(StringComparer.OrdinalIgnoreCase);
            var wildcardPatterns = new List<string>();

            void Match(DirectoryGroup group, string pattern)
            {
                if (!matched.TryGetValue(group.Id, out var entry))
                {
                    entry = new MatchedGroup(group);
                    matched[group.Id] = entry;
                }
                if (!entry.Patterns.Contains(pattern))
                {
                    entry.Patterns.Add(pattern);
                }
            }

            foreach (var pattern in filter.Patterns)
            {
                if (budget.Exhausted)
                {
                    return matched.Values.ToList();
                }

                if (pattern.Contains("*"))
                {
                    wildcardPatterns.Add(pattern);
                }
                else if (Guid.TryParse(pattern, out var groupId))
                {
                    var group = await LoadGroupByIdAsync(groupId, budget);
                    if (group != null)
                    {
                        Match(group, pattern);
                    }
                    else if (!budget.Exhausted)
                    {
                        unmatchedPatterns.Add(pattern);
                    }
                }
                else
                {
                    var byName = await LoadGroupsByExactNameAsync(pattern, budget);
                    foreach (var group in byName)
                    {
                        Match(group, pattern);
                    }
                    if (byName.Count == 0 && !budget.Exhausted)
                    {
                        unmatchedPatterns.Add(pattern);
                    }
                }
            }

            if (wildcardPatterns.Count > 0 && !budget.Exhausted)
            {
                var enumerated = await EnumerateGroupsAsync(wildcardPatterns, budget);
                foreach (var hit in enumerated)
                {
                    Match(hit.Key, hit.Value);
                }

                if (!budget.Exhausted)
                {
                    unmatchedPatterns.AddRange(wildcardPatterns.Where(p => !enumerated.Any(e => e.Value == p)));
                }
            }

            return matched.Values.ToList();
        }

        /// <summary>Direct lookup by object id - one call, exact, and immune to a group being renamed.</summary>
        private async Task<DirectoryGroup> LoadGroupByIdAsync(Guid groupId, CallBudget budget)
        {
            if (!budget.TrySpend())
            {
                return null;
            }

            try
            {
                var group = await _reader.GetGroupByIdAsync(groupId);
                if (group == null)
                {
                    _logger.LogWarning($"User import scope: no Entra ID group has the object id '{groupId}'.");
                }
                return group;
            }
            catch (Exception ex)
            {
                budget.Stop($"the group with object id '{groupId}' could not be read ({Describe(ex)})", ex);
                return null;
            }
        }

        /// <summary>
        /// Exact display-name lookup, server-side. One call whatever the directory size, so this never depends on
        /// a group appearing before the enumeration cap.
        /// </summary>
        private async Task<IReadOnlyList<DirectoryGroup>> LoadGroupsByExactNameAsync(string displayName, CallBudget budget)
        {
            if (!budget.TrySpend())
            {
                return new List<DirectoryGroup>();
            }

            try
            {
                var results = await _reader.FindGroupsByDisplayNameAsync(displayName);
                if (results.Count == 0)
                {
                    _logger.LogWarning($"User import scope: no Entra ID group is named '{displayName}'. The match is on the " +
                        "group's display name and is exact unless the pattern contains '*'; a group object id can be used instead.");
                }
                else if (results.Count > 1)
                {
                    // Display names are not unique in Entra ID. Say so rather than quietly taking them all.
                    _logger.LogWarning($"User import scope: {results.Count} Entra ID groups are named '{displayName}'. All of " +
                        "them are in the scope - use the group's object id instead to select exactly one.");
                }
                return results;
            }
            catch (Exception ex)
            {
                budget.Stop($"the group named '{displayName}' could not be looked up ({Describe(ex)})", ex);
                return new List<DirectoryGroup>();
            }
        }

        /// <summary>
        /// The fallback for genuine wildcard patterns: page the directory and match client-side, because Graph
        /// has no server-side equivalent of the product's '*' syntax. Returns each (group, pattern) match.
        /// </summary>
        private async Task<List<KeyValuePair<DirectoryGroup, string>>> EnumerateGroupsAsync(List<string> wildcardPatterns, CallBudget budget)
        {
            var matched = new List<KeyValuePair<DirectoryGroup, string>>();
            var matchers = wildcardPatterns.Select(p => new KeyValuePair<string, UserGroupsFilterModel>(p, new UserGroupsFilterModel(p))).ToList();
            string nextLink = null;
            var pages = 0;

            do
            {
                if (++pages > _maxGroupPages)
                {
                    budget.Stop($"group discovery stopped after {_maxGroupPages} pages while expanding the wildcard " +
                        $"pattern(s) '{string.Join(";", wildcardPatterns)}', so groups beyond that point were not considered. " +
                        "Name the group exactly, or use its object id, to resolve it without enumerating the directory",
                        UserImportScopeFailureKind.BudgetExhausted);
                    break;
                }

                if (!budget.TrySpend())
                {
                    break;
                }

                DirectoryPage<DirectoryGroup> page;
                try
                {
                    page = await _reader.ListGroupsAsync(nextLink);
                }
                catch (Exception ex)
                {
                    budget.Stop($"group discovery failed ({Describe(ex)})", ex);
                    break;
                }

                foreach (var group in page?.Items ?? new List<DirectoryGroup>())
                {
                    if (string.IsNullOrEmpty(group?.Id) || string.IsNullOrEmpty(group.DisplayName))
                    {
                        continue;
                    }
                    foreach (var matcher in matchers)
                    {
                        if (matcher.Value.Matches(group.DisplayName))
                        {
                            matched.Add(new KeyValuePair<DirectoryGroup, string>(group, matcher.Key));
                        }
                    }
                }

                nextLink = page?.NextLink;
            }
            while (!string.IsNullOrEmpty(nextLink));

            if (matched.Count == 0 && !budget.Exhausted)
            {
                _logger.LogWarning($"User import scope: no Entra ID group display name matches the wildcard pattern(s) " +
                    $"'{string.Join(";", wildcardPatterns)}'.");
            }

            return matched;
        }

        /// <summary>Adds the group's direct user members to <paramref name="members"/>; returns how many were read.</summary>
        private async Task<int> AddGroupMembersAsync(DirectoryGroup group, UserScopeMembers members, CallBudget budget)
        {
            string nextLink = null;
            var pages = 0;
            var read = 0;

            do
            {
                if (++pages > _maxMemberPagesPerGroup)
                {
                    budget.Stop($"reading the members of '{group.DisplayName}' stopped after {_maxMemberPagesPerGroup} pages " +
                        $"(about {_maxMemberPagesPerGroup * GraphGroupDirectoryReader.PageSize:N0} people)",
                        UserImportScopeFailureKind.BudgetExhausted);
                    return read;
                }

                if (!budget.TrySpend())
                {
                    return read;
                }

                DirectoryPage<DirectoryUser> page;
                try
                {
                    page = await _reader.ListUserMembersAsync(group.Id, nextLink);
                }
                catch (Exception ex)
                {
                    budget.Stop($"the members of group '{group.DisplayName}' could not be read ({Describe(ex)})", ex);
                    return read;
                }

                foreach (var member in page?.Items ?? new List<DirectoryUser>())
                {
                    if (member != null)
                    {
                        read++;
                        members.Add(member.Id, member.UserPrincipalName, member.Mail, member.AccountEnabled != false);
                    }
                }

                nextLink = page?.NextLink;
            }
            while (!string.IsNullOrEmpty(nextLink));

            return read;
        }

        private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

        /// <summary>
        /// One shared allowance for every Graph call made while resolving the scope, plus the first reason
        /// resolution stopped early. Separate per-stage caps multiply together; a single total cannot.
        /// </summary>
        private class CallBudget
        {
            private readonly int _total;
            private int _remaining;

            public CallBudget(int total)
            {
                _total = total;
                _remaining = total;
            }

            /// <summary>Why resolution stopped early, or null while it is still complete.</summary>
            public string Reason { get; private set; }

            /// <summary>The kind of the first stop; meaningful only once <see cref="Exhausted"/>.</summary>
            public UserImportScopeFailureKind Kind { get; private set; } = UserImportScopeFailureKind.Unexpected;

            /// <summary>The Graph HTTP status behind the first stop, when it was a failed read.</summary>
            public int? HttpStatus { get; private set; }

            public bool Exhausted => Reason != null;

            /// <summary>Takes one call from the budget; false (and stopped) when none are left.</summary>
            public bool TrySpend()
            {
                if (Exhausted)
                {
                    return false;
                }
                if (--_remaining < 0)
                {
                    Stop($"the budget of {_total:N0} Graph calls for resolving UserGroupsFilter was used up. Narrow the " +
                         "filter, or name groups exactly or by object id so they resolve without enumerating the directory",
                         UserImportScopeFailureKind.BudgetExhausted);
                    return false;
                }
                return true;
            }

            /// <summary>Records the first reason resolution stopped; later ones are consequences of it.</summary>
            public void Stop(string reason, UserImportScopeFailureKind kind, int? httpStatus = null)
            {
                if (Reason == null)
                {
                    Reason = reason;
                    Kind = kind;
                    HttpStatus = httpStatus;
                }
            }

            /// <summary>Records a failed Graph read as the reason resolution stopped.</summary>
            public void Stop(string reason, Exception cause)
                => Stop(reason, UserImportScopeFailureKind.DirectoryRead,
                    cause is DirectoryReadException read ? (int)read.StatusCode : (int?)null);
        }
    }
}
