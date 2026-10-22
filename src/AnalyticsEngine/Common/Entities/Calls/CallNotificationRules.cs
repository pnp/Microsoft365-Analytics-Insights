using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Common.Entities.Calls
{
    /// <summary>
    /// Pure decision logic for the Graph change notifications POSTed to the calls webhook: which of
    /// them are genuinely from our subscription. Extracted from <c>CallRecordWebhookController</c> so
    /// the check can be tested without an HTTP request. See issue #378.
    /// </summary>
    public static class CallNotificationRules
    {
        /// <summary>
        /// Split incoming notifications into those whose <c>clientState</c> is the one the importer
        /// registered with the subscription, and a count of those that aren't.
        /// </summary>
        /// <remarks>
        /// This is the webhook's only authentication: the endpoint is anonymous (Graph has to be able
        /// to POST to it), so a notification whose clientState doesn't match must never be queued.
        /// The comparison is ordinal and exact, deliberately - a case-insensitive or trimmed
        /// comparison would weaken the check. See <see cref="CallWebhookClientState.Match"/>, which also
        /// accepts the raw client secret that subscriptions created before the derived value still carry.
        ///
        /// Deliberately NOT null-checked: the original loop dereferenced the list directly, so a
        /// malformed POST with no <c>value</c> array produced a <see cref="NullReferenceException"/>
        /// that the web app's global exception logger reports. Converting that to an
        /// <see cref="ArgumentNullException"/> would change the exception type and message an operator
        /// sees in App Insights for no benefit.
        /// </remarks>
        public static CallNotificationSelection SelectValidNotifications(IEnumerable<Common.Entities.Models.GraphChangeNotification> notifications, CallWebhookClientState expectedClientState)
        {
            if (expectedClientState == null) throw new ArgumentNullException(nameof(expectedClientState));

            var selection = new CallNotificationSelection();
            foreach (var change in notifications)
            {
                var match = expectedClientState.Match(change.ClientState);
                if (match == CallWebhookClientStateMatch.NoMatch)
                {
                    selection.InvalidCount++;
                    continue;
                }

                selection.Valid.Add(change);
                if (match == CallWebhookClientStateMatch.LegacySecret)
                {
                    selection.LegacyCount++;
                    selection.FirstLegacySubscriptionId = selection.FirstLegacySubscriptionId ?? change.SubscriptionId;
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

        /// <summary>
        /// How many of <see cref="Valid"/> carried the raw client secret: they come from a subscription created
        /// before the derived clientState, which the importer has not replaced yet.
        /// </summary>
        public int LegacyCount { get; set; }

        /// <summary>The Graph subscription the first of those came from, for the log. Not secret.</summary>
        public string FirstLegacySubscriptionId { get; set; }
    }

    /// <summary>
    /// The calls webhook's check of incoming notifications, plus its warning - at most once per instance, and the web
    /// app keeps one per process - that notifications still carry the legacy clientState.
    /// </summary>
    public sealed class CallNotificationAuthenticator
    {
        private readonly CallWebhookClientState _clientState;
        private int _legacyWarningLogged;

        public CallNotificationAuthenticator(CallWebhookClientState clientState)
        {
            _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        }

        public CallNotificationSelection Select(IEnumerable<Common.Entities.Models.GraphChangeNotification> notifications, ILogger logger)
        {
            var selection = CallNotificationRules.SelectValidNotifications(notifications, _clientState);

            // Names neither value: both are credentials for this endpoint, and one of them is the app's client secret.
            if (selection.LegacyCount > 0 && Interlocked.Exchange(ref _legacyWarningLogged, 1) == 0)
            {
                logger?.LogWarning($"[Calls Webhook] Accepted a Teams call notification whose clientState is the legacy value " +
                    $"(the app registration's client secret) from subscription '{selection.FirstLegacySubscriptionId}'. " +
                    "It was created before this version derived the clientState from the secret instead; the importer replaces it " +
                    "and deletes it about an hour later, and until then its notifications are still accepted. Logged once per process.");
            }

            return selection;
        }
    }
}
