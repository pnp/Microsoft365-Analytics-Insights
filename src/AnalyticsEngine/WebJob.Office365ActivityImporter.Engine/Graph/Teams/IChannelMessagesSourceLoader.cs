using Common.Entities.Redis.Teams;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Read port for a single Teams channel's new messages, replies and reactions. Extracted from
    /// <c>TeamChannelExtensions.GetChannelMessagesAndReactions</c> so the channel-crawl loop can be
    /// tested without Graph. See issue #377.
    /// </summary>
    public interface IChannelMessagesSourceLoader
    {
        /// <summary>
        /// Read the channel and set its new messages/reactions on <paramref name="channel"/>.
        /// </summary>
        /// <returns>
        /// The delta token to store for the next incremental read, or <c>null</c> when the source did
        /// not hand one back - in which case the caller must leave any previously stored token alone.
        /// </returns>
        /// <exception cref="ChannelMessagesReadException">
        /// The user-delegated token was rejected. The caller treats this as "the cached token is bad",
        /// deletes it and retries next cycle.
        /// </exception>
        Task<TeamsRedisManager.TeamChannelDeltaTokenInfo> LoadMessagesAndReactions(ChannelWithReactions channel, string teamId);
    }
}
