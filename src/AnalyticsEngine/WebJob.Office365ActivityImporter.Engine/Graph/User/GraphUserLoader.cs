using Common.DataUtils;
using Common.Entities.Config;
using Common.Entities.Redis;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    public class GraphUserLoader
    {
        private readonly ManualGraphCallClient _httpClient;
        private readonly ILogger _telemetry;

        private readonly CacheConnectionManager _cacheConnectionManager;
        public GraphUserLoader(ManualGraphCallClient httpClient, ILogger telemetry)
        {
            this._httpClient = httpClient;
            this._telemetry = telemetry;
            _cacheConnectionManager = CacheConnectionManager.GetConnectionManager(new AppConfig().ConnectionStrings.RedisConnectionString);
        }

        public async Task<List<GraphUser>> LoadAllActiveUsers()
        {
            // Cache delta using tenant ID
            var REDIS_USER_DELTA_KEY = GetRedisUserDeltaCacheKey();
            var usersQueryDelta = await _cacheConnectionManager.GetString(REDIS_USER_DELTA_KEY);

            var initialDeltaUrl = $"https://graph.microsoft.com:443/v1.0/users/delta" +
                "?$select=id,accountEnabled,officeLocation,usageLocation,jobTitle,department,mail,userPrincipalName,manager,companyName,postalCode,country,state" +
                "&$expand=manager";
            if (!string.IsNullOrEmpty(usersQueryDelta))
            {
                initialDeltaUrl += $"&$deltatoken={usersQueryDelta}";
            }

            var results = await _httpClient.LoadAllPagesPlusDeltaWithThrottleRetries<GraphUser>(initialDeltaUrl, _telemetry,
                async (deltaLink) =>
                {
                    var thisPageDelta = StringUtils.ExtractCodeFromGraphUrl(deltaLink);
                    await _cacheConnectionManager.SetString(REDIS_USER_DELTA_KEY, thisPageDelta);
                });


            if (string.IsNullOrEmpty(usersQueryDelta))
            {
                _telemetry.LogInformation($"User import - read {results.Count.ToString("N0")} users (all) from Graph API");
            }
            else
            {
                _telemetry.LogInformation($"User import - read {results.Count.ToString("N0")} updated users from Graph API, using last delta.");
            }

            // Graph for some reason gives duplicates; filter that out
            var allGraphUsers = results.GroupBy(u => u.UserPrincipalName).Select(g => g.First()).ToList();
            var allActiveGraphUsers = allGraphUsers.Where(u => u.AccountEnabled.HasValue && u.AccountEnabled.Value).ToList();

            return allActiveGraphUsers;
        }

        #region Redis

        public async Task ClearUserQueryDeltaCode()
        {
            var REDIS_USER_DELTA_KEY = GetRedisUserDeltaCacheKey();
            await _cacheConnectionManager.DeleteString(REDIS_USER_DELTA_KEY);
            _telemetry.LogInformation("User import - cleared delta token from cache");
        }

        static AppConfig _config = null;
        static string GetRedisUserDeltaCacheKey()
        {
            if (_config == null)
            {
                _config = new AppConfig();
            }
            var REDIS_USER_DELTA_KEY = $"UserDeltaCode-{_config.TenantGUID}";
            return REDIS_USER_DELTA_KEY;
        }

        #endregion

    }
}
