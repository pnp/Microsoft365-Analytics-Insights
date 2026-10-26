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
        /// The channel couldn't be read. Its <see cref="ChannelMessagesReadException.Kind"/> says what that means:
        /// <see cref="ChannelReadFailureKind.SkipChannel"/> - only this channel is unreadable with this token, so the crawl
        /// skips it and carries on; <see cref="ChannelReadFailureKind.Transient"/> - stop this Team's crawl for this cycle
        /// but keep its token; <see cref="ChannelReadFailureKind.TokenRejected"/> - the token can't read this Team, and the
        /// caller deletes it. See <see cref="ChannelReadFailureRules"/>.
        /// </exception>
        Task<TeamChannelDeltaTokenInfo> LoadMessagesAndReactions(ChannelWithReactions channel, string teamId);
    }
}
