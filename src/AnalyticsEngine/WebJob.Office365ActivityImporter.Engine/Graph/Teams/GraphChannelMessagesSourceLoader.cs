using Common.Entities.Models;
using Common.Entities.Redis.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Graph implementation of <see cref="IChannelMessagesSourceLoader"/>, reading channel messages as
    /// the user whose refresh token we hold (channel message content is not available to an
    /// application-only identity). Extracted from <c>TeamChannelExtensions</c>. See issue #377.
    /// </summary>
    public class GraphChannelMessagesSourceLoader : IChannelMessagesSourceLoader
    {
        private readonly RefreshOAuthToken _refreshToken;
        private readonly ITeamChannelDeltaTokenStore _deltaTokenStore;
        private readonly ILogger _logger;

        /// <param name="refreshToken">
        /// User-delegated token to impersonate. May be <c>null</c>, in which case nothing is read and no
        /// delta token is returned - the same "no token, no deep analytics" behaviour as before.
        /// </param>
        public GraphChannelMessagesSourceLoader(RefreshOAuthToken refreshToken, ITeamChannelDeltaTokenStore deltaTokenStore, ILogger logger)
        {
            _refreshToken = refreshToken;
            _deltaTokenStore = deltaTokenStore ?? throw new ArgumentNullException(nameof(deltaTokenStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<TeamsRedisManager.TeamChannelDeltaTokenInfo> LoadMessagesAndReactions(ChannelWithReactions channel, string teamId)
        {
            TeamsRedisManager.TeamChannelDeltaTokenInfo channelDeltaInfo = null;

            // Try and get user-delegated channel stats
            if (_refreshToken != null)
            {
                // Managed to get user-delegated token from refresh-token. Impersonate user.
                // v5+ removed DelegateAuthenticationProvider - drop in our own
                // IAuthenticationProvider that pins the supplied bearer token onto every request.
                var _preCachedTokenClient = new GraphServiceClient(new BearerTokenAuthenticationProvider(_refreshToken.AccessToken));

                var channelMessagesLoader = new ChannelMessagesLoader(_preCachedTokenClient, _deltaTokenStore, _logger);
                try
                {
                    // Load msgs using user token
                    channelDeltaInfo = await channelMessagesLoader.LoadTeamMessagesAndReplies(channel, teamId);
                }
                catch (ODataError ex)
                {
                    // Assume there's an issue with the token. Parent will handle token clean-up
                    throw new ChannelMessagesReadException(ex);
                }
            }

            return channelDeltaInfo;
        }
    }
}
