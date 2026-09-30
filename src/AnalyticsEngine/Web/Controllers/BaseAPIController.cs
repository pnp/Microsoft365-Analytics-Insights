using Common.Entities.Models;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Web.AnalyticsWeb.Controllers
{
    public class BaseAPIController  : ControllerBase
    {
        /// <summary>
        /// Gets the signed-in admin's Graph token: the refresh token captured into the encrypted auth
        /// cookie during the OIDC sign-in redirect. Returns <c>null</c> when the cookie doesn't carry one
        /// (e.g. a session that started before it was captured) - callers answer 401 and the SPA signs in again.
        /// </summary>
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