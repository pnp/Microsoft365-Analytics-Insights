using Microsoft.Extensions.Logging;
using Microsoft.Graph;
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
        public IChannelTabsCollectionPage Tabs { get; internal set; }

        /// <summary>
        /// Sort through messages found and decide what's new since last delta. Set on this object
        /// </summary>
        public void CalculateAndSetNewMessagesAndReactions(List<ChatMessage> rootMsgs, DateTime? newSince, ILogger logger)
        {
            var repliesLoadedCount = 0;
            var newMsgs = new List<ChatMessage>();
            var newReactions = new List<ChatMessageReaction>();


            // Process read messages & figure out which one is relevant.
            // I.e "liked" messages will be included in the delta, even if the message content hasn't changed. 
            // Read new reactions & messages only. Ignore the rest. 
            foreach (var rootMsg in rootMsgs)
            {
                // Parse replies
                repliesLoadedCount += ParseMessageAndReplies(rootMsg, newMsgs, newReactions, newSince);
            }

            // Output the results
            if (newMsgs.Count > 0 || newReactions.Count > 0)
            {
                logger.LogInformation($"Processed {newMsgs.Count.ToString("N0")} new message(s) and {newReactions.Count.ToString("N0")} new reactions(s) in total.");
            }

            this.Messages = newMsgs;
            this.Reactions = newReactions;
        }

        static int ParseMessageAndReplies(ChatMessage rootMsg, List<ChatMessage> newMsgs, List<ChatMessageReaction> newReactions, DateTime? newSince)
        {
            var repliesLoadedCount = 0;
            if (MessageInScope(newSince, rootMsg))
            {
                newMsgs.Add(rootMsg);
            }
            foreach (var r in rootMsg.Reactions)
            {
                if (ReactionInScope(newSince, r))
                {
                    newReactions.Add(r);
                }
            }

            repliesLoadedCount += rootMsg.Replies.Count;
            foreach (var reply in rootMsg.Replies)
            {
                if (MessageInScope(newSince, reply))
                {
                    newMsgs.Add(reply);
                }
                foreach (var r in reply.Reactions)
                {
                    if (ReactionInScope(newSince, r))
                    {
                        newReactions.Add(r);
                    }
                }
            }

            return repliesLoadedCount;
        }

        private static bool ReactionInScope(DateTime? newSince, ChatMessageReaction r)
        {
            return (newSince == null || (newSince.HasValue && r.CreatedDateTime > newSince.Value));
        }

        // If we've done a delta read, only include messages with a more recent created date than the last refresh.
        // This is because for thread replies, in the delta results will be the original thread parent, even though it's not changed & we should have it already.
        private static bool MessageInScope(DateTime? newSince, ChatMessage msg)
        {
            return (newSince == null || (newSince.HasValue && msg.CreatedDateTime > newSince.Value));
        }
    }
}
