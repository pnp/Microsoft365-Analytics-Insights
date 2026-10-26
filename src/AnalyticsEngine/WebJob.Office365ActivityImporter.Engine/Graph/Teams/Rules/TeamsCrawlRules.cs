using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Pure decision logic for "which groups does the Teams crawl visit?", extracted from
    /// <see cref="TeamsFinder"/> and <see cref="TeamsCrawlConfig"/> so it can be unit tested without
    /// Graph or SQL. See issue #377.
    ///
    /// Deliberately a <c>static</c> class rather than an interface: it is a rule, not a dependency
    /// (the convention in issue #381, matching <c>ImportCadenceGate</c> and <c>AuditLogContentDispatcher</c>).
    ///
    /// These methods deliberately do NOT null-check the arguments they iterate or dereference. The
    /// code they were extracted from didn't either, and the resulting <see cref="NullReferenceException"/>
    /// is part of the behaviour being preserved - converting it to an <see cref="ArgumentNullException"/>
    /// would change the exception type and message an operator sees for no benefit.
    /// </summary>
    public static class TeamsCrawlRules
    {
        /// <summary>
        /// Graph property that says which workloads have been provisioned onto an M365 group. A group with a Team has
        /// "Team" in this array. Since Microsoft.Graph 5 the SDK returns it in the typed
        /// <see cref="Group.ResourceProvisioningOptions"/>, not in <c>AdditionalData</c> as v4 did.
        /// </summary>
        public const string ResourceProvisioningOptionsProperty = "resourceProvisioningOptions";

        private const string TeamWorkload = "Team";

        /// <summary>
        /// Whether a group returned by the v1 Graph endpoint has a Team attached, decided from its
        /// <c>resourceProvisioningOptions</c> array. A group that didn't report its workloads at all
        /// counts as not having one - use <see cref="ClassifyGroup"/> when that distinction matters.
        /// </summary>
        public static bool GroupHasTeam(Group group)
        {
            return ClassifyGroup(group) == GroupTeamStatus.HasTeam;
        }

        /// <summary>
        /// Whether a raw <c>resourceProvisioningOptions</c> JSON array (e.g. <c>["Team"]</c>) includes Team.
        /// </summary>
        /// <exception cref="Newtonsoft.Json.JsonReaderException">
        /// The value is not a JSON array. This propagates, exactly as it did before the extraction -
        /// a Graph response we can't parse is a real problem, not something to swallow per group.
        /// </exception>
        public static bool ProvisioningOptionsIncludeTeam(string resourceProvisioningOptionsJson)
        {
            var options = Newtonsoft.Json.Linq.JArray.Parse(resourceProvisioningOptionsJson);
            foreach (var option in options)
            {
                if (option.ToString().ToLower() == "team")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Classify a group returned by the v1 endpoint: does it have a Team, does it definitely not,
        /// or did it not tell us?
        /// </summary>
        /// <remarks>
        /// The three-way answer matters. A group that carries no <c>resourceProvisioningOptions</c>
        /// property at all is neither crawled nor reported as "has no Team associated" - reporting it
        /// would log every security group and distribution list in the tenant on every cycle.
        ///
        /// Classification is per group, rather than a partition of the whole list, so the caller emits
        /// its log line for each group as it goes: a malformed <c>resourceProvisioningOptions</c> on a
        /// later group must not swallow the lines already emitted for earlier ones.
        /// </remarks>
        public static GroupTeamStatus ClassifyGroup(Group group)
        {
            // Microsoft.Graph 5+ (Kiota) deserialises the property into the typed list: null when Graph didn't return
            // it, empty when the group has no workloads. Reading only AdditionalData - where the v4 SDK put it - classed
            // every group as WorkloadsNotReported, so the crawl silently found no Teams at all.
            var provisioningOptions = group.ResourceProvisioningOptions;
            if (provisioningOptions != null)
            {
                return provisioningOptions.Any(option => string.Equals(option, TeamWorkload, StringComparison.OrdinalIgnoreCase))
                    ? GroupTeamStatus.HasTeam
                    : GroupTeamStatus.NoTeam;
            }

            // Fallback: a group built by hand with the raw JSON array in AdditionalData (the v4 shape).
            if (group.AdditionalData == null || !group.AdditionalData.ContainsKey(ResourceProvisioningOptionsProperty))
            {
                return GroupTeamStatus.WorkloadsNotReported;
            }

            return ProvisioningOptionsIncludeTeam(group.AdditionalData[ResourceProvisioningOptionsProperty].ToString())
                ? GroupTeamStatus.HasTeam
                : GroupTeamStatus.NoTeam;
        }

        /// <summary>
        /// Split groups-with-a-Team into those the configured white/blacklist allows us to crawl and
        /// those it excludes. Order within each list is the order the groups were supplied in, which is
        /// what keeps the importer's log output identical.
        /// </summary>
        public static GroupCrawlPartition PartitionByCrawlConfig(IEnumerable<Group> groupsWithTeams, TeamsCrawlConfig crawlConfig)
        {
            var partition = new GroupCrawlPartition();
            foreach (var group in groupsWithTeams)
            {
                if (crawlConfig.CrawlGroup(group.Id))
                {
                    partition.ToCrawl.Add(group);
                }
                else
                {
                    partition.Excluded.Add(group);
                }
            }

            return partition;
        }

        /// <summary>
        /// Whether a single group id passes the crawl white/blacklist.
        /// An empty whitelist means "everything except the blacklist"; a non-empty whitelist means
        /// "only these, and still never the blacklist".
        /// </summary>
        public static bool ShouldCrawlGroup(ICollection<string> whitelistGroupIds, ICollection<string> blacklistGroupIds, string groupId)
        {
            if (whitelistGroupIds.Count == 0)
            {
                return !blacklistGroupIds.Contains(groupId);
            }

            return !blacklistGroupIds.Contains(groupId) && whitelistGroupIds.Contains(groupId);
        }

        /// <summary>
        /// The Entra object id to UPN map Team members are resolved from, built once per crawl from the users already in
        /// SQL. It replaces one SQL query per member per Team on <c>azure_ad_id</c>, an unindexable <c>nvarchar(max)</c>
        /// column, which meant a full scan of <c>users</c> for every member at 200k-user scale.
        /// </summary>
        /// <remarks>
        /// Object ids compare case-insensitively, as the database collation did. When several rows share an object id -
        /// usually a renamed user, whose old row a rename left behind on earlier versions - the row
        /// <see cref="UserIdentityRules.IsPreferredRow(int, string, int, string, string)"/> prefers wins, the same one
        /// manager links use: with no current sign-in name to hand here, the newest row (highest <c>users.id</c>), which
        /// is the one carrying the user's latest sign-in name. It used to be the lowest id, the row the rename left
        /// behind. Rows with no object id or no UPN are left out, so those members are looked up in Graph instead.
        /// </remarks>
        public static Dictionary<string, string> BuildUpnByAzureAdId(IEnumerable<(int UserId, string AzureAdId, string UserPrincipalName)> users)
        {
            var preferredRowByObjectId = new Dictionary<string, (int UserId, string UserPrincipalName)>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in users)
            {
                if (string.IsNullOrEmpty(user.AzureAdId) || string.IsNullOrEmpty(user.UserPrincipalName))
                {
                    continue;
                }

                if (!preferredRowByObjectId.TryGetValue(user.AzureAdId, out var current)
                    || UserIdentityRules.IsPreferredRow(user.UserId, user.UserPrincipalName, current.UserId, current.UserPrincipalName, currentUpn: null))
                {
                    preferredRowByObjectId[user.AzureAdId] = (user.UserId, user.UserPrincipalName);
                }
            }

            return preferredRowByObjectId.ToDictionary(row => row.Key, row => row.Value.UserPrincipalName, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A genuine cancellation, which the crawl lets through rather than treating as one Team's failure. Nothing in the
        /// Teams crawl carries a cancellation token, so a <see cref="System.Threading.Tasks.TaskCanceledException"/> there
        /// is an HttpClient timeout - a failure like any other.
        /// </summary>
        internal static bool IsCancellation(Exception ex) => ex is OperationCanceledException && !(ex is System.Threading.Tasks.TaskCanceledException);
    }

    /// <summary>
    /// What <see cref="TeamsCrawlRules.ClassifyGroup"/> made of a group's provisioned workloads.
    /// </summary>
    public enum GroupTeamStatus
    {
        /// <summary>The group has a Team, so it is crawled.</summary>
        HasTeam,

        /// <summary>The group reported its workloads and none of them is a Team.</summary>
        NoTeam,

        /// <summary>
        /// The group returned no <c>resourceProvisioningOptions</c> at all. Not crawled, and not
        /// reported - see <see cref="TeamsCrawlRules.ClassifyGroup"/>.
        /// </summary>
        WorkloadsNotReported
    }

    /// <summary>
    /// Result of <see cref="TeamsCrawlRules.PartitionByCrawlConfig"/>.
    /// </summary>
    public class GroupCrawlPartition
    {
        public List<Group> ToCrawl { get; } = new List<Group>();
        public List<Group> Excluded { get; } = new List<Group>();
    }
}
