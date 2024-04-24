using Common.Entities.Config;
using Common.Entities.Models;
using Common.Entities.Redis;
using Common.Entities.Redis.Teams;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    public class TeamTokenManager
    {
        public TeamTokenManager(O365Team team)
        {
            this.CacheConnectionManager = CacheConnectionManager.GetConnectionManager(new AppConfig().ConnectionStrings.RedisConnectionString);
            this.Team = team;
        }

        public CacheConnectionManager CacheConnectionManager { get; set; }
        public O365Team Team { get; set; }

        static Lazy<Dictionary<O365Team, RefreshOAuthToken>> _cahedTokens = new Lazy<Dictionary<O365Team, RefreshOAuthToken>>(() => new Dictionary<O365Team, RefreshOAuthToken>());
        public async Task<RefreshOAuthToken> GetRefreshToken(ILogger telemetry)
        {

            if (_cahedTokens.Value.ContainsKey(this.Team))
            {
                return _cahedTokens.Value[this.Team];
            }

            RefreshOAuthToken teamToken = null;

            // Get refresh-token for Team
            var refreshToken = await CacheConnectionManager.GetTeamRefreshToken(this.Team.Id);
            if (refreshToken != null)
            {
                // Get access token from refresh token (note: this might require replacing the old refresh key later)
                bool success = false;
                try
                {
                    teamToken = await RefreshOAuthToken.GetNewRefreshToken(refreshToken, new AppConfig());
                    success = true;
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    if (ex.Message.Contains("Bad Request"))
                    {
                        telemetry.LogError(ex, $"Got error {ex.Message} trying to get access token for team. App registration configuration issue? Check reply URLs match");
                    }
                    else
                    {
                        // Get access key failed. Delete key
                        telemetry.LogError(ex, $"Got error {ex.Message} trying to get access token for team. Removing refresh-token from cache.");
                        await CacheConnectionManager.RemoveTeamAuthToken(this.Team.Id);
                    }

                }

                if (success)
                {
                    telemetry.LogInformation($"Got refresh token for Team '{this.Team.DisplayName}'.");
                }

                _cahedTokens.Value.Add(this.Team, teamToken);

                return teamToken;
            }
            else
            {
                telemetry.LogInformation($"Couldn't find token entry in redis for Team '{this.Team.DisplayName}', or refresh token is null.");
            }

            return teamToken;
        }
    }
}
