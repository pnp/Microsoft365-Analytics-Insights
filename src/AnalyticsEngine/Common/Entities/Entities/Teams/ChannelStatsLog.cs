using Common.Entities.Teams;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.Entities.Teams
{
    /// <summary>
    /// General stats for a channel, on a given date.
    /// </summary>
    [Table("teams_channel_stats_log")]
    public class ChannelStatsLog : ChannelRelatedLogEntity
    {
        public ChannelStatsLog()
        {
            this.KeywordLookups = new List<ChannelLogKeyword>();
            this.LanguageLookups = new List<ChannelLogLanguage>();
        }

        #region Props


        [Column("chats_count")]
        public int? ChatsCount { get; set; }

        public List<ChannelLogKeyword> KeywordLookups { get; set; }
        public List<ChannelLogLanguage> LanguageLookups { get; set; }

        [Column("sentiment_score")]
        public double? SentimentScore { get; set; }

        #endregion

        public static async Task<ChannelStatsLog> GetOrCreateForDateAndChannelId(AnalyticsEntitiesContext context, DateTime date, TeamChannel channelDB)
        {
            // Is there a log for when msg was sent already?
            // EF6 sucks
            var channelStatsDBLog = await context.TeamChannelStats.SingleOrDefaultAsync(l =>
                l.Date.Year == date.Year &&
                l.Date.Month == date.Month &&
                l.Date.Day == date.Day &&
                l.Channel.GraphID == channelDB.GraphID
            );

            if (channelStatsDBLog == null)
            {
                channelStatsDBLog = new ChannelStatsLog
                {
                    Channel = channelDB,
                    Date = date
                };
                context.TeamChannelStats.Add(channelStatsDBLog);
            }

            return channelStatsDBLog;
        }
    }
}
