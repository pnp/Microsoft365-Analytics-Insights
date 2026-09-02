using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Pure decision logic for the Graph change notifications POSTed to the calls webhook: which of
    /// them are genuinely from our subscription. Extracted from <c>CallRecordWebhookController</c> so
    /// the check can be tested without an HTTP request. See issue #378.
    /// </summary>
    public static class CallNotificationRules
    {
        /// <summary>
        /// Split incoming notifications into those whose <c>clientState</c> matches the secret we
        /// registered with the subscription, and a count of those that don't.
        /// </summary>
        /// <remarks>
        /// This is the webhook's only authentication: the endpoint is anonymous (Graph has to be able
        /// to POST to it), so a notification whose clientState doesn't match must never be queued.
        /// The comparison is ordinal and exact, deliberately - a case-insensitive or trimmed
        /// comparison would weaken the check.
        /// </remarks>
        public static CallNotificationSelection SelectValidNotifications(IEnumerable<Common.Entities.Models.GraphChangeNotification> notifications, string expectedClientState)
        {
            if (notifications is null) throw new ArgumentNullException(nameof(notifications));

            var selection = new CallNotificationSelection();
            foreach (var change in notifications)
            {
                if (change.ClientState == expectedClientState)
                {
                    selection.Valid.Add(change);
                }
                else
                {
                    selection.InvalidCount++;
                }
            }

            return selection;
        }
    }

    /// <summary>Result of <see cref="CallNotificationRules.SelectValidNotifications"/>.</summary>
    public class CallNotificationSelection
    {
        public List<Common.Entities.Models.GraphChangeNotification> Valid { get; } = new List<Common.Entities.Models.GraphChangeNotification>();

        public int InvalidCount { get; set; }
    }
}
