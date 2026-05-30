using Azure;
using Azure.AI.TextAnalytics;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Models;
using Common.Entities.Redis;
using Common.Entities.Redis.Teams;
using Common.Entities.Teams;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public static class TeamChannelExtensions
    {
        // Per-process cached cognitive client. Reused across all channels in an importer run
        // because TextAnalyticsClient is thread-safe and pools HTTP connections; rebuilding it
        // per channel would leak sockets. The CognitiveServicesClient wrapper also handles
        // auto-fallback to RBAC when the resource rejects key auth at runtime.
        private static readonly object _cognitiveClientLock = new object();
        private static CognitiveServicesClient _cachedCognitiveClient;
        private static bool _cognitiveClientBuilt;

        private static CognitiveServicesClient GetOrBuildCognitiveClient(AppConfig cognitiveConfig, ILogger telemetry)
        {
            if (_cognitiveClientBuilt) return _cachedCognitiveClient;
            lock (_cognitiveClientLock)
            {
                if (!_cognitiveClientBuilt)
                {
                    _cachedCognitiveClient = cognitiveConfig.CreateCognitiveServicesClient(telemetry);
                    _cognitiveClientBuilt = true;
                }
                return _cachedCognitiveClient;
            }
        }
        /// <summary>
        /// Sets the "Messages" prop on each channel by reading each channel messages.
        /// </summary>
        public static async Task PopulateNewMessagesAndReactions(this List<ChannelWithReactions> channels, Team team, RefreshOAuthToken refreshToken,
            CacheConnectionManager cacheConnectionManager, ILogger telemetry)
        {

            foreach (var channel in channels)
            {
                // Load stats. Will throw ChannelMessagesReadException if token is invalid
                var channelDelta = await channel.GetChannelMessagesAndReactions(team, refreshToken, cacheConnectionManager, telemetry);

                // Save delta token for next read
                if (channelDelta != null)
                {
                    await cacheConnectionManager.SetTeamChannelDeltaTokenInfo(team.Id, channel.Id, channelDelta, telemetry);
                }
            }
        }

        /// <summary>
        /// Save this channel stats. Channel object should be fully-loaded with tabs
        /// </summary>
        static async Task<TeamsRedisManager.TeamChannelDeltaTokenInfo> GetChannelMessagesAndReactions(this ChannelWithReactions channel, Team parentTeam, RefreshOAuthToken refreshToken,
            CacheConnectionManager cacheConnectionManager, ILogger telemetry)
        {
            TeamsRedisManager.TeamChannelDeltaTokenInfo channelDeltaInfo = null;

            // Try and get user-delegated channel stats
            if (refreshToken != null)
            {
                // Managed to get user-delegated token from refresh-token. Impersonate user
                var _preCachedTokenClient = new GraphServiceClient(new DelegateAuthenticationProvider(
                    (requestMessage) =>
                    {
                        requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("bearer", refreshToken.AccessToken);
                        return Task.FromResult(0);
                    })
                );

                var channelMessagesLoader = new ChannelMessagesLoader(_preCachedTokenClient, cacheConnectionManager, telemetry);
                try
                {
                    // Load msgs using user token
                    channelDeltaInfo = await channelMessagesLoader.LoadTeamMessagesAndReplies(channel, parentTeam.Id);
                }
                catch (ServiceException ex)
                {
                    // Assume there's an issue with the token. Parent will handle token clean-up
                    throw new ChannelMessagesReadException(ex);
                }
            }

            return channelDeltaInfo;
        }

        /// <summary>
        /// Loads cognitive data for these messages. Messages may be on different dates, hence list of stats.
        /// </summary>
        internal static async Task<List<MessageCognitiveStats>> GetCognitiveDataStats(this IEnumerable<ChatMessage> allChannelMsgs, ILogger telemetry, ChannelWithReactions parentChannel)
        {
            var allStatsAllDays = new List<MessageCognitiveStats>();

            // Ensure all msgs have stats for them
            var cognitiveConfig = new AppConfig();

            // Save msg stats for channel, including previous days too
            var msgDates = allChannelMsgs.ToList().GetUniqueDates();

            if (!cognitiveConfig.IsValidCognitiveConfig)
            {
                telemetry.LogWarning($"Cognitive config not valid. Cannot load cognitive stats for channel {parentChannel.DisplayName} ({parentChannel.Id}). Adding basic stats with no cognitive insights.");
                // No cognitive available. Add basic stats for all dates
                foreach (var uniqueMsgDate in msgDates)
                {
                    var msgsForDate = allChannelMsgs.GetByDate(uniqueMsgDate);
                    allStatsAllDays.Add(new MessageCognitiveStats(parentChannel, uniqueMsgDate) { ChatsCount = msgsForDate.Count });
                }
                return allStatsAllDays;
            }

            // Use key auth when CognitiveKey is set, otherwise RBAC (ClientSecretCredential)
            // so we still work against resources that have key auth disabled. The wrapper
            // is cached per process and auto-falls back to RBAC on key-auth failure.
            var client = GetOrBuildCognitiveClient(cognitiveConfig, telemetry);
            if (client == null)
            {
                telemetry.LogWarning($"Could not build cognitive client for channel {parentChannel.DisplayName} ({parentChannel.Id}). Adding basic stats with no cognitive insights.");
                foreach (var uniqueMsgDate in msgDates)
                {
                    var msgsForDate = allChannelMsgs.GetByDate(uniqueMsgDate);
                    allStatsAllDays.Add(new MessageCognitiveStats(parentChannel, uniqueMsgDate) { ChatsCount = msgsForDate.Count });
                }
                return allStatsAllDays;
            }

            foreach (var uniqueMsgDate in msgDates)
            {
                // No log - generate new stats
                var msgsForDate = allChannelMsgs.GetByDate(uniqueMsgDate);
                var dateStats = await msgsForDate.LoadSameDayCognitiveDataStats(client, telemetry, parentChannel);
                allStatsAllDays.Add(dateStats);
            }

            return allStatsAllDays;
        }

        public static async Task<TeamChannel> SaveToSql(this ChannelWithReactions channel, TeamsAndCallsDBLookupManager lookupManager, Common.Entities.Entities.TeamDefinition dbTeam)
        {
            // Save channel to SQL if doesn't exist already
            var existingChannelSQL = await lookupManager.GetTeamChannel(channel.Id, channel.DisplayName, dbTeam);
            if (!existingChannelSQL.IsSavedToDB)
            {
                lookupManager.Database.TeamChannels.Add(existingChannelSQL);
            }

            if (channel.Tabs != null)
            {
                foreach (var tab in channel.Tabs)
                {
                    // Add-ins are saved previously so should find any add-ons now no problem
                    TeamAddOnDefinition tabAddOn = null;
                    if (tab.TeamsApp != null)
                    {
                        // Find/create app def. This should be already saved from team installed apps?
                        tabAddOn = await lookupManager.GetTeamAddOnDefinition(tab.TeamsApp.Id, tab.TeamsApp.DisplayName);
                    }

                    // Get tab def
                    var tabDB = await lookupManager.GetOrCreateTeamTab(tab.Id, tab.DisplayName, tab.WebUrl, tabAddOn);

                    // Check for tab log for today (UTC: DB Date column is UTC; using local time
                    // straddles a midnight rollover and produces duplicate per-day rows).
                    var today = DateTime.UtcNow.Date;
                    int todayYear = today.Year, todayMonth = today.Month, todayDay = today.Day;
                    var tabLog = await lookupManager.Database.ChannelTabLogs.SingleOrDefaultAsync(l =>
                        l.Date.Year == todayYear &&
                        l.Date.Month == todayMonth &&
                        l.Date.Day == todayDay &&
                        l.Channel.GraphID == existingChannelSQL.GraphID &&
                        l.TabDefinition.GraphID == tab.Id
                    );

                    if (tabLog == null)
                    {
                        // New tab log for today
                        tabLog = new Common.Entities.Entities.Teams.ChannelTabLog()
                        {
                            Channel = existingChannelSQL,
                            Date = today,
                            TabDefinition = tabDB
                        };
                        lookupManager.Database.ChannelTabLogs.Add(tabLog);
                    }
                }
            }

            return existingChannelSQL;
        }

    }
}
