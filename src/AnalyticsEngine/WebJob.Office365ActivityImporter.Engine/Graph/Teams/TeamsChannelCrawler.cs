using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Walks a Team's channels, reading each one's new messages/reactions and storing the delta token
    /// that makes the next read incremental. Extracted from
    /// <c>TeamChannelExtensions.PopulateNewMessagesAndReactions</c> so the crawl can be tested with no
    /// Graph and no Redis. See issue #377.
    /// </summary>
    public class TeamsChannelCrawler
    {
        private readonly IChannelMessagesSourceLoader _messagesSource;

        public TeamsChannelCrawler(IChannelMessagesSourceLoader messagesSource)
        {
            _messagesSource = messagesSource ?? throw new System.ArgumentNullException(nameof(messagesSource));
        }

        /// <summary>
        /// Sets the "Messages" prop on each channel by reading each channel's messages, then returns
        /// the delta tokens that may be committed after SQL persistence succeeds.
        /// </summary>
        /// <remarks>
        /// A channel that returns no delta token leaves the stored token untouched, so a read that
        /// couldn't produce one doesn't destroy the incremental position. Channels are crawled in
        /// order and a read failure aborts the whole team's crawl, exactly as before - the caller
        /// deletes the team's auth token and retries next cycle.
        /// </remarks>
        public async Task<List<TeamChannelDeltaTokenCommit>> PopulateNewMessagesAndReactions(
            List<ChannelWithReactions> channels,
            string teamId,
            List<TeamChannelDeltaTokenCommit> pendingDeltaTokenCommits = null)
        {
            pendingDeltaTokenCommits = pendingDeltaTokenCommits ?? new List<TeamChannelDeltaTokenCommit>();

            foreach (var channel in channels)
            {
                // Load stats. Will throw ChannelMessagesReadException if token is invalid
                var channelDelta = await _messagesSource.LoadMessagesAndReactions(channel, teamId);

                if (channelDelta != null)
                {
                    pendingDeltaTokenCommits.Add(new TeamChannelDeltaTokenCommit(channel.Id, channelDelta));
                }
            }

            return pendingDeltaTokenCommits;
        }
    }
}
