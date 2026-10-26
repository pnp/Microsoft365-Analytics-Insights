using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
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

            if (status == 429)
            {
                // Throttling is the app's or the tenant's, not the channel's: carrying on would only add requests to a Graph that
                // has just asked us to slow down.
                return ChannelReadFailureKind.Transient;
            }

            // A 400 UnknownError on one channel's delta, a 5xx, a timeout, the network, or anything unrecognised: it says nothing
            // about the token, and nothing about the Team's other channels either.
            return ChannelReadFailureKind.ChannelFailed;
        }

        /// <summary>
        /// Whether Graph's refusal means the delta token this request carried is dead: HTTP 410 (Gone) or the
        /// <c>syncStateNotFound</c> code, or any 400 to a request that carried a stored token - Graph's documented
        /// answers when a token has expired or the channel's sync state was reset, and often a bare 400 <c>UnknownError</c>.
        /// </summary>
        /// <param name="requestCarriedToken">True when the failed request included a stored <c>$deltatoken</c>.</param>
        public static bool IsInvalidDeltaToken(Exception ex, bool requestCarriedToken)
        {
            var status = StatusCode(ex);
            if (status == 410)
            {
                return true;
            }

            var code = (ex as ODataError)?.Error?.Code;
            if (string.Equals(code, "syncStateNotFound", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "resyncRequired", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return requestCarriedToken && status == 400;
        }
    }
}
