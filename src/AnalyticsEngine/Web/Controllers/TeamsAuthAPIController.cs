using Common.Entities.Config;
using Common.Entities.Redis;
using Common.Entities.Redis.Teams;
using System.Collections.Generic;
using System.Net;
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
            var response = new List<TeamAuthStatusResponse>();
            if (teamIds == null)
            {
                return response;
            }

            // Redis is optional. With no cache no Team can have a stored token, so report
            // everything as unauthorised rather than failing.
            var cache = GetConnectionManager();

            foreach (var teamId in teamIds)
            {
                var cachedToken = cache != null ? await cache.GetTeamRefreshToken(teamId) : null;
                response.Add(new TeamAuthStatusResponse { TeamId = teamId, HasAuthToken = cachedToken != null });
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

            // Redis is optional for the web app, but Teams deep analytics specifically needs it to
            // store the per-Team refresh token. Without it, return a clear, actionable message
            // rather than a misleading 401.
            var cache = GetConnectionManager();
            if (cache == null)
            {
                return Content(HttpStatusCode.ServiceUnavailable, new ApiErrorModel(
                    "Teams deep analytics can't be enabled because Redis is not configured for this deployment. " +
                    "Add a Redis connection string so Teams authorisation tokens can be stored."));
            }

            // Get redis-cached token we got on login in Startup.ConfigureAuth
            var auth = await base.GetCachedUserAccessTokenAsync();
            if (auth == null || string.IsNullOrEmpty(auth.RefreshToken))
            {
                return Unauthorized();
            }

            if (authTeamData.TeamIdsToAuth != null)
            {
                foreach (var teamIdToAuth in authTeamData.TeamIdsToAuth)
                {
                    await cache.SetTeamRefreshToken(teamIdToAuth, auth.RefreshToken);
                }
            }

            if (authTeamData.TeamIdsToDeauth != null)
            {
                foreach (var teamIdToDeAuth in authTeamData.TeamIdsToDeauth)
                {
                    await cache.RemoveTeamAuthToken(teamIdToDeAuth);
                }
            }

            return Ok();
        }

        /// <summary>
        /// Gets the Redis cache manager, or <c>null</c> when Redis is not configured (optional).
        /// </summary>
        CacheConnectionManager GetConnectionManager()
        {
            var appConfig = new AppConfig();
            var cache = CacheConnectionManager.TryGetConnectionManager(appConfig.ConnectionStrings.RedisConnectionString, tenantId: appConfig.TenantGUID.ToString(), clientId: appConfig.ClientID, clientSecret: appConfig.ClientSecret);

            return cache;
        }
    }
}
