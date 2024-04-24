using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;

namespace Common.Entities.Redis.Teams
{
    /// <summary>
    /// Teams specific redis caching extensions for ConnectionManager
    /// </summary>
    public static class TeamsRedisManager
    {
        #region TeamsRefreshToken

        public static async Task<string> GetTeamRefreshToken(this CacheConnectionManager connectionManager, string teamId)
        {
            var tokenString = await connectionManager.GetString(GetRedisTeamKey(teamId));
            if (tokenString == null)
            {
                return null;
            }
            else
            {
                return tokenString;
            }
        }

        public static async Task SetTeamRefreshToken(this CacheConnectionManager connectionManager, string teamId, string refreshToken)
        {
            await connectionManager.SetString(GetRedisTeamKey(teamId), refreshToken);
        }

        public static async Task RemoveTeamAuthToken(this CacheConnectionManager connectionManager, string teamId)
        {
            await connectionManager.DeleteString(GetRedisTeamKey(teamId));
        }

        const string TEAM_TOKEN_KEY_PREFIX = "TeamToken_";
        static string GetRedisTeamKey(string teamId)
        {
            return TEAM_TOKEN_KEY_PREFIX + teamId;
        }


        #endregion


        public static async Task<TeamChannelDeltaTokenInfo> GetTeamChannelDeltaTokenInfo(this CacheConnectionManager connectionManager, string teamId, string channelId)
        {
            var tokenString = await connectionManager.GetString(GetRedisTeamChannelKey(teamId, channelId));
            if (tokenString == null)
            {
                return null;
            }
            else
            {
                try
                {
                    return JsonConvert.DeserializeObject<TeamChannelDeltaTokenInfo>(tokenString);
                }
                catch (JsonReaderException)
                {
                    return null;
                }
            }
        }

        public static async Task SetTeamChannelDeltaTokenInfo(this CacheConnectionManager connectionManager, string teamId, string channelId, TeamChannelDeltaTokenInfo refreshTokenInfo, ILogger telemetry)
        {
            if (connectionManager is null)
            {
                throw new ArgumentNullException(nameof(connectionManager));
            }

            if (string.IsNullOrEmpty(teamId))
            {
                throw new ArgumentException($"'{nameof(teamId)}' cannot be null or empty.", nameof(teamId));
            }

            if (string.IsNullOrEmpty(channelId))
            {
                throw new ArgumentException($"'{nameof(channelId)}' cannot be null or empty.", nameof(channelId));
            }

            if (refreshTokenInfo is null)
            {
                throw new ArgumentNullException(nameof(refreshTokenInfo));
            }

            telemetry.LogInformation($"Updated cached delta token for channel '{channelId}' in team '{teamId}'");
            await connectionManager.SetString(GetRedisTeamChannelKey(teamId, channelId), JsonConvert.SerializeObject(refreshTokenInfo));
        }

        public static async Task RemoveTeamChannelDeltaToken(this CacheConnectionManager connectionManager, string teamId, string channelId, ILogger telemetry)
        {
            telemetry.LogInformation($"Removed cached delta token for channel '{channelId}' in team '{teamId}'");
            await connectionManager.DeleteString(GetRedisTeamChannelKey(teamId, channelId));
        }

        const string TEAM_CHANNEL_TOKEN_KEY_PREFIX = "TeamTokenChannelDelta_";
        static string GetRedisTeamChannelKey(string teamId, string channelId)
        {
            return $"{TEAM_CHANNEL_TOKEN_KEY_PREFIX}-{teamId}-{channelId}";
        }

        public class TeamChannelDeltaTokenInfo
        {
            public string Token { get; set; } = string.Empty;
            public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        }
    }
}
