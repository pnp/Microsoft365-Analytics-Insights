using Common.Entities;
using System.Collections.Generic;
using static WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents.PageUpdateEventAppInsightsQueryResult;

namespace WebJob.AppInsightsImporter.Engine.PageUpdates.Rules
{
    /// <summary>What the comment/like rules decided for one incoming event.</summary>
    public enum PageUserEventOutcome
    {
        /// <summary>No email or no SharePoint id - nothing usable to key the record on.</summary>
        Invalid,

        /// <summary>A record with this SharePoint id is already stored against the URL.</summary>
        AlreadyStored,

        /// <summary>Not seen before; create it.</summary>
        New
    }

    /// <summary>One incoming comment or like, classified.</summary>
    public sealed class PageUserEventDecision<TEvent> where TEvent : UserBasedCustomAIEvent
    {
        internal PageUserEventDecision(TEvent pageUserEvent, PageUserEventOutcome outcome, string normalisedEmail)
        {
            Event = pageUserEvent;
            Outcome = outcome;
            NormalisedEmail = normalisedEmail;
        }

        public TEvent Event { get; }
        public PageUserEventOutcome Outcome { get; }

        /// <summary>
        /// The e-mail lower-cased, as it is used to look a user up. <c>null</c> for an
        /// <see cref="PageUserEventOutcome.Invalid"/> event, since there was nothing to normalise.
        /// </summary>
        public string NormalisedEmail { get; }
    }

    /// <summary>
    /// De-duplication and validation for the page comments and likes carried on a page-update event.
    ///
    /// Extracted from <c>PageUpdateManager.ProcessCustomAppInsightsEvents</c> (issue #369) so the rule can be
    /// asserted without a database or cognitive services.
    /// </summary>
    public static class PageUserEventRules
    {
        /// <summary>
        /// Classify every incoming event, <b>in input order</b>, against what is already stored for the URL.
        /// Order matters: the caller logs invalid events and creates new ones as it walks this list, so
        /// re-ordering would change the operator-facing log sequence and what has already happened when a
        /// creation throws.
        ///
        /// Two pieces of existing behaviour are preserved deliberately:
        /// <list type="bullet">
        /// <item>matching is by SharePoint id only. There is no unique index on (url, spID) - a user really
        ///       can leave two comments on the same page - so the id is the only thing that can identify a
        ///       record already stored;</item>
        /// <item>duplicate ids <b>within one batch</b> are each reported as <see cref="PageUserEventOutcome.New"/>,
        ///       because the stored set is not updated as the caller creates records. In practice the
        ///       page-update compile step has already de-duplicated likes and comments by SharePoint id
        ///       before this is reached.</item>
        /// </list>
        /// </summary>
        public static List<PageUserEventDecision<TEvent>> Classify<TEvent, TStored>(List<TEvent> eventValues, List<TStored> storedRecords)
            where TEvent : UserBasedCustomAIEvent
            where TStored : SPUrlUserRecord
        {
            var decisions = new List<PageUserEventDecision<TEvent>>();
            if (eventValues == null)
            {
                return decisions;
            }

            foreach (var eventVal in eventValues)
            {
                if (string.IsNullOrEmpty(eventVal.Email) || !eventVal.SharePointId.HasValue)
                {
                    decisions.Add(new PageUserEventDecision<TEvent>(eventVal, PageUserEventOutcome.Invalid, null));
                    continue;
                }

                var email = eventVal.Email.ToLowerInvariant();

                var alreadyStored = false;
                if (storedRecords != null)
                {
                    foreach (var stored in storedRecords)
                    {
                        if (stored.SpID == eventVal.SharePointId)
                        {
                            alreadyStored = true;
                            break;
                        }
                    }
                }

                decisions.Add(new PageUserEventDecision<TEvent>(eventVal,
                    alreadyStored ? PageUserEventOutcome.AlreadyStored : PageUserEventOutcome.New, email));
            }

            return decisions;
        }

        /// <summary>
        /// Whether to call the cognitive-services text analytics API for this batch of new comments. Skipped
        /// when the service is not configured, and when there is nothing new to score - a tenant with no new
        /// comments in a cycle must generate no cognitive-services traffic (and no bill).
        /// </summary>
        public static bool ShouldRequestSentiment(bool hasCognitiveClient, int newCommentCount)
        {
            return hasCognitiveClient && newCommentCount > 0;
        }
    }
}
