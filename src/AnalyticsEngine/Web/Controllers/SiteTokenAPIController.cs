using Common.Entities.Config;
using Common.Entities.Models;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Hands the signed-in user's own Graph token to the Teams permissions page, which is part of the
    /// Administration area. Nothing else in the portal needs it.
    /// </summary>
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    public class SiteTokenAPIController : BaseAPIController
    {
        /// <summary>
        /// The code a 503 from this API carries when Microsoft Entra ID couldn't be reached to refresh the admin's token.
        /// The API sends the code and never text: the page words it (<c>src/auth/siteToken.ts</c>, checked against this
        /// constant by <c>src/i18n/lint/serverAuthoredText.test.ts</c>).
        /// </summary>
        internal const string EntraUnavailableCode = "entraUnavailable";

        // POST: api/SiteTokenAPI
        // Returns a fresh Microsoft Graph access token for the signed-in admin to the SPA.
        public async Task<JSonToken> Post()
        {
            var stored = await base.GetCachedUserAccessTokenAsync();

            // The cookie only stores the (long-lived) refresh token, so mint a fresh access token from it. This also
            // transparently handles the ~1h access-token expiry for long sessions. A 503 when Entra ID can't be reached.
            var token = await MintAccessTokenAsync(stored,
                refreshToken => RefreshOAuthToken.GetNewRefreshToken(refreshToken, new AppConfig()));

            if (token == null)
            {
                // The SPA reads this as "not connected" and offers "Connect to Microsoft Teams" (issue #670). The user
                // is signed in, so Startup.ConfigureAuth leaves off the session-expired header and the SPA doesn't
                // bounce them through a pointless sign-in.
                throw new HttpResponseException(System.Net.HttpStatusCode.Unauthorized);
            }

            return token;
        }

        /// <summary>
        /// A fresh Graph access token minted from the stored refresh token, or <c>null</c> when there is no working
        /// one: the admin hasn't connected Microsoft Teams (signing in no longer captures a token - issue #670), or
        /// the stored refresh token has expired, been revoked, or can't be used until the admin signs in again.
        /// </summary>
        /// <exception cref="HttpResponseException">A 503 carrying <see cref="EntraUnavailableCode"/>: Microsoft Entra ID
        /// couldn't be reached, timed out, was throttling or failing. Connecting again can't fix that, because the
        /// connection's sign-in ends by redeeming a code at the same token endpoint, from this same server.</exception>
        /// <remarks>
        /// A refresh that fails for any other reason is "no token". The cookie carries no access token to fall back to:
        /// an access token would normally be expired by the time a refresh token failed, and returning it would make
        /// Graph reject the request while the page misleadingly reported "No Teams found".
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
            catch (System.Exception ex) when (RefreshTokenFailureRules.RemedyFor(ex) == RefreshTokenRemedy.TryAgainLater)
            {
                throw new HttpResponseException(EntraUnavailable());
            }
            catch (System.Exception)
            {
                // Expired or revoked, blocked until the admin signs in again (MFA, Conditional Access, sign-in frequency),
                // or a failure this version doesn't recognise: the remedy the page can offer is to connect again, which
                // captures a new refresh token.
                return null;
            }

            return string.IsNullOrEmpty(refreshed?.AccessToken) ? null : new JSonToken(refreshed);
        }

        private static HttpResponseMessage EntraUnavailable() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(JsonConvert.SerializeObject(new { code = EntraUnavailableCode }), Encoding.UTF8, "application/json"),
        };
    }
}
