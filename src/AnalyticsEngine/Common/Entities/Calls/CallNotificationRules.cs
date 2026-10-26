using Common.Entities.Models;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
        /// registered with the subscription, and those that aren't, with why.
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
        public static CallNotificationSelection SelectValidNotifications(IEnumerable<GraphChangeNotification> notifications, CallWebhookClientState expectedClientState)
        {
            if (expectedClientState == null) throw new ArgumentNullException(nameof(expectedClientState));

            var selection = new CallNotificationSelection();
            foreach (var change in notifications)
            {
                var match = expectedClientState.Match(change.ClientState);
                if (match == CallWebhookClientStateMatch.NoMatch)
                {
                    selection.Refused.Add(new RefusedCallNotification
                    {
                        Notification = change,
                        Reason = !expectedClientState.IsAvailable
                            ? CallNotificationRefusalReason.NoClientSecretConfigured
                            : string.IsNullOrEmpty(change.ClientState)
                                ? CallNotificationRefusalReason.MissingClientState
                                : CallNotificationRefusalReason.ClientStateMismatch
                    });
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

        /// <summary>
        /// Whether a notification that matched neither the current nor the legacy clientState comes from the subscription
        /// the importer created with the client secret in use before a rotation - and if so, whether that is still
        /// acceptable. See issue E-2 of the release review: a rotation used to make the webhook refuse (and Graph never
        /// re-send) every call notification until the importer's next import cycle replaced the subscription.
        /// </summary>
        /// <remarks>
        /// Fail-closed throughout. All of these must hold: the web app has a client secret of its own; the importer's
        /// record names a clientState other than the current one; the notification comes from the subscription that
        /// record names, exactly; and its clientState hashes to the recorded fingerprint, compared in constant time. Then it
        /// is accepted until <see cref="CallSubscriptionRules.PreviousClientStateWindow"/> after the importer noticed the
        /// rotation - or, before it has, until the subscription's recorded expiry, which only the importer can extend.
        /// A record with neither time is refused. Nothing here can be satisfied by a value the importer did not record.
        /// </remarks>
        public static PreviousClientStateDecision EvaluatePreviousClientState(GraphChangeNotification notification, CallSubscriptionRecord record,
            CallWebhookClientState current, DateTime nowUtc, out DateTime? acceptedUntilUtc)
        {
            acceptedUntilUtc = null;
            if (notification == null || record == null || current == null || !current.IsAvailable) return PreviousClientStateDecision.NotApplicable;
            if (string.IsNullOrEmpty(record.SubscriptionId) || string.IsNullOrEmpty(record.ClientStateFingerprint)) return PreviousClientStateDecision.NotApplicable;
            if (string.Equals(record.ClientStateFingerprint, current.Fingerprint, StringComparison.OrdinalIgnoreCase)) return PreviousClientStateDecision.NotApplicable;
            if (!string.Equals(notification.SubscriptionId, record.SubscriptionId, StringComparison.Ordinal)) return PreviousClientStateDecision.NotApplicable;
            if (!CallWebhookClientState.MatchesFingerprint(notification.ClientState, record.ClientStateFingerprint)) return PreviousClientStateDecision.NotApplicable;

            acceptedUntilUtc = record.SupersededUtc.HasValue
                ? record.SupersededUtc.Value + CallSubscriptionRules.PreviousClientStateWindow
                : record.ExpiresUtc;

            return acceptedUntilUtc.HasValue && nowUtc < acceptedUntilUtc.Value
                ? PreviousClientStateDecision.Accepted
                : PreviousClientStateDecision.WindowClosed;
        }
    }

    /// <summary>Outcome of <see cref="CallNotificationRules.EvaluatePreviousClientState"/>.</summary>
    public enum PreviousClientStateDecision
    {
        /// <summary>Not the previous clientState of the importer's recorded subscription: the notification stays refused.</summary>
        NotApplicable,

        /// <summary>The previous clientState, within its window: accept.</summary>
        Accepted,

        /// <summary>The previous clientState, but its window has closed: refuse.</summary>
        WindowClosed
    }

    /// <summary>Why the webhook refused a notification. Names neither the presented nor the expected clientState.</summary>
    public enum CallNotificationRefusalReason
    {
        /// <summary>No client secret is configured, so there is nothing to compare with and every notification is refused.</summary>
        NoClientSecretConfigured,

        /// <summary>The notification carried no clientState.</summary>
        MissingClientState,

        /// <summary>A clientState that is neither the current, the legacy nor the importer-recorded previous value.</summary>
        ClientStateMismatch,

        /// <summary>The previous client secret's clientState, after <see cref="CallSubscriptionRules.PreviousClientStateWindow"/>.</summary>
        PreviousClientStateWindowClosed
    }

    public class RefusedCallNotification
    {
        public GraphChangeNotification Notification { get; set; }
        public CallNotificationRefusalReason Reason { get; set; }
    }

    /// <summary>Result of <see cref="CallNotificationRules.SelectValidNotifications"/>.</summary>
    public class CallNotificationSelection
    {
        public List<GraphChangeNotification> Valid { get; } = new List<GraphChangeNotification>();

        /// <summary>The notifications refused, with why. Never queued.</summary>
        public List<RefusedCallNotification> Refused { get; } = new List<RefusedCallNotification>();

        public int InvalidCount => Refused.Count;

        /// <summary>
        /// How many of <see cref="Valid"/> carried the raw client secret: they come from a subscription created
        /// before the derived clientState, which the importer has not replaced yet.
        /// </summary>
        public int LegacyCount { get; set; }

        /// <summary>The Graph subscription the first of those came from, for the log. Not secret.</summary>
        public string FirstLegacySubscriptionId { get; set; }

        /// <summary>How many of <see cref="Valid"/> carried the previous client secret's clientState (see <see cref="CallNotificationRules.EvaluatePreviousClientState"/>).</summary>
        public int PreviousCount { get; set; }
    }

    /// <summary>
    /// The calls webhook's check of incoming notifications, and its warnings. The web app keeps one per process.
    /// </summary>
    /// <remarks>
    /// Graph is answered 200 for a refused notification (a stranger learns nothing), so it never re-sends it and the call is
    /// not imported. Each refusal is therefore logged at Warning - per subscription and reason, at most once per
    /// <see cref="RefusalLogInterval"/> with the count since - so a rotation, a misconfiguration or a forgery is visible.
    /// Subscription ids are logged only when they are GUIDs (Graph's are), so an anonymous caller can neither inject text
    /// into the log nor grow the throttle's table beyond <see cref="MaxTrackedLogKeys"/>.
    /// </remarks>
    public sealed class CallNotificationAuthenticator
    {
        public static readonly TimeSpan RefusalLogInterval = TimeSpan.FromMinutes(15);
        public const int MaxTrackedLogKeys = 32;

        private const string LogTag = "[Calls Webhook]";
        private readonly CallWebhookClientState _clientState;
        private readonly ICallSubscriptionRecordSource _previousSource;
        private readonly IClock _clock;
        private readonly object _logGate = new object();
        private readonly Dictionary<string, ThrottledLogEntry> _logEntries = new Dictionary<string, ThrottledLogEntry>(StringComparer.Ordinal);
        private int _legacyWarningLogged;

        /// <param name="previousSource">The importer's subscription record; null without a Storage account.</param>
        public CallNotificationAuthenticator(CallWebhookClientState clientState, ICallSubscriptionRecordSource previousSource = null, IClock clock = null)
        {
            _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
            _previousSource = previousSource;
            _clock = clock ?? SystemClock.Instance;
        }

        /// <summary>The current and legacy clientState only. See <see cref="SelectAsync"/>.</summary>
        public CallNotificationSelection Select(IEnumerable<GraphChangeNotification> notifications, ILogger logger)
        {
            var selection = CallNotificationRules.SelectValidNotifications(notifications, _clientState);
            LogOutcome(selection, logger, new Dictionary<string, DateTime?>());
            return selection;
        }

        /// <summary>
        /// The current and legacy clientState, then - only for notifications that matched neither, and only when there are
        /// any - the previous client secret's, from the importer's record.
        /// </summary>
        public async Task<CallNotificationSelection> SelectAsync(IEnumerable<GraphChangeNotification> notifications, ILogger logger)
        {
            var selection = CallNotificationRules.SelectValidNotifications(notifications, _clientState);
            var acceptedUntil = new Dictionary<string, DateTime?>(StringComparer.Ordinal);

            var candidates = selection.Refused.Where(r => r.Reason == CallNotificationRefusalReason.ClientStateMismatch && !string.IsNullOrEmpty(r.Notification.SubscriptionId)).ToList();
            if (candidates.Count > 0 && _previousSource != null)
            {
                var record = await _previousSource.GetAsync().ConfigureAwait(false);
                var now = _clock.UtcNow;
                foreach (var refused in candidates)
                {
                    var decision = CallNotificationRules.EvaluatePreviousClientState(refused.Notification, record, _clientState, now, out var until);
                    if (decision == PreviousClientStateDecision.Accepted)
                    {
                        selection.Refused.Remove(refused);
                        selection.Valid.Add(refused.Notification);
                        selection.PreviousCount++;
                        acceptedUntil[refused.Notification.SubscriptionId] = until;
                    }
                    else if (decision == PreviousClientStateDecision.WindowClosed)
                    {
                        refused.Reason = CallNotificationRefusalReason.PreviousClientStateWindowClosed;
                    }
                }
            }

            LogOutcome(selection, logger, acceptedUntil);
            return selection;
        }

        private void LogOutcome(CallNotificationSelection selection, ILogger logger, Dictionary<string, DateTime?> previousAcceptedUntil)
        {
            if (logger == null) return;

            // Names neither value: both are credentials for this endpoint, and one of them is the app's client secret.
            if (selection.LegacyCount > 0 && Interlocked.Exchange(ref _legacyWarningLogged, 1) == 0)
            {
                logger.LogWarning($"{LogTag} Accepted a Teams call notification whose clientState is the legacy value " +
                    $"(the app registration's client secret) from subscription '{selection.FirstLegacySubscriptionId}'. " +
                    "It was created before this version derived the clientState from the secret instead; the importer replaces it " +
                    "and deletes it about an hour later, and until then its notifications are still accepted. Logged once per process.");
            }

            var now = _clock.UtcNow;
            foreach (var group in previousAcceptedUntil)
            {
                var count = selection.Valid.Count(n => n.SubscriptionId == group.Key && _clientState.Match(n.ClientState) == CallWebhookClientStateMatch.NoMatch);
                // Authenticated, so the id is Graph's own and safe to log as it is.
                if (ShouldLog("previous|" + group.Key, count, now, out var total, out var since))
                {
                    logger.LogWarning($"{LogTag} Accepted {total} notification(s) since {since:u} from subscription '{group.Key}' carrying the " +
                        "clientState of the client secret in use before the last rotation, recognised by the fingerprint the importer recorded. " +
                        $"Accepted until {group.Value:u} at the latest while the importer replaces that subscription. Logged at most every {RefusalLogInterval.TotalMinutes:0} minutes.");
                }
            }

            foreach (var group in selection.Refused.GroupBy(r => new { Id = SafeId(r.Notification?.SubscriptionId), r.Reason }))
            {
                if (!ShouldLog("refused|" + group.Key.Id + "|" + group.Key.Reason, group.Count(), now, out var total, out var since)) continue;

                logger.LogWarning($"{LogTag} Refused {total} notification(s) since {since:u} from subscription '{group.Key.Id}': {group.Key.Reason}. " +
                    $"{Explain(group.Key.Reason)} Graph was answered 200, so it does not send them again and those calls are not imported. " +
                    $"Logged at most every {RefusalLogInterval.TotalMinutes:0} minutes per subscription and reason.");
            }
        }

        private static string Explain(CallNotificationRefusalReason reason)
        {
            switch (reason)
            {
                case CallNotificationRefusalReason.NoClientSecretConfigured:
                    return "The web app has no client secret (app setting 'ClientSecret'), which the webhook's clientState is derived from, so it refuses every notification.";
                case CallNotificationRefusalReason.MissingClientState:
                    return "The notification carried no clientState; Graph always sends the one the subscription was created with, so this did not come from this deployment's subscription.";
                case CallNotificationRefusalReason.PreviousClientStateWindowClosed:
                    return $"It carries the clientState of the client secret in use before the last rotation, which is accepted for {CallSubscriptionRules.PreviousClientStateWindow.TotalHours:0} hours after the importer notices the rotation; " +
                        "the importer has not managed to replace the subscription since. Check the importer's [Calls Webhook] lines.";
                default:
                    return "The clientState is neither this deployment's current value nor, for the subscription the importer recorded, the previous client secret's. " +
                        "After a client-secret rotation the importer replaces the subscription within minutes; a subscription that is not this deployment's is another app's misdirected webhook, or a forgery.";
            }
        }

        private bool ShouldLog(string key, int count, DateTime now, out int total, out DateTime since)
        {
            lock (_logGate)
            {
                if (!_logEntries.TryGetValue(key, out var entry))
                {
                    if (_logEntries.Count >= MaxTrackedLogKeys) key = "overflow";
                    if (!_logEntries.TryGetValue(key, out entry))
                    {
                        entry = new ThrottledLogEntry { SinceUtc = now };
                        _logEntries[key] = entry;
                    }
                }

                entry.Count += count;
                if (entry.LastLoggedUtc.HasValue && now - entry.LastLoggedUtc.Value < RefusalLogInterval)
                {
                    total = 0;
                    since = default;
                    return false;
                }

                total = entry.Count;
                since = entry.SinceUtc;
                entry.Count = 0;
                entry.SinceUtc = now;
                entry.LastLoggedUtc = now;
                return true;
            }
        }

        /// <summary>A GUID as Graph writes it, or a placeholder: the id comes from an anonymous request.</summary>
        internal static string SafeId(string subscriptionId)
        {
            if (string.IsNullOrEmpty(subscriptionId)) return "(none)";
            return Guid.TryParse(subscriptionId, out var id) ? id.ToString("D") : "(not a GUID)";
        }

        private sealed class ThrottledLogEntry
        {
            public int Count { get; set; }
            public DateTime SinceUtc { get; set; }
            public DateTime? LastLoggedUtc { get; set; }
        }
    }
}
