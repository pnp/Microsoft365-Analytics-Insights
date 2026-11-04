using DataUtils;
using Microsoft.Graph;
using Microsoft.Graph.Models;
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


        /// <summary>
        /// What is sent to Azure AI Language for one message: the message's own text, and nothing else.
        /// </summary>
        /// <remarks>
        /// A root message's <c>Replies</c> are deliberately not included. The channel's message list already holds every
        /// new reply as a message of its own (<see cref="ChannelMessageScopeRules.SelectNewMessagesAndReactions"/>), and
        /// <c>UserGroupsFilter</c> is applied to that list (<see cref="ChannelMessageScopeRules.RestrictToUserScope"/>), not
        /// to a root's <c>Replies</c>. Adding the replies here again sent the reply of someone outside the scope to
        /// Azure AI Language with an in-scope root, and analysed every other reply twice, the second time on the root's day.
        /// </remarks>
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

            return new List<TextAnalysisSample<ChatMessage>>
            {
                new TextAnalysisSample<ChatMessage> { Text = msg.Body.ToPlainText(), Id = msg.Id, Parent = msg }
            };
        }
    }
}
