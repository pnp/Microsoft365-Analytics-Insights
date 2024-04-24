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

        public async Task<RefreshOAuthToken> GetUserAccessTokenAsync()
        {
            var config = new AppConfig();
            var redisConManager = CacheConnectionManager.GetConnectionManager(config.ConnectionStrings.RedisConnectionString);
            var authToken = await redisConManager.GetToken(ClaimsPrincipal.Current);

            return authToken;
        }
    }
}