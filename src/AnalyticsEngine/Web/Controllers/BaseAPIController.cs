using Common.Entities.Models;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Shared plumbing for the controllers that need the signed-in user's Graph token. Abstract so ASP.NET Core
    /// never treats it as a controller of its own: it carries no <c>[Authorize]</c>, so anything it exposed
    /// would have been reachable anonymously at <c>api/BaseAPI</c>.
    /// </summary>
    public abstract class BaseAPIController : ControllerBase
    {
        /// <summary>
        /// Gets the signed-in admin's Graph token: the refresh token captured into the encrypted auth cookie
        /// by the on-demand Microsoft Teams connection. Returns <c>null</c> when the cookie does not carry one;
        /// callers answer 401 and the Teams page offers the connection.
        /// </summary>
        /// <remarks>
        /// <c>[NonAction]</c> because Web API treats every public method of a controller as an action and
        /// infers GET from the "Get" prefix: without it, <c>GET api/SiteTokenAPI</c> and
        /// <c>GET api/TeamsAuthAPI</c> both answered with this method's result - the caller's long-lived
        /// Graph refresh token, as JSON, to script. Pinned by
        /// <c>PortalPermissionTests.NoGetRequest_HandsTheCallersRefreshTokenToScript</c> (#660).
        /// </remarks>
        [NonAction]
        public Task<RefreshOAuthToken> GetCachedUserAccessTokenAsync()
        {
            return Task.FromResult(GetUserTokenFromClaims());
        }

        /// <summary>
        /// Reads the Graph refresh token from the current user's auth-cookie claims, or returns
        /// <c>null</c> when it isn't present.
        /// </summary>
        protected RefreshOAuthToken GetUserTokenFromClaims()
        {
            var identity = User?.Identity as ClaimsIdentity;
            var refreshToken = identity?.FindFirst(GraphTokenClaims.RefreshToken)?.Value;
            if (string.IsNullOrEmpty(refreshToken))
            {
                return null;
            }

            return new RefreshOAuthToken { RefreshToken = refreshToken };
        }
    }
}