using Azure.Messaging.ServiceBus;
using Common.Entities.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;

namespace Tests.UnitTests
{
    /// <summary>
    /// First unit coverage for the Teams calls import (issue #378). Everything here runs with no
    /// Graph, no Service Bus and no SQL.
    /// </summary>
    [TestClass]
    public class CallsImportTests
    {
        private static readonly Uri WebhookUrl = new Uri("https://contoso-analytics.example/api/CallRecordWebhook");
        private static readonly DateTime Now = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        private static ODataError GraphError(int statusCode, string message)
        {
            return new ODataError
            {
                ResponseStatusCode = statusCode,
                Error = new MainError { Message = message }
            };
        }

        #region Webhook subscription lifecycle

        /// <summary>
        /// With no subscription in place the webhook must be created, with the client state Graph will
        /// echo back on every notification (the webhook's only authentication) and the two-day expiry
        /// that is the maximum Graph permits for this resource.
        /// </summary>
        [TestMethod]
        public async Task CreateOrUpdateWebhook_WithNoExistingSubscription_CreatesOneExpiringInTwoDays()
        {
            var subscriptions = new FakeCallRecordSubscriptionManager();
            var webhook = new CallWebhook(subscriptions, new CapturingLogger(), new FixedClock(Now));

            await webhook.CreateOrUpdateWebhook(WebhookUrl, "the-client-state");

            Assert.AreEqual(1, subscriptions.Creates.Count);
            Assert.AreEqual(0, subscriptions.Renewals.Count);
            Assert.AreEqual(WebhookUrl, subscriptions.Creates[0].NotificationUrl);
            Assert.AreEqual("the-client-state", subscriptions.Creates[0].ClientState);
            Assert.AreEqual(Now.AddDays(2), subscriptions.Creates[0].ExpiryUtc);
        }

        /// <summary>
        /// When a subscription already exists it must be RENEWED, not duplicated: creating a second
        /// subscription for the same URL would double every call notification, and letting the
        /// existing one lapse stops the calls import silently.
        /// </summary>
        [TestMethod]
        public async Task CreateOrUpdateWebhook_WithAnExistingSubscription_RenewsItInsteadOfCreatingASecond()
        {
            var subscriptions = new FakeCallRecordSubscriptionManager()
                .WithExistingSubscription("existing-sub-id", Now.AddHours(6));
            var webhook = new CallWebhook(subscriptions, new CapturingLogger(), new FixedClock(Now));

            await webhook.CreateOrUpdateWebhook(WebhookUrl, "the-client-state");

            Assert.AreEqual(0, subscriptions.Creates.Count, "A second subscription for the same URL would duplicate every call notification.");
            Assert.AreEqual(1, subscriptions.Renewals.Count);
            Assert.AreEqual("existing-sub-id", subscriptions.Renewals[0].SubscriptionId);
            Assert.AreEqual(Now.AddDays(2), subscriptions.Renewals[0].ExpiryUtc, "Renewal must push the expiry out, not leave it where it was.");
        }

        /// <summary>
        /// The renewal path is the one that failed silently before commit 560e501: an unawaited call
        /// meant a failing renewal produced no error at all and the calls import just stopped. A
        /// permission failure here must be reported as CRITICAL, name the exact Graph permission an
        /// admin has to grant, and still propagate so Program.cs's TrackException runs.
        /// </summary>
        [TestMethod]
        public async Task CreateOrUpdateWebhook_WhenRenewalIsForbidden_ReportsTheMissingPermissionAndRethrows()
        {
            var logger = new CapturingLogger();
            var subscriptions = new FakeCallRecordSubscriptionManager()
                .WithExistingSubscription("existing-sub-id", Now.AddHours(6))
                .FailingRenewWith(GraphError(403, "Insufficient privileges to complete the operation."));

            var webhook = new CallWebhook(subscriptions, logger, new FixedClock(Now));

            await Assert.ThrowsExceptionAsync<ODataError>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, "the-client-state"));

            var critical = logger.Entries.Where(e => e.Level == LogLevel.Critical).ToList();
            Assert.AreEqual(1, critical.Count, "A failed renewal must produce exactly one actionable critical log.");
            StringAssert.Contains(critical[0].Message, "CallRecords.Read.All", "The operator needs to be told which permission to grant.");
            StringAssert.Contains(critical[0].Message, "renew subscription 'existing-sub-id'", "The message must say which operation failed, and on which subscription.");
            StringAssert.Contains(critical[0].Message, "403 Forbidden");
        }

        /// <summary>
        /// A non-permission Graph failure is still fatal to the calls import, so it must also be
        /// reported and re-thrown - but without wrongly telling the admin to grant a permission they
        /// have already granted.
        /// </summary>
        /// <remarks>
        /// The example here used to be "Subscription validation request failed." That message is now
        /// classified and diagnosed specifically (issue #273), so it is no longer an example of the
        /// generic path. Using a genuinely unrelated Graph failure keeps this test honest.
        /// </remarks>
        [TestMethod]
        public async Task CreateOrUpdateWebhook_WhenCreateFailsForAnotherReason_ReportsItWithoutBlamingPermissions()
        {
            var logger = new CapturingLogger();
            var subscriptions = new FakeCallRecordSubscriptionManager()
                .FailingCreateWith(GraphError(429, "Too many requests. Please retry later."));

            var webhook = new CallWebhook(subscriptions, logger, new FixedClock(Now));

            await Assert.ThrowsExceptionAsync<ODataError>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, "the-client-state"));

            var critical = logger.Entries.Where(e => e.Level == LogLevel.Critical).Single();
            StringAssert.Contains(critical.Message, "Too many requests. Please retry later.");
            Assert.IsFalse(critical.Message.Contains("CallRecords.Read.All"),
                "Only a 403 should point the admin at the Graph permission; saying so for every failure sends them down the wrong path.");
            Assert.IsFalse(critical.Message.Contains("called BACK"),
                "An unrelated Graph failure must not be diagnosed as a validation-callback problem.");
        }

        #region Validation-callback failure diagnosis (issue #273)

        /// <summary>
        /// The failure actually observed in production: Graph accepts the request, calls back to our
        /// notification endpoint to validate it, and something in front of that endpoint refuses the
        /// call. Graph reports this as a 400 whose message contains the word "Forbidden" - which is what
        /// OUR endpoint returned to Graph, not the tenant refusing a Graph permission.
        ///
        /// Without the diagnosis this produced a generic "couldn't create subscription" critical with no
        /// next step, and the word "Forbidden" sent admins to re-check the app registration's Graph
        /// permissions, which is the wrong place entirely.
        /// </summary>
        [TestMethod]
        public async Task CreateOrUpdateWebhook_WhenGraphsValidationCallbackIsRefused_ExplainsItIsOurEndpointNotAPermission()
        {
            var logger = new CapturingLogger();
            var subscriptions = new FakeCallRecordSubscriptionManager()
                .FailingCreateWith(GraphError(400,
                    "Subscription validation request failed. HTTP status code is 'Forbidden'. " +
                    "Notification endpoint must respond with 200 OK to validation request."));

            var webhook = new CallWebhook(subscriptions, logger, new FixedClock(Now));

            await Assert.ThrowsExceptionAsync<ODataError>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, "the-client-state"));

            var critical = logger.Entries.Where(e => e.Level == LogLevel.Critical).Single();

            StringAssert.Contains(critical.Message, "NOT a Graph permission problem",
                "The whole point is to stop admins re-checking permissions because the word 'Forbidden' appears.");
            Assert.IsFalse(critical.Message.Contains("CallRecords.Read.All"),
                "Naming the permission here is exactly the wrong turn this diagnosis exists to prevent.");
            StringAssert.Contains(critical.Message, WebhookUrl.ToString(),
                "The admin needs to see which URL Graph tried to call back.");
            StringAssert.Contains(critical.Message, "ANONYMOUSLY",
                "An auth layer in front of the endpoint is one of the two most likely causes.");
            StringAssert.Contains(critical.Message, "access restrictions",
                "An IP allow-list / private endpoint is the other most likely cause.");
            StringAssert.Contains(critical.Message, "'Forbidden'",
                "The status our endpoint gave Graph is the most useful single detail; it must survive into the message.");
            StringAssert.Contains(critical.Message, "Until this is fixed, NO Teams call records will be imported.",
                "The impact line must be kept - silent data loss is the reason this is CRITICAL.");
        }

        /// <summary>
        /// The other observed variant. A timeout points at cold start, not at a network block, so it
        /// must NOT tell the admin to go looking at IP restrictions and auth layers.
        /// </summary>
        [TestMethod]
        public async Task CreateOrUpdateWebhook_WhenGraphsValidationCallbackTimesOut_PointsAtColdStartNotAtNetworkBlocks()
        {
            var logger = new CapturingLogger();
            var subscriptions = new FakeCallRecordSubscriptionManager()
                .WithExistingSubscription("existing-sub-id", Now.AddHours(6))
                .FailingRenewWith(GraphError(400, "Subscription validation request timed out."));

            var webhook = new CallWebhook(subscriptions, logger, new FixedClock(Now));

            await Assert.ThrowsExceptionAsync<ODataError>(() => webhook.CreateOrUpdateWebhook(WebhookUrl, "the-client-state"));

            var critical = logger.Entries.Where(e => e.Level == LogLevel.Critical).Single();

            StringAssert.Contains(critical.Message, "Always On",
                "Cold start is the usual cause of the timeout variant, and Always On is the fix.");
            StringAssert.Contains(critical.Message, "renew subscription 'existing-sub-id'",
                "The message must still say which operation failed, and on which subscription.");
            Assert.IsFalse(critical.Message.Contains("access restrictions"),
                "A timeout is not an IP-restriction symptom; sending the admin there wastes the diagnosis.");
            Assert.IsFalse(critical.Message.Contains("CallRecords.Read.All"));
        }

        /// <summary>
        /// The classifier is a pure rule, so pin its edges directly: both Graph variants, the status
        /// extraction, and - most importantly - that it does NOT fire on unrelated messages. A
        /// false positive would replace a correct permission diagnosis with a wrong network one.
        /// </summary>
        [TestMethod]
        public void ClassifyValidationCallbackFailure_RecognisesGraphsVariantsAndNothingElse()
        {
            var refused = CallSubscriptionRules.ClassifyValidationCallbackFailure(
                "Subscription validation request failed. HTTP status code is 'Forbidden'. " +
                "Notification endpoint must respond with 200 OK to validation request.");
            Assert.IsTrue(refused.IsValidationCallbackFailure);
            Assert.IsFalse(refused.TimedOut);
            Assert.AreEqual("Forbidden", refused.EndpointStatus);

            var timedOut = CallSubscriptionRules.ClassifyValidationCallbackFailure("Subscription validation request timed out.");
            Assert.IsTrue(timedOut.IsValidationCallbackFailure);
            Assert.IsTrue(timedOut.TimedOut);
            Assert.IsNull(timedOut.EndpointStatus, "There is no endpoint status to report when nothing responded.");

            var noStatusNamed = CallSubscriptionRules.ClassifyValidationCallbackFailure(
                "Subscription validation request failed. Notification endpoint must respond with 200 OK to validation request.");
            Assert.IsTrue(noStatusNamed.IsValidationCallbackFailure);
            Assert.IsNull(noStatusNamed.EndpointStatus, "Graph does not always name a status; the diagnosis must cope.");

            Assert.IsFalse(CallSubscriptionRules.ClassifyValidationCallbackFailure(
                "Insufficient privileges to complete the operation.").IsValidationCallbackFailure,
                "A real permission failure must keep its own, different diagnosis.");
            Assert.IsFalse(CallSubscriptionRules.ClassifyValidationCallbackFailure(null).IsValidationCallbackFailure);
            Assert.IsFalse(CallSubscriptionRules.ClassifyValidationCallbackFailure(string.Empty).IsValidationCallbackFailure);
        }

        #endregion

        /// <summary>
        /// A tenant can hold call-records subscriptions belonging to other applications, and this
        /// deployment can share a tenant with another install. Matching on both the resource and the
        /// notification URL is what stops us renewing - or reporting on - somebody else's webhook.
        /// </summary>
        [TestMethod]
        public void IsCallRecordsSubscriptionFor_MatchesOnlyOurResourceAndOurUrl()
        {
            Assert.IsTrue(CallSubscriptionRules.IsCallRecordsSubscriptionFor("/communications/callRecords", WebhookUrl.ToString(), WebhookUrl));
            Assert.IsFalse(CallSubscriptionRules.IsCallRecordsSubscriptionFor("/users", WebhookUrl.ToString(), WebhookUrl),
                "A subscription on another resource is not ours.");
            Assert.IsFalse(CallSubscriptionRules.IsCallRecordsSubscriptionFor("/communications/callRecords", "https://another-install.example/api/CallRecordWebhook", WebhookUrl),
                "A call-records subscription pointing at another deployment is not ours to renew.");
        }

        /// <summary>
        /// When several subscriptions match, the status page must report the one expiring LAST - that is
        /// the one actually keeping the webhook alive. Reporting an older one would show an operator an
        /// expiry that has already passed on a perfectly healthy deployment.
        /// </summary>
        [TestMethod]
        public async Task GetCallRecordsSubscriptionInfo_ReportsTheLatestExpiringSubscription()
        {
            var subscriptions = new FakeCallRecordSubscriptionManager()
                .WithExistingSubscription("older", Now.AddHours(1))
                .WithExistingSubscription("newest", Now.AddDays(2))
                .WithExistingSubscription("no-expiry-known", null);

            var webhook = new CallWebhook(subscriptions, new CapturingLogger(), new FixedClock(Now));

            var info = await webhook.GetCallRecordsSubscriptionInfo(WebhookUrl);

            Assert.IsTrue(info.Exists);
            Assert.AreEqual("newest", info.SubscriptionId);
            Assert.AreEqual(new DateTimeOffset(Now.AddDays(2)), info.ExpirationDateTime);
        }

        /// <summary>No matching subscription must be reported as "missing", not as an error.</summary>
        [TestMethod]
        public async Task GetCallRecordsSubscriptionInfo_WithNoSubscription_ReportsItAsMissing()
        {
            var webhook = new CallWebhook(new FakeCallRecordSubscriptionManager(), new CapturingLogger(), new FixedClock(Now));

            var info = await webhook.GetCallRecordsSubscriptionInfo(WebhookUrl);

            Assert.IsFalse(info.Exists);
            Assert.IsNull(info.SubscriptionId);
            Assert.IsNull(info.ExpirationDateTime);
        }

        #endregion

        #region Webhook notifications -> queue

        /// <summary>
        /// The webhook endpoint is anonymous - Graph has to be able to POST to it - so the clientState
        /// echoed back is its only authentication. A notification with the wrong (or missing)
        /// clientState must never reach the queue, or anyone who knows the URL could inject calls.
        /// </summary>
        [TestMethod]
        public void SelectValidNotifications_DropsNotificationsWhoseClientStateDoesNotMatch()
        {
            var notifications = new List<GraphChangeNotification>
            {
                new GraphChangeNotification { ClientState = "the-secret", ResourceData = new ResourceData { Id = "call-1" } },
                new GraphChangeNotification { ClientState = "not-the-secret", ResourceData = new ResourceData { Id = "spoofed" } },
                new GraphChangeNotification { ClientState = null, ResourceData = new ResourceData { Id = "no-state" } },
                new GraphChangeNotification { ClientState = "THE-SECRET", ResourceData = new ResourceData { Id = "wrong-case" } },
            };

            var selection = CallNotificationRules.SelectValidNotifications(notifications, "the-secret");

            CollectionAssert.AreEqual(new[] { "call-1" }, selection.Valid.Select(n => n.ResourceData.Id).ToArray());
            Assert.AreEqual(3, selection.InvalidCount, "Wrong, missing and differently-cased client states must all be rejected and counted.");
        }

        /// <summary>
        /// Every accepted notification must reach the queue, including one Graph sent without a call id
        /// (which the importer deliberately queues anyway rather than dropping on the floor), and the
        /// queued body must be the notification itself - a mangled payload would fail to deserialise on
        /// the other side and dead-letter the message.
        /// </summary>
        [TestMethod]
        public async Task AddChangeMsgToQueue_QueuesEveryNotificationIncludingOneWithNoCallId()
        {
            var queue = new InMemoryCallNotificationQueueSender();
            var changes = new List<GraphChangeNotification>
            {
                new GraphChangeNotification { ClientState = "s", ResourceData = new ResourceData { Id = "call-1" } },
                new GraphChangeNotification { ClientState = "s", ResourceData = new ResourceData { Id = string.Empty } },
            };

            await CallQueueProcessor.AddChangeMsgToQueue(changes, new CapturingLogger(), queue);

            Assert.AreEqual(2, queue.SentMessages.Count);

            // Round-trip BOTH bodies: asserting only the first would let a change that queued a
            // placeholder for the id-less notification still pass, and an unparseable body just
            // dead-letters on the other side.
            var first = JsonConvert.DeserializeObject<GraphChangeNotification>(queue.SentMessages[0]);
            var second = JsonConvert.DeserializeObject<GraphChangeNotification>(queue.SentMessages[1]);
            Assert.AreEqual("call-1", first.ResourceData.Id, "The queued body must round-trip back to the same notification.");
            Assert.AreEqual(string.Empty, second.ResourceData.Id, "The id-less notification must be queued as itself, not as a placeholder.");
        }

        /// <summary>
        /// A queue send that fails must propagate. The webhook controller turns that into a 500 so
        /// Graph re-delivers the notification; swallowing it would return 200 and lose the call
        /// silently. The fake fails as a faulted task, so this also pins that the send is actually
        /// awaited - a dropped await compiles here and would make the failure invisible.
        /// </summary>
        [TestMethod]
        public async Task AddChangeMsgToQueue_WhenTheSendFails_PropagatesSoTheWebhookCanReportFailure()
        {
            var queue = new FailingCallNotificationQueueSender(new ServiceBusException("namespace unreachable", ServiceBusFailureReason.ServiceCommunicationProblem));
            var changes = new List<GraphChangeNotification>
            {
                new GraphChangeNotification { ClientState = "s", ResourceData = new ResourceData { Id = "call-1" } },
            };

            await Assert.ThrowsExceptionAsync<ServiceBusException>(() =>
                CallQueueProcessor.AddChangeMsgToQueue(changes, new CapturingLogger(), queue));

            Assert.AreEqual(1, queue.SendAttempts);
        }

        #endregion

        #region Queue message -> saved call record

        private static CallRecordDTO Call(string graphCallId, string organiserEmail)
        {
            return new CallRecordDTO { GraphCallID = graphCallId, OrganizerEmail = organiserEmail };
        }

        private static GraphChangeNotification Notification(string callId)
        {
            return new GraphChangeNotification { ResourceData = new ResourceData { Id = callId } };
        }

        [TestMethod]
        public async Task CallRecordImporter_SavesACallThatHasAnOrganiser()
        {
            var source = new FakeCallRecordSourceLoader().Returning("call-1", Call("call-1", "someone@contoso.local"));
            var store = new InMemoryCallRecordPersistenceManager();
            var importer = new CallRecordImporter(source, store, new CapturingLogger());

            var result = await importer.ImportFromNotification(Notification("call-1"));

            Assert.IsNotNull(result);
            Assert.AreEqual(1, store.Saved.Count);
            Assert.AreEqual("call-1", store.Saved[0].GraphCallID);
        }

        /// <summary>
        /// A notification with no call id is unprocessable, so the importer must not go to Graph or the
        /// database for it - and must report failure so the queue message is abandoned rather than
        /// silently completed.
        /// </summary>
        [TestMethod]
        public async Task CallRecordImporter_WithNoCallIdInTheNotification_TouchesNeitherGraphNorTheDatabase()
        {
            var source = new FakeCallRecordSourceLoader();
            var store = new InMemoryCallRecordPersistenceManager();
            var importer = new CallRecordImporter(source, store, new CapturingLogger());

            var result = await importer.ImportFromNotification(Notification(string.Empty));

            Assert.IsNull(result);
            Assert.AreEqual(0, source.RequestedCallIds.Count);
            Assert.AreEqual(0, store.Saved.Count);
        }

        /// <summary>
        /// A call Graph can't return (throttled, deleted, not replicated yet) must NOT be reported as
        /// processed: returning null is what makes the caller abandon the queue message so it is
        /// retried instead of lost.
        /// </summary>
        [TestMethod]
        public async Task CallRecordImporter_WhenGraphCannotLoadTheCall_ReportsFailureSoTheMessageIsRetried()
        {
            var source = new FakeCallRecordSourceLoader();      // knows about no calls at all
            var store = new InMemoryCallRecordPersistenceManager();
            var importer = new CallRecordImporter(source, store, new CapturingLogger());

            var result = await importer.ImportFromNotification(Notification("call-that-graph-wont-return"));

            Assert.IsNull(result);
            CollectionAssert.AreEqual(new[] { "call-that-graph-wont-return" }, source.RequestedCallIds.ToArray());
            Assert.AreEqual(0, store.Saved.Count);
        }

        /// <summary>
        /// Pre-existing behaviour, pinned deliberately: a call whose organiser can't be resolved is NOT
        /// saved (the organiser is a required foreign key on the call record) but IS reported as
        /// processed, so the queue message is completed rather than retried forever. Changing either
        /// half of that would either block the queue or start writing incomplete call records.
        /// </summary>
        [TestMethod]
        public async Task CallRecordImporter_WithNoOrganiserEmail_DoesNotSaveButStillReportsSuccess()
        {
            var source = new FakeCallRecordSourceLoader().Returning("call-2", Call("call-2", null));
            var store = new InMemoryCallRecordPersistenceManager();
            var importer = new CallRecordImporter(source, store, new CapturingLogger());

            var result = await importer.ImportFromNotification(Notification("call-2"));

            Assert.IsNotNull(result, "Returning null here would make the queue retry a call that can never succeed.");
            Assert.AreEqual(0, store.Saved.Count, "A call with no organiser must not be written - Organizer is a required relationship.");
        }

        /// <summary>
        /// A failing save must propagate. CallQueueProcessor catches it, reports the import as
        /// unsuccessful and ABANDONS the Service Bus message so the call is retried; swallowing it
        /// would complete the message and lose the call record permanently. The fake fails as a
        /// faulted task, so this also pins that the save is actually awaited - dropping the await
        /// compiles, and would report success before the save had even run.
        /// </summary>
        [TestMethod]
        public async Task CallRecordImporter_WhenTheSaveFails_PropagatesSoTheQueueMessageIsRetried()
        {
            var source = new FakeCallRecordSourceLoader().Returning("call-3", Call("call-3", "someone@contoso.local"));
            var store = new InMemoryCallRecordPersistenceManager().FailingWith(new InvalidOperationException("simulated SQL failure"));
            var importer = new CallRecordImporter(source, store, new CapturingLogger());

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => importer.ImportFromNotification(Notification("call-3")));
        }

        #endregion

        /// <summary>Minimal <see cref="ILogger"/> that records level and formatted message for assertions.</summary>
        private class CapturingLogger : ILogger
        {
            public class Entry
            {
                public LogLevel Level { get; set; }
                public string Message { get; set; }
            }

            public List<Entry> Entries { get; } = new List<Entry>();

            public IDisposable BeginScope<TState>(TState state) => new NoopScope();

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                Entries.Add(new Entry { Level = logLevel, Message = formatter(state, exception) });
            }

            private class NoopScope : IDisposable
            {
                public void Dispose() { }
            }
        }
    }
}
