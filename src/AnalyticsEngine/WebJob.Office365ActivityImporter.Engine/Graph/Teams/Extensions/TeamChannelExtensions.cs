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
        public static async Task<List<TeamChannelDeltaTokenCommit>> PopulateNewMessagesAndReactions(this List<ChannelWithReactions> channels, Team team, RefreshOAuthToken refreshToken,
            ITeamChannelDeltaTokenStore deltaTokenStore, ILogger logger, List<TeamChannelDeltaTokenCommit> pendingDeltaTokenCommits = null)
        {
            if (channels.Count == 0 || refreshToken == null)
            {
                return pendingDeltaTokenCommits ?? new List<TeamChannelDeltaTokenCommit>();
            }
            var messagesSource = new GraphChannelMessagesSourceLoader(refreshToken, deltaTokenStore, logger);
            return await new TeamsChannelCrawler(messagesSource).PopulateNewMessagesAndReactions(channels, team.Id, pendingDeltaTokenCommits);
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
