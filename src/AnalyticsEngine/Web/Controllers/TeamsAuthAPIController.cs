using Common.Entities.Config;
using Common.Entities.Redis;
using Common.Entities.Redis.Teams;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    public class TeamsAuthAPIController : BaseAPIController
    {
        /// <summary>
        /// Gets auth status for a list of TeamIDs
        /// </summary>
        // POST: api/TeamsAuthAPI
        public async Task<List<TeamAuthStatusResponse>> Post([FromBody] List<string> teamIds)
        {
            var cache = GetConnectionManager();

            var response = new List<TeamAuthStatusResponse>();

            foreach (var teamId in teamIds)
            {
                var cachedToken = await cache.GetTeamRefreshToken(teamId);
                if (cachedToken != null)
                {
                    response.Add(new TeamAuthStatusResponse { TeamId = teamId, HasAuthToken = true });
                }
                else
                {
                    response.Add(new TeamAuthStatusResponse { TeamId = teamId, HasAuthToken = false });
                }

            }
            return response;
        }

        /// <summary>
        /// Upload refresh token for a Team ID
        /// </summary>
        // PUT: api/TeamsAuthAPI
        public async Task<IHttpActionResult> Put([FromBody] AuthTeamRequest authTeamData)
        {
            if (authTeamData == null)
            {
                return NotFound();
            }

            // Get redis-cached token we got on login in Startup.ConfigureAuth
            var auth = await base.GetUserAccessTokenAsync();

            var cache = GetConnectionManager();
            foreach (var teamIdToAuth in authTeamData.TeamIdsToAuth)
            {
                await cache.SetTeamRefreshToken(teamIdToAuth, auth.RefreshToken);
            }

            foreach (var teamIdToDeAuth in authTeamData.TeamIdsToDeauth)
            {
                await cache.RemoveTeamAuthToken(teamIdToDeAuth);
            }

            return Ok();
        }

        CacheConnectionManager GetConnectionManager()
        {
            var cache = CacheConnectionManager.GetConnectionManager(new AppConfig().ConnectionStrings.RedisConnectionString);

            return cache;
        }
    }
}
