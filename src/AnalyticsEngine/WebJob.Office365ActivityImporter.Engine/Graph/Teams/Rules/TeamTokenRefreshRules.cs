using Common.Entities.Models;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// What a failure to redeem a Team's stored refresh token means for that stored token. Pure rule; see
    /// <see cref="TeamTokenManager"/>. The classification itself is shared with the portal:
    /// <see cref="RefreshTokenFailureRules"/>.
    /// </summary>
    public static class TeamTokenRefreshRules
    {
        /// <summary>
        /// True only when Entra ID said the refresh token itself is no good: HTTP 400, OAuth error <c>invalid_grant</c>, and
        /// a dead-token reason (see <see cref="RefreshTokenFailureRules.ClassifyRefusal"/>). Only then is it deleted.
        /// </summary>
        /// <remarks>
        /// Everything else says nothing about the token, and deleting it would make an administrator authorise the Team
        /// again for nothing: Entra ID unreachable or slow, throttling (429), a 5xx, any other 400, a body that isn't
        /// Entra ID's JSON - and above all <c>invalid_client</c> (401), which is the importer's own app credential being
        /// rejected, e.g. after its client secret expired. Deleting on that would wipe every Team's authorisation at once.
        /// </remarks>
        public static bool IsRefreshTokenRejected(Exception ex) => ClassifyRefusal(ex) == RefreshTokenRefusal.TokenDead;

        /// <summary>Why Entra ID refused to redeem a stored refresh token; see <see cref="RefreshTokenFailureRules.ClassifyRefusal"/>.</summary>
        public static RefreshTokenRefusal ClassifyRefusal(Exception ex) => RefreshTokenFailureRules.ClassifyRefusal(ex);

        /// <summary>
        /// The refusal for a log line: HTTP status, OAuth error and suberror, the AADSTS codes, and what an administrator
        /// should do about it. Never the token, the client secret or the response body (whose description can name the user).
        /// </summary>
        public static string Describe(Exception ex)
        {
            if (ex is RefreshTokenRequestException refused)
            {
                var description = $"HTTP {(int)refused.StatusCode}, OAuth error '{refused.Error ?? "(none)"}'";
                if (!string.IsNullOrEmpty(refused.SubError))
                {
                    description += $", suberror '{refused.SubError}'";
                }
                if (refused.ErrorCodes.Count > 0)
                {
                    description += ", AADSTS" + string.Join(" AADSTS", refused.ErrorCodes);
                }

                switch (ClassifyRefusal(ex))
                {
                    case RefreshTokenRefusal.BlockedBySignInPolicy:
                        description += " - a sign-in policy (Conditional Access, MFA, a compliant device, password expiry) is blocking " +
                            "silent use of the Team's authorisation. The token is kept and works again once the policy allows it: exclude " +
                            "the importer's sign-in from the policy, or authorise the Team again in a way that satisfies it";
                        break;
                    case RefreshTokenRefusal.UnrecognisedReason:
                        description += " - not a reason this version recognises as a dead token, so the token is kept; if this " +
                            "repeats, authorise the Team again";
                        break;
                }

                if (string.Equals(refused.Error, "invalid_client", StringComparison.OrdinalIgnoreCase))
                {
                    description += " - the importer's own app credential was rejected: check its client secret or certificate hasn't expired";
                }
                return description;
            }

            return ex is TaskCanceledException ? "timed out" : ex.GetType().Name;
        }
    }
}
