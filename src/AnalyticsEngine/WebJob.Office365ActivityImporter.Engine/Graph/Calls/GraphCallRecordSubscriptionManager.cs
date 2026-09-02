using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Graph implementation of <see cref="ICallRecordSubscriptionManager"/>. Holds every Graph call the
    /// calls webhook makes, so <see cref="CallWebhook"/> is left with only the decision and the
    /// operator-facing reporting. See issue #378.
    ///
    /// Graph failures are deliberately NOT caught here: <see cref="CallWebhook"/> turns them into the
    /// actionable "which permission to grant" message and re-throws, and swallowing them here is what
    /// made a failing renewal invisible before commit 560e501.
    /// </summary>
    public class GraphCallRecordSubscriptionManager : ICallRecordSubscriptionManager
    {
        private readonly GraphServiceClient _client;

        public GraphCallRecordSubscriptionManager(GraphServiceClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>
        /// Walk every subscriptions page (the default Graph page size may not include the
        /// call-records subscription when the tenant has many subscriptions) and return those
        /// matching the call-records resource for this web-app's notification URL.
        /// </summary>
        public async Task<IReadOnlyList<CallRecordSubscription>> FindCallRecordSubscriptions(Uri notificationUrl)
        {
            if (notificationUrl is null) throw new ArgumentNullException(nameof(notificationUrl));

            var matchingSubs = new List<CallRecordSubscription>();
            var firstPage = await _client.Subscriptions.GetAsync();
            if (firstPage != null)
            {
                var iterator = PageIterator<Subscription, SubscriptionCollectionResponse>.CreatePageIterator(
                    _client,
                    firstPage,
                    sub =>
                    {
                        if (CallSubscriptionRules.IsCallRecordsSubscriptionFor(sub.Resource, sub.NotificationUrl, notificationUrl))
                        {
                            matchingSubs.Add(ToCallRecordSubscription(sub));
                        }

                        return true;
                    });

                await iterator.IterateAsync();
            }

            return matchingSubs;
        }

        public async Task<CallRecordSubscription> CreateSubscription(Uri notificationUrl, string clientState, DateTime expiryUtc)
        {
            if (notificationUrl is null) throw new ArgumentNullException(nameof(notificationUrl));

            var result = await _client.Subscriptions.PostAsync(new Subscription()
            {
                NotificationUrl = notificationUrl.ToString(),
                Resource = CallSubscriptionRules.CallRecordsResource,
                ClientState = clientState,
                ChangeType = "created",
                ExpirationDateTime = expiryUtc
            });

            return ToCallRecordSubscription(result);
        }

        public async Task<CallRecordSubscription> RenewSubscription(string subscriptionId, DateTime expiryUtc)
        {
            // https://docs.microsoft.com/en-us/graph/api/subscription-update?view=graph-rest-beta&tabs=http
            var result = await _client.Subscriptions[subscriptionId].PatchAsync(
                new Subscription
                {
                    ExpirationDateTime = expiryUtc
                }
            );

            return ToCallRecordSubscription(result);
        }

        private static CallRecordSubscription ToCallRecordSubscription(Subscription sub)
        {
            if (sub == null) return null;

            return new CallRecordSubscription
            {
                Id = sub.Id,
                Resource = sub.Resource,
                NotificationUrl = sub.NotificationUrl,
                ExpirationDateTime = sub.ExpirationDateTime
            };
        }
    }
}
