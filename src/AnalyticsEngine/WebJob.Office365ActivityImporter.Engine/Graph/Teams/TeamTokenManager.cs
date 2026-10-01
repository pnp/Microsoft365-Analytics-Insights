using Common.Entities.Config;
using Common.Entities.Models;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Resolves the per-Team delegated (refresh) OAuth token used to read channel messages, from the runtime state
    /// store (<see cref="StatePartitions.TeamsAuth"/>) that the portal's Teams permissions page writes to.
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
    /// What it did do was leak and race, for every team that reached the add - token storage configured, and a
    /// refresh-token entry present for that team. The dictionary retained a <b>reference</b> to each such
    /// <c>O365Team</c>, and therefore to whatever that object went on to hold, including its channel messages
    /// and reactions, for the life of the WebJob process - while never being read and never being cleared.
    /// And the Teams crawl runs teams in parallel (<c>TeamsImporter.RefreshAndSaveAllTeamsData</c> via
    /// <c>ParallelListProcessor</c>), so that unsynchronised <c>Dictionary.Add</c> was a genuine data race
    /// whenever two chunks reached it at once.
    /// </para>
    /// </summary>
    public class TeamTokenManager
    {
        public TeamTokenManager(O365Team team, AppConfig appConfig, ILogger logger)
        {
            this.TokenStore = TeamsTokenStore.TryOpen(appConfig, logger);
            if (this.TokenStore != null)
            {
                this.ChannelDeltaTokenStore = new PersistedTeamChannelDeltaTokenStore(
                    StateStore.TryOpen(appConfig, StatePartitions.TeamsChannels, logger), logger);
            }
            else
            {
                logger.LogWarning("No Storage connection string found in config. No deep Teams analytics will be possible.");
            }
            this.Team = team;
        }

        /// <summary>Where the per-Team tokens are kept; <c>null</c> when no Storage connection string is configured.</summary>
        public TeamsTokenStore TokenStore { get; }

        /// <summary>Where channel delta tokens are kept; <c>null</c> when no Storage connection string is configured.</summary>
        public ITeamChannelDeltaTokenStore ChannelDeltaTokenStore { get; }

        public O365Team Team { get; set; }

        public async Task<RefreshOAuthToken> GetRefreshToken(ILogger logger)
        {
            if (TokenStore == null)
            {
                // No storage for tokens
                return null;
            }

            RefreshOAuthToken teamToken = null;

            // Get refresh-token for Team
            var refreshToken = await TokenStore.GetRefreshTokenAsync(this.Team.Id);
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
                        logger.LogError(ex, $"Got error {ex.Message} trying to get access token for team. Removing refresh-token from storage.");
                        await TokenStore.RemoveRefreshTokenAsync(this.Team.Id);
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
                logger.LogInformation($"Couldn't find a stored token for Team '{this.Team.DisplayName}', or refresh token is null.");
            }

            return teamToken;
        }
    }
}
