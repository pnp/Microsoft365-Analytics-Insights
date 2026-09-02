using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Pure decision logic for "of everything Graph returned for a channel, what is actually new since
    /// the last delta read?". Extracted from <c>ChannelWithReactions.CalculateAndSetNewMessagesAndReactions</c>
    /// so the rule can be unit tested without Graph or Redis. See issue #377.
    ///
    /// Why this is not simply "everything the delta returned": a delta response also re-serves the
    /// unchanged parent of a thread whose reply changed, and re-serves a message whose only change was
    /// a reaction. Without this filter the importer would re-count old messages every cycle.
    /// </summary>
    public static class ChannelMessageScopeRules
    {
        /// <summary>
        /// Select the messages and reactions that count as new.
        /// </summary>
        /// <param name="rootMsgs">Root messages from the channel delta read, with their replies already loaded.</param>
        /// <param name="newSince">
        /// When the delta token was last updated, or <c>null</c> for a full (first) read - in which case
        /// everything is new.
        /// </param>
        public static ChannelMessageScope SelectNewMessagesAndReactions(IEnumerable<ChatMessage> rootMsgs, DateTime? newSince)
        {
            if (rootMsgs is null) throw new ArgumentNullException(nameof(rootMsgs));

            var scope = new ChannelMessageScope();

            foreach (var rootMsg in rootMsgs)
            {
                if (MessageInScope(newSince, rootMsg))
                {
                    scope.NewMessages.Add(rootMsg);
                }
                foreach (var r in rootMsg.Reactions)
                {
                    if (ReactionInScope(newSince, r))
                    {
                        scope.NewReactions.Add(r);
                    }
                }

                scope.RepliesSeen += rootMsg.Replies.Count;
                foreach (var reply in rootMsg.Replies)
                {
                    if (MessageInScope(newSince, reply))
                    {
                        scope.NewMessages.Add(reply);
                    }
                    foreach (var r in reply.Reactions)
                    {
                        if (ReactionInScope(newSince, r))
                        {
                            scope.NewReactions.Add(r);
                        }
                    }
                }
            }

            return scope;
        }

        /// <summary>
        /// If we've done a delta read, only include messages created after the last refresh. This is
        /// because a thread's parent message appears in the delta results whenever a reply changes,
        /// even though the parent itself hasn't changed and we already have it.
        /// </summary>
        public static bool MessageInScope(DateTime? newSince, ChatMessage msg)
        {
            return (newSince == null || (newSince.HasValue && msg.CreatedDateTime > newSince.Value));
        }

        /// <summary>
        /// Same rule for a reaction: on a delta read only reactions added since the last refresh count.
        /// </summary>
        public static bool ReactionInScope(DateTime? newSince, ChatMessageReaction reaction)
        {
            return (newSince == null || (newSince.HasValue && reaction.CreatedDateTime > newSince.Value));
        }
    }

    /// <summary>
    /// What <see cref="ChannelMessageScopeRules.SelectNewMessagesAndReactions"/> decided was new.
    /// </summary>
    public class ChannelMessageScope
    {
        /// <summary>New root messages and new replies, in the order Graph returned them.</summary>
        public List<ChatMessage> NewMessages { get; } = new List<ChatMessage>();

        /// <summary>New reactions, from both root messages and replies.</summary>
        public List<ChatMessageReaction> NewReactions { get; } = new List<ChatMessageReaction>();

        /// <summary>
        /// Total replies walked, in or out of scope. Counted because the original code counted it;
        /// nothing consumes it yet.
        /// </summary>
        public int RepliesSeen { get; set; }
    }
}
