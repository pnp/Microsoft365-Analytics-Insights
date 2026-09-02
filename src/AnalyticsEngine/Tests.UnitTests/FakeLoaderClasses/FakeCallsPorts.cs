using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;

namespace UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="ICallRecordSubscriptionManager"/>. Records what the webhook asked Graph to
    /// do, and can be told to fail a create or a renew the way Graph does when the app registration is
    /// missing the CallRecords.Read.All permission. See issue #378.
    /// </summary>
    public class FakeCallRecordSubscriptionManager : ICallRecordSubscriptionManager
    {
        private readonly List<CallRecordSubscription> _existing = new List<CallRecordSubscription>();

        private Exception _createFailure;
        private Exception _renewFailure;

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

        public FakeCallRecordSubscriptionManager WithExistingSubscription(string id, DateTimeOffset? expiry)
        {
            _existing.Add(new CallRecordSubscription
            {
                Id = id,
                Resource = CallSubscriptionRules.CallRecordsResource,
                NotificationUrl = "https://contoso-analytics.example/api/CallRecordWebhook",
                ExpirationDateTime = expiry
            });
            return this;
        }

        public FakeCallRecordSubscriptionManager FailingCreateWith(Exception ex)
        {
            _createFailure = ex;
            return this;
        }

        public FakeCallRecordSubscriptionManager FailingRenewWith(Exception ex)
        {
            _renewFailure = ex;
            return this;
        }

        public Task<IReadOnlyList<CallRecordSubscription>> FindCallRecordSubscriptions(Uri notificationUrl)
        {
            return Task.FromResult<IReadOnlyList<CallRecordSubscription>>(_existing);
        }

        public Task<CallRecordSubscription> CreateSubscription(Uri notificationUrl, string clientState, DateTime expiryUtc)
        {
            Creates.Add(new CreateCall { NotificationUrl = notificationUrl, ClientState = clientState, ExpiryUtc = expiryUtc });

            if (_createFailure != null) throw _createFailure;

            return Task.FromResult(new CallRecordSubscription
            {
                Id = "new-subscription-id",
                Resource = CallSubscriptionRules.CallRecordsResource,
                NotificationUrl = notificationUrl.ToString(),
                ExpirationDateTime = expiryUtc
            });
        }

        public Task<CallRecordSubscription> RenewSubscription(string subscriptionId, DateTime expiryUtc)
        {
            Renewals.Add(new RenewCall { SubscriptionId = subscriptionId, ExpiryUtc = expiryUtc });

            if (_renewFailure != null) throw _renewFailure;

            return Task.FromResult(new CallRecordSubscription
            {
                Id = subscriptionId,
                Resource = CallSubscriptionRules.CallRecordsResource,
                ExpirationDateTime = expiryUtc
            });
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
        public List<CallRecordDTO> Saved { get; } = new List<CallRecordDTO>();

        public Task SaveOrReplaceCallRecord(CallRecordDTO call)
        {
            Saved.Add(call);
            return Task.CompletedTask;
        }
    }
}
