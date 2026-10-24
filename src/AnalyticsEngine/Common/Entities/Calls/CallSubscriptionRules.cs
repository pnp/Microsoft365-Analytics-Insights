using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.Calls
{
    /// <summary>
    /// Pure decision logic for the Teams calls webhook subscription: which Graph subscriptions are
    /// ours, whether to create or renew, and how long the subscription should live. Extracted from
    /// the importer's <c>CallWebhook</c> so it can be tested without Graph. See issue #378. Lives in
    /// <c>Common.Entities</c> because the web app reads the same subscription for its status page.
    ///
    /// A <c>static</c> class rather than an interface, per issue #381's conventions - it is a rule,
    /// not a dependency, and takes the current time as a parameter rather than depending on a clock.
    /// </summary>
    public static class CallSubscriptionRules
    {
        /// <summary>The Graph resource path the calls import subscribes to.</summary>
        public const string CallRecordsResource = "/communications/callRecords";

        /// <summary>
        /// Added to the webhook URL for subscriptions whose clientState is <see cref="CallWebhookClientState"/>. A
        /// separate notification URL is what keeps a rollback safe: importers from before the derived clientState match
        /// their subscription by exact URL, so they never find - and renew - one that the web app of their own version
        /// would refuse every notification from. They create their own on the bare URL instead, and this one lapses.
        /// The web app needs nothing to accept it: Graph adds its validation token as <c>&amp;validationToken=...</c>,
        /// and the action ignores the extra parameter.
        /// </summary>
        public const string ClientStateVersionQuery = "cs=v1";

        /// <summary>The notification URL subscriptions with the current clientState are created on.</summary>
        public static Uri CurrentNotificationUrl(Uri webAppUrl)
        {
            if (webAppUrl == null) throw new ArgumentNullException(nameof(webAppUrl));
            return new Uri(webAppUrl.ToString() + (string.IsNullOrEmpty(webAppUrl.Query) ? "?" : "&") + ClientStateVersionQuery);
        }

        /// <summary>
        /// How far ahead the subscription expiry is set. Two days is the maximum Graph will permit for
        /// this resource - see
        /// https://docs.microsoft.com/en-us/graph/api/resources/subscription?view=graph-rest-beta#properties
        /// </summary>
        public const int SubscriptionLifetimeDays = 2;

        /// <summary>
        /// A subscription is renewed once less than this is left of it. Graph validates the notification endpoint on a
        /// renewal as well as on a create, so renewing on every check (the importer checks every
        /// <c>CallSubscriptionMaintainer.HealthyCheckInterval</c>) would put the webhook through a validation round-trip
        /// that often for nothing. Half the lifetime leaves a day for a failing renewal to be retried before it lapses.
        /// </summary>
        public static readonly TimeSpan RenewWhenRemainingBelow = TimeSpan.FromHours(24);

        /// <summary>
        /// How long, after the importer notices a client-secret rotation, the web app keeps accepting notifications from
        /// the subscription created with the previous secret's clientState (<see cref="CallSubscriptionRecord.SupersededUtc"/>).
        /// The importer replaces that subscription within minutes when the webhook is reachable; this only has to cover a
        /// replacement that keeps failing, and it bounds how long a previous secret's clientState is honoured at all.
        /// </summary>
        public static readonly TimeSpan PreviousClientStateWindow = TimeSpan.FromHours(24);

        /// <summary>The webhook's path under the web app's URL. Program.cs and the status page build the same URL.</summary>
        public const string WebhookPath = "api/CallRecordWebhook";

        /// <summary>The webhook's own URL for a deployment's <c>WebAppURL</c> setting, or null when that isn't a valid absolute URL.</summary>
        public static Uri WebhookUrlFor(string webAppUrl)
        {
            if (string.IsNullOrWhiteSpace(webAppUrl)) return null;
            return Uri.TryCreate(webAppUrl + WebhookPath, UriKind.Absolute, out var url) ? url : null;
        }

        /// <summary>
        /// Whether a subscription expiring at <paramref name="expiry"/> should be renewed now. An unknown expiry is renewed:
        /// renewing a healthy subscription costs one Graph call, letting one lapse costs every call until it is noticed.
        /// </summary>
        public static bool NeedsRenewal(DateTimeOffset? expiry, DateTime nowUtc)
        {
            return expiry == null || expiry.Value.UtcDateTime - nowUtc < RenewWhenRemainingBelow;
        }

        /// <summary>
        /// Graph documents a <b>409 Conflict</b> for a subscription that duplicates an existing one ("already exists for the
        /// requested combination"). Observed behaviour for call records is to accept the duplicate, which the create-then-
        /// delete replacement relies on; this is what the importer falls back on if Graph starts enforcing it.
        /// </summary>
        public static bool IsDuplicateSubscriptionConflict(int httpStatus) => httpStatus == 409;

        /// <summary>
        /// Whether a Graph subscription is this deployment's call-records subscription. Both the
        /// resource and the notification URL must match: a tenant can legitimately have call-records
        /// subscriptions belonging to other applications, and renewing one of those would take over
        /// another product's webhook. The URL is either the webhook's own (<paramref name="webAppUrl"/>,
        /// which subscriptions from before the derived clientState use) or its
        /// <see cref="CurrentNotificationUrl"/> - exactly, nothing else.
        /// </summary>
        public static bool IsCallRecordsSubscriptionFor(string resource, string notificationUrl, Uri webAppUrl)
        {
            return resource == CallRecordsResource
                && (notificationUrl == webAppUrl.ToString() || notificationUrl == CurrentNotificationUrl(webAppUrl).ToString());
        }

        /// <summary>When a subscription created or renewed at <paramref name="nowUtc"/> should expire.</summary>
        public static DateTime ExpiryFor(DateTime nowUtc)
        {
            return nowUtc.AddDays(SubscriptionLifetimeDays);
        }

        /// <summary>
        /// Decide whether to create a new subscription or renew an existing one.
        /// </summary>
        /// <remarks>
        /// <see cref="CallSubscriptionActionKind.Renew"/> means "a subscription exists, keep it": whether it is actually
        /// renewed now is <see cref="NeedsRenewal"/>'s decision, because the importer checks far more often than the
        /// subscription needs renewing (see <see cref="RenewWhenRemainingBelow"/>).
        /// When more than one matches, the first is the one kept (Graph's own ordering), which is what the
        /// importer has always done.
        /// </remarks>
        public static CallSubscriptionAction Decide(IReadOnlyList<CallRecordSubscription> existingSubscriptions, DateTime nowUtc)
        {
            var expiry = ExpiryFor(nowUtc);

            if (existingSubscriptions.Count == 0)
            {
                return new CallSubscriptionAction { Kind = CallSubscriptionActionKind.Create, ExpiryUtc = expiry };
            }

            return new CallSubscriptionAction
            {
                Kind = CallSubscriptionActionKind.Renew,
                ExistingSubscriptionId = existingSubscriptions[0].Id,
                ExpiryUtc = expiry
            };
        }

        /// <summary>
        /// Classify a Graph subscription error as a failure of Graph's <b>validation callback</b> to our
        /// notification endpoint, rather than a failure of the Graph call itself. See issue #273.
        /// </summary>
        /// <remarks>
        /// When a subscription is created or renewed, Graph POSTs a validation request to the
        /// notification URL and expects an anonymous 200 OK echoing the <c>validationToken</c>. If that
        /// callback fails, Graph rejects the subscription with a <b>400 Bad Request</b> whose message
        /// describes what happened at <i>our</i> endpoint.
        ///
        /// This distinction matters because the message is actively misleading: the common variant reads
        /// "HTTP status code is 'Forbidden'", where the <c>Forbidden</c> is what our endpoint returned to
        /// Graph - it is NOT the tenant refusing a Graph permission. An admin who reads the raw message
        /// goes and re-checks the importer's <c>CallWebhook.REQUIRED_GRAPH_PERMISSION</c>, which is the wrong
        /// place: a genuine permission problem surfaces as a 403 from Graph itself, not as a 400 wrapping
        /// our own status code.
        /// </remarks>
        public static SubscriptionValidationFailure ClassifyValidationCallbackFailure(string graphErrorMessage)
        {
            if (string.IsNullOrWhiteSpace(graphErrorMessage)) return SubscriptionValidationFailure.NotAValidationFailure;

            // Graph's wording for both variants starts the same way; match on that stem so a later
            // rewording of the tail does not silently drop us back to the generic message.
            if (graphErrorMessage.IndexOf("Subscription validation request", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return SubscriptionValidationFailure.NotAValidationFailure;
            }

            var timedOut = graphErrorMessage.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0;

            return new SubscriptionValidationFailure
            {
                IsValidationCallbackFailure = true,
                TimedOut = timedOut,
                EndpointStatus = timedOut ? null : ExtractEndpointStatus(graphErrorMessage),
            };
        }

        /// <summary>
        /// Pull the status our endpoint returned out of Graph's message, e.g. the <c>Forbidden</c> in
        /// "HTTP status code is 'Forbidden'". Returns null when Graph did not include one.
        /// </summary>
        private static string ExtractEndpointStatus(string graphErrorMessage)
        {
            const string marker = "HTTP status code is";
            var markerAt = graphErrorMessage.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerAt < 0) return null;

            var openQuote = graphErrorMessage.IndexOf('\'', markerAt + marker.Length);
            if (openQuote < 0) return null;

            var closeQuote = graphErrorMessage.IndexOf('\'', openQuote + 1);
            if (closeQuote <= openQuote + 1) return null;

            return graphErrorMessage.Substring(openQuote + 1, closeQuote - openQuote - 1);
        }

        /// <summary>
        /// Which subscription to report on a status page when several match. The one expiring latest is
        /// the one actually keeping the webhook alive, so that is the one whose expiry an operator
        /// needs to see.
        /// </summary>
        public static CallRecordSubscription SelectCurrentForStatus(IReadOnlyList<CallRecordSubscription> matchingSubscriptions)
        {
            return matchingSubscriptions
                .OrderByDescending(s => s.ExpirationDateTime ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
        }
    }

    public enum CallSubscriptionActionKind
    {
        Create,
        Renew
    }

    /// <summary>
    /// The outcome of <see cref="CallSubscriptionRules.ClassifyValidationCallbackFailure"/>. See issue #273.
    /// </summary>
    public class SubscriptionValidationFailure
    {
        public static readonly SubscriptionValidationFailure NotAValidationFailure = new SubscriptionValidationFailure();

        /// <summary>Graph's validation callback to our notification endpoint is what failed.</summary>
        public bool IsValidationCallbackFailure { get; set; }

        /// <summary>Graph gave up waiting rather than getting a response it disliked.</summary>
        public bool TimedOut { get; set; }

        /// <summary>
        /// The status our endpoint returned to Graph (e.g. "Forbidden"), when Graph reported one.
        /// Null for the timeout variant, and when Graph did not name a status.
        /// </summary>
        public string EndpointStatus { get; set; }
    }

    /// <summary>What <see cref="CallSubscriptionRules.Decide"/> decided to do.</summary>
    public class CallSubscriptionAction
    {
        public CallSubscriptionActionKind Kind { get; set; }

        /// <summary>Only set when <see cref="Kind"/> is <see cref="CallSubscriptionActionKind.Renew"/>.</summary>
        public string ExistingSubscriptionId { get; set; }

        public DateTime ExpiryUtc { get; set; }
    }
}
