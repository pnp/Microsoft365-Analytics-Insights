using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public static class ChatMessageListExtensions
    {
        /// <summary>
        /// Generates stats where there aren't any already
        /// </summary>
        public static async Task<List<MessageCognitiveStats>> GetMessagesStats(this List<ChannelWithReactions> channels, ILogger telemetry)
        {
            var allStats = new List<MessageCognitiveStats>();
            if (channels is null)
            {
                throw new ArgumentNullException(nameof(channels));
            }

            foreach (var channel in channels)
            {
                var channelStats = await channel.Messages.GetCognitiveDataStats(telemetry, channel);
                allStats.AddRange(channelStats);
            }

            return allStats;
        }

        public static List<ChatMessage> GetByDate(this IEnumerable<ChatMessage> allMsgs, DateTime date)
        {
            var msgs = new List<ChatMessage>();
            msgs.AddRange(allMsgs.Where(m => m.CreatedDateTime.Value.DateTime.Date == date.Date));

            return msgs;
        }

        public static List<DateTime> GetUniqueDates(this IEnumerable<ChatMessage> allMsgs)
        {
            var dates = new List<DateTime>();
            if (allMsgs != null)
            {
                foreach (var msg in allMsgs)
                {
                    if (msg.CreatedDateTime.HasValue)
                    {
                        var msgDate = msg.CreatedDateTime.Value.Date;
                        if (!dates.Contains(msgDate))
                        {
                            dates.Add(msgDate);
                        }
                    }
                }
            }

            return dates;
        }

    }
}
