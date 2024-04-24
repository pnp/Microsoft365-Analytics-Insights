using Azure.AI.TextAnalytics;
using Common.DataUtils;
using Common.Entities.Config;
using Common.Entities.Redis;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public static class TeamsCognitiveExtensions
    {

        /// <summary>
        /// Loads Azure Cognitive data for a message
        /// </summary>
        /// <returns>Stats and whether it was returned from redis or not</returns>
        public static async Task<(MessageCognitiveStats, bool)> LoadCognitiveStatsFromCacheOrAI(this ChatMessage msg, TextAnalyticsClient client, ILogger telemetry, ChannelWithReactions parentChannel)
        {
            var stats = new MessageCognitiveStats(parentChannel, msg.CreatedDateTime.Value.DateTime);

            var languageBatchInput = CognitiveHelper.BuildTextAnalysisSampleList(msg);

            TeamsMessagesCognitiveStats results = null;
            var fromCache = false;

            // Have we cached this language lookup in redis?
            var redis = CacheConnectionManager.GetConnectionManager(new AppConfig().ConnectionStrings.RedisConnectionString);

            var redisKey = languageBatchInput.Select(i => i.Id + i.Text).Aggregate((a, b) => a + b);
            var cachedResultsJson = await redis.GetStringCache(redisKey);
            if (!string.IsNullOrEmpty(cachedResultsJson))
            {
                try
                {
                    results = JsonConvert.DeserializeObject<TeamsMessagesCognitiveStats>(cachedResultsJson);
                    fromCache = true;
                }
                catch (Exception)
                {
                    // Ignore
                }
            }

            if (cachedResultsJson == null)
            {
                var sentimentAndLang = await languageBatchInput.GetCognitiveDataStats(client, telemetry);

                var listProcessor = new ParallelCallsForSingleReturnListHander<string, ExtractKeyPhrasesResult>();

                // Break down API calls into chunks of 10 max and compile results
                var keyPhrasesResponses = await listProcessor.CallAndCompileToSingleList(languageBatchInput.Select(i => i.Text), async (List<string> chunk) =>
                {
                    var result = await client.ExtractKeyPhrasesBatchAsync(chunk);
                    return result.Value.ToList();
                }, 10);


                if (sentimentAndLang == null)
                {
                    // Message is an adaptive card or something not user generated
                    telemetry.LogDebug($"Return empty stats for ChatMessage {msg.Id}. Message contains no user-genereated data in message.");

                    return (stats, false);
                }

                results = new TeamsMessagesCognitiveStats
                {
                    SentimentAndLanguages = sentimentAndLang.Select(s => s.CognitiveStat).ToList(),
                    KeyPhrases = keyPhrasesResponses.Where(r => !r.HasError).SelectMany(k => k.KeyPhrases).ToList()
                };
                await redis.CacheStringOneDay(redisKey, JsonConvert.SerializeObject(results));
            }

            if (fromCache)
            {
                telemetry.LogInformation($"Loaded cognitive stats from cache for ChatMessage {msg.Id}");
            }
            else
            {
                telemetry.LogInformation($"Loaded cognitive stats from AI for ChatMessage {msg.Id}");
            }

            // Compile stats
            stats = CognitiveHelper.BuildChatStats(results, parentChannel, msg.CreatedDateTime.Value.DateTime);
            return (stats, fromCache);
        }

        public class TeamsMessagesCognitiveStats
        {
            public List<CognitiveStat> SentimentAndLanguages { get; set; }
            public List<string> KeyPhrases { get; set; } = new List<string>();
        }


        /// <summary>
        /// Loads Azure Cognitive data for a message
        /// </summary>
        public static async Task<MessageCognitiveStats> LoadSameDayCognitiveDataStats(this List<ChatMessage> msgsInChannel, TextAnalyticsClient client, ILogger telemetry, ChannelWithReactions parentChannel)
        {
            if (msgsInChannel != null && msgsInChannel.Count > 0)
            {
                var firstMsgDate = msgsInChannel[0].CreatedDateTime.Value.DateTime;
                var dayStats = new MessageCognitiveStats(parentChannel, firstMsgDate);
                dayStats.ChatsCount = msgsInChannel.Count;

                var allStats = new List<MessageCognitiveStats>();
                foreach (var msg in msgsInChannel)
                {
                    if (msg.CreatedDateTime.HasValue && msg.CreatedDateTime.Value.DateTime.Date == firstMsgDate.Date)
                    {
                        var (statsResult, fromCache) = await msg.LoadCognitiveStatsFromCacheOrAI(client, telemetry, parentChannel);
                        allStats.Add(statsResult);
                    }
                    else
                    {
                        throw new InvalidOperationException("Messages need to be on the same day");
                    }
                }

                dayStats.Concat(allStats);

                return dayStats;
            }

            return null;
        }

    }
}
