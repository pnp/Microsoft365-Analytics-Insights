using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Port for the Microsoft Graph change-notification subscription that feeds the Teams calls import.
    /// Extracted from <see cref="CallWebhook"/> so the create-vs-renew logic can be tested without
    /// Graph. See issue #378.
    ///
    /// Why this matters: the subscription expires after ~3 days and is renewed on every import cycle.
    /// A renewal that fails silently stops the calls import with no error at all, which is exactly the
    /// bug commit 560e501 ("Calls webhook: surface 403/permission failures clearly + fix silent
    /// renewal") had to fix once already. Implementations MUST let Graph failures propagate so
    /// <see cref="CallWebhook"/> can report them.
    /// </summary>
    public interface ICallRecordSubscriptionManager
    {
        /// <summary>
        /// Every call-records subscription pointing at this deployment's notification URL, in the order
        /// Graph returned them. Implementations must walk all pages: the tenant may have many
        /// subscriptions and the one we care about is not necessarily on the first page.
        /// </summary>
        Task<IReadOnlyList<CallRecordSubscription>> FindCallRecordSubscriptions(Uri notificationUrl);

        Task<CallRecordSubscription> CreateSubscription(Uri notificationUrl, string clientState, DateTime expiryUtc);

        Task<CallRecordSubscription> RenewSubscription(string subscriptionId, DateTime expiryUtc);
    }

    /// <summary>
    /// A Graph change-notification subscription, reduced to the fields the calls import reasons about.
    /// </summary>
    public class CallRecordSubscription
    {
        public string Id { get; set; }

        /// <summary>Graph resource path, e.g. <c>/communications/callRecords</c>.</summary>
        public string Resource { get; set; }

        public string NotificationUrl { get; set; }

        public DateTimeOffset? ExpirationDateTime { get; set; }
    }
}
