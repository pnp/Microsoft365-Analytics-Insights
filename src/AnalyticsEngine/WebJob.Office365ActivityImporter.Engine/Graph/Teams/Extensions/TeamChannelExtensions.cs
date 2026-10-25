using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities;
using Common.Entities.Models;
using Common.Entities.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public static class TeamChannelExtensions
    {
        /// <summary>
        /// Sets Messages on each channel. The crawl itself uses the existing source and token ports.
        /// </summary>
        /// <param name="remintAccessToken">
        /// Mints a new access token from the Team's refresh token, for when Graph refuses the current one part-way
        /// through the crawl; see <see cref="GraphChannelMessagesSourceLoader"/>.
        /// </param>
        public static async Task<List<TeamChannelDeltaTokenCommit>> PopulateNewMessagesAndReactions(this List<ChannelWithReactions> channels, Team team, RefreshOAuthToken refreshToken,
            ITeamChannelDeltaTokenStore deltaTokenStore, ILogger logger, List<TeamChannelDeltaTokenCommit> pendingDeltaTokenCommits = null,
            TeamsChannelBudgetProgress budget = null, System.Func<Task<RefreshOAuthToken>> remintAccessToken = null,
            TeamsChannelFailureLedger failures = null, TeamsChannelCrawlStats stats = null)
        {
            if (channels.Count == 0 || refreshToken == null)
            {
                return pendingDeltaTokenCommits ?? new List<TeamChannelDeltaTokenCommit>();
            }
            var messagesSource = new GraphChannelMessagesSourceLoader(refreshToken, remintAccessToken, deltaTokenStore, logger);
            return await new TeamsChannelCrawler(messagesSource, budget, failures, stats, logger).PopulateNewMessagesAndReactions(channels, team.Id, pendingDeltaTokenCommits);
        }

        public static async Task<TeamChannel> SaveToSql(this ChannelWithReactions channel, TeamsAndCallsDBLookupManager lookupManager, TeamDefinition dbTeam)
        {
            using (var store = new SqlTeamsPersistenceStore(lookupManager))
            {
                return await store.SaveChannel(channel, dbTeam);
            }
        }
    }
}
