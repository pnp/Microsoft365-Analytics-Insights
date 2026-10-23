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
        /// Nothing to say the token is bad - throttling, a Graph or network outage, a timeout, anything unrecognised.
        /// Stop reading this Team's channels this cycle, keep its authorisation, and save what was read.
        /// </summary>
        Transient,

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
