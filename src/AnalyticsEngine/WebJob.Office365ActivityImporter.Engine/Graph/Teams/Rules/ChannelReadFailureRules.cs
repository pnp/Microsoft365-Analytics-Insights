using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using System;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// What a failed read of one channel's messages, with a Team's delegated token, means for the rest of that Team's
    /// crawl and for its stored authorisation. Pure rule; see <see cref="GraphChannelMessagesSourceLoader"/>.
    /// </summary>
    /// <remarks>
    /// Every failure used to count as "the stored token is bad", so it was deleted on a throttled request, a Graph
    /// outage or one private channel the authorising person isn't in - and an administrator had to authorise the Team
    /// again. Now only evidence about the token itself deletes it.
    /// </remarks>
    public static class ChannelReadFailureRules
    {
        /// <summary>The HTTP status Graph answered with, or <c>null</c> when there was no answer (network, timeout).</summary>
        public static int? StatusCode(Exception ex)
        {
            return ex is ApiException apiException && apiException.ResponseStatusCode > 0
                ? apiException.ResponseStatusCode
                : (int?)null;
        }

        public static bool IsUnauthorized(Exception ex) => StatusCode(ex) == 401;

        /// <param name="ex">The failure.</param>
        /// <param name="membershipType">The channel's type; <c>null</c> when Graph didn't say.</param>
        /// <param name="freshTokenRejected">
        /// True when the refused access token was minted, because the one before it was refused, within
        /// <see cref="GraphChannelMessagesSourceLoader.FreshTokenWindow"/> of this refusal. A token that has merely been
        /// in use for a long time proves nothing when refused: it may simply have expired.
        /// </param>
        public static ChannelReadFailureKind Classify(Exception ex, ChannelMembershipType? membershipType, bool freshTokenRejected)
        {
            var status = StatusCode(ex);
            if (status == 401)
            {
                // An access token lasts 60 to 90 minutes and a big channel can take longer to read, so an old token being
                // refused proves nothing. A replacement minted moments ago being refused does.
                return freshTokenRejected ? ChannelReadFailureKind.TokenRejected : ChannelReadFailureKind.Transient;
            }

            if (status == 403)
            {
                if (membershipType == ChannelMembershipType.Private || membershipType == ChannelMembershipType.Shared)
                {
                    // Only members of a private or shared channel can read it.
                    return ChannelReadFailureKind.SkipChannel;
                }

                // Every member of a Team can read its standard channels, so the person whose token this is can't read
                // this Team. With no channel type to go on, don't guess: keep the token and try again next cycle.
                return membershipType == ChannelMembershipType.Standard
                    ? ChannelReadFailureKind.TokenRejected
                    : ChannelReadFailureKind.Transient;
            }

            if (status == 404)
            {
                // Channels are listed with the app's identity and read with the person's; one can be deleted in between.
                return ChannelReadFailureKind.SkipChannel;
            }

            // Throttling, a 5xx, a timeout, the network, or anything unrecognised: nothing to say the token is bad.
            return ChannelReadFailureKind.Transient;
        }
    }
}
