using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public class ChannelWithReactions
    {
        public ChannelWithReactions()
        {
        }

        public ChannelWithReactions(Channel c) : this()
        {
            this.Id = c.Id;
            this.DisplayName = c.DisplayName;

            // We don't use hardly any data for channel so leave the rest for now
        }

        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; }

        public List<ChatMessageReaction> Reactions { get; set; } = new List<ChatMessageReaction>();
        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
        // v5+ exposes channel tabs as List<TeamsTab>; the v4 IChannelTabsCollectionPage interface
        // is gone. PopulateNewMessagesAndReactions / SaveToSql iterate this directly.
        public List<TeamsTab> Tabs { get; internal set; }

        /// <summary>
        /// Sort through messages found and decide what's new since last delta. Set on this object.
        /// The selection rule itself lives in <see cref="ChannelMessageScopeRules"/> so it can be
        /// tested without Graph.
        /// </summary>
        public void CalculateAndSetNewMessagesAndReactions(List<ChatMessage> rootMsgs, DateTime? newSince, ILogger logger)
        {
            // Process read messages & figure out which one is relevant.
            // I.e "liked" messages will be included in the delta, even if the message content hasn't changed. 
            // Read new reactions & messages only. Ignore the rest. 
            var scope = ChannelMessageScopeRules.SelectNewMessagesAndReactions(rootMsgs, newSince);

            // Output the results
            if (scope.NewMessages.Count > 0 || scope.NewReactions.Count > 0)
            {
                logger.LogInformation($"Processed {scope.NewMessages.Count.ToString("N0")} new message(s) and {scope.NewReactions.Count.ToString("N0")} new reactions(s) in total.");
            }

            this.Messages = scope.NewMessages;
            this.Reactions = scope.NewReactions;
        }
    }
}
