using System;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace Common.Entities.Models
{
    /// <summary>
    /// What a failure to redeem a refresh token at Microsoft Entra ID's token endpoint means. Pure rules, shared by the
    /// importer (<see cref="ClassifyRefusal"/>: whether a Team's stored token is dead) and the portal
    /// (<see cref="RemedyFor"/>: whether connecting again would help the admin, or only waiting would).
    /// </summary>
    public static class RefreshTokenFailureRules
    {
        /// <summary>
        /// AADSTS codes that mean the refresh token itself is expired or revoked, so only a new authorisation can fix it:
        /// 70000 (the refresh token isn't valid), 70008 (expired or revoked), 700082 (expired through inactivity), 700084
        /// (a single-page-app token past its fixed lifetime), 50173 (revoked - e.g. the user's sessions were revoked),
        /// 50132 (the session ended with a password expiry or change) and 65001 (consent was withdrawn).
        /// </summary>
        internal static readonly int[] DeadTokenCodes = { 70000, 70008, 700082, 700084, 50173, 50132, 65001 };

        /// <summary>
        /// AADSTS codes that mean a sign-in policy is blocking silent use of a token that is otherwise fine: every
        /// Conditional Access code (53000-53999: a compliant or joined device required, the app not approved, access
        /// blocked, MFA registration blocked...), 50055 (password expired), 50072/50079 (MFA registration required),
        /// 50074/50076 (MFA required), 50097 (device authentication required), 50158 (an external security challenge),
        /// and 70043/70044 (Conditional Access sign-in frequency).
        /// </summary>
        internal static readonly int[] SignInPolicyCodes = { 50055, 50072, 50074, 50076, 50079, 50097, 50158, 70043, 70044 };

        private static readonly string[] DeadTokenSubErrors = { "bad_token", "token_expired", "consent_required" };
        private static readonly string[] SignInPolicySubErrors = { "basic_action", "additional_action", "message_only", "user_password_expired" };

        /// <summary>OAuth errors that say the grant can't be used without the person signing in again.</summary>
        private static readonly string[] SignInRequiredErrors = { "invalid_grant", "interaction_required" };

        /// <summary>
        /// Why Entra ID refused to redeem a stored refresh token, as far as the stored token is concerned.
        /// </summary>
        /// <remarks>
        /// <c>invalid_grant</c> is not one answer. Entra ID also returns it when a sign-in policy blocks a silent refresh -
        /// MFA required, a compliant device required, blocked by Conditional Access - and such a policy applies to every
        /// Team the same person authorised, often every Team in the tenant: deleting on it would wipe them all in one
        /// crawl, though each token works again as soon as the policy allows it. So a policy reason keeps the token, and
        /// wins over a dead-token reason in the same answer. A dead-token reason - by AADSTS code or suberror, or a bare
        /// <c>invalid_grant</c> with neither - deletes it. An <c>invalid_grant</c> whose code this version doesn't
        /// recognise keeps it too: a new policy variant would show up exactly there, and a kept token that never works
        /// costs one failed refresh and an error line per cycle, until the Team is authorised again, whereas a wrongly
        /// deleted one costs that re-authorisation for every affected Team, with no way back.
        /// </remarks>
        public static RefreshTokenRefusal ClassifyRefusal(Exception ex)
        {
            if (!(ex is RefreshTokenRequestException refused)
                || refused.StatusCode != HttpStatusCode.BadRequest
                || !string.Equals(refused.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                return RefreshTokenRefusal.NotATokenProblem;
            }

            if (HasSignInPolicyReason(refused))
            {
                return RefreshTokenRefusal.BlockedBySignInPolicy;
            }

            if (HasDeadTokenReason(refused) || (refused.ErrorCodes.Count == 0 && string.IsNullOrEmpty(refused.SubError)))
            {
                return RefreshTokenRefusal.TokenDead;
            }

            return RefreshTokenRefusal.UnrecognisedReason;
        }

        /// <summary>
        /// What a failed redemption leaves the person whose token it is to do: sign in again, try again later, or - for a
        /// failure this version doesn't recognise - <see cref="RefreshTokenRemedy.Unknown"/>.
        /// </summary>
        /// <remarks>
        /// When Entra ID answered, what it said about the grant comes first: <c>invalid_grant</c>,
        /// <c>interaction_required</c>, or a dead-token or sign-in-policy reason (see <see cref="ClassifyRefusal"/>)
        /// means only an interactive sign-in can help, whatever the HTTP status. Otherwise <c>temporarily_unavailable</c>,
        /// throttling (429), a timeout (408) or a server error (5xx) means Entra ID couldn't answer for now. No answer at
        /// all is the same: <c>RefreshOAuthToken.GetNewRefreshToken</c> throws a <see cref="RefreshTokenRequestException"/>
        /// whenever Entra ID answered, so a plain <see cref="HttpRequestException"/> means it couldn't be reached (DNS, a
        /// refused or reset connection, TLS), and an <see cref="OperationCanceledException"/> is the HTTP client's
        /// timeout - the redemption takes no cancellation token.
        /// </remarks>
        public static RefreshTokenRemedy RemedyFor(Exception ex)
        {
            switch (ex)
            {
                case RefreshTokenRequestException refused:
                    if (IsOneOf(refused.Error, SignInRequiredErrors) || HasSignInPolicyReason(refused) || HasDeadTokenReason(refused))
                    {
                        return RefreshTokenRemedy.SignInAgain;
                    }

                    var status = (int)refused.StatusCode;
                    return string.Equals(refused.Error, "temporarily_unavailable", StringComparison.OrdinalIgnoreCase)
                        || status == 408 || status == 429 || status >= 500
                        ? RefreshTokenRemedy.TryAgainLater
                        : RefreshTokenRemedy.Unknown;

                case HttpRequestException _:
                case OperationCanceledException _:
                    return RefreshTokenRemedy.TryAgainLater;

                default:
                    return RefreshTokenRemedy.Unknown;
            }
        }

        private static bool HasSignInPolicyReason(RefreshTokenRequestException refused)
            => refused.ErrorCodes.Any(IsSignInPolicyCode) || IsOneOf(refused.SubError, SignInPolicySubErrors);

        private static bool HasDeadTokenReason(RefreshTokenRequestException refused)
            => refused.ErrorCodes.Any(code => DeadTokenCodes.Contains(code)) || IsOneOf(refused.SubError, DeadTokenSubErrors);

        private static bool IsSignInPolicyCode(int code) => (code >= 53000 && code <= 53999) || SignInPolicyCodes.Contains(code);

        private static bool IsOneOf(string value, string[] candidates)
            => !string.IsNullOrEmpty(value) && candidates.Any(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What a refused refresh-token redemption means for the stored token. See <see cref="RefreshTokenFailureRules.ClassifyRefusal"/>.</summary>
    public enum RefreshTokenRefusal
    {
        /// <summary>Not Entra ID saying <c>invalid_grant</c>: an outage, throttling, the app's own credential... Kept.</summary>
        NotATokenProblem,

        /// <summary>The token is expired or revoked. Deleted: only a new authorisation can fix it.</summary>
        TokenDead,

        /// <summary>A sign-in policy blocks silent use of the token. Kept: it works again once the policy allows it.</summary>
        BlockedBySignInPolicy,

        /// <summary><c>invalid_grant</c> for a reason this version doesn't recognise. Kept, on the safe side.</summary>
        UnrecognisedReason
    }

    /// <summary>What a failed refresh-token redemption leaves the person to do. See <see cref="RefreshTokenFailureRules.RemedyFor"/>.</summary>
    public enum RefreshTokenRemedy
    {
        /// <summary>A failure this version doesn't recognise; callers keep doing what they did before.</summary>
        Unknown,

        /// <summary>Sign in again: the token is dead, or Entra ID won't use it without an interactive sign-in.</summary>
        SignInAgain,

        /// <summary>Try again later: Entra ID couldn't be reached, timed out, was throttling or failing.</summary>
        TryAgainLater
    }
}
