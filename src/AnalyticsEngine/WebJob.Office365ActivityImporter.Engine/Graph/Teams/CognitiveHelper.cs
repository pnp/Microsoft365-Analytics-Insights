using Common.DataUtils;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine.Entities;
using static WebJob.Office365ActivityImporter.Engine.Graph.Teams.TeamsCognitiveExtensions;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Helper class for building or reading cognitive API objects
    /// </summary>
    public class CognitiveHelper
    {
        internal static MessageCognitiveStats BuildChatStats(TeamsMessagesCognitiveStats results, ChannelWithReactions parentChannel, DateTime forDate)
        {
            var chatStats = new MessageCognitiveStats(parentChannel, forDate);

            chatStats.Languages.AddRange(results.SentimentAndLanguages.Select(r => r.LanguageName).Distinct());
            chatStats.Sentiment = results.SentimentAndLanguages.Select(r => r.SentimentScore).Where(s => s.HasValue).Average();

            foreach (var keyphrase in results.KeyPhrases)
            {
                if (!chatStats.KeyWords.ContainsKey(keyphrase))
                {
                    chatStats.KeyWords.Add(keyphrase, 1);
                }
                else
                {
                    chatStats.KeyWords[keyphrase] = chatStats.KeyWords[keyphrase] + 1;
                }
            }

            return chatStats;
        }


        internal static List<TextAnalysisSample<ChatMessage>> BuildTextAnalysisSampleList(ChatMessage msg)
        {
            if (msg is null)
            {
                throw new ArgumentNullException(nameof(msg));
            }
            if (string.IsNullOrEmpty(msg.Id))
            {
                throw new ArgumentNullException("Message ID");
            }

            var l = new List<TextAnalysisSample<ChatMessage>>
            {
                new TextAnalysisSample<ChatMessage> { Text = msg.Body.ToPlainText(), Id = msg.Id, Parent = msg }
            };

            if (msg.Replies != null)
            {
                foreach (var reply in msg.Replies)
                {
                    l.AddRange(BuildTextAnalysisSampleList(reply));
                }
            }

            return l;
        }
    }
}
