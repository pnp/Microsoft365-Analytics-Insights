using Microsoft.Graph.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Read port for the M365 groups the Teams crawl starts from, so <see cref="TeamsFinder"/>'s
    /// selection rules can be exercised without Graph. See issue #377.
    ///
    /// The two methods mirror the two Graph strategies the finder supports; the caller picks one and
    /// applies <see cref="TeamsCrawlRules"/> to whatever comes back.
    /// </summary>
    public interface ITeamsGroupSourceLoader
    {
        /// <summary>
        /// Every group in the tenant, with the <c>resourceProvisioningOptions</c> property loaded so the
        /// caller can work out which ones have a Team (the v1 endpoint has no server-side filter for it).
        /// </summary>
        Task<List<Group>> LoadGroupsWithProvisioningOptions();

        /// <summary>
        /// Only groups that have a Team, filtered server-side. Requires the beta endpoint, which the
        /// importer does not use yet.
        /// </summary>
        Task<List<Group>> LoadGroupsFilteredToTeams();
    }
}
