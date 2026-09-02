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
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
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

        private static CognitiveServicesClient GetOrBuildCognitiveClient(AppConfig cognitiveConfig, ILogger logger)
        {
            if (_cognitiveClientBuilt) return _cachedCognitiveClient;
            lock (_cognitiveClientLock)
            {
                if (!_cognitiveClientBuilt)
                {
                    _cachedCognitiveClient = cognitiveConfig.CreateCognitiveServicesClient(logger);
                    _cognitiveClientBuilt = true;
                }
                return _cachedCognitiveClient;
            }
        }
        /// <summary>
        /// Sets the "Messages" prop on each channel by reading each channel messages.
        /// The crawl itself lives in <see cref="TeamsChannelCrawler"/> so it can be tested without
        /// Graph or Redis; this overload wires up the production adapters.
        /// </summary>
        public static async Task PopulateNewMessagesAndReactions(this List<ChannelWithReactions> channels, Team team, RefreshOAuthToken refreshToken,
            CacheConnectionManager cacheConnectionManager, ILogger logger)
        {
            // Nothing to crawl: return before building any adapter, so a team with no channels - or one
            // we hold no user token for - still touches neither Redis, the logger nor team.Id, exactly
            // as the original per-channel loop did (it read messages only when a token was present, and
            // saved a delta token only when one came back).
            if (channels.Count == 0 || refreshToken == null)
            {
                return;
            }

            var deltaTokenStore = new RedisTeamChannelDeltaTokenStore(cacheConnectionManager, logger);
            var messagesSource = new GraphChannelMessagesSourceLoader(refreshToken, deltaTokenStore, logger);

            await new TeamsChannelCrawler(messagesSource, deltaTokenStore).PopulateNewMessagesAndReactions(channels, team.Id);
        }

        /// <summary>
        /// Loads cognitive data for these messages. Messages may be on different dates, hence list of stats.
        /// </summary>
        internal static async Task<List<MessageCognitiveStats>> GetCognitiveDataStats(this IEnumerable<ChatMessage> allChannelMsgs, ILogger logger, ChannelWithReactions parentChannel)
        {
            var allStatsAllDays = new List<MessageCognitiveStats>();

            // Ensure all msgs have stats for them
            var cognitiveConfig = new AppConfig();

            // Save msg stats for channel, including previous days too
            var msgDates = allChannelMsgs.ToList().GetUniqueDates();

            if (!cognitiveConfig.IsValidCognitiveConfig)
            {
                logger.LogWarning($"Cognitive config not valid. Cannot load cognitive stats for channel {parentChannel.DisplayName} ({parentChannel.Id}). Adding basic stats with no cognitive insights.");
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
            var client = GetOrBuildCognitiveClient(cognitiveConfig, logger);
            if (client == null)
            {
                logger.LogWarning($"Could not build cognitive client for channel {parentChannel.DisplayName} ({parentChannel.Id}). Adding basic stats with no cognitive insights.");
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
                var dateStats = await msgsForDate.LoadSameDayCognitiveDataStats(client, logger, parentChannel);
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
                    // Get tab def
                    var tabDB = await lookupManager.GetOrCreateTeamTab(tab.Id, tab.DisplayName, tab.WebUrl);

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
