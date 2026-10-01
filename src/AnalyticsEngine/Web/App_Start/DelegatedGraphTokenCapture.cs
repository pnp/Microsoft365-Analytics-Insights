using Common.Entities.Models;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Owin.Security;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// Handles the OIDC callback for the signed-in admin's delegated Graph token. It redeems the authorisation
    /// code only when the round trip was a Teams connection, and never lets a failure end the sign-in (issue #670).
    /// </summary>
    /// <remarks>
    /// Anything thrown from Katana's <c>AuthorizationCodeReceived</c> notification is caught by the middleware,
    /// passed to <c>AuthenticationFailed</c> and then re-thrown, turning the whole callback into a server error.
    /// That is how a missing consent for an optional feature used to lock everyone out of the portal. So both
    /// entry points here catch everything, report it, and send the admin back to the Teams permissions page with
    /// an outcome key the page can explain.
    /// </remarks>
    public sealed class DelegatedGraphTokenCapture
    {
        private readonly Func<string, string, Task<RefreshOAuthToken>> _redeemCode;
        private readonly Action<string, Exception> _report;

        /// <param name="redeemCode">Redeems an authorisation code for the given scopes at the token endpoint.</param>
        /// <param name="report">Records a failure for operators. Must not throw, but is guarded anyway.</param>
        public DelegatedGraphTokenCapture(
            Func<string, string, Task<RefreshOAuthToken>> redeemCode,
            Action<string, Exception> report)
        {
            _redeemCode = redeemCode ?? throw new ArgumentNullException(nameof(redeemCode));
            _report = report;
        }

        /// <summary>
        /// Called from <c>AuthorizationCodeReceived</c>. On a failed Teams connection it leaves the admin signed
        /// in and points the ticket's redirect back at the Teams permissions page.
        /// </summary>
        public async Task OnAuthorizationCodeReceivedAsync(AuthenticationTicket ticket, string code)
        {
            // A plain sign-in. Nothing else in the portal needs a Graph token, so there is nothing to redeem. An
            // authorisation code is single-use and short-lived, so leaving it unredeemed costs nothing.
            if (ticket?.Identity == null || !DelegatedGraphConsent.IsTeamsConnect(ticket.Properties))
            {
                return;
            }

            RefreshOAuthToken token;
            try
            {
                token = await _redeemCode(code, DelegatedGraphConsent.TeamsConnectScopes);
            }
            catch (Exception ex)
            {
                var failure = DelegatedGraphConsent.DescribeTokenFailure(ex);
                Report(
                    $"Teams connection failed: Entra ID would not issue a token for the delegated Teams permissions " +
                    $"({Describe(failure)}). Signing in to the portal is unaffected.{AdminAction(failure)} {ex.Message}",
                    ex);

                ticket.Properties.RedirectUri = DelegatedGraphConsent.FailureReturnUri(failure);
                return;
            }

            if (string.IsNullOrEmpty(token?.RefreshToken))
            {
                // A Teams authorisation outlives this session, so without a refresh token there is nothing the
                // importer could use.
                Report("Teams connection failed: Entra ID issued no refresh token for the delegated Teams permissions. " +
                       "Check the runtime app registration allows offline_access.", null);

                ticket.Properties.RedirectUri = DelegatedGraphConsent.FailureReturnUri(
                    new ConsentFailure(DelegatedGraphConsent.OutcomeFailed, null));
                return;
            }

            // The auth cookie carries it from here; SiteTokenAPI mints access tokens from it for the SPA.
            ticket.Identity.AddClaim(new Claim(GraphTokenClaims.RefreshToken, token.RefreshToken));
        }

        /// <summary>
        /// Called from <c>AuthenticationFailed</c>. Returns where to send the browser when the failed round trip
        /// was a Teams connection, or <c>null</c> for a sign-in, which keeps Katana's default handling.
        /// </summary>
        public string OnAuthenticationFailed(OpenIdConnectMessage response, Exception exception, ISecureDataFormat<AuthenticationProperties> stateFormat)
        {
            try
            {
                var properties = DelegatedGraphConsent.ReadStateProperties(response?.State, stateFormat);
                if (!DelegatedGraphConsent.IsTeamsConnect(properties))
                {
                    return null;
                }

                var failure = DelegatedGraphConsent.DescribeAuthorizationFailure(response, exception);
                Report(
                    $"Teams connection failed at Entra ID sign-in ({Describe(failure)}). The admin was returned to the " +
                    $"Teams permissions page, still signed in.{AdminAction(failure)} {exception?.Message}",
                    exception);

                return DelegatedGraphConsent.FailureReturnUri(failure);
            }
            catch (Exception)
            {
                // Unable to tell: leave Katana's default handling in place rather than guess.
                return null;
            }
        }

        private static string Describe(ConsentFailure failure)
        {
            return string.IsNullOrEmpty(failure.ErrorCode) ? failure.Outcome : $"{failure.Outcome}, {failure.ErrorCode}";
        }

        private static string AdminAction(ConsentFailure failure)
        {
            return failure.Outcome == DelegatedGraphConsent.OutcomeConsentRequired
                ? " An Entra ID administrator needs to add the delegated Microsoft Graph permissions Team.ReadBasic.All " +
                  "and ChannelMessage.Read.All to the runtime app registration and grant admin consent for them."
                : string.Empty;
        }

        private void Report(string message, Exception exception)
        {
            try
            {
                _report?.Invoke(message, exception);
            }
            catch (Exception)
            {
                // Telemetry must not turn a handled failure into an unhandled one.
            }
        }
    }
}
