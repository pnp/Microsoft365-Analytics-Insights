using Common.Entities.Config;
using Common.Entities.Models;
using Common.Entities.Redis;
using Common.Entities.Redis.Auth;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web.Http;

namespace Web.AnalyticsWeb.Controllers
{
    public class BaseAPIController : ApiController
    {
        /// <summary>
        /// Gets the signed-in admin's Graph token. Primary source is the encrypted auth cookie
        /// (the refresh token captured during the OIDC sign-in redirect), which works without
        /// Redis. Falls back to Redis for legacy deployments / cookies issued before this change.
        /// Returns <c>null</c> when neither source has a token.
        /// </summary>
        public async Task<RefreshOAuthToken> GetCachedUserAccessTokenAsync()
        {
            // Primary: token carried in the auth cookie (no Redis needed).
            var fromCookie = GetUserTokenFromClaims();
            if (fromCookie != null)
            {
                return fromCookie;
            }

            // Fallback: Redis (when configured).
            var config = new AppConfig();
            var redisConManager = CacheConnectionManager.TryGetConnectionManager(config.ConnectionStrings.RedisConnectionString, tenantId: config.TenantGUID.ToString(), clientId: config.ClientID, clientSecret: config.ClientSecret);
            if (redisConManager == null)
            {
                // No cookie token and no Redis - callers treat this as "no token" and fall back to
                // client-side auth where appropriate.
                return null;
            }

            var authToken = await redisConManager.GetToken(ClaimsPrincipal.Current);

            return authToken;
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
