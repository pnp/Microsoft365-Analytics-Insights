using System;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// A channel's messages couldn't be read with the Team's delegated token. <see cref="Kind"/> says what that means;
    /// see <see cref="ChannelReadFailureRules"/>.
    /// </summary>
    public class ChannelMessagesReadException : Exception
    {
        /// <summary>
        /// An unclassified failure. It counts as <see cref="ChannelReadFailureKind.Transient"/>, which never deletes the
        /// Team's authorisation.
        /// </summary>
        public ChannelMessagesReadException(Exception inner) : this(ChannelReadFailureKind.Transient, "Couldn't read channel messages. Check inner exception", inner) { }

        public ChannelMessagesReadException(ChannelReadFailureKind kind, string message, Exception inner) : base(message, inner)
        {
            Kind = kind;
        }

        public ChannelReadFailureKind Kind { get; }
    }

    /// <summary>
    /// What a failed channel read means for the rest of the Team's crawl and for its stored authorisation.
    /// </summary>
    public enum ChannelReadFailureKind
    {
        /// <summary>
        /// Nothing to say the token is bad, and no reason to think the next channel will fail too - but also no reason to keep
        /// hitting Graph: throttling, a token Graph refused that may simply have aged, a channel type that isn't known.
        /// Stop reading this Team's channels this cycle, keep its authorisation, and save what was read. An unclassified failure
        /// (<see cref="ChannelMessagesReadException(Exception)"/>) is this kind.
        /// </summary>
        Transient,

        /// <summary>
        /// This channel's read failed for a reason that isn't authentication or throttling: Graph's documented persistent
        /// <c>400 UnknownError</c> on a channel's <c>messages/delta</c>, a 5xx, a timeout, the network. Carry on with the Team's
        /// other channels - one bad channel must not stall the rest - and leave this channel's delta token alone so it resumes
        /// when Graph answers again. Several in a row means Graph itself is down, so the crawl then stops as for
        /// <see cref="Transient"/>; see <see cref="TeamsChannelCrawler.MaxConsecutiveChannelFailures"/>.
        /// </summary>
        ChannelFailed,

        /// <summary>
        /// Only this channel is unreadable with this person's token: a private or shared channel they aren't a member
        /// of, or a channel deleted since it was listed. Skip it - no delta token is written - and carry on.
        /// </summary>
        SkipChannel,

        /// <summary>
        /// The token can't read this Team: a freshly minted access token was refused, a standard channel - which every
        /// member can read - was forbidden, or Entra ID refused the refresh token. The stored authorisation is deleted.
        /// </summary>
        TokenRejected
    }
}
