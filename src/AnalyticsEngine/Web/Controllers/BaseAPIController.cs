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
        /// Get the cached user access token from Redis. This is set on ConfigureAuth so it can be accessed by the API controllers.
        /// </summary>
        /// <returns></returns>
        public async Task<RefreshOAuthToken> GetCachedUserAccessTokenAsync()
        {
            var config = new AppConfig();
            var redisConManager = CacheConnectionManager.GetConnectionManager(config.ConnectionStrings.RedisConnectionString);
            var authToken = await redisConManager.GetToken(ClaimsPrincipal.Current);

            return authToken;
        }
    }
}
