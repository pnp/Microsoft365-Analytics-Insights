using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Pure decision logic for the Teams calls webhook subscription: which Graph subscriptions are
    /// ours, whether to create or renew, and how long the subscription should live. Extracted from
    /// <see cref="CallWebhook"/> so it can be tested without Graph. See issue #378.
    ///
    /// A <c>static</c> class rather than an interface, per issue #381's conventions - it is a rule,
    /// not a dependency, and takes the current time as a parameter rather than depending on a clock.
    /// </summary>
    public static class CallSubscriptionRules
    {
        /// <summary>The Graph resource path the calls import subscribes to.</summary>
        public const string CallRecordsResource = "/communications/callRecords";

        /// <summary>
        /// How far ahead the subscription expiry is set. Two days is the maximum Graph will permit for
        /// this resource - see
        /// https://docs.microsoft.com/en-us/graph/api/resources/subscription?view=graph-rest-beta#properties
        /// </summary>
        public const int SubscriptionLifetimeDays = 2;

        /// <summary>
        /// Whether a Graph subscription is this deployment's call-records subscription. Both the
        /// resource and the notification URL must match: a tenant can legitimately have call-records
        /// subscriptions belonging to other applications, and renewing one of those would take over
        /// another product's webhook.
        /// </summary>
        public static bool IsCallRecordsSubscriptionFor(string resource, string notificationUrl, Uri webAppUrl)
        {
            return resource == CallRecordsResource && notificationUrl == webAppUrl.ToString();
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
        /// Renewal is unconditional when a subscription exists - it does not wait for the expiry to get
        /// close. The importer runs on a cycle that may be hours apart and Graph caps the lifetime at
        /// two days, so pushing the expiry out every cycle is what keeps the webhook alive.
        /// When more than one matches, the first is renewed (Graph's own ordering), which is what the
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

    /// <summary>What <see cref="CallSubscriptionRules.Decide"/> decided to do.</summary>
    public class CallSubscriptionAction
    {
        public CallSubscriptionActionKind Kind { get; set; }

        /// <summary>Only set when <see cref="Kind"/> is <see cref="CallSubscriptionActionKind.Renew"/>.</summary>
        public string ExistingSubscriptionId { get; set; }

        public DateTime ExpiryUtc { get; set; }
    }
}
