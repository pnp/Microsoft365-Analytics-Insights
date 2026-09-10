using Common.Entities.Redis.Teams;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// A channel delta token returned by Graph during a crawl. It is only a pending checkpoint until
    /// the corresponding SQL work has succeeded.
    /// </summary>
    public class TeamChannelDeltaTokenCommit
    {
        public TeamChannelDeltaTokenCommit(string channelId, TeamsRedisManager.TeamChannelDeltaTokenInfo deltaTokenInfo)
        {
            ChannelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
            DeltaTokenInfo = deltaTokenInfo ?? throw new ArgumentNullException(nameof(deltaTokenInfo));
        }

        public string ChannelId { get; }
        public TeamsRedisManager.TeamChannelDeltaTokenInfo DeltaTokenInfo { get; }
    }

    public static class TeamChannelDeltaTokenCommitter
    {
        public static async Task CommitPendingTokens(
            ITeamChannelDeltaTokenStore deltaTokenStore,
            string teamId,
            IEnumerable<TeamChannelDeltaTokenCommit> pendingCommits)
        {
            if (deltaTokenStore == null) throw new ArgumentNullException(nameof(deltaTokenStore));
            if (pendingCommits == null) return;

            foreach (var pendingCommit in pendingCommits)
            {
                await deltaTokenStore.SetDeltaToken(teamId, pendingCommit.ChannelId, pendingCommit.DeltaTokenInfo);
            }
        }
    }
}
