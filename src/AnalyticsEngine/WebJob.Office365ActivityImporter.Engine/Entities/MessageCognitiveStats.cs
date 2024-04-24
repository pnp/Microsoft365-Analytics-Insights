using Common.Entities;
using Common.Entities.Entities.Teams;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace WebJob.Office365ActivityImporter.Engine.Entities
{
    /// <summary>
    /// Stats model about a series of chat messages. Usually a Teams channel. 
    /// </summary>
    public class MessageCognitiveStats
    {
        #region Constructors

        public MessageCognitiveStats(ChannelWithReactions channel, DateTime forDate)
        {
            this.KeyWords = new Dictionary<string, int>();
            this.Languages = new List<string>();
            this.Channel = channel;
            this.ForDate = forDate;
        }

        #endregion

        #region Props

        public int ChatsCount { get; set; }

        public Dictionary<string, int> KeyWords { get; set; }
        public double? Sentiment { get; set; }

        public List<string> Languages { get; set; }

        public ChannelWithReactions Channel { get; set; }

        public DateTime ForDate { get; set; }

        #endregion


        public override string ToString()
        {
            var s = $"Sentiment: {Sentiment}. ";

            if (KeyWords.Count > 0)
            {
                s += "Keywords & count: ";
                foreach (var kp in KeyWords.Keys)
                {
                    s += $"[{KeyWords[kp]}]{kp},";
                }
                s = s.TrimEnd(",".ToCharArray()) + ". ";
            }
            else
            {
                s += "No keywords. ";
            }

            if (Languages.Count > 0)
            {
                s += "Languages: ";
                foreach (var lang in Languages)
                {
                    s += $"{lang},";
                }
                s = s.TrimEnd(",".ToCharArray()) + ". ";
            }
            else
            {
                s += "No languages.";
            }

            return s;
        }

        public void IncrementMessageStatsWithThis(ChannelStatsLog existingLog)
        {
            var previousSentimentTotal = existingLog.SentimentScore * existingLog.ChatsCount;
            var thisSentimentTotal = this.Sentiment * this.ChatsCount;
            var newSentimentScore = (previousSentimentTotal + thisSentimentTotal) / (existingLog.ChatsCount + this.ChatsCount);

            existingLog.SentimentScore = newSentimentScore;

            existingLog.ChatsCount += this.ChatsCount;
        }

        internal async Task InsertOrAppendSqlStats(TeamsAndCallsDBLookupManager lookupManager, Common.Entities.Entities.TeamDefinition dbTeam)
        {
            var dbChannel = await lookupManager.GetTeamChannel(this.Channel.Id, this.Channel.DisplayName, dbTeam);
            var existingLog = await lookupManager.Database.TeamChannelStats
                .Where(s => s.Date == this.ForDate.Date && s.ChannelID == dbChannel.ID)
                .SingleOrDefaultAsync();

            if (existingLog == null)
            {
                // Stats not seen today. Easy - insert.
                existingLog = new ChannelStatsLog
                {
                    Channel = dbChannel,
                    ChatsCount = this.ChatsCount,
                    SentimentScore = this.Sentiment,
                    Date = this.ForDate.Date
                };
                lookupManager.Database.TeamChannelStats.Add(existingLog);
            }
            else
            {
                // Update stats that exist already, if the stats have new messages from our DB log
                this.IncrementMessageStatsWithThis(existingLog);
            }

            foreach (var kw in this.KeyWords.Keys)
            {
                var kwDef = await lookupManager.GetOrCreateKeyword(kw);
                var addNew = !existingLog.IsSavedToDB;

                if (existingLog.IsSavedToDB)
                {
                    var existingKwLookup = await lookupManager.Database.TeamChannelStatKeywords
                        .Where(s => s.ChannelStatsLogID == existingLog.ID && s.KeyWordID == kwDef.ID)
                        .FirstOrDefaultAsync();     // Hack: avoid "InvalidOperationException: Sequence contains more than one element" error

                    addNew = existingKwLookup == null;
                }

                if (addNew)
                    existingLog.KeywordLookups.Add(new ChannelLogKeyword { KeyWord = kwDef, ChannelStatsLog = existingLog });
            }

            foreach (var lang in this.Languages)
            {
                var langDef = await lookupManager.GetOrCreateLanguage(lang);
                var addNew = !existingLog.IsSavedToDB;

                if (existingLog.IsSavedToDB)
                {
                    var existingLangLookup = await lookupManager.Database.TeamChannelStatLanguages
                        .Where(s => s.ChannelStatsLogID == existingLog.ID && s.LanguageID == langDef.ID)
                        .SingleOrDefaultAsync();

                    addNew = existingLangLookup == null;
                }
                if (addNew)
                    existingLog.LanguageLookups.Add(new ChannelLogLanguage { Language = langDef, ChannelStatsLog = existingLog });
            }
        }

        internal void Concat(List<MessageCognitiveStats> otherStats)
        {
            if (otherStats == null) return;

            var sentiments = new List<double>();
            foreach (var otherStat in otherStats)
            {
                // Sanity. MessageStats are just for a single date & channel.
                if (otherStat.Channel?.Id != this.Channel?.Id)
                {
                    throw new ArgumentOutOfRangeException("Cannot concat messages from another channel");
                }
                if (otherStat.ForDate.Date != this.ForDate.Date)
                {
                    throw new ArgumentOutOfRangeException("Cannot concat messages from another date");
                }

                if (otherStat.Sentiment.HasValue)
                {
                    sentiments.Add(otherStat.Sentiment.Value);
                }

                this.Concat(otherStat);
            }

            // Average out sentiment
            if (sentiments.Count > 0)
            {
                this.Sentiment = sentiments.Average();
            }
        }


        /// <summary>
        /// Concat stats, minus sentiment
        /// </summary>
        void Concat(MessageCognitiveStats otherStats)
        {
            if (otherStats is null) throw new ArgumentNullException(nameof(otherStats));

            this.ChatsCount += otherStats.ChatsCount;

            // Add keywords
            foreach (string keyphrase in otherStats.KeyWords.Keys)
            {
                if (!this.KeyWords.ContainsKey(keyphrase))
                {
                    this.KeyWords.Add(keyphrase, 1);
                }
                else
                {
                    this.KeyWords[keyphrase] = this.KeyWords[keyphrase] + 1;
                }
            }

            // Add langs
            foreach (var lang in otherStats.Languages)
            {
                if (!this.Languages.Contains(lang))
                {
                    this.Languages.Add(lang);
                }
            }
        }
    }

}
