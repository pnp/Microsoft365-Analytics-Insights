using System;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Read-only snapshot of the Teams call-records webhook subscription in Microsoft Graph,
    /// used to display webhook health (e.g. on the web homepage). Returned by
    /// <see cref="CallWebhook.GetCallRecordsSubscriptionInfo(System.Uri)"/>.
    /// </summary>
    public class CallRecordSubscriptionInfo
    {
        /// <summary>
        /// Whether a matching <c>/communications/callRecords</c> subscription currently exists for
        /// this deployment's webhook notification URL.
        /// </summary>
        public bool Exists { get; set; }

        /// <summary>Graph subscription id, when one exists.</summary>
        public string SubscriptionId { get; set; }

        /// <summary>
        /// When the subscription expires. Graph caps call-record subscriptions at ~3 days, so the
        /// importer web-job renews it on every import cycle.
        /// </summary>
        public DateTimeOffset? ExpirationDateTime { get; set; }
    }
}
