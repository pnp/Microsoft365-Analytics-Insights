using Common.Entities.Calls;
using System;
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
    /// <remarks>
    /// The read half, <see cref="ICallRecordSubscriptionReader"/>, lives in <c>Common.Entities</c> so the
    /// web app can show the subscription's status without referencing this engine.
    /// </remarks>
    public interface ICallRecordSubscriptionManager : ICallRecordSubscriptionReader
    {
        Task<CallRecordSubscription> CreateSubscription(Uri notificationUrl, string clientState, DateTime expiryUtc);

        Task<CallRecordSubscription> RenewSubscription(string subscriptionId, DateTime expiryUtc);
    }
}
