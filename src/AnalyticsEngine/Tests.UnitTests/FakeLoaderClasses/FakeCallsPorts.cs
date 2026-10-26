using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Common.Entities.Calls;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;

namespace UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="ICallRecordSubscriptionManager"/>. Records what the webhook asked Graph to
    /// do, and can be told to fail a create or a renew the way Graph does when the app registration is
    /// missing the CallRecords.Read.All permission. See issue #378.
    /// </summary>
    /// <remarks>
    /// Stateful, like Graph: a created subscription is found by the next cycle and a deleted one is not, and
    /// <see cref="FindCallRecordSubscriptions"/> applies the real filter (resource and notification URL), so another
    /// app's subscriptions in the same tenant can be modelled.
    /// </remarks>
    public class FakeCallRecordSubscriptionManager : ICallRecordSubscriptionManager
    {
        private readonly List<CallRecordSubscription> _existing = new List<CallRecordSubscription>();

        private Exception _createFailure;
        private int _createFailuresLeft;
        private bool _refuseDuplicates;
        private Exception _renewFailure;
        private Exception _deleteFailure;
        private int _created;

        public class CreateCall
        {
            public Uri NotificationUrl { get; set; }
            public string ClientState { get; set; }
            public DateTime ExpiryUtc { get; set; }
        }

        public class RenewCall
        {
            public string SubscriptionId { get; set; }
            public DateTime ExpiryUtc { get; set; }
        }

        public List<CreateCall> Creates { get; } = new List<CreateCall>();
        public List<RenewCall> Renewals { get; } = new List<RenewCall>();
        public List<string> Deletes { get; } = new List<string>();

        /// <summary>Every change asked of Graph, in order: "create:&lt;new id&gt;", "renew:&lt;id&gt;", "delete:&lt;id&gt;".</summary>
        public List<string> Operations { get; } = new List<string>();

        /// <summary>The subscriptions Graph holds now, including other apps' ones.</summary>
        public IReadOnlyList<CallRecordSubscription> All => _existing;

        public FakeCallRecordSubscriptionManager WithExistingSubscription(string id, DateTimeOffset? expiry)
        {
            return WithSubscription(id, CallSubscriptionRules.CallRecordsResource, "https://contoso-analytics.example/api/CallRecordWebhook", expiry);
        }

        /// <summary>A subscription on the webhook's current (<c>?cs=v1</c>) URL, as the importer now creates them.</summary>
        public FakeCallRecordSubscriptionManager WithCurrentSubscription(string id, DateTimeOffset? expiry)
        {
            return WithSubscription(id, CallSubscriptionRules.CallRecordsResource,
                CallSubscriptionRules.CurrentNotificationUrl(new Uri("https://contoso-analytics.example/api/CallRecordWebhook")).ToString(), expiry);
        }

        /// <summary>Any subscription in the tenant - including one that is not this deployment's.</summary>
        public FakeCallRecordSubscriptionManager WithSubscription(string id, string resource, string notificationUrl, DateTimeOffset? expiry)
        {
            _existing.Add(new CallRecordSubscription
            {
                Id = id,
                Resource = resource,
                NotificationUrl = notificationUrl,
                ExpirationDateTime = expiry
            });
            return this;
        }

        /// <param name="times">How many creates fail before they start succeeding - a cold web app warms up.</param>
        public FakeCallRecordSubscriptionManager FailingCreateWith(Exception ex, int times = int.MaxValue)
        {
            _createFailure = ex;
            _createFailuresLeft = times;
            return this;
        }

        /// <summary>
        /// Enforce what Graph documents but does not do today for call records: a second subscription on the same resource
        /// is refused with 409 Conflict.
        /// </summary>
        public FakeCallRecordSubscriptionManager RefusingDuplicates()
        {
            _refuseDuplicates = true;
            return this;
        }

        public FakeCallRecordSubscriptionManager FailingRenewWith(Exception ex)
        {
            _renewFailure = ex;
            return this;
        }

        public FakeCallRecordSubscriptionManager FailingDeleteWith(Exception ex)
        {
            _deleteFailure = ex;
            return this;
        }

        public Task<IReadOnlyList<CallRecordSubscription>> FindCallRecordSubscriptions(Uri notificationUrl)
        {
            return Task.FromResult<IReadOnlyList<CallRecordSubscription>>(
                _existing.Where(s => CallSubscriptionRules.IsCallRecordsSubscriptionFor(s.Resource, s.NotificationUrl, notificationUrl)).ToList());
        }

        public Task<CallRecordSubscription> CreateSubscription(Uri notificationUrl, string clientState, DateTime expiryUtc)
        {
            Creates.Add(new CreateCall { NotificationUrl = notificationUrl, ClientState = clientState, ExpiryUtc = expiryUtc });

            // Faulted task rather than a synchronous throw, deliberately: a real Graph failure only
            // surfaces when the returned task is awaited. Throwing synchronously here would let a
            // caller that forgot to await still land in the catch block - which is exactly the
            // silent-failure bug commit 560e501 had to fix.
            if (_createFailure != null && _createFailuresLeft > 0)
            {
                _createFailuresLeft--;
                return Task.FromException<CallRecordSubscription>(_createFailure);
            }

            if (_refuseDuplicates && _existing.Any(s => s.Resource == CallSubscriptionRules.CallRecordsResource
                && CallSubscriptionRules.IsCallRecordsSubscriptionFor(s.Resource, s.NotificationUrl, new Uri("https://contoso-analytics.example/api/CallRecordWebhook"))))
            {
                Operations.Add("conflict");
                return Task.FromException<CallRecordSubscription>(new Microsoft.Graph.Models.ODataErrors.ODataError
                {
                    ResponseStatusCode = 409,
                    Error = new Microsoft.Graph.Models.ODataErrors.MainError { Code = "ExtensionError", Message = "Subscription already exists for the requested combination." }
                });
            }

            _created++;
            var created = new CallRecordSubscription
            {
                Id = _created == 1 ? "new-subscription-id" : "new-subscription-id-" + _created,
                Resource = CallSubscriptionRules.CallRecordsResource,
                NotificationUrl = notificationUrl.ToString(),
                ExpirationDateTime = expiryUtc
            };
            _existing.Add(created);
            Operations.Add("create:" + created.Id);
            return Task.FromResult(created);
        }

        public Task<CallRecordSubscription> RenewSubscription(string subscriptionId, DateTime expiryUtc)
        {
            Renewals.Add(new RenewCall { SubscriptionId = subscriptionId, ExpiryUtc = expiryUtc });

            // See the note in CreateSubscription: the failure must only be observable by awaiting.
            if (_renewFailure != null) return Task.FromException<CallRecordSubscription>(_renewFailure);

            Operations.Add("renew:" + subscriptionId);
            foreach (var renewed in _existing.Where(s => s.Id == subscriptionId)) renewed.ExpirationDateTime = expiryUtc;
            return Task.FromResult(new CallRecordSubscription
            {
                Id = subscriptionId,
                Resource = CallSubscriptionRules.CallRecordsResource,
                ExpirationDateTime = expiryUtc
            });
        }

        public Task DeleteSubscription(string subscriptionId)
        {
            Deletes.Add(subscriptionId);
            if (_deleteFailure != null) return Task.FromException(_deleteFailure);

            _existing.RemoveAll(s => s.Id == subscriptionId);
            Operations.Add("delete:" + subscriptionId);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// <see cref="ICallNotificationQueueSender"/> whose sends fail the way Service Bus does when the
    /// namespace is unreachable - as a faulted task, so a caller that forgot to await it would not
    /// see the failure. See issue #378.
    /// </summary>
    public class FailingCallNotificationQueueSender : ICallNotificationQueueSender
    {
        private readonly Exception _failure;

        public FailingCallNotificationQueueSender(Exception failure)
        {
            _failure = failure ?? throw new ArgumentNullException(nameof(failure));
        }

        public int SendAttempts { get; private set; }

        public Task SendAsync(string messageBody)
        {
            SendAttempts++;
            return Task.FromException(_failure);
        }
    }

    /// <summary>
    /// In-memory <see cref="ICallRecordSourceLoader"/>: returns scripted call records by id, and
    /// records which ids were asked for. See issue #378.
    /// </summary>
    public class FakeCallRecordSourceLoader : ICallRecordSourceLoader
    {
        private readonly Dictionary<string, CallRecordDTO> _callsById = new Dictionary<string, CallRecordDTO>(StringComparer.Ordinal);

        public List<string> RequestedCallIds { get; } = new List<string>();

        public FakeCallRecordSourceLoader Returning(string callId, CallRecordDTO call)
        {
            _callsById[callId] = call;
            return this;
        }

        public Task<CallRecordDTO> LoadCallRecord(string callId)
        {
            RequestedCallIds.Add(callId);
            _callsById.TryGetValue(callId, out var call);
            return Task.FromResult(call);
        }
    }

    /// <summary>
    /// In-memory <see cref="ICallRecordPersistenceManager"/>: keeps everything it was asked to save.
    /// See issue #378.
    /// </summary>
    public class InMemoryCallRecordPersistenceManager : ICallRecordPersistenceManager
    {
        private Exception _saveFailure;

        public List<CallRecordDTO> Saved { get; } = new List<CallRecordDTO>();

        /// <summary>
        /// Make saves fail the way SQL does - as a faulted task, so the failure is only observable by
        /// awaiting it.
        /// </summary>
        public InMemoryCallRecordPersistenceManager FailingWith(Exception failure)
        {
            _saveFailure = failure;
            return this;
        }

        public List<Common.Entities.UserScope.UserImportScope> ScopesUsed { get; } = new List<Common.Entities.UserScope.UserImportScope>();

        public Task SaveOrReplaceCallRecord(CallRecordDTO call, Common.Entities.UserScope.UserImportScope userScope)
        {
            ScopesUsed.Add(userScope);
            if (_saveFailure != null) return Task.FromException(_saveFailure);

            Saved.Add(call);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// In-memory <see cref="ICallQueueMessageSettlement"/>: records what was done with the message, in order, and can
    /// make queueing a retry fail the way Service Bus does.
    /// </summary>
    public class FakeCallQueueMessageSettlement : ICallQueueMessageSettlement
    {
        public const string Complete = "Complete";
        public const string Abandon = "Abandon";
        public const string DeadLetter = "DeadLetter";
        public const string ScheduleRetry = "ScheduleRetry";

        public List<string> Operations { get; } = new List<string>();
        public string DeadLetterReason { get; private set; }
        public string DeadLetterDescription { get; private set; }
        public CallQueueRetryState ScheduledState { get; private set; }
        public DateTime? ScheduledForUtc { get; private set; }

        /// <summary>When set, queueing a retry fails with this (as a faulted task).</summary>
        public Exception ScheduleFailure { get; set; }

        public Task CompleteAsync()
        {
            Operations.Add(Complete);
            return Task.CompletedTask;
        }

        public Task AbandonAsync()
        {
            Operations.Add(Abandon);
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(string reason, string description)
        {
            Operations.Add(DeadLetter);
            DeadLetterReason = reason;
            DeadLetterDescription = description;
            return Task.CompletedTask;
        }

        public Task ScheduleRetryAsync(CallQueueRetryState state, DateTime enqueueAtUtc)
        {
            Operations.Add(ScheduleRetry);
            if (ScheduleFailure != null) return Task.FromException(ScheduleFailure);

            ScheduledState = state;
            ScheduledForUtc = enqueueAtUtc;
            return Task.CompletedTask;
        }
    }
}
