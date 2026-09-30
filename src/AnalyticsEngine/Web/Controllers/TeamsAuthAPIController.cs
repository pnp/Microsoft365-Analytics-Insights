using Common.Entities.Config;
using Common.Entities.State;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

            // Without a Storage connection string no Team can have a stored token, so report
            // everything as unauthorised rather than failing.
            var tokens = GetTokenStore();
            var authorised = tokens != null
                ? await tokens.GetAuthorisationStatusAsync(teamIds)
                : new Dictionary<string, bool>();

            foreach (var teamId in teamIds)
            {
                response.Add(new TeamAuthStatusResponse
                {
                    TeamId = teamId,
                    HasAuthToken = teamId != null && authorised.TryGetValue(teamId, out var hasToken) && hasToken,
                });
            }
            return response;
        }

        /// <summary>
        /// Upload refresh token for a Team ID
        /// </summary>
        // PUT: api/TeamsAuthAPI
        public async Task<IActionResult> Put([FromBody] AuthTeamRequest authTeamData)
        {
            if (authTeamData == null)
            {
                return NotFound();
            }

            // Teams deep analytics needs somewhere to keep the per-Team refresh token the importer reads.
            // Without it, return a clear, actionable message rather than a misleading 401.
            var tokens = GetTokenStore();
            if (tokens == null)
            {
                return StatusCode((int)HttpStatusCode.ServiceUnavailable, new ApiErrorModel(
                    "Teams deep analytics can't be enabled because Azure Storage is not configured for this deployment. " +
                    "Add a Storage connection string so Teams authorisation tokens can be stored."));
            }

            // The refresh token captured into the auth cookie at sign-in (Startup.ConfigureAuth)
            var auth = await base.GetCachedUserAccessTokenAsync();
            if (auth == null || string.IsNullOrEmpty(auth.RefreshToken))
            {
                return Unauthorized();
            }

            if (authTeamData.TeamIdsToAuth != null)
            {
                foreach (var teamIdToAuth in authTeamData.TeamIdsToAuth.Where(id => !string.IsNullOrEmpty(id)))
                {
                    await tokens.SetRefreshTokenAsync(teamIdToAuth, auth.RefreshToken);
                }
            }

            if (authTeamData.TeamIdsToDeauth != null)
            {
                foreach (var teamIdToDeAuth in authTeamData.TeamIdsToDeauth.Where(id => !string.IsNullOrEmpty(id)))
                {
                    await tokens.RemoveRefreshTokenAsync(teamIdToDeAuth);
                }
            }

            return Ok();
        }

        /// <summary>
        /// The per-Team token store, or <c>null</c> when no Storage connection string is configured.
        /// </summary>
        TeamsTokenStore GetTokenStore()
        {
            return TeamsTokenStore.TryOpen(new AppConfig());
        }
    }
}