using Common.Entities.Calls;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Threading;
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
        private readonly CancellationToken _stopping;
        private readonly string _applicationId;

        public GraphCallRecordSubscriptionManager(GraphServiceClient client) : this(client, CancellationToken.None) { }

        internal GraphCallRecordSubscriptionManager(GraphServiceClient client, CancellationToken stopping, string applicationId = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _stopping = stopping;
            _applicationId = applicationId;
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
            var firstPage = await _client.Subscriptions.GetAsync(rc => rc.Options.Add(GraphResponseEvidence.RequireCollection), _stopping);
            if (firstPage?.Value == null)
            {
                // A 200 without the subscriptions list: whether the subscription exists is unknown, so this is a failed read.
                // Read as "none", the webhook went on to create a subscription it may already have had (#847).
                throw new GraphPageWithoutValueException(1);
            }

            var iterator = PageIterator<Subscription, SubscriptionCollectionResponse>.CreatePageIterator(
                _client,
                firstPage,
                async sub =>
                {
                    if (CallSubscriptionRules.IsCallRecordsSubscriptionFor(sub.Resource, sub.NotificationUrl, notificationUrl))
                    {
                        if (_applicationId != null && string.IsNullOrWhiteSpace(sub.ApplicationId))
                        {
                            var detail = await ReadBackAsync(sub.Id);
                            if (detail?.Id != sub.Id || string.IsNullOrWhiteSpace(detail?.ApplicationId)
                                || detail.Resource != sub.Resource || detail.NotificationUrl != sub.NotificationUrl)
                                throw new InvalidOperationException("Graph did not confirm ownership of a listed call subscription; leaving subscriptions unchanged.");
                            sub = detail;
                        }
                        if (_applicationId == null || string.Equals(sub.ApplicationId, _applicationId, StringComparison.OrdinalIgnoreCase))
                            matchingSubs.Add(ToCallRecordSubscription(sub));
                    }

                    return true;
                }, next =>
                {
                    next.AddRequestOptions(new[] { GraphResponseEvidence.RequireCollection });
                    return next;
                });

            await iterator.IterateAsync(_stopping);
            _stopping.ThrowIfCancellationRequested(); // PageIterator can stop between pages without throwing.

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
            }, rc => rc.Options.Add(GraphResponseEvidence.RequireDirect), _stopping);

            if (string.IsNullOrWhiteSpace(result?.Id))
                throw new InvalidOperationException("Graph returned no subscription ID; subscription maintenance cannot continue.");
            var id = result.Id;
            CheckCreateFields(result, notificationUrl, clientState);
            if (!HasCreateFields(result))
            {
                result = await ReadBackAsync(id);
                if (result?.Id != id || !HasCreateFields(result))
                    throw new InvalidOperationException("Graph did not confirm the created call subscription; cleanup is deferred.");
                CheckCreateFields(result, notificationUrl, clientState);
            }
            _stopping.ThrowIfCancellationRequested();
            return ToCallRecordSubscription(result);
        }

        private bool HasCreateFields(Subscription sub) => sub.Resource != null && sub.NotificationUrl != null
            && sub.ChangeType != null && sub.ExpirationDateTime.HasValue && (_applicationId == null || sub.ApplicationId != null);

        private void CheckCreateFields(Subscription sub, Uri url, string state)
        {
            if (sub.Resource != null && sub.Resource != CallSubscriptionRules.CallRecordsResource
                || sub.NotificationUrl != null && sub.NotificationUrl != url.ToString()
                || sub.ChangeType != null && !string.Equals(sub.ChangeType, "created", StringComparison.OrdinalIgnoreCase)
                || _applicationId != null && sub.ApplicationId != null && !string.Equals(sub.ApplicationId, _applicationId, StringComparison.OrdinalIgnoreCase)
                || sub.ClientState != null && sub.ClientState != state)
                throw new InvalidOperationException("Graph returned a different call subscription than requested; cleanup is deferred.");
        }

        // GET /subscriptions/{id} uses the existing resource permission. A failure here is not a POST
        // refusal (in particular, not permission to perform the duplicate-conflict delete).
        // https://learn.microsoft.com/graph/api/subscription-get?view=graph-rest-1.0
        private async Task<Subscription> ReadBackAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Graph returned no subscription ID to confirm.");
            try
            {
                return await _client.Subscriptions[id].GetAsync(rc => rc.Options.Add(GraphResponseEvidence.RequireDirect), _stopping);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException || ex is GraphDecisionRedirectException || ex is TimeoutException))
            {
                _stopping.ThrowIfCancellationRequested();
                throw new InvalidOperationException($"Graph could not confirm the call subscription by ID ({ex.GetType().Name}); cleanup is deferred.");
            }
        }

        public async Task<CallRecordSubscription> RenewSubscription(string subscriptionId, DateTime expiryUtc)
        {
            // https://docs.microsoft.com/en-us/graph/api/subscription-update?view=graph-rest-beta&tabs=http
            var result = await _client.Subscriptions[subscriptionId].PatchAsync(
                new Subscription
                {
                    ExpirationDateTime = expiryUtc
                }, rc =>
                {
                    rc.Options.Add(GraphOverallTimeoutHandler.DistinguishDeadline);
                    rc.Options.Add(GraphResponseEvidence.RequireDirect);
                }, _stopping);

            return ToCallRecordSubscription(result);
        }

        public async Task DeleteSubscription(string subscriptionId)
        {
            if (string.IsNullOrEmpty(subscriptionId)) throw new ArgumentException("A subscription id is required.", nameof(subscriptionId));

            // https://learn.microsoft.com/en-us/graph/api/subscription-delete?view=graph-rest-1.0
            await _client.Subscriptions[subscriptionId].DeleteAsync(rc => rc.Options.Add(GraphResponseEvidence.RequireDirect), _stopping);
        }

        private static CallRecordSubscription ToCallRecordSubscription(Subscription sub)
        {
            if (string.IsNullOrWhiteSpace(sub?.Id))
                throw new InvalidOperationException("Graph returned no subscription ID; subscription maintenance cannot continue.");

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
