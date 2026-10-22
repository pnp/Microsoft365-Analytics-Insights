using Azure.Core;
using Azure.Messaging.ServiceBus;
using Common.Entities.Models;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;

namespace Tests.UnitTests
{
    /// <summary>
    /// The operator's replay of the Teams calls dead-letter queue (<c>--replayCallDeadLetters</c>). No Service Bus: the
    /// queue is <see cref="FakeCallDeadLetterQueue"/>, except where the real adapter is built offline to check what it reads
    /// and sends.
    /// </summary>
    [TestClass]
    public class CallDeadLetterReplayTests
    {
        internal static readonly DateTime Now = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
        internal const string ClientState = "contoso-client-state-0000";
        private const string MaxDeliveryCountExceeded = "MaxDeliveryCountExceeded";

        internal static string Notification(string callId)
        {
            return JsonConvert.SerializeObject(new GraphChangeNotification
            {
                ClientState = ClientState,
                ChangeType = "created",
                Resource = $"communications/callRecords/{callId}",
                ResourceData = new ResourceData { Id = callId }
            });
        }

        private static Task<CallDeadLetterReplaySummary> Run(FakeCallDeadLetterQueue queue, CallDeadLetterReplayOptions options, RecordingLogger logger = null)
        {
            return new CallDeadLetterReplayer(queue, queue, logger ?? new RecordingLogger(), queue.Clock).RunAsync(options);
        }

        private static List<string> Ids(IEnumerable<CallDeadLetterMessage> messages) => messages.Select(m => m.MessageId).ToList();

        #region What is read and what is sent

        /// <summary>
        /// Reading the queue itself instead would take live notifications away from the importer and "replay" them.
        /// Built offline: creating a client, receiver and sender opens no connection, and asks for no token.
        /// </summary>
        [TestMethod]
        public async Task ServiceBusAdapter_ReadsOnlyTheDeadLetterSubQueue_InPeekLockMode_AndSendsToTheQueueItself()
        {
            var client = new ServiceBusClient("contoso.servicebus.windows.net", new NoTokenCredential());
            var queue = new ServiceBusCallDeadLetterQueue(client, "contoso-calls", ownsClient: true);
            try
            {
                Assert.AreEqual("contoso-calls/$deadletterqueue", queue.ReceiveEntityPath.ToLowerInvariant());
                Assert.AreEqual(ServiceBusReceiveMode.PeekLock, queue.ReceiveMode);
                Assert.AreEqual("contoso-calls", queue.SendEntityPath);
            }
            finally
            {
                await queue.DisposeAsync();
            }
        }

        /// <summary>
        /// At-least-once: the dead-lettered original is deleted only after its copy is safely on the queue.
        /// </summary>
        [TestMethod]
        public async Task Replay_SendsEachCopyBeforeDeletingItsDeadLetter()
        {
            var queue = new FakeCallDeadLetterQueue();
            queue.AddDeadLetter("m1", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddDays(-1));
            queue.AddDeadLetter("m2", MaxDeliveryCountExceeded, Now.AddDays(-2));

            var summary = await Run(queue, new CallDeadLetterReplayOptions());

            CollectionAssert.AreEqual(new[] { "receive", "send:m1", "complete:m1", "send:m2", "complete:m2", "receive" }, queue.Operations);
            Assert.AreEqual(2, summary.Replayed);
            Assert.AreEqual(0, queue.DeadLetters.Count);
            Assert.AreEqual(2, queue.Sent.Count);
            Assert.IsTrue(summary.Succeeded);
        }

        [TestMethod]
        public async Task Replay_WhenASendFails_TheDeadLetterIsAbandonedNotCompleted_SoItStaysInTheDeadLetterQueue()
        {
            var queue = new FakeCallDeadLetterQueue();
            var fails = queue.AddDeadLetter("m-fails", MaxDeliveryCountExceeded, Now.AddDays(-1));
            queue.AddDeadLetter("m-ok", MaxDeliveryCountExceeded, Now.AddDays(-1));
            queue.FailSendFor = m => m == fails;

            var summary = await Run(queue, new CallDeadLetterReplayOptions());

            Assert.AreEqual(1, summary.Failed);
            Assert.AreEqual(1, summary.Replayed);
            CollectionAssert.Contains(queue.Operations, "abandon:m-fails");
            CollectionAssert.DoesNotContain(queue.Operations, "complete:m-fails");
            CollectionAssert.AreEqual(new[] { "m-fails" }, Ids(queue.DeadLetters));
            Assert.IsFalse(queue.IsLocked(fails), "Released, so a later run can replay it.");
            Assert.IsFalse(summary.Succeeded);
        }

        /// <summary>
        /// The copy is on the queue, so the call will be imported: not a failure. The original is left to return to the
        /// dead-letter queue when its lock lapses, and a later run replaying it again is harmless.
        /// </summary>
        [TestMethod]
        public async Task Replay_WhenTheDeadLetterCantBeDeletedAfterItsCopyWasSent_ItIsReportedNotFailedOrAbandoned()
        {
            var queue = new FakeCallDeadLetterQueue();
            var stuck = queue.AddDeadLetter("m-stuck", MaxDeliveryCountExceeded, Now.AddDays(-1));
            queue.FailCompleteFor = m => m == stuck;

            var summary = await Run(queue, new CallDeadLetterReplayOptions());

            Assert.AreEqual(1, summary.Replayed);
            Assert.AreEqual(1, summary.ReplayedButNotRemoved);
            Assert.AreEqual(0, summary.Failed);
            CollectionAssert.DoesNotContain(queue.Operations, "abandon:m-stuck");
            Assert.IsTrue(summary.Succeeded);
        }

        /// <summary>
        /// The copy must reach the importer as the notification Graph sent, remember how often it was replayed, and start
        /// the retry bookkeeping afresh - or a replayed call would be dead-lettered again at its first failure.
        /// </summary>
        [TestMethod]
        public async Task ReplayCopy_KeepsTheNotificationAndItsProperties_CountsTheReplay_AndRestartsTheRetries()
        {
            var queue = new FakeCallDeadLetterQueue();
            var retried = queue.AddDeadLetter("m-retried", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddDays(-1), contentType: "application/json",
                properties: new Dictionary<string, object>
                {
                    ["custom"] = "kept",
                    ["Diagnostic-Id"] = "00-0000-00",
                    [CallQueueRetryState.FailuresProperty] = 10,
                    [CallQueueRetryState.SystemicFailuresProperty] = 2,
                    [CallQueueRetryState.RetriesProperty] = 12,
                    [CallQueueRetryState.FirstFailureUtcProperty] = Now.AddDays(-3).ToString("o"),
                    [CallQueueRetryState.OriginalMessageIdProperty] = "webhook-message",
                    [CallQueueRetryState.LastErrorProperty] = "boom",
                    [CallDeadLetterReplayer.ReplayCountProperty] = 1
                });
            retried.CorrelationId = "correlation-1";
            retried.Subject = "callRecord";
            var firstTime = queue.AddDeadLetter("m-first", MaxDeliveryCountExceeded, Now.AddDays(-2));

            await Run(queue, new CallDeadLetterReplayOptions());

            var copy = queue.Sent[0];
            Assert.AreNotEqual("m-retried", copy.MessageId, "Its own id, so duplicate detection could never discard it.");
            Assert.AreEqual(retried.Body.ToString(), copy.Body.ToString());
            Assert.AreEqual("application/json", copy.ContentType);
            Assert.AreEqual("correlation-1", copy.CorrelationId);
            Assert.AreEqual("callRecord", copy.Subject);
            Assert.AreEqual("kept", copy.ApplicationProperties["custom"]);
            Assert.AreEqual("boom", copy.ApplicationProperties[CallQueueRetryState.LastErrorProperty]);
            Assert.AreEqual(2, copy.ApplicationProperties[CallDeadLetterReplayer.ReplayCountProperty]);
            Assert.AreEqual(Now.AddDays(-3).ToString("o"), copy.ApplicationProperties[CallDeadLetterReplayer.FirstQueuedUtcProperty],
                "The first failure is the earliest sign of the notification, and outlives the restarted bookkeeping.");
            foreach (var notCopied in new[] { "DeadLetterReason", "DeadLetterErrorDescription", "Diagnostic-Id" })
            {
                Assert.IsFalse(copy.ApplicationProperties.ContainsKey(notCopied), notCopied);
            }

            var asTheImporterReadsIt = CallQueueRetryState.FromMessage(new CallQueueMessage
            {
                MessageId = copy.MessageId,
                ApplicationProperties = new Dictionary<string, object>(copy.ApplicationProperties)
            });
            Assert.AreEqual(0, asTheImporterReadsIt.Failures);
            Assert.AreEqual(0, asTheImporterReadsIt.SystemicFailures);
            Assert.AreEqual(0, asTheImporterReadsIt.Retries);
            Assert.IsNull(asTheImporterReadsIt.FirstFailureUtc);
            Assert.AreEqual("webhook-message", asTheImporterReadsIt.OriginalMessageId);

            var firstCopy = queue.Sent[1];
            Assert.AreEqual(1, firstCopy.ApplicationProperties[CallDeadLetterReplayer.ReplayCountProperty]);
            Assert.AreEqual("m-first", firstCopy.ApplicationProperties[CallQueueRetryState.OriginalMessageIdProperty]);
            Assert.AreEqual(firstTime.EnqueuedTime.UtcDateTime.ToString("o"), firstCopy.ApplicationProperties[CallDeadLetterReplayer.FirstQueuedUtcProperty]);
        }

        /// <summary>
        /// The same, through the Service Bus types: Service Bus keeps the dead-letter reason in the message's application
        /// properties, so a copy that carried it would look dead-lettered while it sat on the queue.
        /// </summary>
        [TestMethod]
        public void ServiceBusAdapter_ReplayCopyKeepsBodyContentTypeAndProperties_ButNotTheDeadLetterReason()
        {
            var body = Notification("call-1");
            var received = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString(body), messageId: "m1", correlationId: "correlation-1",
                contentType: "application/json", sequenceNumber: 42, enqueuedTime: new DateTimeOffset(Now.AddDays(-2)),
                properties: new Dictionary<string, object>
                {
                    ["custom"] = "kept",
                    ["DeadLetterReason"] = CallQueueRetryPolicy.RetriesExhaustedReason,
                    ["DeadLetterErrorDescription"] = "Call 'call-1': Not imported after 10 failed attempts."
                });

            var deadLetter = ServiceBusCallDeadLetterQueue.FromReceived(received);
            Assert.AreEqual(CallQueueRetryPolicy.RetriesExhaustedReason, deadLetter.DeadLetterReason);
            Assert.AreEqual(42, deadLetter.SequenceNumber);

            var sent = ServiceBusCallDeadLetterQueue.ToServiceBusMessage(CallDeadLetterReplayer.BuildReplayMessage(deadLetter, Now));

            Assert.AreEqual(body, sent.Body.ToString());
            Assert.AreEqual("application/json", sent.ContentType);
            Assert.AreEqual("correlation-1", sent.CorrelationId);
            Assert.AreNotEqual("m1", sent.MessageId);
            Assert.AreEqual("kept", sent.ApplicationProperties["custom"]);
            Assert.AreEqual(1, sent.ApplicationProperties[CallDeadLetterReplayer.ReplayCountProperty]);
            var onTheQueue = ServiceBusModelFactory.ServiceBusReceivedMessage(body: sent.Body, messageId: sent.MessageId, properties: sent.ApplicationProperties);
            Assert.IsNull(onTheQueue.DeadLetterReason);
            Assert.IsNull(onTheQueue.DeadLetterErrorDescription);
        }

        #endregion

        #region Which messages are replayed

        [TestMethod]
        public async Task DryRun_SummarisesByReasonAndAge_AndChangesNothing()
        {
            var queue = new FakeCallDeadLetterQueue();
            queue.AddDeadLetter("a1", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddHours(-3));
            queue.AddDeadLetter("a2", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddDays(-3));
            queue.AddDeadLetter("b1", MaxDeliveryCountExceeded, Now.AddDays(-10));
            queue.AddDeadLetter("old", MaxDeliveryCountExceeded, Now.AddDays(-45));
            queue.AddDeadLetter("bad", CallQueueMessageHandler.MissingCallIdReason, Now.AddDays(-1));
            queue.AddDeadLetter("none", null, Now.AddDays(-2));
            var before = Ids(queue.DeadLetters);

            var summary = await Run(queue, new CallDeadLetterReplayOptions { DryRun = true });

            Assert.IsTrue(queue.Operations.All(o => o == "peek"), "A dry run only browses: nothing is received (locked), sent, completed or abandoned.");
            CollectionAssert.AreEqual(before, Ids(queue.DeadLetters));
            Assert.AreEqual(0, queue.Sent.Count);
            Assert.AreEqual(6, summary.Scanned);
            Assert.AreEqual(4, summary.Replayed, "a1, a2, b1 and none would be replayed.");
            Assert.AreEqual(1, summary.SkippedByReason[CallDeadLetterSkipReason.NeverImportable]);
            Assert.AreEqual(1, summary.SkippedByReason[CallDeadLetterSkipReason.CallRecordNoLongerInGraph]);
            Assert.AreEqual(2, summary.ByDeadLetterReason[CallQueueRetryPolicy.RetriesExhaustedReason].Count);
            Assert.AreEqual(2, summary.ByDeadLetterReason[MaxDeliveryCountExceeded].Count);
            Assert.AreEqual(Now.AddDays(-45), summary.ByDeadLetterReason[MaxDeliveryCountExceeded].OldestFirstQueuedUtc);
            Assert.AreEqual(1, summary.ByDeadLetterReason[CallDeadLetterReplaySummary.NoReason].Count);
            CollectionAssert.AreEqual(new[] { 1, 3, 1, 1, 0 }, summary.ByAge, "Under a day, 1-7 days, 7-30 days, over 30 days, unknown.");
            Assert.IsTrue(summary.Succeeded);
        }

        [TestMethod]
        public async Task Max_LimitsHowManyARunReplays_OldestFirst()
        {
            var queue = new FakeCallDeadLetterQueue();
            var browsed = new FakeCallDeadLetterQueue();
            for (var i = 0; i < 250; i++)
            {
                queue.AddDeadLetter($"m{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
                browsed.AddDeadLetter($"m{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
            }

            var summary = await Run(queue, new CallDeadLetterReplayOptions { MaxMessages = 120 });
            var dryRun = await Run(browsed, new CallDeadLetterReplayOptions { MaxMessages = 120, DryRun = true });

            Assert.AreEqual(120, summary.Scanned);
            Assert.AreEqual(120, summary.Replayed);
            Assert.IsTrue(summary.ReachedMaxMessages);
            Assert.AreEqual(130, queue.DeadLetters.Count);
            Assert.AreEqual("m120", queue.DeadLetters[0].MessageId);
            CollectionAssert.AreEqual(new[] { 100, 100 }, queue.RequestedBatchSizes, "Read in batches; what is left of the last is released.");
            Assert.IsFalse(queue.DeadLetters.Any(queue.IsLocked));
            Assert.AreEqual(120, dryRun.Scanned);
            Assert.AreEqual(120, dryRun.Replayed);
        }

        /// <summary>
        /// Skipped messages stay at the head of the dead-letter queue run after run. If they used up --max, every run would
        /// spend it on the same old messages and never reach the calls behind them.
        /// </summary>
        [TestMethod]
        public async Task Max_CountsOnlyWhatTheRunReplays_SoItWalksPastSkippedMessages()
        {
            Func<FakeCallDeadLetterQueue> deadLetters = () =>
            {
                var q = new FakeCallDeadLetterQueue();
                for (var i = 0; i < 30; i++)
                {
                    q.AddDeadLetter($"old-{i}", MaxDeliveryCountExceeded, Now.AddDays(-45));
                }
                for (var i = 0; i < 25; i++)
                {
                    q.AddDeadLetter($"call-{i}", MaxDeliveryCountExceeded, Now.AddDays(-1));
                }
                return q;
            };

            var queue = deadLetters();
            var summary = await Run(queue, new CallDeadLetterReplayOptions { MaxMessages = 20 });
            var dryRun = await Run(deadLetters(), new CallDeadLetterReplayOptions { MaxMessages = 20, DryRun = true });

            Assert.AreEqual(20, summary.Replayed);
            Assert.AreEqual(30, summary.SkippedByReason[CallDeadLetterSkipReason.CallRecordNoLongerInGraph]);
            Assert.AreEqual(50, summary.Scanned);
            Assert.IsTrue(summary.ReachedMaxMessages);
            Assert.IsTrue(summary.Succeeded);
            CollectionAssert.AreEqual(Enumerable.Range(0, 30).Select(i => $"old-{i}").Concat(Enumerable.Range(20, 5).Select(i => $"call-{i}")).ToList(),
                Ids(queue.DeadLetters));
            Assert.IsFalse(queue.DeadLetters.Any(queue.IsLocked));
            Assert.AreEqual(20, dryRun.Replayed);
            Assert.AreEqual(50, dryRun.Scanned);
        }

        /// <summary>A run walks past skipped messages only as far as its scan limit, and says when it stopped there.</summary>
        [TestMethod]
        public async Task ScanLimit_BoundsHowManyDeadLettersARunLooksAt_AndTheSummarySaysSo()
        {
            var queue = new FakeCallDeadLetterQueue();
            for (var i = 0; i < 200; i++)
            {
                queue.AddDeadLetter($"old-{i}", MaxDeliveryCountExceeded, Now.AddDays(-45));
            }
            queue.AddDeadLetter("call-0", MaxDeliveryCountExceeded, Now.AddDays(-1));
            var logger = new RecordingLogger();

            var summary = await Run(queue, new CallDeadLetterReplayOptions { ScanLimit = 150 }, logger);

            Assert.AreEqual(150, summary.Scanned);
            Assert.AreEqual(0, summary.Replayed);
            Assert.IsTrue(summary.ReachedScanLimit);
            CollectionAssert.AreEqual(new[] { 100, 50 }, queue.RequestedBatchSizes);
            Assert.IsFalse(queue.DeadLetters.Any(queue.IsLocked));
            Assert.IsTrue(summary.Succeeded, "It did what it was asked; the summary says there may be more.");
            Assert.IsTrue(logger.Entries.Any(e => e.Message.StartsWith("Reached the scan limit")));
        }

        /// <summary>
        /// Nothing else ever removes dead letters, and Graph's 30-day retention makes the old ones unimportable for good. An
        /// operator can delete them - but only by asking, and can see first what would go.
        /// </summary>
        [TestMethod]
        public async Task Purge_DeletesOnlyWhatCanNeverBeImported_OnlyWhenAsked_ReportingItInADryRunFirst()
        {
            Func<FakeCallDeadLetterQueue> deadLetters = () =>
            {
                var q = new FakeCallDeadLetterQueue();
                q.AddDeadLetter("old", MaxDeliveryCountExceeded, Now.AddDays(-45));
                q.AddDeadLetter("no-call-id", CallQueueMessageHandler.MissingCallIdReason, Now.AddDays(-1));
                q.AddDeadLetter("call", MaxDeliveryCountExceeded, Now.AddDays(-1));
                return q;
            };

            var byDefault = deadLetters();
            var summary = await Run(byDefault, new CallDeadLetterReplayOptions());
            Assert.AreEqual(0, summary.Purged);
            CollectionAssert.AreEqual(new[] { "old", "no-call-id" }, Ids(byDefault.DeadLetters), "Nothing is deleted unless asked for.");

            var browsed = deadLetters();
            var logger = new RecordingLogger();
            summary = await Run(browsed, new CallDeadLetterReplayOptions { DryRun = true, PurgeUnimportable = true }, logger);
            Assert.AreEqual(1, summary.PurgedByReason[CallDeadLetterSkipReason.CallRecordNoLongerInGraph]);
            Assert.AreEqual(3, browsed.DeadLetters.Count);
            Assert.IsTrue(browsed.Operations.All(o => o == "peek"));
            Assert.IsTrue(logger.Entries.Any(e => e.Message.StartsWith("Would delete: 1")));

            var purged = deadLetters();
            logger = new RecordingLogger();
            summary = await Run(purged, new CallDeadLetterReplayOptions { PurgeUnimportable = true }, logger);
            Assert.AreEqual(1, summary.Purged);
            Assert.AreEqual(1, summary.Replayed);
            CollectionAssert.Contains(purged.Operations, "complete:old");
            CollectionAssert.AreEqual(new[] { "no-call-id" }, Ids(purged.DeadLetters), "A never-importable one goes only with --purge-invalid.");
            Assert.IsTrue(logger.Entries.Any(e => e.Message.Contains("deleted dead-lettered message 'old'")));
            Assert.IsTrue(summary.Succeeded);

            var everything = deadLetters();
            summary = await Run(everything, new CallDeadLetterReplayOptions { PurgeUnimportable = true, PurgeInvalid = true });
            Assert.AreEqual(2, summary.Purged);
            Assert.AreEqual(0, everything.DeadLetters.Count);

            var undeletable = deadLetters();
            undeletable.FailCompleteFor = m => m.MessageId == "old";
            summary = await Run(undeletable, new CallDeadLetterReplayOptions { PurgeUnimportable = true });
            Assert.AreEqual(1, summary.PurgeFailed);
            Assert.IsFalse(summary.Succeeded);
            CollectionAssert.Contains(Ids(undeletable.DeadLetters), "old");
            Assert.IsFalse(undeletable.DeadLetters.Any(undeletable.IsLocked));
        }

        /// <summary>
        /// Service Bus doesn't promise a full batch, so getting past held messages whose locks lapsed can take far more
        /// receives than the batches they first filled: what matters is how many messages come round again.
        /// </summary>
        [TestMethod]
        public async Task Replay_GetsPastLapsedMessages_EvenWhenReceivesReturnPartBatches()
        {
            var queue = new FakeCallDeadLetterQueue { MaxReturned = 40 };
            for (var i = 0; i < 1500; i++)
            {
                queue.AddDeadLetter($"no-call-id-{i}", CallQueueMessageHandler.MissingCallIdReason, Now.AddHours(-2));
            }
            for (var i = 0; i < 30; i++)
            {
                queue.AddDeadLetter($"call-{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
            }
            // Once all 1,500 are held their locks lapse, and they come round again ahead of the last calls.
            queue.BeforeReceive = n =>
            {
                if (n == 39)
                {
                    queue.Clock.Advance(TimeSpan.FromMinutes(2));
                }
            };

            var summary = await Run(queue, new CallDeadLetterReplayOptions());

            Assert.AreEqual(30, summary.Replayed);
            Assert.AreEqual(1530, summary.Scanned);
            Assert.IsNull(summary.StoppedEarly);
            Assert.IsTrue(summary.Succeeded);
        }

        /// <summary>
        /// When held messages lose their locks faster than the run gets past them it cannot reach the calls behind: it must
        /// say it stopped early and fail, not report a finished run.
        /// </summary>
        [TestMethod]
        public async Task Replay_WhenLapsedMessagesKeepComingRound_SaysItStoppedEarly_AndExitsWithFailure()
        {
            var queue = new FakeCallDeadLetterQueue { MaxReturned = 40 };
            for (var i = 0; i < 300; i++)
            {
                queue.AddDeadLetter($"no-call-id-{i}", CallQueueMessageHandler.MissingCallIdReason, Now.AddHours(-2));
            }
            for (var i = 0; i < 50; i++)
            {
                queue.AddDeadLetter($"call-{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
            }
            // From the ninth receive on, every lock lapses before the next receive.
            queue.BeforeReceive = n =>
            {
                if (n >= 9)
                {
                    queue.Clock.Advance(TimeSpan.FromMinutes(2));
                }
            };
            var logger = new RecordingLogger();

            var exit = await CallDeadLetterReplayCommand.RunAsync(new[] { "--replayCallDeadLetters" }, logger, () => queue, queue.Clock, CancellationToken.None);

            Assert.AreEqual(CallDeadLetterReplayCommand.ExitFailed, exit);
            Assert.IsTrue(logger.Entries.Any(e => e.Message.StartsWith("Stopped early:")));
            Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("replay finished")));
            Assert.AreEqual(30, queue.DeadLetters.Count(m => m.MessageId.StartsWith("call-")), "The calls it never reached are still there.");
        }

        /// <summary>
        /// On a run longer than the queue's lock duration, the messages it skipped come round again as their locks lapse -
        /// spread through the queue, each a few seconds after the last, so once it has read everything no receive ever comes
        /// back empty. That is the end of the queue, not a run that gave up.
        /// </summary>
        [TestMethod]
        public async Task Replay_ThatHasReadTheWholeQueue_FinishesNormally_ThoughWhatItSkippedKeepsComingRound()
        {
            // Each receive, with the replays it brings, takes five seconds, and waits up to five more for a lock to lapse.
            var queue = new FakeCallDeadLetterQueue { ReceiveWait = TimeSpan.FromSeconds(5) };
            for (var i = 0; i < 3000; i++)
            {
                if (i % 30 == 0)
                {
                    queue.AddDeadLetter($"no-call-id-{i}", CallQueueMessageHandler.MissingCallIdReason, Now.AddHours(-2));
                }
                else
                {
                    queue.AddDeadLetter($"call-{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
                }
            }
            queue.BeforeReceive = n => queue.Clock.Advance(TimeSpan.FromSeconds(5));
            var logger = new RecordingLogger();

            var exit = await CallDeadLetterReplayCommand.RunAsync(new[] { "--replayCallDeadLetters", "--max", "5000" }, logger, () => queue, queue.Clock,
                CancellationToken.None);

            Assert.IsFalse(logger.Entries.Any(e => e.Message.StartsWith("Stopped early")), string.Join(Environment.NewLine, logger.Entries.Select(e => e.Message).Where(m => m.StartsWith("Stopped early"))));
            Assert.AreEqual(CallDeadLetterReplayCommand.ExitSucceeded, exit);
            Assert.AreEqual(2900, queue.Sent.Count);
            Assert.AreEqual(100, queue.DeadLetters.Count);
            Assert.IsTrue(logger.Entries.Any(e => e.Message.StartsWith("Reached the end of the dead-letter queue")));
            Assert.IsFalse(queue.DeadLetters.Any(queue.IsLocked));
        }

        /// <summary>
        /// A message another receiver had locked when the run went past it lies behind the last one the run looked at, so
        /// looking beyond that can't show it. One browse from the start finds it, and the run carries on until it has it.
        /// </summary>
        [TestMethod]
        public async Task Replay_DoesNotCallItTheEnd_WhileAMessageItPassedLockedElsewhereIsUnread()
        {
            var queue = new FakeCallDeadLetterQueue { ReceiveWait = TimeSpan.FromSeconds(5) };
            for (var i = 0; i < 10; i++)
            {
                queue.AddDeadLetter($"no-call-id-{i}", CallQueueMessageHandler.MissingCallIdReason, Now.AddHours(-2));
            }
            var lockedElsewhere = queue.AddDeadLetter("call-locked-elsewhere", MaxDeliveryCountExceeded, Now.AddHours(-1));
            for (var i = 0; i < 10; i++)
            {
                queue.AddDeadLetter($"call-{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
            }
            queue.LockElsewhere(lockedElsewhere, TimeSpan.FromSeconds(150));
            // A minute between receives: the skipped messages come round on every receive after the first.
            queue.BeforeReceive = n => queue.Clock.Advance(TimeSpan.FromSeconds(60));

            var summary = await Run(queue, new CallDeadLetterReplayOptions());

            Assert.AreEqual(11, summary.Replayed, "Including the one locked elsewhere when the run went past it.");
            Assert.AreEqual(10, summary.ReachedEndHolding);
            Assert.IsTrue(summary.Succeeded);
        }

        /// <summary>
        /// Service Bus starts a peek given no sequence number just after the last message any earlier peek returned - the
        /// check for anything beyond the last message read included. So the browse for messages the run went past must give
        /// its own start, or one such check that found something moves it past the very message it is looking for.
        /// </summary>
        [TestMethod]
        public async Task Replay_BrowsesFromTheStart_HoweverFarEarlierPeeksMovedTheReceiversOwnCursor()
        {
            var queue = new FakeCallDeadLetterQueue { MaxReturned = 10 };
            for (var i = 0; i < 10; i++)
            {
                queue.AddDeadLetter($"no-call-id-{i}", CallQueueMessageHandler.MissingCallIdReason, Now.AddHours(-2));
            }
            var lockedElsewhere = queue.AddDeadLetter("call-locked-elsewhere", MaxDeliveryCountExceeded, Now.AddHours(-1));
            for (var i = 0; i < 20; i++)
            {
                queue.AddDeadLetter($"call-{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
            }
            queue.LockElsewhere(lockedElsewhere, TimeSpan.FromSeconds(200));
            // The skipped ten come round on the second receive, when the check for anything beyond them finds the message
            // locked elsewhere; the calls behind are read next; the ten come round again once all of those are read.
            var times = new Dictionary<int, int> { [1] = 1, [2] = 69, [3] = 1, [4] = 69, [5] = 70, [6] = 70 };
            long? cursorWhenItReachedTheEnd = null;
            queue.BeforeReceive = n =>
            {
                queue.Clock.Advance(TimeSpan.FromSeconds(times.TryGetValue(n, out var seconds) ? seconds : 70));
                if (n == 3)
                {
                    queue.MaxReturned = int.MaxValue;
                }
                if (n == 4)
                {
                    cursorWhenItReachedTheEnd = queue.LastPeekedSequenceNumber;
                }
            };

            var summary = await Run(queue, new CallDeadLetterReplayOptions());

            Assert.IsTrue(cursorWhenItReachedTheEnd >= lockedElsewhere.SequenceNumber, "The scenario: the receiver's own cursor had passed the message.");
            Assert.AreEqual(21, summary.Replayed, "Including the one locked elsewhere when the run went past it.");
            CollectionAssert.DoesNotContain(Ids(queue.DeadLetters), "call-locked-elsewhere");
            Assert.IsTrue(summary.Succeeded);
        }

        /// <summary>
        /// Releasing what a run held costs a round trip a message: each must be released once - by its latest receipt, newest
        /// first - and not at all once its lock has lapsed anyway.
        /// </summary>
        [TestMethod]
        public async Task Release_AbandonsEachHeldMessageOnce_ByItsLatestLock_NewestFirst()
        {
            var queue = new FakeCallDeadLetterQueue { MaxReturned = 40 };
            for (var i = 0; i < 150; i++)
            {
                queue.AddDeadLetter($"no-call-id-{i}", CallQueueMessageHandler.MissingCallIdReason, Now.AddHours(-2));
            }
            for (var i = 0; i < 10; i++)
            {
                queue.AddDeadLetter($"call-{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
            }
            // A second a receive; before the fifth, the locks from the first two receives lapse, and the first 40 come
            // round again. The 40 from the second have lapsed too by the time the run puts back what it holds.
            queue.BeforeReceive = n => queue.Clock.Advance(n == 5 ? TimeSpan.FromSeconds(58) : TimeSpan.FromSeconds(1));

            var summary = await Run(queue, new CallDeadLetterReplayOptions());

            Assert.AreEqual(10, summary.Replayed);
            Assert.AreEqual(110, queue.Abandoned.Count, "The 40 received again, once each by their latest receipt, and the 70 still locked from the third and fourth.");
            Assert.AreEqual(110, queue.Abandoned.Select(m => m.SequenceNumber).Distinct().Count());
            Assert.AreEqual(0, queue.StaleSettlements, "No round trip spent on a lock that had lapsed or been superseded.");
            CollectionAssert.AreEqual(queue.Abandoned.OrderByDescending(m => m.LockedUntil).Select(m => m.SequenceNumber).ToList(),
                queue.Abandoned.Select(m => m.SequenceNumber).ToList(), "Newest first.");
            Assert.IsTrue(queue.Abandoned.Select(m => m.LockedUntil).Distinct().Count() > 1);
            Assert.IsFalse(queue.DeadLetters.Any(queue.IsLocked));
        }

        [TestMethod]
        public async Task Release_SkipsMessagesWhoseLocksHaveLapsedAlready()
        {
            var queue = new FakeCallDeadLetterQueue();
            for (var i = 0; i < 3; i++)
            {
                queue.AddDeadLetter($"no-call-id-{i}", CallQueueMessageHandler.MissingCallIdReason, Now.AddHours(-2));
            }
            // By the time the run finds nothing more to receive, the three locks it holds have lapsed.
            queue.AfterEmptyReceive = () => queue.Clock.Advance(TimeSpan.FromMinutes(2));

            var summary = await Run(queue, new CallDeadLetterReplayOptions());

            Assert.AreEqual(3, summary.SkippedByReason[CallDeadLetterSkipReason.NeverImportable]);
            Assert.AreEqual(0, queue.Abandoned.Count);
            Assert.IsTrue(summary.Succeeded);
        }

        /// <summary>Ctrl+C while releasing stops it: the locks lapse by themselves within the lock duration.</summary>
        [TestMethod]
        public async Task Release_StopsWhenCancelled_LeavingTheRestToLapse()
        {
            var queue = new FakeCallDeadLetterQueue();
            for (var i = 0; i < 5; i++)
            {
                queue.AddDeadLetter($"no-call-id-{i}", CallQueueMessageHandler.MissingCallIdReason, Now.AddHours(-2));
            }
            using (var cancellation = new CancellationTokenSource())
            {
                queue.OnAbandon = receipt => cancellation.Cancel();

                var summary = await new CallDeadLetterReplayer(queue, queue, new RecordingLogger(), queue.Clock)
                    .RunAsync(new CallDeadLetterReplayOptions(), cancellation.Token);

                Assert.AreEqual(1, queue.Abandoned.Count);
                Assert.AreEqual(4, queue.DeadLetters.Count(queue.IsLocked), "The rest are left to lapse.");
                Assert.IsTrue(summary.Cancelled);
                Assert.IsFalse(summary.Succeeded);
            }
        }

        [TestMethod]
        public async Task Reason_ReplaysOnlyMessagesDeadLetteredForIt_InAnyCase()
        {
            var queue = new FakeCallDeadLetterQueue();
            queue.AddDeadLetter("a1", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddDays(-1));
            queue.AddDeadLetter("b1", MaxDeliveryCountExceeded, Now.AddDays(-1));
            queue.AddDeadLetter("n1", null, Now.AddDays(-1));
            queue.AddDeadLetter("a2", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddDays(-1));

            var summary = await Run(queue, new CallDeadLetterReplayOptions { Reason = "callimportretriesexhausted" });

            Assert.AreEqual(2, summary.Replayed);
            Assert.AreEqual(2, summary.SkippedByReason[CallDeadLetterSkipReason.OtherReason]);
            CollectionAssert.AreEqual(new[] { "b1", "n1" }, Ids(queue.DeadLetters));
            Assert.IsFalse(queue.DeadLetters.Any(queue.IsLocked));
        }

        /// <summary>
        /// A replayed call that fails again comes back with its count: without a cap it could go round for ever.
        /// </summary>
        [TestMethod]
        public async Task ReplayCap_AMessageReplayedThreeTimesStaysInTheDeadLetterQueue_AndIsReported()
        {
            var logger = new RecordingLogger();
            var queue = new FakeCallDeadLetterQueue();
            queue.AddDeadLetter("m-capped", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddDays(-1),
                properties: new Dictionary<string, object> { [CallDeadLetterReplayer.ReplayCountProperty] = 3 });
            queue.AddDeadLetter("m-twice", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddDays(-1),
                properties: new Dictionary<string, object> { [CallDeadLetterReplayer.ReplayCountProperty] = "2" });

            var summary = await Run(queue, new CallDeadLetterReplayOptions(), logger);

            Assert.AreEqual(1, summary.SkippedByReason[CallDeadLetterSkipReason.ReplayLimitReached]);
            CollectionAssert.AreEqual(new[] { "m-capped" }, summary.ReplayLimitReachedMessageIds.ToList());
            CollectionAssert.AreEqual(new[] { "m-capped" }, Ids(queue.DeadLetters));
            Assert.AreEqual(3, queue.Sent.Single().ApplicationProperties[CallDeadLetterReplayer.ReplayCountProperty], "Its third and last replay.");
            Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("m-capped")));
        }

        /// <summary>The reasons are the ones <see cref="CallQueueMessageHandler"/> gives; replaying those only dead-letters them again.</summary>
        [TestMethod]
        public async Task NeverImportableReasons_AreSkippedByDefault_AndReplayedWithIncludeInvalid()
        {
            Func<FakeCallDeadLetterQueue> deadLetters = () =>
            {
                var q = new FakeCallDeadLetterQueue();
                q.AddDeadLetter("malformed", CallQueueMessageHandler.InvalidNotificationReason, Now.AddHours(-1), body: "not a notification");
                q.AddDeadLetter("no-call-id", CallQueueMessageHandler.MissingCallIdReason, Now.AddHours(-1));
                q.AddDeadLetter("not-retryable", CallQueueMessageHandler.NotRetryableReason, Now.AddHours(-1));
                q.AddDeadLetter("outage", CallQueueRetryPolicy.SystemicRetriesExhaustedReason, Now.AddHours(-1));
                return q;
            };

            var byDefault = deadLetters();
            var summary = await Run(byDefault, new CallDeadLetterReplayOptions());
            Assert.AreEqual(3, summary.SkippedByReason[CallDeadLetterSkipReason.NeverImportable]);
            CollectionAssert.AreEqual(new[] { "send:outage" }, byDefault.Operations.Where(o => o.StartsWith("send:")).ToList());
            CollectionAssert.AreEqual(new[] { "malformed", "no-call-id", "not-retryable" }, Ids(byDefault.DeadLetters));

            var included = deadLetters();
            summary = await Run(included, new CallDeadLetterReplayOptions { IncludeInvalid = true });
            Assert.AreEqual(4, summary.Replayed);
            Assert.AreEqual(0, included.DeadLetters.Count);
        }

        /// <summary>
        /// Graph deletes a call record 30 days after the call, so replaying an older notification can only fail. Its age
        /// is taken from the earliest time the message shows it was queued.
        /// </summary>
        [TestMethod]
        public async Task Replay_LeavesNotificationsOlderThanGraphKeepsCallRecords()
        {
            var queue = new FakeCallDeadLetterQueue();
            queue.AddDeadLetter("old", MaxDeliveryCountExceeded, Now.AddDays(-31));
            queue.AddDeadLetter("recent", MaxDeliveryCountExceeded, Now.AddDays(-29));
            queue.AddDeadLetter("retried-for-weeks", CallQueueRetryPolicy.RetryWindowExpiredReason, Now.AddDays(-1),
                properties: new Dictionary<string, object> { [CallQueueRetryState.FirstFailureUtcProperty] = Now.AddDays(-40).ToString("o") });
            queue.AddDeadLetter("replayed-before", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddHours(-1),
                properties: new Dictionary<string, object>
                {
                    [CallDeadLetterReplayer.FirstQueuedUtcProperty] = Now.AddDays(-35).ToString("o"),
                    [CallDeadLetterReplayer.ReplayCountProperty] = 1
                });

            var summary = await Run(queue, new CallDeadLetterReplayOptions());

            Assert.AreEqual(3, summary.SkippedByReason[CallDeadLetterSkipReason.CallRecordNoLongerInGraph]);
            CollectionAssert.AreEqual(new[] { "send:recent" }, queue.Operations.Where(o => o.StartsWith("send:")).ToList());
        }

        /// <summary>
        /// A received message that is skipped stays locked until the run ends: released at once it would be back at the
        /// head of the dead-letter queue, and the run would never get past it to the calls queued behind.
        /// </summary>
        [TestMethod]
        public async Task Replay_HoldsSkippedMessagesUntilItEnds_SoItReachesTheCallsBehindThem()
        {
            var queue = new FakeCallDeadLetterQueue();
            for (var i = 0; i < 150; i++)
            {
                queue.AddDeadLetter($"no-call-id-{i}", CallQueueMessageHandler.MissingCallIdReason, Now.AddHours(-2));
            }
            for (var i = 0; i < 10; i++)
            {
                queue.AddDeadLetter($"call-{i}", CallQueueRetryPolicy.SystemicRetriesExhaustedReason, Now.AddHours(-1));
            }

            var summary = await Run(queue, new CallDeadLetterReplayOptions());

            Assert.AreEqual(160, summary.Scanned);
            Assert.AreEqual(10, summary.Replayed);
            Assert.AreEqual(150, summary.SkippedByReason[CallDeadLetterSkipReason.NeverImportable]);
            Assert.AreEqual(150, queue.DeadLetters.Count);
            Assert.IsFalse(queue.DeadLetters.Any(queue.IsLocked), "Every skipped message is released by the end.");
            Assert.IsTrue(queue.Operations.FindIndex(o => o.StartsWith("abandon:")) > queue.Operations.FindLastIndex(o => o.StartsWith("send:")));
        }

        /// <summary>
        /// The installer creates the queue with Service Bus's default one-minute lock, so on a long run the messages held at
        /// the head of the dead-letter queue lose their locks and are received again: the run must carry on past them to the
        /// calls behind, not take them for the end of the queue.
        /// </summary>
        [TestMethod]
        public async Task Replay_CarriesOnPastHeldMessagesWhoseLocksLapsed()
        {
            var queue = new FakeCallDeadLetterQueue();
            for (var i = 0; i < 150; i++)
            {
                queue.AddDeadLetter($"no-call-id-{i}", CallQueueMessageHandler.MissingCallIdReason, Now.AddHours(-2));
            }
            for (var i = 0; i < 60; i++)
            {
                queue.AddDeadLetter($"call-{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
            }
            queue.BeforeReceive = receiveNumber =>
            {
                if (receiveNumber == 3)
                {
                    queue.ExpireLocks();
                }
            };

            var summary = await Run(queue, new CallDeadLetterReplayOptions());

            Assert.AreEqual(210, summary.Scanned);
            Assert.AreEqual(60, summary.Replayed);
            Assert.AreEqual(150, summary.SkippedByReason[CallDeadLetterSkipReason.NeverImportable], "Each counted once, however often it came round.");
            Assert.AreEqual(150, queue.DeadLetters.Count);
            Assert.IsFalse(queue.DeadLetters.Any(queue.IsLocked));
        }

        /// <summary>
        /// The importer dead-letters an invalid notification as soon as it reads it, which can be while the run that
        /// replayed it is still reading: that copy must not be replayed again by the same run.
        /// </summary>
        [TestMethod]
        public async Task Replay_DoesNotReplayAgainACopyDeadLetteredWhileTheRunIsStillGoing()
        {
            var queue = new FakeCallDeadLetterQueue();
            queue.AddDeadLetter("m1", CallQueueMessageHandler.InvalidNotificationReason, Now.AddHours(-1), body: "not a notification");
            queue.OnSent = replay => queue.AddDeadLetter(replay.MessageId, CallQueueMessageHandler.InvalidNotificationReason, Now,
                body: replay.Body.ToString(), properties: replay.ApplicationProperties);

            var summary = await Run(queue, new CallDeadLetterReplayOptions { IncludeInvalid = true });

            Assert.AreEqual(1, queue.Sent.Count);
            Assert.AreEqual(1, summary.SkippedByReason[CallDeadLetterSkipReason.ReplayedInThisRun]);
            Assert.AreEqual(queue.Sent[0].MessageId, queue.DeadLetters.Single().MessageId);
        }

        #endregion

        /// <summary>
        /// The body carries the webhook subscription's clientState - the only thing that authenticates a notification -
        /// so no path through the replay may log it, or any of the notification.
        /// </summary>
        [TestMethod]
        public async Task Logs_NeverCarryANotificationOrItsClientState()
        {
            var logger = new RecordingLogger();
            var queue = new FakeCallDeadLetterQueue();
            queue.AddDeadLetter("m-ok", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddDays(-1));
            var sendFails = queue.AddDeadLetter("m-send-fails", MaxDeliveryCountExceeded, Now.AddDays(-1));
            var deleteFails = queue.AddDeadLetter("m-delete-fails", CallQueueRetryPolicy.SystemicRetriesExhaustedReason, Now.AddDays(-1));
            queue.AddDeadLetter("m-malformed", CallQueueMessageHandler.InvalidNotificationReason, Now.AddDays(-1), body: "{\"clientState\":\"" + ClientState + "\",");
            queue.AddDeadLetter("m-capped", null, Now.AddDays(-1), properties: new Dictionary<string, object> { [CallDeadLetterReplayer.ReplayCountProperty] = 3 });
            queue.AddDeadLetter("m-old", MaxDeliveryCountExceeded, Now.AddDays(-60));
            queue.FailSendFor = m => m == sendFails;
            queue.FailCompleteFor = m => m == deleteFails;
            var bodies = queue.DeadLetters.Select(m => m.Body.ToString()).ToList();

            await Run(queue, new CallDeadLetterReplayOptions { DryRun = true }, logger);
            await Run(queue, new CallDeadLetterReplayOptions { IncludeInvalid = true }, logger);
            await Run(queue, new CallDeadLetterReplayOptions { PurgeUnimportable = true, PurgeInvalid = true }, logger);

            var logged = string.Join(Environment.NewLine, logger.Entries.Select(e => e.Message));
            StringAssert.Contains(logged, "m-ok", "Message ids are what identify a message in the logs.");
            StringAssert.Contains(logged, "m-send-fails");
            StringAssert.Contains(logged, "deleted dead-lettered message 'm-old'");
            Assert.IsFalse(logged.Contains(ClientState), "The clientState was logged.");
            Assert.IsFalse(logged.IndexOf("clientState", StringComparison.OrdinalIgnoreCase) >= 0, "Part of a notification was logged.");
            Assert.IsFalse(bodies.Any(b => logged.Contains(b)), "A message body was logged.");
        }

        #region Command line

        [TestMethod]
        public void CommandLine_ReadsTheOptions_InAnyCase()
        {
            Assert.IsTrue(CallDeadLetterReplayCommand.TryParse(new[] { "--replayCallDeadLetters" }, out var bare, out var error), error);
            Assert.IsFalse(bare.DryRun, "Without --dry-run it is a real replay.");
            Assert.AreEqual(CallDeadLetterReplayOptions.DefaultMaxMessages, bare.MaxMessages);
            Assert.IsNull(bare.Reason);
            Assert.IsFalse(bare.IncludeInvalid);

            Assert.IsTrue(CallDeadLetterReplayCommand.TryParse(
                new[] { "--include-invalid", "--REPLAYCALLDEADLETTERS", "--max", "50", "--Dry-Run", "--reason", CallQueueRetryPolicy.RetriesExhaustedReason }, out var all, out error), error);
            Assert.AreEqual(50, all.MaxMessages);
            Assert.IsTrue(all.DryRun);
            Assert.AreEqual(CallQueueRetryPolicy.RetriesExhaustedReason, all.Reason);
            Assert.IsTrue(all.IncludeInvalid);

            Assert.IsFalse(bare.PurgeUnimportable || bare.PurgeInvalid, "Deleting is never the default.");
            Assert.IsTrue(CallDeadLetterReplayCommand.TryParse(
                new[] { "--replayCallDeadLetters", "--scan-limit", "500", "--Purge-Unimportable", "--purge-invalid" }, out var purge, out error), error);
            Assert.AreEqual(500, purge.ScanLimit);
            Assert.IsTrue(purge.PurgeUnimportable);
            Assert.IsTrue(purge.PurgeInvalid);
        }

        /// <summary>A command line it does not understand is refused before the queue is opened: a mistyped
        /// <c>--dry-run</c> must never become a real replay.</summary>
        [TestMethod]
        public async Task CommandLine_RefusesWhatItDoesNotRecognise_WithoutOpeningTheQueue()
        {
            var refused = new[]
            {
                new[] { "--replayCallDeadLetters", "--dryrun" },
                new[] { "--replayCallDeadLetters", "--max" },
                new[] { "--replayCallDeadLetters", "--max", "0" },
                new[] { "--replayCallDeadLetters", "--max", "10001" },
                new[] { "--replayCallDeadLetters", "--max", "ten" },
                new[] { "--replayCallDeadLetters", "--reason" },
                new[] { "--replayCallDeadLetters", "--reason", "--dry-run" },
                new[] { "--replayCallDeadLetters", "--dry-run", "--DRY-RUN" },
                new[] { "--replayCallDeadLetters", "--callId", "call-1" },
                new[] { "--replayCallDeadLetters", "--scan-limit", "0" },
                new[] { "--replayCallDeadLetters", "--scan-limit", "100001" },
                new[] { "--replayCallDeadLetters", "--include-invalid", "--purge-invalid" }
            };
            foreach (var args in refused)
            {
                var opened = false;
                var logger = new RecordingLogger();
                var exit = await CallDeadLetterReplayCommand.RunAsync(args, logger, () => { opened = true; return new FakeCallDeadLetterQueue(); },
                    new FixedClock(Now), CancellationToken.None);

                var commandLine = string.Join(" ", args);
                Assert.AreEqual(CallDeadLetterReplayCommand.ExitUsage, exit, commandLine);
                Assert.IsFalse(opened, commandLine);
                StringAssert.Contains(logger.Entries.Last().Message, "Usage:", commandLine);
            }
        }

        /// <summary>
        /// Nothing that looks like a replay command line may fall through to the import: run from the Kudu console, that
        /// would start a second importer alongside the WebJob. A mistyped switch, the replay's options without the switch,
        /// and a switch whose hyphens copying and pasting turned into an en or em dash are all refused - while the
        /// importer's own arguments are left alone.
        /// </summary>
        [TestMethod]
        public async Task Dispatch_AnythingLikeAReplayCommandLineIsRefused_RatherThanStartingAnImport()
        {
            var refused = new[]
            {
                new { Args = new[] { "--replay-call-dead-letters" }, Says = $"the switch is {CallDeadLetterReplayCommand.Switch}" },
                new { Args = new[] { "--replayCallDeadLetter", "--dry-run" }, Says = $"the switch is {CallDeadLetterReplayCommand.Switch}" },
                new { Args = new[] { "--REPLAYCALLS" }, Says = $"the switch is {CallDeadLetterReplayCommand.Switch}" },
                new { Args = new[] { "--dry-run", "--purge-unimportable" }, Says = $"{CallDeadLetterReplayCommand.Switch} is missing" },
                new { Args = new[] { "--MAX", "5" }, Says = $"{CallDeadLetterReplayCommand.Switch} is missing" },
                new { Args = new[] { "\u2013replayCallDeadLetters" }, Says = "en or em dash" },
                new { Args = new[] { "\u2014replayCallDeadLetters", "--dry-run" }, Says = "en or em dash" },
                new { Args = new[] { "-replayCallDeadLetters", "-dry-run" }, Says = "two hyphens" },
                new { Args = new[] { "/replayCallDeadLetters" }, Says = "two hyphens" },
                new { Args = new[] { "--replayCallDeadLetters", "/dry-run" }, Says = "two hyphens" }
            };
            foreach (var commandLine in refused)
            {
                var text = string.Join(" ", commandLine.Args);
                Assert.IsTrue(CallDeadLetterReplayCommand.IsRequested(commandLine.Args), $"'{text}' must not start the import.");

                var opened = false;
                var logger = new RecordingLogger();
                var exit = await CallDeadLetterReplayCommand.RunAsync(commandLine.Args, logger, () => { opened = true; return new FakeCallDeadLetterQueue(); },
                    new FixedClock(Now), CancellationToken.None);

                Assert.AreEqual(CallDeadLetterReplayCommand.ExitUsage, exit, text);
                Assert.IsFalse(opened, text);
                StringAssert.Contains(logger.Entries.Last().Message, commandLine.Says, text);
                StringAssert.Contains(logger.Entries.Last().Message, "Usage:", text);
            }

            foreach (var importArgs in new[]
            {
                new string[0],
                new[] { "--webhook", "https://contoso.example/api/CallRecordWebhook" },
                new[] { "--callId", "call-1" },
                new[] { "--traceAuditEmail", "someone@contoso.com", "--traceAuditDir", @"C:\Contoso\Trace" },
                new[] { "--traceAuditDir", "/contoso/trace" },
                new[] { "--repair-copilot-history", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z", @"C:\Contoso\Private\checkpoint.json" }
            })
            {
                Assert.IsFalse(CallDeadLetterReplayCommand.IsRequested(importArgs), $"'{string.Join(" ", importArgs)}' is the importer's own.");
            }
        }

        /// <summary>
        /// The importer's host runs the replay instead of the import only when the switch is given, and exits with a code
        /// that says whether everything was replayed.
        /// </summary>
        [TestMethod]
        public async Task Dispatch_OnlyTheSwitchRunsTheReplay_AndTheExitCodeSaysWhetherItAllWorked()
        {
            Assert.IsFalse(CallDeadLetterReplayCommand.IsRequested(new string[0]), "The import, as the WebJob runs it.");
            Assert.IsFalse(CallDeadLetterReplayCommand.IsRequested(new[] { "--callId", "call-1" }));
            Assert.IsFalse(CallDeadLetterReplayCommand.IsRequested(new[] { "--traceAuditEmail", "someone@contoso.com" }));
            Assert.IsTrue(CallDeadLetterReplayCommand.IsRequested(new[] { "--dry-run", "--REPLAYCALLDEADLETTERS" }));

            var queue = new FakeCallDeadLetterQueue();
            queue.AddDeadLetter("m1", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddDays(-1));
            var opened = 0;
            var exit = await CallDeadLetterReplayCommand.RunAsync(new[] { "--replayCallDeadLetters" }, new RecordingLogger(),
                () => { opened++; return queue; }, new FixedClock(Now), CancellationToken.None);
            Assert.AreEqual(CallDeadLetterReplayCommand.ExitSucceeded, exit);
            Assert.AreEqual(1, opened);
            Assert.AreEqual(1, queue.Sent.Count);
            Assert.IsTrue(queue.Disposed);

            // Sending keeps failing: the run stops rather than failing every message in turn, and says so.
            var failing = new FakeCallDeadLetterQueue { FailSendFor = m => true };
            for (var i = 0; i < 20; i++)
            {
                failing.AddDeadLetter($"f{i}", MaxDeliveryCountExceeded, Now.AddDays(-1));
            }
            exit = await CallDeadLetterReplayCommand.RunAsync(new[] { "--replayCallDeadLetters" }, new RecordingLogger(),
                () => failing, new FixedClock(Now), CancellationToken.None);
            Assert.AreEqual(CallDeadLetterReplayCommand.ExitFailed, exit);
            Assert.AreEqual(CallDeadLetterReplayer.MaxConsecutiveSendFailures, failing.Operations.Count(o => o.StartsWith("send:")));
            Assert.AreEqual(20, failing.DeadLetters.Count);
            Assert.IsFalse(failing.DeadLetters.Any(failing.IsLocked));
            Assert.IsTrue(failing.Disposed);

            exit = await CallDeadLetterReplayCommand.RunAsync(new[] { "--replayCallDeadLetters", "--dry-run" }, new RecordingLogger(),
                () => { throw new InvalidOperationException("Service Bus is not configured on this deployment."); }, new FixedClock(Now), CancellationToken.None);
            Assert.AreEqual(CallDeadLetterReplayCommand.ExitFailed, exit);
        }

        #endregion

        /// <summary>A credential for a client that must never connect.</summary>
        private sealed class NoTokenCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => throw new InvalidOperationException("This test must not connect to Service Bus.");

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => throw new InvalidOperationException("This test must not connect to Service Bus.");
        }
    }

    /// <summary>
    /// The calls queue and its dead-letter queue in memory, with Service Bus's PeekLock rules: a received message stays in
    /// the dead-letter queue, locked and hidden from further receives, until it is completed (deleted) or abandoned, or its
    /// lock lapses on <see cref="Clock"/>. As in Service Bus, each receipt is a new object with its own lock token, and
    /// settling a receipt whose lock has lapsed or been superseded fails. Peeking locks and changes nothing.
    /// </summary>
    internal sealed class FakeCallDeadLetterQueue : ICallDeadLetterQueue
    {
        private sealed class Lock
        {
            public string Token;
            public DateTime Until;
        }

        private readonly Dictionary<long, Lock> _locks = new Dictionary<long, Lock>();
        private long _nextSequenceNumber = 1;
        private long _lastPeeked = -1;

        /// <summary>Where Service Bus's own "carry on from the last peek" would start from, less one: moved by every peek that returns messages.</summary>
        public long LastPeekedSequenceNumber => _lastPeeked;
        private int _receives;

        public FakeCallDeadLetterQueue(FixedClock clock = null)
        {
            Clock = clock ?? new FixedClock(CallDeadLetterReplayTests.Now);
        }

        public FixedClock Clock { get; }

        /// <summary>Service Bus's default, which the installer leaves as it is.</summary>
        public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>The most messages a receive or peek returns, however many were asked for: Service Bus doesn't promise a full batch.</summary>
        public int MaxReturned { get; set; } = int.MaxValue;

        /// <summary>
        /// How long a receive waits for a lock to lapse when no message is available, as the adapter's does (five seconds);
        /// zero to return at once.
        /// </summary>
        public TimeSpan ReceiveWait { get; set; } = TimeSpan.Zero;

        public string QueueName => "contoso-calls";

        /// <summary>What is in the dead-letter queue.</summary>
        public List<CallDeadLetterMessage> DeadLetters { get; } = new List<CallDeadLetterMessage>();

        /// <summary>Replays sent to the queue itself.</summary>
        public List<CallReplayMessage> Sent { get; } = new List<CallReplayMessage>();

        /// <summary>"peek", "receive", and "send:", "complete:" or "abandon:" followed by the dead-lettered message's id.</summary>
        public List<string> Operations { get; } = new List<string>();

        /// <summary>The batch size asked for by each peek or receive.</summary>
        public List<int> RequestedBatchSizes { get; } = new List<int>();

        /// <summary>Every receipt the replay tried to abandon, in order.</summary>
        public List<CallDeadLetterMessage> Abandoned { get; } = new List<CallDeadLetterMessage>();

        /// <summary>Completes and abandons of a receipt whose lock had lapsed or been superseded: each a wasted round trip.</summary>
        public int StaleSettlements { get; private set; }

        public Func<CallDeadLetterMessage, bool> FailSendFor { get; set; } = m => false;
        public Func<CallDeadLetterMessage, bool> FailCompleteFor { get; set; } = m => false;

        /// <summary>Runs after each send that succeeds.</summary>
        public Action<CallReplayMessage> OnSent { get; set; }

        /// <summary>Runs before each receive, with its number (1 for the first).</summary>
        public Action<int> BeforeReceive { get; set; }

        /// <summary>Runs when a receive finds nothing to return.</summary>
        public Action AfterEmptyReceive { get; set; }

        /// <summary>Runs after each abandon that succeeds.</summary>
        public Action<CallDeadLetterMessage> OnAbandon { get; set; }

        public bool Disposed { get; private set; }

        public bool IsLocked(CallDeadLetterMessage message) =>
            _locks.TryGetValue(message.SequenceNumber, out var held) && held.Until > Clock.UtcNow;

        /// <summary>Every lock lapses, as each does when held longer than <see cref="LockDuration"/>.</summary>
        public void ExpireLocks() => _locks.Clear();

        /// <summary>Locks a message as another receiver would, so receives here pass it by until the lock lapses.</summary>
        public void LockElsewhere(CallDeadLetterMessage message, TimeSpan duration) =>
            _locks[message.SequenceNumber] = new Lock { Token = "elsewhere", Until = Clock.UtcNow + duration };

        /// <summary>Dead-letters a notification, with the reason in its application properties as Service Bus puts it.</summary>
        public CallDeadLetterMessage AddDeadLetter(string messageId, string reason, DateTime enqueuedUtc, string body = null,
            IDictionary<string, object> properties = null, string contentType = null)
        {
            var applicationProperties = new Dictionary<string, object>(properties ?? new Dictionary<string, object>());
            if (reason != null)
            {
                applicationProperties["DeadLetterReason"] = reason;
                applicationProperties["DeadLetterErrorDescription"] = $"Message '{messageId}': simulated.";
            }
            var message = new CallDeadLetterMessage
            {
                MessageId = messageId,
                SequenceNumber = _nextSequenceNumber++,
                EnqueuedTime = new DateTimeOffset(enqueuedUtc),
                DeadLetterReason = reason,
                Body = BinaryData.FromString(body ?? CallDeadLetterReplayTests.Notification("call-" + messageId)),
                ContentType = contentType,
                ApplicationProperties = applicationProperties
            };
            DeadLetters.Add(message);
            return message;
        }

        public Task<IReadOnlyList<CallDeadLetterMessage>> PeekAsync(long fromSequenceNumber, int maxMessages, CancellationToken cancellationToken)
        {
            Operations.Add("peek");
            RequestedBatchSizes.Add(maxMessages);
            return Task.FromResult(Peek(fromSequenceNumber, maxMessages));
        }

        // As the SDK's receiver: a peek begins at the given sequence number, or with none just after the last message any
        // peek returned - and every peek that returns messages moves that point, whatever it began at.
        private IReadOnlyList<CallDeadLetterMessage> Peek(long fromSequenceNumber, int maxMessages)
        {
            var batch = DeadLetters.Where(m => m.SequenceNumber >= fromSequenceNumber).OrderBy(m => m.SequenceNumber)
                .Take(Math.Min(maxMessages, MaxReturned)).Select(m => Receipt(m, null, default(DateTimeOffset))).ToList();
            if (batch.Count > 0)
            {
                _lastPeeked = batch[batch.Count - 1].SequenceNumber;
            }
            return batch;
        }

        public Task<IReadOnlyList<CallDeadLetterMessage>> ReceiveAsync(int maxMessages, CancellationToken cancellationToken)
        {
            Operations.Add("receive");
            RequestedBatchSizes.Add(maxMessages);
            BeforeReceive?.Invoke(++_receives);
            if (ReceiveWait > TimeSpan.Zero && !DeadLetters.Any(m => !IsLocked(m)))
            {
                var nextLapse = _locks.Values.Where(l => l.Until > Clock.UtcNow).Select(l => (DateTime?)l.Until).Min();
                Clock.Advance(nextLapse.HasValue && nextLapse.Value - Clock.UtcNow <= ReceiveWait ? nextLapse.Value - Clock.UtcNow : ReceiveWait);
            }
            var receipts = new List<CallDeadLetterMessage>();
            foreach (var message in DeadLetters.Where(m => !IsLocked(m)).OrderBy(m => m.SequenceNumber).Take(Math.Min(maxMessages, MaxReturned)).ToList())
            {
                var held = new Lock { Token = Guid.NewGuid().ToString("N"), Until = Clock.UtcNow + LockDuration };
                _locks[message.SequenceNumber] = held;
                receipts.Add(Receipt(message, held.Token, new DateTimeOffset(held.Until)));
            }
            if (receipts.Count == 0)
            {
                AfterEmptyReceive?.Invoke();
            }
            return Task.FromResult<IReadOnlyList<CallDeadLetterMessage>>(receipts);
        }

        public Task CompleteAsync(CallDeadLetterMessage receipt, CancellationToken cancellationToken)
        {
            Operations.Add("complete:" + receipt.MessageId);
            var message = Locked(receipt);
            if (FailCompleteFor(message))
            {
                throw new ServiceBusException("Simulated failure to complete.", ServiceBusFailureReason.ServiceTimeout, QueueName, null);
            }
            _locks.Remove(message.SequenceNumber);
            DeadLetters.Remove(message);
            return Task.CompletedTask;
        }

        public Task AbandonAsync(CallDeadLetterMessage receipt, CancellationToken cancellationToken)
        {
            Operations.Add("abandon:" + receipt.MessageId);
            Abandoned.Add(receipt);
            cancellationToken.ThrowIfCancellationRequested();
            _locks.Remove(Locked(receipt).SequenceNumber);
            OnAbandon?.Invoke(receipt);
            return Task.CompletedTask;
        }

        public Task<bool> HasMessagesAfterAsync(long sequenceNumber, CancellationToken cancellationToken)
        {
            Operations.Add("peek-after");
            return Task.FromResult(Peek(sequenceNumber + 1, 1).Count > 0);
        }

        public Task SendAsync(CallReplayMessage message, CancellationToken cancellationToken)
        {
            var body = message.Body?.ToString();
            var source = DeadLetters.FirstOrDefault(m => m.Body?.ToString() == body);
            Operations.Add("send:" + source?.MessageId);
            if (source != null && FailSendFor(source))
            {
                throw new ServiceBusException("Simulated failure to send.", ServiceBusFailureReason.ServiceCommunicationProblem, QueueName, null);
            }
            Sent.Add(message);
            OnSent?.Invoke(message);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return default(ValueTask);
        }

        // The message a receipt is for, provided the receipt still holds its lock.
        private CallDeadLetterMessage Locked(CallDeadLetterMessage receipt)
        {
            var message = DeadLetters.FirstOrDefault(m => m.SequenceNumber == receipt.SequenceNumber);
            if (message == null || !_locks.TryGetValue(receipt.SequenceNumber, out var held) || held.Token != receipt.LockToken || held.Until <= Clock.UtcNow)
            {
                StaleSettlements++;
                throw new ServiceBusException("The lock supplied is invalid: it has expired, or the message was already removed.",
                    ServiceBusFailureReason.MessageLockLost, QueueName, null);
            }
            return message;
        }

        private static CallDeadLetterMessage Receipt(CallDeadLetterMessage message, string lockToken, DateTimeOffset lockedUntil)
        {
            return new CallDeadLetterMessage
            {
                MessageId = message.MessageId,
                SequenceNumber = message.SequenceNumber,
                EnqueuedTime = message.EnqueuedTime,
                DeadLetterReason = message.DeadLetterReason,
                Body = message.Body,
                ContentType = message.ContentType,
                CorrelationId = message.CorrelationId,
                Subject = message.Subject,
                ApplicationProperties = message.ApplicationProperties,
                LockToken = lockToken,
                LockedUntil = lockedUntil
            };
        }
    }
}