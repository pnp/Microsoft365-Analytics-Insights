using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Pure decision logic for "which groups does the Teams crawl visit?", extracted from
    /// <see cref="TeamsFinder"/> and <see cref="TeamsCrawlConfig"/> so it can be unit tested without
    /// Graph or SQL. See issue #377.
    ///
    /// Deliberately a <c>static</c> class rather than an interface: it is a rule, not a dependency
    /// (the convention in issue #381, matching <c>ImportCadenceGate</c> and <c>AuditLogContentDispatcher</c>).
    /// </summary>
    public static class TeamsCrawlRules
    {
        /// <summary>
        /// Graph property (returned in <c>AdditionalData</c>) that says which workloads have been
        /// provisioned onto an M365 group. A group with a Team has "Team" in this array.
        /// </summary>
        public const string ResourceProvisioningOptionsProperty = "resourceProvisioningOptions";

        /// <summary>
        /// Whether a group returned by the v1 Graph endpoint has a Team attached, decided from its
        /// <c>resourceProvisioningOptions</c> array.
        /// </summary>
        /// <remarks>
        /// A group with no <c>resourceProvisioningOptions</c> at all is treated as having no Team, and
        /// is NOT reported as such - see <see cref="PartitionGroupsWithTeams"/>.
        /// </remarks>
        public static bool GroupHasTeam(Group group)
        {
            if (group is null) throw new ArgumentNullException(nameof(group));

            if (!group.AdditionalData.ContainsKey(ResourceProvisioningOptionsProperty))
            {
                return false;
            }

            return ProvisioningOptionsIncludeTeam(group.AdditionalData[ResourceProvisioningOptionsProperty].ToString());
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
        /// Split groups returned by the v1 endpoint into those with a Team and those without.
        /// </summary>
        /// <remarks>
        /// Groups that carry no <c>resourceProvisioningOptions</c> property land in neither list: the
        /// original code neither crawled nor logged them, and that is preserved so operator-facing
        /// log output is unchanged.
        /// </remarks>
        public static GroupTeamPartition PartitionGroupsWithTeams(IEnumerable<Group> groups)
        {
            if (groups is null) throw new ArgumentNullException(nameof(groups));

            var partition = new GroupTeamPartition();
            foreach (var group in groups)
            {
                if (!group.AdditionalData.ContainsKey(ResourceProvisioningOptionsProperty))
                {
                    continue;
                }

                if (GroupHasTeam(group))
                {
                    partition.WithTeam.Add(group);
                }
                else
                {
                    partition.WithoutTeam.Add(group);
                }
            }

            return partition;
        }

        /// <summary>
        /// Split groups-with-a-Team into those the configured white/blacklist allows us to crawl and
        /// those it excludes. Order within each list is the order the groups were supplied in, which is
        /// what keeps the importer's log output identical.
        /// </summary>
        public static GroupCrawlPartition PartitionByCrawlConfig(IEnumerable<Group> groupsWithTeams, TeamsCrawlConfig crawlConfig)
        {
            if (groupsWithTeams is null) throw new ArgumentNullException(nameof(groupsWithTeams));
            if (crawlConfig is null) throw new ArgumentNullException(nameof(crawlConfig));

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
            if (whitelistGroupIds is null) throw new ArgumentNullException(nameof(whitelistGroupIds));
            if (blacklistGroupIds is null) throw new ArgumentNullException(nameof(blacklistGroupIds));

            if (whitelistGroupIds.Count == 0)
            {
                return !blacklistGroupIds.Contains(groupId);
            }

            return !blacklistGroupIds.Contains(groupId) && whitelistGroupIds.Contains(groupId);
        }
    }

    /// <summary>
    /// Result of <see cref="TeamsCrawlRules.PartitionGroupsWithTeams"/>.
    /// </summary>
    public class GroupTeamPartition
    {
        public List<Group> WithTeam { get; } = new List<Group>();

        /// <summary>
        /// Groups that advertised their provisioned workloads but have no Team. These are the ones the
        /// importer logs as "has no Team associated".
        /// </summary>
        public List<Group> WithoutTeam { get; } = new List<Group>();
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
