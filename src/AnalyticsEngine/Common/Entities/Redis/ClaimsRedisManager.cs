using Common.Entities.Models;
using Newtonsoft.Json;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Common.Entities.Redis.Auth
{
    /// <summary>
    /// Teams specific redis caching extensions for ConnectionManager
    /// </summary>
    public static class ClaimsRedisManager
    {
        private const string TOKEN_KEY_PREFIX = "UserToken_";



        public static async Task SaveToken(this CacheConnectionManager connectionManager, ClaimsPrincipal signedInUser, RefreshOAuthToken authToken)
        {
            await connectionManager.SetString(GetRedisKey(signedInUser), authToken.ToString());
        }
        public static async Task<RefreshOAuthToken> GetToken(this CacheConnectionManager connectionManager, ClaimsPrincipal current)
        {
            var token = await connectionManager.GetString(GetRedisKey(current));
            return JsonConvert.DeserializeObject<RefreshOAuthToken>(token);
        }

        static string GetRedisKey(ClaimsPrincipal user)
        {
            return TOKEN_KEY_PREFIX + user.Identity.Name;
        }

    }
}
