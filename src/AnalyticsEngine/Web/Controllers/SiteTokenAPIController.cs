using Common.Entities.Config;
using Common.Entities.Models;
using System.Threading.Tasks;
using System.Web.Http;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    public class SiteTokenAPIController : BaseAPIController
    {
        // POST: api/SiteTokenAPI
        // Returns a fresh Microsoft Graph access token for the signed-in admin to the SPA.
        public async Task<JSonToken> Post()
        {
            var auth = await base.GetCachedUserAccessTokenAsync();
            if (auth == null || string.IsNullOrEmpty(auth.RefreshToken))
            {
                // No usable token (e.g. signed in before token capture, or no Redis fallback).
                throw new HttpResponseException(System.Net.HttpStatusCode.Unauthorized);
            }

            // The cookie only stores the (long-lived) refresh token, so mint a fresh access token
            // from it. This also transparently handles the ~1h access-token expiry for long sessions.
            var config = new AppConfig();
            RefreshOAuthToken refreshed;
            try
            {
                refreshed = await RefreshOAuthToken.GetNewRefreshToken(auth.RefreshToken, config);
            }
            catch
            {
                // Fall back to a stored access token if we happen to have one (Redis path); otherwise
                // signal the SPA to re-authenticate.
                if (!string.IsNullOrEmpty(auth.AccessToken))
                {
                    return new JSonToken(auth);
                }
                throw new HttpResponseException(System.Net.HttpStatusCode.Unauthorized);
            }

            if (refreshed == null || string.IsNullOrEmpty(refreshed.AccessToken))
            {
                throw new HttpResponseException(System.Net.HttpStatusCode.Unauthorized);
            }

            return new JSonToken(refreshed);
        }
    }
}
