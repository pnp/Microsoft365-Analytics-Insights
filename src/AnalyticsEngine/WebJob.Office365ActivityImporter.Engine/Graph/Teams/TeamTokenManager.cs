using Common.Entities.Config;
using Common.Entities.Models;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
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
        private readonly System.Func<string, Task<RefreshOAuthToken>> _redeemRefreshToken;
        private readonly ILogger _logger;
        private readonly object _ownedTokensLock = new object();
        private readonly List<string> _tokensThisCrawlOwns = new List<string>();
        private string _refreshToken;

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
            _logger = logger;
            _redeemRefreshToken = refreshToken => RefreshOAuthToken.GetNewRefreshToken(refreshToken, new AppConfig());
        }

        /// <summary>The stores and the token endpoint supplied, so the token decisions can be tested without Azure.</summary>
        internal TeamTokenManager(O365Team team, TeamsTokenStore tokenStore, ITeamChannelDeltaTokenStore channelDeltaTokenStore,
            System.Func<string, Task<RefreshOAuthToken>> redeemRefreshToken, ILogger logger = null)
        {
            this.Team = team;
            this.TokenStore = tokenStore;
            this.ChannelDeltaTokenStore = channelDeltaTokenStore;
            _redeemRefreshToken = redeemRefreshToken ?? throw new System.ArgumentNullException(nameof(redeemRefreshToken));
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        /// <summary>
        /// Where the per-Team tokens are kept; <c>null</c> when no Storage connection string is configured. Private, so the
        /// only way the importer removes a Team's token is <see cref="RemoveRefusedRefreshTokenAsync"/>.
        /// </summary>
        private TeamsTokenStore TokenStore { get; }

        /// <summary>Where channel delta tokens are kept; <c>null</c> when no Storage connection string is configured.</summary>
        public ITeamChannelDeltaTokenStore ChannelDeltaTokenStore { get; }

        public O365Team Team { get; set; }

        /// <summary>What happened the last time <see cref="GetRefreshToken"/> ran.</summary>
        public TeamTokenStatus Status { get; private set; } = TeamTokenStatus.NotChecked;

        /// <summary>
        /// A fresh delegated access token for the Team, or <c>null</c> when there isn't one this cycle - see
        /// <see cref="Status"/> for why.
        /// </summary>
        /// <remarks>
        /// The stored refresh token is deleted only when Entra ID says it is dead (<c>invalid_grant</c>); see
        /// <see cref="TeamTokenRefreshRules.IsRefreshTokenRejected"/>. It used to be the other way round: a dead token
        /// (HTTP 400) was kept and logged as a reply-URL problem every cycle, while a network error, throttling, an
        /// outage or the importer's own expired client secret deleted it. Even then it is deleted only while it is still
        /// the token that was read: see <see cref="RemoveRefusedRefreshTokenAsync"/>.
        /// </remarks>
        public async Task<RefreshOAuthToken> GetRefreshToken(ILogger logger)
        {
            if (TokenStore == null)
            {
                // No storage for tokens
                Status = TeamTokenStatus.NoStorage;
                return null;
            }

            // Get refresh-token for Team
            var refreshToken = await TokenStore.GetRefreshTokenAsync(this.Team.Id);
            if (refreshToken == null)
            {
                logger.LogInformation($"Couldn't find a stored token for Team '{this.Team.DisplayName}', or refresh token is null.");
                Status = TeamTokenStatus.NotAuthorised;
                return null;
            }

            _refreshToken = refreshToken;
            Own(refreshToken);
            RefreshOAuthToken teamToken;
            try
            {
                teamToken = await RedeemRefreshTokenAsync();
            }
            catch (System.Exception ex) when (!TeamsCrawlRules.IsCancellation(ex))
            {
                if (TeamTokenRefreshRules.IsRefreshTokenRejected(ex))
                {
                    logger.LogError(ex, $"Entra ID no longer accepts the refresh token read for Team '{this.Team.DisplayName}' ({TeamTokenRefreshRules.Describe(ex)}).");
                    Status = await RemoveRefusedRefreshTokenAsync(logger) == RefusedTokenOutcome.KeptNewAuthorisation
                        ? TeamTokenStatus.RefreshFailed
                        : TeamTokenStatus.Revoked;
                }
                else
                {
                    logger.LogError(ex, $"Got error {ex.Message} trying to get access token for Team '{this.Team.DisplayName}' ({TeamTokenRefreshRules.Describe(ex)}). " +
                        "Keeping its refresh token; this is retried next cycle.");
                    Status = TeamTokenStatus.RefreshFailed;
                }
                return null;
            }

            logger.LogInformation($"Got refresh token for Team '{this.Team.DisplayName}'.");
            Status = TeamTokenStatus.Refreshed;
            return teamToken;
        }

        /// <summary>
        /// Deletes the Team's stored refresh token after Entra ID or Graph refused the token this crawl holds - but only
        /// while the stored token is still one this crawl read, or a renewed one it stored itself.
        /// </summary>
        /// <remarks>
        /// The refusal is evidence about the token this crawl read, which can be hours old: renewing access tokens lets a
        /// crawl run that long. Deleting whatever was stored by then deleted any authorisation an administrator made in the
        /// meantime - for example after the password of the person whose token it was had been reset, which revokes their
        /// refresh tokens. See <see cref="TeamsTokenStore.TryRemoveRefreshTokenAsync"/>.
        /// </remarks>
        public async Task<RefusedTokenOutcome> RemoveRefusedRefreshTokenAsync(ILogger logger)
        {
            if (TokenStore == null) return RefusedTokenOutcome.AlreadyRemoved;

            string[] owned;
            lock (_ownedTokensLock)
            {
                owned = _tokensThisCrawlOwns.ToArray();
            }

            if (await TokenStore.TryRemoveRefreshTokenAsync(this.Team.Id, owned))
            {
                logger.LogWarning($"Deleted the refresh token stored for Team '{this.Team.DisplayName}'. Authorise the Team again on the " +
                    "Teams permissions page to resume its deep analytics.");
                return RefusedTokenOutcome.Removed;
            }

            // Not ours to delete. Whether the Team is still authorised decides only the status it is saved with.
            var authorisation = await TokenStore.GetAuthorisationStatusAsync(new[] { this.Team.Id });
            if (authorisation.TryGetValue(this.Team.Id, out var stillAuthorised) && stillAuthorised)
            {
                logger.LogInformation($"Kept the refresh token stored for Team '{this.Team.DisplayName}': the Team was authorised again while " +
                    "this crawl ran, and only the token the crawl read was refused. The new authorisation is used next cycle.");
                return RefusedTokenOutcome.KeptNewAuthorisation;
            }

            logger.LogInformation($"No refresh token to delete for Team '{this.Team.DisplayName}': its authorisation was removed while this crawl ran.");
            return RefusedTokenOutcome.AlreadyRemoved;
        }

        private void Own(string refreshToken)
        {
            lock (_ownedTokensLock)
            {
                if (!_tokensThisCrawlOwns.Contains(refreshToken)) _tokensThisCrawlOwns.Add(refreshToken);
            }
        }

        /// <summary>
        /// Mints a new access token from the refresh token <see cref="GetRefreshToken"/> read. Throws when Entra ID refuses
        /// or can't be reached; the caller decides what that means for the stored token. Used for a second access token
        /// when Graph refuses the first part-way through a long crawl.
        /// </summary>
        /// <remarks>
        /// Entra ID hands back a new refresh token each time, and each lasts about 90 days from issue, so the new one is
        /// stored - conditionally, see <see cref="TeamsTokenStore.TryReplaceRefreshTokenAsync"/>. Keeping only the token
        /// captured when the Team was authorised meant the authorisation lapsed about 90 days later.
        /// </remarks>
        public async Task<RefreshOAuthToken> RedeemRefreshTokenAsync()
        {
            if (_refreshToken == null)
            {
                throw new System.InvalidOperationException("No refresh token has been read for this Team.");
            }

            var redeemed = _refreshToken;
            var token = await _redeemRefreshToken(redeemed);

            var rotated = token?.RefreshToken;
            if (!string.IsNullOrEmpty(rotated) && !string.Equals(rotated, redeemed, System.StringComparison.Ordinal))
            {
                // Any further access token this crawl needs is minted from the newest token for this grant, and a refusal
                // of it may delete it from storage: it is this crawl's, if the write below lands.
                _refreshToken = rotated;
                Own(rotated);
                await StoreRotatedRefreshToken(redeemed, rotated);
            }
            return token;
        }

        private async Task StoreRotatedRefreshToken(string redeemed, string rotated)
        {
            try
            {
                if (await TokenStore.TryReplaceRefreshTokenAsync(this.Team.Id, redeemed, rotated))
                {
                    _logger.LogInformation($"Stored the renewed refresh token for Team '{this.Team.DisplayName}'.");
                }
                else
                {
                    _logger.LogInformation($"Didn't store the renewed refresh token for Team '{this.Team.DisplayName}': its authorisation was " +
                        "removed or renewed while it was being used, and that change stands.");
                }
            }
            catch (System.Exception ex) when (!TeamsCrawlRules.IsCancellation(ex))
            {
                // Costs nothing yet: the token just redeemed stays valid until its own expiry.
                _logger.LogWarning(ex, $"Couldn't store the renewed refresh token for Team '{this.Team.DisplayName}': {ex.Message}. " +
                    "The stored one still works and is renewed again next cycle.");
            }
        }
    }

    /// <summary>What <see cref="TeamTokenManager.GetRefreshToken"/> found.</summary>
    public enum TeamTokenStatus
    {
        NotChecked,

        /// <summary>No Storage connection string, so no Team can be authorised.</summary>
        NoStorage,

        /// <summary>The Team has no stored token: nobody has authorised it.</summary>
        NotAuthorised,

        /// <summary>A fresh access token was minted.</summary>
        Refreshed,

        /// <summary>
        /// Entra ID said the token read is dead (<c>invalid_grant</c>), and the Team is no longer authorised: the token was
        /// deleted, or its authorisation had already been removed.
        /// </summary>
        Revoked,

        /// <summary>
        /// No access token this cycle for a reason that says nothing about the stored token - Entra ID unreachable,
        /// throttling, an outage, the importer's own credential rejected, or the token read was refused after an
        /// administrator had authorised the Team again. The Team is still authorised.
        /// </summary>
        RefreshFailed
    }

    /// <summary>What <see cref="TeamTokenManager.RemoveRefusedRefreshTokenAsync"/> did with the Team's stored token.</summary>
    public enum RefusedTokenOutcome
    {
        /// <summary>The stored token was one this crawl read or stored itself, and it was deleted.</summary>
        Removed,

        /// <summary>An administrator authorised the Team again while the crawl ran: that token was kept.</summary>
        KeptNewAuthorisation,

        /// <summary>There was no stored token any more: the Team's authorisation was removed while the crawl ran.</summary>
        AlreadyRemoved
    }
}
