using Common.Entities.Config;
using Common.Entities.Models;
using Common.Entities.Redis;
using Common.Entities.Redis.Teams;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Resolves the per-Team delegated (refresh) OAuth token used to read channel messages, from the Redis
    /// token cache the Teams sign-up page writes to.
    ///
    /// <para>
    /// This class used to hold a process-wide <c>static Lazy&lt;Dictionary&lt;O365Team, RefreshOAuthToken&gt;&gt;</c>
    /// "cache". It was removed as part of issue #376 (separating composition from orchestration), and removing
    /// it changes no observable behaviour, because it could never produce a hit:
    /// </para>
    /// <list type="number">
    /// <item><description><c>O365Team</c> does not override <c>Equals</c>/<c>GetHashCode</c>, so the dictionary
    /// keyed on it used <b>reference</b> equality.</description></item>
    /// <item><description><see cref="GetRefreshToken"/> has exactly one call site
    /// (<c>O365Team.LoadTeamFull</c>), which calls it once against an <c>O365Team</c> it has just constructed -
    /// so the lookup was always a miss and the add always a new entry.</description></item>
    /// </list>
    /// <para>
    /// What it did do was leak and race. Every crawled team, on every cycle, added a fully-populated
    /// <c>O365Team</c> (its channels, messages, members and reactions) plus a token to a dictionary that was
    /// never read and never cleared - for the life of the WebJob process. And the Teams crawl runs teams in
    /// parallel (<c>TeamsImporter.RefreshAndSaveAllTeamsData</c> via <c>ParallelListProcessor</c>), so that
    /// unsynchronised <c>Dictionary.Add</c> was a genuine data race.
    /// </para>
    /// </summary>
    public class TeamTokenManager
    {
        public TeamTokenManager(O365Team team, AppConfig appConfig, ILogger logger)
        {
            if (!string.IsNullOrEmpty(appConfig.ConnectionStrings.RedisConnectionString))
            {
                this.CacheConnectionManager = CacheConnectionManager.GetConnectionManager(appConfig.ConnectionStrings.RedisConnectionString, tenantId: appConfig.TenantGUID.ToString(), clientId: appConfig.ClientID, clientSecret: appConfig.ClientSecret);
            }
            else
            {
                logger.LogWarning("No redis connection string found in config. No deep Teams analytics will be possible.");
            }
            this.Team = team;
        }

        public CacheConnectionManager CacheConnectionManager { get; set; }
        public O365Team Team { get; set; }

        public async Task<RefreshOAuthToken> GetRefreshToken(ILogger logger)
        {
            if (CacheConnectionManager == null)
            {
                // No redis
                return null;
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
                        logger.LogError(ex, $"Got error {ex.Message} trying to get access token for team. App registration configuration issue? Check reply URLs match");
                    }
                    else
                    {
                        // Get access key failed. Delete key
                        logger.LogError(ex, $"Got error {ex.Message} trying to get access token for team. Removing refresh-token from cache.");
                        await CacheConnectionManager.RemoveTeamAuthToken(this.Team.Id);
                    }

                }

                if (success)
                {
                    logger.LogInformation($"Got refresh token for Team '{this.Team.DisplayName}'.");
                }

                return teamToken;
            }
            else
            {
                logger.LogInformation($"Couldn't find token entry in redis for Team '{this.Team.DisplayName}', or refresh token is null.");
            }

            return teamToken;
        }
    }
}
