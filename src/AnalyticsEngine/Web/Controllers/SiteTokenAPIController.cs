using Common.Entities.Config;
using Common.Entities.Models;
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
    [RequirePortalPermission(PortalPermission.Administration)]
    [Route("api/SiteTokenAPI")]
    public class SiteTokenAPIController : BaseAPIController
    {
        // POST: api/SiteTokenAPI
        // Returns a fresh Microsoft Graph access token for the signed-in admin to the SPA.
        [HttpPost]
        public async Task<IActionResult> Post()
        {
            var stored = await base.GetCachedUserAccessTokenAsync();

            // The cookie only stores the (long-lived) refresh token, so mint a fresh access token from it. This also
            // transparently handles the ~1h access-token expiry for long sessions.
            var token = await MintAccessTokenAsync(stored,
                refreshToken => RefreshOAuthToken.GetNewRefreshToken(refreshToken, new AppConfig()));

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
        /// the stored refresh token has expired or been revoked.
        /// </summary>
        /// <remarks>
        /// A refresh that fails is "no token". The cookie carries no access token to fall back to: an access token
        /// would normally be expired by the time a refresh token failed, and returning it would make Graph reject
        /// the request while the page misleadingly reported "No Teams found".
        /// </remarks>
        internal static async Task<JSonToken> MintAccessTokenAsync(
            RefreshOAuthToken stored,
            System.Func<string, Task<RefreshOAuthToken>> refresh)
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
            catch (System.Exception)
            {
                // Expired, revoked, blocked by a sign-in frequency policy, or Entra ID unreachable: in every case the
                // remedy the page can offer is to connect again, which captures a new refresh token.
                return null;
            }

            return string.IsNullOrEmpty(refreshed?.AccessToken) ? null : new JSonToken(refreshed);
        }
    }
}