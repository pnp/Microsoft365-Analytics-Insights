using Azure.Messaging.ServiceBus;
using Common.Entities.Calls;
using Common.Entities.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity.Core;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph;
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

            await CallNotificationDispatcher.AddChangeMsgToQueue(changes, new CapturingLogger(), queue);

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
                CallNotificationDispatcher.AddChangeMsgToQueue(changes, new CapturingLogger(), queue));

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
        /// database for it - and must not report it as processed. (The queue handler dead-letters such a
        /// message before it reaches the importer: see
        /// <see cref="QueueHandler_ANotificationThatCanNeverBeImported_IsDeadLetteredAtOnceWithTheReason"/>.)
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
        /// processed: returning null is what makes the queue handler retry the message later instead of
        /// completing it and losing the call.
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
        /// A failing save must propagate. The queue handler catches it, reports the import as
        /// unsuccessful and re-queues the notification for a later retry; swallowing it would complete
        /// the message and lose the call record permanently. The fake fails as a faulted task, so this
        /// also pins that the save is actually awaited - dropping the await compiles, and would report
        /// success before the save had even run.
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

        #region Every page of a call's sessions

        /// <summary>
        /// Graph returns at most 60 sessions inline and links to the rest. When the only in-scope attendee joined on
        /// the second page, reading just the first page made the call look out of scope: it was skipped and its queue
        /// message completed, so it was lost. Every page must be read - before the email look-ups and the scope check.
        /// </summary>
        [TestMethod]
        public async Task CallRecordImporter_ReadsEverySessionsPage_SoAnInScopeAttendeeOnPageTwoKeepsTheCall()
        {
            var call = new SyntheticMultiPageCall();
            var graph = call.CreateGraph();
            var store = new InMemoryCallRecordPersistenceManager();
            var importer = new CallRecordImporter(SyntheticMultiPageCall.CreateSourceLoader(graph), store, new CapturingLogger(),
                TestUserScopes.Provider(TestUserScopes.Of(call.LateAttendeeUpn)));

            var result = await importer.ImportFromNotification(Notification(call.CallId));

            Assert.IsNotNull(result);
            Assert.AreEqual(1, store.Saved.Count, "The only in-scope attendee is on the second page of sessions.");
            Assert.AreEqual(call.TotalSessions, store.Saved[0].Sessions.Count, "Every session, from both pages, is passed to the save.");
            Assert.AreEqual(call.LateAttendeeUpn, store.Saved[0].Sessions.Last().Callee.UserEmailAddress, "Email addresses are resolved on the later page too.");
            CollectionAssert.AreEqual(new[] { new Uri(call.CallRecordUrl).AbsoluteUri, new Uri(call.SecondPageUrl).AbsoluteUri }, graph.CallRecordRequests,
                "The call record, then its one continuation page.");
            Assert.AreEqual(call.TotalSessions + 1, graph.Requests.Count(r => r.Contains("/v1.0/users/")),
                "One look-up per person: the organiser is on every session but is looked up once.");
        }

        /// <summary>
        /// Graph's documentation shows the continuation link as ".../v1.0/$metadata#communications/callRecords('id')/sessions?...".
        /// Requested as it stands, everything after '#' is dropped and the metadata document comes back instead of the
        /// page, so the link must be turned into the resource URL it names.
        /// </summary>
        [TestMethod]
        public async Task GraphCallRecordSourceLoader_FollowsTheDocumentedMetadataShapedSessionsLink()
        {
            var call = new SyntheticMultiPageCall(documentedNextLinkShape: true);
            var graph = call.CreateGraph();

            var loaded = await SyntheticMultiPageCall.CreateSourceLoader(graph).LoadCallRecord(call.CallId);

            Assert.AreEqual(call.TotalSessions, loaded.Sessions.Count);
            Assert.AreEqual(new Uri(call.SecondPageUrl).AbsoluteUri, graph.CallRecordRequests.Last());
        }

        /// <summary>
        /// A continuation link is followed with the importer's Graph token, so it must stay on Graph's host.
        /// </summary>
        [TestMethod]
        public void SessionsContinuationLink_ToAnotherHost_IsRefusedAsNotRetryable()
        {
            var ex = Assert.ThrowsException<CallRecordSessionPagingException>(() => CallRecordSessionPaging.ResolveContinuationUrl(
                "https://contoso.example/v1.0/communications/callRecords('c')/sessions?$skiptoken=x",
                "https://graph.microsoft.com/v1.0/communications/callRecords/c?$expand=sessions($expand=segments)", "c"));

            Assert.IsTrue(ex.IsPermanent);
        }

        /// <summary>
        /// A continuation page that fails must fail the whole load: saving the first page alone would store the call
        /// without the rest of its attendees, and under UserGroupsFilter could skip it altogether.
        /// </summary>
        [TestMethod]
        public async Task CallRecordImporter_WhenALaterSessionsPageFails_SavesNothingAndThrows()
        {
            var call = new SyntheticMultiPageCall();
            var graph = call.CreateGraph();
            graph.Fail(call.SecondPageUrl, HttpStatusCode.InternalServerError);
            var store = new InMemoryCallRecordPersistenceManager();
            var importer = new CallRecordImporter(SyntheticMultiPageCall.CreateSourceLoader(graph), store, new CapturingLogger());

            var ex = await Assert.ThrowsExceptionAsync<CallRecordSessionPagingException>(() => importer.ImportFromNotification(Notification(call.CallId)));

            Assert.IsFalse(ex.IsPermanent, "A page that failed to load may well load next time.");
            Assert.AreEqual(0, store.Saved.Count);
        }

        /// <summary>
        /// End to end through the queue handler: the failed page means the notification is retried later, not
        /// completed as processed.
        /// </summary>
        [TestMethod]
        public async Task QueueHandler_WhenALaterSessionsPageFails_RetriesTheCallLaterInsteadOfCompletingIt()
        {
            var call = new SyntheticMultiPageCall();
            var graph = call.CreateGraph();
            graph.Fail(call.SecondPageUrl, HttpStatusCode.InternalServerError);
            var store = new InMemoryCallRecordPersistenceManager();
            var importer = new CallRecordImporter(SyntheticMultiPageCall.CreateSourceLoader(graph), store, new CapturingLogger());
            var settlement = new FakeCallQueueMessageSettlement();

            var outcome = await QueueHandler(async n => (await importer.Import(n)).Outcome).HandleAsync(QueueMessage(call.CallId), settlement);

            Assert.AreEqual(CallQueueMessageOutcome.RetryScheduled, outcome);
            CollectionAssert.AreEqual(new[] { FakeCallQueueMessageSettlement.ScheduleRetry, FakeCallQueueMessageSettlement.Complete }, settlement.Operations,
                "The message is only completed once a retry of it is queued.");
            Assert.AreEqual(1, settlement.ScheduledState.Failures);
            Assert.AreEqual(0, store.Saved.Count);
        }

        /// <summary>
        /// Paging that never ends must fail loudly - dead-lettered with the reason, since the same call would page the
        /// same way again - rather than loop for ever or save whatever had been read when it was cut off.
        /// </summary>
        [TestMethod]
        public async Task QueueHandler_WhenSessionsPagingRunsAway_DeadLettersTheCallRatherThanSavingPartOfIt()
        {
            var call = new SyntheticMultiPageCall();
            var graph = call.CreateGraph();
            graph.Serve(call.SecondPageUrl, call.SecondPageJson(nextLink: call.SecondPageUrl + "-more"));
            graph.Generate = uri => uri.AbsoluteUri.Contains("/sessions") ? call.SecondPageJson(nextLink: uri.AbsoluteUri + "-more") : null;
            var store = new InMemoryCallRecordPersistenceManager();
            var importer = new CallRecordImporter(SyntheticMultiPageCall.CreateSourceLoader(graph), store, new CapturingLogger());
            var settlement = new FakeCallQueueMessageSettlement();

            var outcome = await QueueHandler(async n => (await importer.Import(n)).Outcome).HandleAsync(QueueMessage(call.CallId), settlement);

            Assert.AreEqual(CallQueueMessageOutcome.DeadLettered, outcome);
            Assert.AreEqual(CallQueueMessageHandler.NotRetryableReason, settlement.DeadLetterReason);
            StringAssert.Contains(settlement.DeadLetterDescription, call.CallId);
            StringAssert.Contains(settlement.DeadLetterDescription, $"more than {CallRecordSessionPaging.MaxPages} pages");
            Assert.AreEqual(CallRecordSessionPaging.MaxPages, graph.CallRecordRequests.Count, "Stops at the cap.");
            Assert.AreEqual(0, store.Saved.Count);
        }

        /// <summary>A continuation link that repeats is a loop: stop at once instead of requesting it again.</summary>
        [TestMethod]
        public async Task CallRecordImporter_WhenGraphRepeatsASessionsLink_StopsWithANotRetryableFailure()
        {
            var call = new SyntheticMultiPageCall();
            var graph = call.CreateGraph();
            graph.Serve(call.SecondPageUrl, call.SecondPageJson(nextLink: call.SecondPageUrl));
            var importer = new CallRecordImporter(SyntheticMultiPageCall.CreateSourceLoader(graph), new InMemoryCallRecordPersistenceManager(), new CapturingLogger());

            var ex = await Assert.ThrowsExceptionAsync<CallRecordSessionPagingException>(() => importer.ImportFromNotification(Notification(call.CallId)));

            Assert.IsTrue(ex.IsPermanent);
            Assert.AreEqual(2, graph.CallRecordRequests.Count);
        }

        #endregion

        #region Queue message settlement and back-off

        private static readonly DateTime QueueNow = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);

        private static CallQueueMessage QueueMessage(string callId, string messageId = "message-1", Dictionary<string, object> properties = null)
        {
            return new CallQueueMessage
            {
                MessageId = messageId,
                Body = JsonConvert.SerializeObject(new GraphChangeNotification { ClientState = "the-client-state", ResourceData = new ResourceData { Id = callId } }),
                ApplicationProperties = properties ?? new Dictionary<string, object>()
            };
        }

        /// <summary>A handler on a fixed clock, whose pause waits move the clock on instead of sleeping.</summary>
        private static CallQueueMessageHandler QueueHandler(Func<GraphChangeNotification, Task<CallImportOutcome>> importCall, FixedClock clock = null,
            CallImportCircuitBreaker breaker = null, List<TimeSpan> waits = null)
        {
            clock = clock ?? new FixedClock(QueueNow);
            return new CallQueueMessageHandler(importCall, new CapturingLogger(), clock, CallQueueRetryPolicy.Default, breaker ?? new CallImportCircuitBreaker(),
                (wait, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    waits?.Add(wait);
                    clock.Advance(wait);
                    return Task.CompletedTask;
                });
        }

        private static Task<CallImportOutcome> Saved() => Task.FromResult(CallImportOutcome.Saved);

        private static Task<CallImportOutcome> NotImported() => Task.FromResult(CallImportOutcome.NotImported);

        private static Task<CallImportOutcome> Fails(Exception ex) => Task.FromException<CallImportOutcome>(ex);

        private static Exception SqlUnavailable() =>
            new EntityException("The underlying provider failed on Open.", new TimeoutException("Connection Timeout Expired."));

        /// <summary>What AutoThrottleHttpClient throws once it has run out of retries for a 504.</summary>
        private static Exception GatewayTimeoutAfterRetries() =>
            new HttpRequestException("Response status code does not indicate success: 504 (Gateway Timeout).");

        /// <summary>A breaker paused by two different calls failing as SQL does when it is down.</summary>
        private static CallImportCircuitBreaker PausedBreaker()
        {
            var breaker = new CallImportCircuitBreaker();
            breaker.RecordFailure(CallImportFailureKind.Systemic, "outage-call-1", wasProbe: false, QueueNow);
            breaker.RecordFailure(CallImportFailureKind.Systemic, "outage-call-2", wasProbe: false, QueueNow);
            Assert.IsTrue(breaker.IsPaused);
            return breaker;
        }

        /// <summary>
        /// Abandoning a failed message made Service Bus redeliver it at once, so the queue's 10 deliveries were spent
        /// within seconds and a minute-long SQL failover dead-lettered every call that arrived during it. A failure now
        /// queues a copy for later, with the bookkeeping on it, and only then completes the original.
        /// </summary>
        [TestMethod]
        public async Task QueueHandler_WhenAnImportFails_QueuesADelayedRetryInsteadOfAbandoning()
        {
            var settlement = new FakeCallQueueMessageSettlement();
            var handler = QueueHandler(n => Fails(new InvalidOperationException("simulated failure")));

            var outcome = await handler.HandleAsync(QueueMessage("call-1"), settlement);

            Assert.AreEqual(CallQueueMessageOutcome.RetryScheduled, outcome);
            CollectionAssert.AreEqual(new[] { FakeCallQueueMessageSettlement.ScheduleRetry, FakeCallQueueMessageSettlement.Complete }, settlement.Operations);
            Assert.AreEqual(QueueNow.AddMinutes(1), settlement.ScheduledForUtc);
            Assert.AreEqual(1, settlement.ScheduledState.Failures);
            Assert.AreEqual(1, settlement.ScheduledState.Retries);
            Assert.AreEqual(QueueNow, settlement.ScheduledState.FirstFailureUtc);
            Assert.AreEqual("message-1", settlement.ScheduledState.OriginalMessageId);
            StringAssert.Contains(settlement.ScheduledState.LastError, "simulated failure");
        }

        [TestMethod]
        public async Task QueueHandler_RetryDelaysDoubleUpToAnHour()
        {
            // Retries already made -> delay before the next one.
            var expectedMinutes = new Dictionary<int, int> { { 0, 1 }, { 1, 2 }, { 4, 16 }, { 5, 32 }, { 6, 60 }, { 8, 60 } };

            foreach (var expected in expectedMinutes)
            {
                var settlement = new FakeCallQueueMessageSettlement();
                var message = QueueMessage("call-1", properties: new Dictionary<string, object>
                {
                    [CallQueueRetryState.RetriesProperty] = expected.Key,
                    [CallQueueRetryState.FirstFailureUtcProperty] = QueueNow.AddMinutes(-90).ToString("o")
                });

                await QueueHandler(n => NotImported()).HandleAsync(message, settlement);

                Assert.AreEqual(QueueNow.AddMinutes(expected.Value), settlement.ScheduledForUtc, $"After {expected.Key} retries.");
                Assert.AreEqual(expected.Key + 1, settlement.ScheduledState.Retries);
            }
        }

        [TestMethod]
        public async Task QueueHandler_AfterItsLastAllowedFailure_DeadLettersTheCallWithTheReason()
        {
            var settlement = new FakeCallQueueMessageSettlement();
            var message = QueueMessage("call-1", properties: new Dictionary<string, object>
            {
                [CallQueueRetryState.FailuresProperty] = CallQueueRetryPolicy.Default.MaxFailures - 1,
                [CallQueueRetryState.RetriesProperty] = CallQueueRetryPolicy.Default.MaxFailures - 1,
                [CallQueueRetryState.FirstFailureUtcProperty] = QueueNow.AddHours(-4).ToString("o")
            });

            var outcome = await QueueHandler(n => Fails(new InvalidOperationException("simulated failure"))).HandleAsync(message, settlement);

            Assert.AreEqual(CallQueueMessageOutcome.DeadLettered, outcome);
            CollectionAssert.AreEqual(new[] { FakeCallQueueMessageSettlement.DeadLetter }, settlement.Operations);
            Assert.AreEqual(CallQueueRetryPolicy.RetriesExhaustedReason, settlement.DeadLetterReason);
            StringAssert.Contains(settlement.DeadLetterDescription, "call-1");
            StringAssert.Contains(settlement.DeadLetterDescription, "simulated failure", "The last error stays with the message for whoever replays the dead-letter queue.");
        }

        /// <summary>
        /// Redelivery can't fix a notification with no call id, or a body that isn't a notification, so they are
        /// dead-lettered with a reason on the first delivery instead of after ten.
        /// </summary>
        [TestMethod]
        public async Task QueueHandler_ANotificationThatCanNeverBeImported_IsDeadLetteredAtOnceWithTheReason()
        {
            var imports = 0;
            var handler = QueueHandler(n => { imports++; return Saved(); });

            var noCallId = new FakeCallQueueMessageSettlement();
            await handler.HandleAsync(QueueMessage(string.Empty), noCallId);
            var notANotification = new FakeCallQueueMessageSettlement();
            await handler.HandleAsync(new CallQueueMessage { MessageId = "message-2", Body = "this is not a notification" }, notANotification);

            CollectionAssert.AreEqual(new[] { FakeCallQueueMessageSettlement.DeadLetter }, noCallId.Operations);
            Assert.AreEqual(CallQueueMessageHandler.MissingCallIdReason, noCallId.DeadLetterReason);
            Assert.AreEqual(CallQueueMessageHandler.InvalidNotificationReason, notANotification.DeadLetterReason);
            Assert.AreEqual(0, imports);
        }

        [TestMethod]
        public async Task QueueHandler_WhenTheRetryCannotBeQueued_AbandonsTheMessageRatherThanCompletingIt()
        {
            var settlement = new FakeCallQueueMessageSettlement
            {
                ScheduleFailure = new ServiceBusException("namespace unreachable", ServiceBusFailureReason.ServiceCommunicationProblem)
            };

            var outcome = await QueueHandler(n => NotImported()).HandleAsync(QueueMessage("call-1"), settlement);

            Assert.AreEqual(CallQueueMessageOutcome.Abandoned, outcome);
            CollectionAssert.AreEqual(new[] { FakeCallQueueMessageSettlement.ScheduleRetry, FakeCallQueueMessageSettlement.Abandon }, settlement.Operations,
                "Completing it with no retry queued would lose the call.");
        }

        /// <summary>
        /// An outage must not burn through the backlog: once a second call fails the same way the queue pauses, the next
        /// call waits it out and goes through as a probe, and the failures aren't counted against the calls' own
        /// allowance of failed attempts.
        /// </summary>
        [TestMethod]
        public async Task QueueHandler_WhenSqlIsDown_PausesTheQueueAndResumesWhenAProbeIsSaved()
        {
            var clock = new FixedClock(QueueNow);
            var breaker = new CallImportCircuitBreaker();
            var waits = new List<TimeSpan>();
            var sqlDown = true;
            var importTimes = new List<DateTime>();
            var handler = QueueHandler(n =>
            {
                importTimes.Add(clock.UtcNow);
                return sqlDown ? Fails(SqlUnavailable()) : Saved();
            }, clock, breaker, waits);

            var first = new FakeCallQueueMessageSettlement();
            Assert.AreEqual(CallQueueMessageOutcome.RetryScheduled, await handler.HandleAsync(QueueMessage("call-1"), first));
            Assert.AreEqual(0, first.ScheduledState.Failures, "An outage isn't the call's fault, so it uses up none of its failed attempts...");
            Assert.AreEqual(1, first.ScheduledState.SystemicFailures, "...though it is counted on its own allowance.");
            Assert.IsFalse(breaker.IsPaused, "One call failing is not yet an outage.");

            Assert.AreEqual(CallQueueMessageOutcome.RetryScheduled, await handler.HandleAsync(QueueMessage("call-2", "message-2"), new FakeCallQueueMessageSettlement()));
            Assert.IsTrue(breaker.IsPaused, "A second call failing the same way is.");

            sqlDown = false;
            Assert.AreEqual(CallQueueMessageOutcome.Completed, await handler.HandleAsync(QueueMessage("call-3", "message-3"), new FakeCallQueueMessageSettlement()));

            CollectionAssert.AreEqual(new[] { CallImportCircuitBreaker.DefaultInitialPause }, waits, "The next call waits out the pause.");
            Assert.AreEqual(QueueNow + CallImportCircuitBreaker.DefaultInitialPause, importTimes.Last(), "...and is only tried when it ends.");
            Assert.IsFalse(breaker.IsPaused, "A call saved again resumes the queue.");
        }

        [TestMethod]
        public async Task QueueHandler_WhileTheOutageLasts_EachPauseIsLongerAndOnlyOneCallIsTriedPerPause()
        {
            var clock = new FixedClock(QueueNow);
            var waits = new List<TimeSpan>();
            var imports = 0;
            var handler = QueueHandler(n => { imports++; return Fails(SqlUnavailable()); }, clock, new CallImportCircuitBreaker(), waits);

            for (var i = 1; i <= 4; i++)
            {
                var settlement = new FakeCallQueueMessageSettlement();
                Assert.AreEqual(CallQueueMessageOutcome.RetryScheduled, await handler.HandleAsync(QueueMessage($"call-{i}", $"message-{i}"), settlement));
            }

            Assert.AreEqual(4, imports);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2) }, waits);
        }

        /// <summary>
        /// "Systemic" is judged from one error, and one call can produce such errors on its own - a 504 on its own
        /// oversized call record, say. It must never pause everyone else's imports, however often it is retried.
        /// </summary>
        [TestMethod]
        public async Task QueueHandler_OneCallFailingLikeAnOutageOnItsOwn_NeverPausesTheQueue()
        {
            var clock = new FixedClock(QueueNow);
            var breaker = new CallImportCircuitBreaker();
            var waits = new List<TimeSpan>();
            var handler = QueueHandler(n => n.ResourceData.Id == "oversized-call" ? Fails(GatewayTimeoutAfterRetries()) : Saved(), clock, breaker, waits);

            var properties = new Dictionary<string, object>();
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var retry = new FakeCallQueueMessageSettlement();
                Assert.AreEqual(CallQueueMessageOutcome.RetryScheduled, await handler.HandleAsync(QueueMessage("oversized-call", $"message-{attempt}", properties), retry));
                Assert.IsFalse(breaker.IsPaused, $"Attempt {attempt}: one call can't pause the queue.");
                Assert.AreEqual(attempt, retry.ScheduledState.SystemicFailures);

                properties = new Dictionary<string, object>();
                retry.ScheduledState.WriteTo(properties);
                clock.Advance(TimeSpan.FromMinutes(1));
            }

            Assert.AreEqual(CallQueueMessageOutcome.Completed, await handler.HandleAsync(QueueMessage("another-call", "message-6"), new FakeCallQueueMessageSettlement()));
            Assert.AreEqual(0, waits.Count, "No import ever waited for a pause.");
        }

        /// <summary>
        /// A call that keeps failing in a way that usually means an outage, while the queue carries on, has a problem of
        /// its own: it is given up on with a reason saying so after a bounded number of attempts, rather than retried
        /// for the full three days.
        /// </summary>
        [TestMethod]
        public async Task QueueHandler_ACallThatKeepsFailingLikeAnOutage_IsDeadLetteredWithItsOwnReason()
        {
            var clock = new FixedClock(QueueNow);
            var handler = QueueHandler(n => Fails(GatewayTimeoutAfterRetries()), clock);

            var properties = new Dictionary<string, object>();
            FakeCallQueueMessageSettlement settlement = null;
            var attempts = 0;
            while (attempts < 200)
            {
                attempts++;
                settlement = new FakeCallQueueMessageSettlement();
                var outcome = await handler.HandleAsync(QueueMessage("oversized-call", $"message-{attempts}", properties), settlement);
                if (outcome == CallQueueMessageOutcome.DeadLettered)
                {
                    break;
                }

                // Deliver the retry when it is due, carrying its bookkeeping.
                properties = new Dictionary<string, object>();
                settlement.ScheduledState.WriteTo(properties);
                clock.Advance(settlement.ScheduledForUtc.Value - clock.UtcNow);
            }

            Assert.AreEqual(CallQueueRetryPolicy.SystemicRetriesExhaustedReason, settlement.DeadLetterReason,
                "Given up on for failing like an outage too often - not left to run out the three-day window.");
            Assert.AreEqual(3 * CallQueueRetryPolicy.Default.MaxFailures, attempts);
            Assert.IsTrue(clock.UtcNow - QueueNow < TimeSpan.FromDays(2), $"Given up on after {clock.UtcNow - QueueNow}.");
            StringAssert.Contains(settlement.DeadLetterDescription, "oversized-call");
            StringAssert.Contains(settlement.DeadLetterDescription, "504");
        }

        /// <summary>
        /// A call processed without being saved - nobody on it in UserGroupsFilter - only read Graph. During a SQL outage
        /// it is not evidence that SQL is back: it is completed (it needs nothing more), but the queue stays paused, and the
        /// in-scope calls that do fail on SQL make the pause longer.
        /// </summary>
        [TestMethod]
        public async Task QueueHandler_DuringASqlOutage_AnOutOfScopeProbeLeavesTheQueuePaused()
        {
            const string pilot = "pilot@contoso.com";
            CallRecordDTO CallWith(string callId, string attendee) => new CallRecordDTO
            {
                GraphCallID = callId,
                OrganizerEmail = "organiser@contoso.com",
                Sessions = new List<CallSessionDTO>
                {
                    new CallSessionDTO
                    {
                        Caller = new ParticipantEndpointDTO { UserEmailAddress = "organiser@contoso.com" },
                        Callee = new ParticipantEndpointDTO { UserEmailAddress = attendee }
                    }
                }
            };

            var source = new FakeCallRecordSourceLoader()
                .Returning("call-1", CallWith("call-1", pilot))
                .Returning("call-2", CallWith("call-2", pilot))
                .Returning("out-of-scope-call", CallWith("out-of-scope-call", "outsider@contoso.com"))
                .Returning("call-3", CallWith("call-3", pilot));
            var store = new InMemoryCallRecordPersistenceManager().FailingWith(SqlUnavailable());
            var importer = new CallRecordImporter(source, store, new CapturingLogger(), TestUserScopes.Provider(TestUserScopes.Of(pilot)));
            var clock = new FixedClock(QueueNow);
            var breaker = new CallImportCircuitBreaker();
            var handler = QueueHandler(async n => (await importer.Import(n)).Outcome, clock, breaker);

            await handler.HandleAsync(QueueMessage("call-1"), new FakeCallQueueMessageSettlement());
            await handler.HandleAsync(QueueMessage("call-2", "message-2"), new FakeCallQueueMessageSettlement());
            Assert.IsTrue(breaker.IsPaused);

            var probe = new FakeCallQueueMessageSettlement();
            Assert.AreEqual(CallQueueMessageOutcome.Completed, await handler.HandleAsync(QueueMessage("out-of-scope-call", "message-3"), probe),
                "Out of scope, so there is nothing to save or retry.");
            Assert.IsTrue(breaker.IsPaused, "It never touched SQL, so it proves nothing about the outage.");

            await handler.HandleAsync(QueueMessage("call-3", "message-4"), new FakeCallQueueMessageSettlement());
            Assert.AreEqual(TimeSpan.FromMinutes(2), breaker.TryEnter(clock.UtcNow, out _), "The next in-scope call fails on SQL, and the pause grows.");
            Assert.AreEqual(0, store.Saved.Count);
        }

        /// <summary>
        /// When SQL drops, the imports already running all fail together. That is one outage, so it starts one short
        /// pause rather than escalating straight to the longest - and the first call alone starts none.
        /// </summary>
        [TestMethod]
        public void CircuitBreaker_ImportsAlreadyRunningWhenAPauseStarts_DoNotLengthenIt()
        {
            var breaker = new CallImportCircuitBreaker();

            Assert.IsNull(breaker.RecordFailure(CallImportFailureKind.Systemic, "call-0", wasProbe: false, QueueNow));
            Assert.AreEqual(CallImportCircuitBreaker.DefaultInitialPause, breaker.RecordFailure(CallImportFailureKind.Systemic, "call-1", wasProbe: false, QueueNow));
            for (var i = 2; i < 10; i++)
            {
                Assert.IsNull(breaker.RecordFailure(CallImportFailureKind.Systemic, $"call-{i}", wasProbe: false, QueueNow.AddSeconds(i)));
            }

            Assert.AreEqual(TimeSpan.FromSeconds(30), breaker.TryEnter(QueueNow.AddSeconds(30), out var isProbe), "Still the first pause, ending a minute after it began.");
            Assert.AreEqual(TimeSpan.Zero, breaker.TryEnter(QueueNow.AddMinutes(1), out isProbe));
            Assert.IsTrue(isProbe, "Once it ends, one import is let through to test the water.");
            Assert.AreEqual(CallImportCircuitBreaker.ProbePollInterval, breaker.TryEnter(QueueNow.AddMinutes(1), out isProbe), "...and only one.");
        }

        /// <summary>
        /// Calls with problems of their own, failing now and then while others are saved, are not an outage: a save in
        /// between means SQL and Graph work, so their failures never add up to a pause.
        /// </summary>
        [TestMethod]
        public void CircuitBreaker_FailuresSeparatedByASavedCall_DoNotPauseTheQueue()
        {
            var breaker = new CallImportCircuitBreaker();

            Assert.IsNull(breaker.RecordFailure(CallImportFailureKind.Systemic, "bad-call-1", wasProbe: false, QueueNow));
            breaker.RecordSaved();
            Assert.IsNull(breaker.RecordFailure(CallImportFailureKind.Systemic, "bad-call-2", wasProbe: false, QueueNow.AddSeconds(30)));

            Assert.IsFalse(breaker.IsPaused);
        }

        [TestMethod]
        public async Task QueueHandler_WhenTheProcessorStopsWhilePaused_HandsTheMessageBackUntried()
        {
            var breaker = PausedBreaker();
            var imports = 0;
            var settlement = new FakeCallQueueMessageSettlement();

            using (var stopping = new CancellationTokenSource())
            {
                stopping.Cancel();
                var outcome = await QueueHandler(n => { imports++; return Saved(); }, breaker: breaker)
                    .HandleAsync(QueueMessage("call-1"), settlement, stopping.Token);

                Assert.AreEqual(CallQueueMessageOutcome.Abandoned, outcome);
            }

            CollectionAssert.AreEqual(new[] { FakeCallQueueMessageSettlement.Abandon }, settlement.Operations);
            Assert.AreEqual(0, imports);
        }

        /// <summary>
        /// Only failures every call would hit may pause the queue. A Graph 404 wraps the framework's
        /// HttpRequestException, which on its own means the transport failed - the status must decide.
        /// </summary>
        [TestMethod]
        public void FailureClassifier_TellsOutagesFromProblemsWithOneCall()
        {
            const string url = "https://graph.microsoft.com/v1.0/communications/callRecords/c";
            Func<HttpStatusCode, GraphHttpException> graphError = status =>
                new GraphHttpException(status, url, "{}", new HttpRequestException($"Response status code does not indicate success: {(int)status}."));

            Assert.AreEqual(CallImportFailureKind.Transient, CallImportFailureClassifier.Classify(
                new GraphResourceNotFoundException(url, "{}", new HttpRequestException("Response status code does not indicate success: 404 (Not Found)."))));
            Assert.AreEqual(CallImportFailureKind.Transient, CallImportFailureClassifier.Classify(graphError(HttpStatusCode.InternalServerError)));
            Assert.AreEqual(CallImportFailureKind.Transient, CallImportFailureClassifier.Classify(new InvalidOperationException("something about this call")));
            Assert.AreEqual(CallImportFailureKind.Transient, CallImportFailureClassifier.Classify(
                new CallRecordSessionPagingException("c", "page 2 failed", false, graphError(HttpStatusCode.InternalServerError))));

            Assert.AreEqual(CallImportFailureKind.Systemic, CallImportFailureClassifier.Classify(graphError(HttpStatusCode.Forbidden)));
            Assert.AreEqual(CallImportFailureKind.Systemic, CallImportFailureClassifier.Classify(
                new CallRecordSessionPagingException("c", "page 2 failed", false, graphError(HttpStatusCode.Unauthorized))));
            Assert.AreEqual(CallImportFailureKind.Systemic, CallImportFailureClassifier.Classify(new ODataError { ResponseStatusCode = 429 }));
            Assert.AreEqual(CallImportFailureKind.Systemic, CallImportFailureClassifier.Classify(new HttpRequestException("No such host is known.")));
            Assert.AreEqual(CallImportFailureKind.Systemic, CallImportFailureClassifier.Classify(SqlUnavailable()));
            Assert.AreEqual(CallImportFailureKind.Systemic, CallImportFailureClassifier.Classify(new TimeoutException("Execution Timeout Expired.")));

            Assert.AreEqual(CallImportFailureKind.Permanent, CallImportFailureClassifier.Classify(new CallRecordSessionPagingException("c", "too many pages", true)));
        }

        /// <summary>
        /// The retry copy is a new message, so Service Bus counts its deliveries from one again: the bookkeeping has to
        /// travel on the message itself, and the notification must arrive unchanged.
        /// </summary>
        [TestMethod]
        public void RetryCopy_KeepsTheNotificationAndCarriesTheRetryStateAcrossTheQueue()
        {
            var body = JsonConvert.SerializeObject(new GraphChangeNotification { ResourceData = new ResourceData { Id = "call-1" } });
            var original = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString(body), messageId: "message-1",
                properties: new Dictionary<string, object> { ["custom"] = "kept" }, deliveryCount: 3);
            var state = new CallQueueRetryState { Failures = 2, Retries = 3, FirstFailureUtc = QueueNow, OriginalMessageId = "message-1", LastError = "boom" };

            var copy = ServiceBusCallQueueMessageSettlement.BuildRetryMessage(original, state);

            Assert.AreEqual(body, copy.Body.ToString());
            Assert.AreNotEqual("message-1", copy.MessageId, "Its own id, so duplicate detection could never discard it.");
            Assert.AreEqual("kept", copy.ApplicationProperties["custom"]);

            var redelivered = ServiceBusModelFactory.ServiceBusReceivedMessage(body: copy.Body, messageId: copy.MessageId, properties: copy.ApplicationProperties);
            var read = CallQueueRetryState.FromMessage(CallQueueMessage.From(redelivered));
            Assert.AreEqual(2, read.Failures);
            Assert.AreEqual(3, read.Retries);
            Assert.AreEqual(QueueNow, read.FirstFailureUtc);
            Assert.AreEqual("message-1", read.OriginalMessageId);
            Assert.AreEqual("boom", read.LastError);
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
