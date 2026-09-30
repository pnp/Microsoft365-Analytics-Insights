using System;
using System.Threading.Tasks;

namespace Common.Entities.Calls
{
    /// <summary>
    /// Read-only check of the current call-records webhook subscription, for status display. Shared by
    /// the importer's <c>CallWebhook</c> and the web app, which reads the subscription without the
    /// Microsoft Graph SDK.
    /// </summary>
    public static class CallRecordSubscriptionStatus
    {
        /// <summary>
        /// Does NOT create or renew anything. Returns whether a matching subscription currently exists
        /// and, if so, when it expires. Any Graph error is allowed to propagate so the caller can
        /// surface it as an explicit "couldn't check" state.
        /// </summary>
        public static async Task<CallRecordSubscriptionInfo> ReadAsync(ICallRecordSubscriptionReader subscriptions, Uri webAppUrl)
        {
            if (subscriptions == null) throw new ArgumentNullException(nameof(subscriptions));

            var matchingSubs = await subscriptions.FindCallRecordSubscriptions(webAppUrl);

            // If more than one matches (shouldn't normally happen), report the one that expires
            // latest - that's the subscription keeping the webhook alive.
            var current = CallSubscriptionRules.SelectCurrentForStatus(matchingSubs);

            if (current == null)
            {
                return new CallRecordSubscriptionInfo { Exists = false };
            }

            return new CallRecordSubscriptionInfo
            {
                Exists = true,
                SubscriptionId = current.Id,
                ExpirationDateTime = current.ExpirationDateTime,
            };
        }
    }
}
