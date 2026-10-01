using Common.Entities.Models;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web.Http;

namespace Web.AnalyticsWeb.Controllers
{
    public class BaseAPIController : ApiController
    {
        /// <summary>
        /// Gets the signed-in admin's Graph token: the refresh token captured into the encrypted auth cookie
        /// by the on-demand Microsoft Teams connection. Returns <c>null</c> when the cookie does not carry one;
        /// callers answer 401 and the Teams page offers the connection.
        /// </summary>
        public Task<RefreshOAuthToken> GetCachedUserAccessTokenAsync()
        {
            return Task.FromResult(GetUserTokenFromClaims());
        }

        /// <summary>
        /// Reads the Graph refresh token from the current user's auth-cookie claims, or returns
        /// <c>null</c> when it isn't present.
        /// </summary>
        protected static RefreshOAuthToken GetUserTokenFromClaims()
        {
            var identity = ClaimsPrincipal.Current?.Identity as ClaimsIdentity;
            var refreshToken = identity?.FindFirst(GraphTokenClaims.RefreshToken)?.Value;
            if (string.IsNullOrEmpty(refreshToken))
            {
                return null;
            }

            return new RefreshOAuthToken { RefreshToken = refreshToken };
        }
    }
}
