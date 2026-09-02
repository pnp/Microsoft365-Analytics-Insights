using System;
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
        private readonly ITeamChannelDeltaTokenStore _deltaTokenStore;

        public TeamsChannelCrawler(IChannelMessagesSourceLoader messagesSource, ITeamChannelDeltaTokenStore deltaTokenStore)
        {
            _messagesSource = messagesSource ?? throw new ArgumentNullException(nameof(messagesSource));
            _deltaTokenStore = deltaTokenStore ?? throw new ArgumentNullException(nameof(deltaTokenStore));
        }

        /// <summary>
        /// Sets the "Messages" prop on each channel by reading each channel's messages, then saves the
        /// delta token for the next read.
        /// </summary>
        /// <remarks>
        /// A channel that returns no delta token leaves the stored token untouched, so a read that
        /// couldn't produce one doesn't destroy the incremental position. Channels are crawled in
        /// order and a read failure aborts the whole team's crawl, exactly as before - the caller
        /// deletes the team's auth token and retries next cycle.
        /// </remarks>
        public async Task PopulateNewMessagesAndReactions(List<ChannelWithReactions> channels, string teamId)
        {
            if (channels is null) throw new ArgumentNullException(nameof(channels));

            foreach (var channel in channels)
            {
                // Load stats. Will throw ChannelMessagesReadException if token is invalid
                var channelDelta = await _messagesSource.LoadMessagesAndReactions(channel, teamId);

                // Save delta token for next read
                if (channelDelta != null)
                {
                    await _deltaTokenStore.SetDeltaToken(teamId, channel.Id, channelDelta);
                }
            }
        }
    }
}
