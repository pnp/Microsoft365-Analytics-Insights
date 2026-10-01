using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Common.Entities.Calls
{
    /// <summary>
    /// Read port for the Microsoft Graph change-notification subscription that feeds the Teams calls
    /// import. The importer's <c>ICallRecordSubscriptionManager</c> extends it with create and renew;
    /// the web app only ever reads it, for the status page.
    /// </summary>
    public interface ICallRecordSubscriptionReader
    {
        /// <summary>
        /// Every call-records subscription pointing at this deployment's notification URL, in the order
        /// Graph returned them. Implementations must walk all pages: the tenant may have many
        /// subscriptions and the one we care about is not necessarily on the first page.
        /// </summary>
        Task<IReadOnlyList<CallRecordSubscription>> FindCallRecordSubscriptions(Uri notificationUrl);
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
