using Common.Entities.Config;
using Common.Entities.Models;
using DataUtils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Hands the signed-in user's own Graph token to the Teams permissions page, which is part of the
    /// Administration area. Nothing else in the portal needs it.
    /// </summary>
    [Authorize]
    [ApiReplyExceptionFilter]
    [RequirePortalPermission(PortalPermission.Administration)]
    [Route("api/SiteTokenAPI")]
    public class SiteTokenAPIController : BaseAPIController
    {
        /// <summary>
        /// The code a 503 from this API carries when Microsoft Entra ID couldn't be reached to refresh the admin's token.
        /// The API sends the code and never text: the page words it (<c>src/auth/siteToken.ts</c>, checked against this
        /// constant by <c>src/i18n/lint/serverAuthoredText.test.ts</c>).
        /// </summary>
        internal const string EntraUnavailableCode = "entraUnavailable";

        private static readonly System.Lazy<AnalyticsLogger> ProductionLogger = new System.Lazy<AnalyticsLogger>(
            () => new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, "SiteToken"));

        // POST: api/SiteTokenAPI
        // Returns a fresh Microsoft Graph access token for the signed-in admin to the SPA.
        [HttpPost]
        public async Task<IActionResult> Post()
        {
            var stored = await base.GetCachedUserAccessTokenAsync();

            // The cookie only stores the (long-lived) refresh token, so mint a fresh access token from it. This also
            // transparently handles the ~1h access-token expiry for long sessions. A 503 when Entra ID can't be reached.
            var token = await MintAccessTokenAsync(stored,
                refreshToken => RefreshOAuthToken.GetNewRefreshToken(refreshToken, new AppConfig()),
                ProductionLogger.Value);

            if (token == null)
            {
                // The SPA reads this as "not connected" and offers "Connect to Microsoft Teams" (issue #670). The user
                // is signed in, so Startup.ConfigureAuth leaves off the session-expired header and the SPA doesn't
                // bounce them through a pointless sign-in.
                return Unauthorized();
            }

            return Ok(token);
        }

        /// <summary>
        /// A fresh Graph access token minted from the stored refresh token, or <c>null</c> when there is no working
        /// one: the admin hasn't connected Microsoft Teams (signing in no longer captures a token - issue #670), or
        /// the stored refresh token has expired, been revoked, or can't be used until the admin signs in again.
        /// </summary>
        /// <exception cref="ApiReplyException">A 503 carrying <see cref="EntraUnavailableCode"/>: Microsoft Entra ID
        /// couldn't be reached, timed out, was throttling or failing. Connecting again can't fix that, because the
        /// connection's sign-in ends by redeeming a code at the same token endpoint, from this same server.</exception>
        /// <remarks>
        /// A refresh that fails for any other reason is "no token". The cookie carries no access token to fall back to:
        /// an access token would normally be expired by the time a refresh token failed, and returning it would make
        /// Graph reject the request while the page misleadingly reported "No Teams found".
        /// </remarks>
        internal static async Task<JSonToken> MintAccessTokenAsync(
            RefreshOAuthToken stored,
            System.Func<string, Task<RefreshOAuthToken>> refresh,
            ILogger logger = null)
        {
            if (string.IsNullOrEmpty(stored?.RefreshToken))
            {
                return null;
            }

            RefreshOAuthToken refreshed;
            try
            {
                refreshed = await refresh(stored.RefreshToken);
            }
            catch (System.Exception ex) when (RefreshTokenFailureRules.RemedyFor(ex) == RefreshTokenRemedy.TryAgainLater)
            {
                LogRefreshFailure(logger, ex, RefreshTokenRemedy.TryAgainLater, "entraUnavailable503");
                throw new ApiReplyException(EntraUnavailable(), EntraUnavailableCode);
            }
            catch (System.Exception ex)
            {
                // Expired or revoked, blocked until the admin signs in again (MFA, Conditional Access, sign-in frequency),
                // or a failure this version doesn't recognise: the remedy the page can offer is to connect again, which
                // captures a new refresh token.
                LogRefreshFailure(logger, ex, RefreshTokenFailureRules.RemedyFor(ex), "notConnected401");
                return null;
            }

            return string.IsNullOrEmpty(refreshed?.AccessToken) ? null : new JSonToken(refreshed);
        }

        /// <summary>
        /// One Warning per failed refresh, so "the Teams page says not connected" or "Entra unavailable" can be matched to
        /// what Entra ID answered. Classification and Entra's own identifiers only: never the token, the user, or the
        /// response body (its description can name the user).
        /// </summary>
        private static void LogRefreshFailure(ILogger logger, System.Exception ex, RefreshTokenRemedy remedy, string outcome)
        {
            if (logger == null) return;

            try
            {
                var refused = ex as RefreshTokenRequestException;
                var message = $"SiteToken refresh failed: outcome={outcome} remedy={remedy} exceptionType={ex.GetType().Name} " +
                    $"httpStatus={(refused != null ? ((int)refused.StatusCode).ToString() : "none")} " +
                    $"error={refused?.Error ?? "none"} suberror={refused?.SubError ?? "none"} " +
                    $"aadstsCodes=[{(refused != null ? string.Join(",", refused.ErrorCodes) : string.Empty)}] " +
                    $"traceId={refused?.TraceId ?? "none"} correlationId={refused?.CorrelationId ?? "none"}";
                logger.Log(LogLevel.Warning, 0, message, null, (state, _) => state);
            }
            catch (System.Exception)
            {
                // Logging must not change the answer the page gets.
            }
        }

        private static IActionResult EntraUnavailable() => new ObjectResult(new { code = EntraUnavailableCode })
        {
            StatusCode = (int)HttpStatusCode.ServiceUnavailable,
        };
    }
}