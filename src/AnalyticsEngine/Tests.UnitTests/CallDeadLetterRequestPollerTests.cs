using Common.Entities.Calls;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    /// The importer's side of a portal request to reprocess or clear the Teams calls dead-letter queue
    /// (<see cref="CallDeadLetterRequestPoller"/>): with an in-memory state store, <see cref="FakeCallDeadLetterQueue"/> and a
    /// fixed clock. No Service Bus, no Azure Storage.
    /// </summary>
    [TestClass]
    public class CallDeadLetterRequestPollerTests
    {
        private static readonly DateTime Now = CallDeadLetterReplayTests.Now;
        private const string MaxDeliveryCountExceeded = "MaxDeliveryCountExceeded";

        private sealed class Importer
        {
            public FixedClock Clock;
            public InMemoryKeyValueStore Values;
            public CallDeadLetterRequestStore Store;
            public FakeCallDeadLetterQueue Queue;
            public RecordingLogger Logger = new RecordingLogger();
            public int Opened;
            public CallDeadLetterRequestPoller Poller;

            public async Task<CallDeadLetterRequest> RecordedAsync() => (await Store.ReadAsync()).Request;
        }

        private static Importer NewImporter(TimeSpan? budget = null, IConditionalKeyValueStore values = null, Func<ICallDeadLetterQueue> openQueue = null)
        {
            var importer = new Importer { Clock = new FixedClock(Now) };
            importer.Values = new InMemoryKeyValueStore(() => importer.Clock.UtcNow);
            importer.Store = new CallDeadLetterRequestStore(values ?? importer.Values, isDurable: true);
            importer.Queue = new FakeCallDeadLetterQueue(importer.Clock);
            importer.Poller = new CallDeadLetterRequestPoller(importer.Store, openQueue ?? (() =>
            {
                importer.Opened++;
                return importer.Queue;
            }), importer.Logger, importer.Clock, budget);
            return importer;
        }

        private static async Task RequestAsync(Importer importer, string action, DateTime? requestedUtc = null)
        {
            var current = await importer.Store.ReadAsync();
            Assert.IsTrue(await importer.Store.TryReplaceAsync(CallDeadLetterRequestTests.Waiting(action, requestedUtc ?? Now.AddMinutes(-1)), current.VersionToken));
        }

        private static void AddEveryKind(FakeCallDeadLetterQueue queue)
        {
            queue.AddDeadLetter("retries-exhausted-1", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddHours(-3));
            queue.AddDeadLetter("retries-exhausted-2", CallQueueRetryPolicy.RetriesExhaustedReason, Now.AddDays(-2));
            queue.AddDeadLetter("delivery-count", MaxDeliveryCountExceeded, Now.AddDays(-1));
            queue.AddDeadLetter("malformed", CallQueueMessageHandler.InvalidNotificationReason, Now.AddHours(-1), body: "not a notification");
            queue.AddDeadLetter("old", MaxDeliveryCountExceeded, Now.AddDays(-45));
        }

        [TestMethod]
        public async Task Reprocess_ReplaysWhatCanBeImported_LeavesTheRest_AndRecordsTheResult()
        {
            var importer = NewImporter();
            AddEveryKind(importer.Queue);
            await RequestAsync(importer, CallDeadLetterActions.Reprocess);

            Assert.IsTrue(await importer.Poller.RunOnceAsync());

            CollectionAssert.AreEquivalent(new[] { "malformed", "old" }, importer.Queue.DeadLetters.Select(m => m.MessageId).ToList(),
                "The replayer's defaults: what can never be imported, and what Graph no longer has, stay where they are.");
            Assert.AreEqual(3, importer.Queue.Sent.Count);
            Assert.AreEqual(1, importer.Opened);
            Assert.IsTrue(importer.Queue.Disposed);

            var recorded = await importer.RecordedAsync();
            Assert.AreEqual(Now, recorded.ClaimedUtc);
            Assert.AreEqual(Now, recorded.FinishedUtc);
            Assert.AreEqual(CallDeadLetterRequestStates.Finished, CallDeadLetterRequestRules.StateOf(recorded, Now));
            Assert.AreEqual(CallDeadLetterOutcomes.Succeeded, recorded.Outcome);
            Assert.IsNull(recorded.FailureCode);
            Assert.AreEqual(1, recorded.Result.Passes);
            Assert.AreEqual(5, recorded.Result.Scanned);
            Assert.AreEqual(3, recorded.Result.Replayed);
            Assert.AreEqual(0, recorded.Result.Deleted);
            Assert.AreEqual(1, recorded.Result.Skipped[CallDeadLetterSkipCodes.NeverImportable]);
            Assert.AreEqual(1, recorded.Result.Skipped[CallDeadLetterSkipCodes.CallRecordNoLongerInGraph]);
            var retriesExhausted = recorded.Result.Reasons.Single(r => r.Reason == CallQueueRetryPolicy.RetriesExhaustedReason);
            Assert.AreEqual(2, retriesExhausted.Replayed);
            Assert.AreEqual(0, retriesExhausted.Left);
            var deliveryCount = recorded.Result.Reasons.Single(r => r.Reason == MaxDeliveryCountExceeded);
            Assert.AreEqual(1, deliveryCount.Replayed);
            Assert.AreEqual(1, deliveryCount.Left);
            Assert.AreEqual(1, recorded.Result.Reasons.Single(r => r.Reason == CallQueueMessageHandler.InvalidNotificationReason).Left);

            var logged = importer.Logger.Entries.Select(e => e.Message).ToList();
            Assert.AreEqual(1, logged.Count(m => m.StartsWith("Teams calls dead-letter request from the portal: reprocess")), "One line says the run was requested from the portal.");
            Assert.IsTrue(logged.Contains("Teams calls dead-letter replay finished."), "The replayer's own summary is logged as for the operator's replay.");
            Assert.IsTrue(logged.Any(m => m.StartsWith($"Teams calls dead-letter request from the portal {CallDeadLetterRequestTests.RequestId} (reprocess) finished: succeeded")));
            Assert.IsFalse(logged.Any(m => m.Contains(CallDeadLetterReplayTests.ClientState) || m.Contains("communications/callRecords")), "No message content is logged.");
        }

        [TestMethod]
        public async Task Clear_DeletesEveryDeadLetter_WhateverItsReasonOrAge_AndRecordsTheResult()
        {
            var importer = NewImporter();
            AddEveryKind(importer.Queue);
            await RequestAsync(importer, CallDeadLetterActions.Clear);

            Assert.IsTrue(await importer.Poller.RunOnceAsync());

            Assert.AreEqual(0, importer.Queue.DeadLetters.Count);
            Assert.AreEqual(0, importer.Queue.Sent.Count, "Clearing replays nothing.");
            var recorded = await importer.RecordedAsync();
            Assert.AreEqual(CallDeadLetterOutcomes.Succeeded, recorded.Outcome);
            Assert.AreEqual(5, recorded.Result.Deleted);
            Assert.AreEqual(0, recorded.Result.Replayed);
            Assert.AreEqual(0, recorded.Result.Skipped.Count);
            Assert.AreEqual(2, recorded.Result.Reasons.Single(r => r.Reason == MaxDeliveryCountExceeded).Deleted);
            Assert.IsTrue(recorded.Result.Reasons.All(r => r.Left == 0));
            Assert.IsTrue(importer.Logger.Entries.Any(e => e.Message.StartsWith("Teams calls dead-letter request from the portal: clear (DELETE every dead-lettered call")));
        }

        /// <summary>
        /// A backlog bigger than one pass is worked through in passes, each with the replayer's default limit, until the end of
        /// the dead-letter queue - or until the time budget is spent, when what was done is recorded as partial.
        /// </summary>
        [TestMethod]
        public async Task Reprocess_RepeatsWhileThereIsMore_WithinItsTimeBudget()
        {
            var importer = NewImporter();
            for (var i = 0; i < 2500; i++)
            {
                importer.Queue.AddDeadLetter($"m{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
            }
            await RequestAsync(importer, CallDeadLetterActions.Reprocess);

            await importer.Poller.RunOnceAsync();

            var recorded = await importer.RecordedAsync();
            Assert.AreEqual(CallDeadLetterOutcomes.Succeeded, recorded.Outcome);
            Assert.AreEqual(3, recorded.Result.Passes, "1,000, 1,000 and 500.");
            Assert.AreEqual(2500, recorded.Result.Replayed);
            Assert.AreEqual(0, importer.Queue.DeadLetters.Count);

            var outOfTime = NewImporter();
            for (var i = 0; i < 2500; i++)
            {
                outOfTime.Queue.AddDeadLetter($"m{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
            }
            outOfTime.Queue.BeforeReceive = receive => outOfTime.Clock.Advance(TimeSpan.FromSeconds(100));
            await RequestAsync(outOfTime, CallDeadLetterActions.Reprocess);

            await outOfTime.Poller.RunOnceAsync();

            recorded = await outOfTime.RecordedAsync();
            Assert.AreEqual(CallDeadLetterOutcomes.Partial, recorded.Outcome);
            Assert.AreEqual(CallDeadLetterFailureCodes.TimeBudget, recorded.FailureCode);
            Assert.AreEqual(1, recorded.Result.Passes, "Its first pass took over 15 minutes, so it stopped there.");
            Assert.AreEqual(1000, recorded.Result.Replayed);
            Assert.AreEqual(1500, outOfTime.Queue.DeadLetters.Count);
        }

        /// <summary>
        /// Two importer instances read the same waiting request at the same moment. Only one claim lands, so the calls queue
        /// is opened - and the request carried out - once.
        /// </summary>
        [TestMethod]
        public async Task ARequest_IsClaimedOnce_WhenTwoImporterInstancesRaceForIt()
        {
            var clock = new FixedClock(Now);
            var values = new InMemoryKeyValueStore(() => clock.UtcNow);
            var racing = new RacingKeyValueStore(values, readers: 2);
            var queue = new FakeCallDeadLetterQueue(clock);
            AddEveryKind(queue);
            var opened = 0;
            Func<ICallDeadLetterQueue> open = () =>
            {
                Interlocked.Increment(ref opened);
                return queue;
            };
            var first = new CallDeadLetterRequestPoller(new CallDeadLetterRequestStore(racing, isDurable: true), open, new RecordingLogger(), clock);
            var second = new CallDeadLetterRequestPoller(new CallDeadLetterRequestStore(racing, isDurable: true), open, new RecordingLogger(), clock);
            Assert.IsTrue(await new CallDeadLetterRequestStore(values, isDurable: true).TryReplaceAsync(CallDeadLetterRequestTests.Waiting(), null));

            var claimed = await Task.WhenAll(first.RunOnceAsync(), second.RunOnceAsync());

            Assert.AreEqual(1, claimed.Count(c => c), "Exactly one instance claims it.");
            Assert.AreEqual(1, opened);
            Assert.AreEqual(3, queue.Sent.Count, "Each call is replayed once.");
            Assert.AreEqual(CallDeadLetterOutcomes.Succeeded, (await new CallDeadLetterRequestStore(values, isDurable: true).ReadAsync()).Request.Outcome);
        }

        /// <summary>Only a waiting request is claimed: not a finished one, not one another instance is running, and not a stale one.</summary>
        [TestMethod]
        public async Task OnlyAWaitingRequestIsClaimed_NeverAFinishedRunningOrStaleOne()
        {
            var finished = CallDeadLetterRequestTests.Waiting(CallDeadLetterActions.Clear, Now.AddMinutes(-10));
            finished.ClaimedUtc = Now.AddMinutes(-9);
            finished.FinishedUtc = Now.AddMinutes(-8);
            finished.Outcome = CallDeadLetterOutcomes.Succeeded;

            var running = CallDeadLetterRequestTests.Waiting(CallDeadLetterActions.Clear, Now.AddMinutes(-10));
            running.ClaimedUtc = Now.AddMinutes(-9);

            var notPickedUp = CallDeadLetterRequestTests.Waiting(CallDeadLetterActions.Clear, Now.AddMinutes(-16));

            foreach (var request in new[] { finished, running, notPickedUp })
            {
                var importer = NewImporter();
                AddEveryKind(importer.Queue);
                Assert.IsTrue(await importer.Store.TryReplaceAsync(request, null));
                var before = await importer.Values.GetStringAsync(CallDeadLetterRequestStore.Key);

                Assert.IsFalse(await importer.Poller.RunOnceAsync());

                Assert.AreEqual(0, importer.Opened, CallDeadLetterRequestRules.StateOf(request, Now));
                Assert.AreEqual(5, importer.Queue.DeadLetters.Count);
                Assert.AreEqual(before, await importer.Values.GetStringAsync(CallDeadLetterRequestStore.Key), "The request is left exactly as it was.");
            }

            var none = NewImporter();
            Assert.IsFalse(await none.Poller.RunOnceAsync());
            Assert.AreEqual(0, none.Opened);
        }

        /// <summary>
        /// The importer stopping mid-run cancels the replay. Nothing is recorded, so the request stays claimed and goes stale two
        /// hours later - when the portal lets it be requested again.
        /// </summary>
        [TestMethod]
        public async Task ACancelledRun_LeavesTheRequestClaimed_SoItGoesStale()
        {
            var importer = NewImporter();
            AddEveryKind(importer.Queue);
            await RequestAsync(importer, CallDeadLetterActions.Reprocess);
            using (var stopping = new CancellationTokenSource())
            {
                importer.Queue.OnSent = sent => stopping.Cancel();

                Assert.IsTrue(await importer.Poller.RunOnceAsync(stopping.Token));
            }

            Assert.AreEqual(1, importer.Queue.Sent.Count, "It stopped after the message in hand.");
            var recorded = await importer.RecordedAsync();
            Assert.AreEqual(Now, recorded.ClaimedUtc);
            Assert.IsNull(recorded.FinishedUtc);
            Assert.IsNull(recorded.Outcome);
            Assert.AreEqual(CallDeadLetterRequestStates.Running, CallDeadLetterRequestRules.StateOf(recorded, Now.AddHours(2)));
            Assert.AreEqual(CallDeadLetterRequestStates.NotFinished, CallDeadLetterRequestRules.StateOf(recorded, Now.AddHours(2).AddMinutes(1)));
            Assert.IsTrue(importer.Logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("was interrupted: the importer is stopping")));

            importer.Clock.Advance(TimeSpan.FromHours(3));
            Assert.IsFalse(await importer.Poller.RunOnceAsync(), "A stale request is not resumed by surprise; it is requested again from the portal.");
        }

        [TestMethod]
        public async Task WhenNothingCanBeDone_TheRequestIsRecordedAsFailed_WithWhy()
        {
            var unopenable = NewImporter(openQueue: () => throw new InvalidOperationException("Simulated: Service Bus can't be reached."));
            await RequestAsync(unopenable, CallDeadLetterActions.Clear);
            Assert.IsTrue(await unopenable.Poller.RunOnceAsync());
            var recorded = await unopenable.RecordedAsync();
            Assert.AreEqual(CallDeadLetterOutcomes.Failed, recorded.Outcome);
            Assert.AreEqual(CallDeadLetterFailureCodes.QueueUnavailable, recorded.FailureCode);
            Assert.IsNull(recorded.Result);
            Assert.IsFalse(unopenable.Logger.Entries.Any(e => e.Message.Contains("Simulated")), "The exception's message, which can carry a host name, isn't logged.");

            var unsendable = NewImporter();
            for (var i = 0; i < 20; i++)
            {
                unsendable.Queue.AddDeadLetter($"m{i}", MaxDeliveryCountExceeded, Now.AddHours(-1));
            }
            unsendable.Queue.FailSendFor = m => true;
            await RequestAsync(unsendable, CallDeadLetterActions.Reprocess);
            Assert.IsTrue(await unsendable.Poller.RunOnceAsync());
            recorded = await unsendable.RecordedAsync();
            Assert.AreEqual(CallDeadLetterOutcomes.Failed, recorded.Outcome);
            Assert.AreEqual(CallDeadLetterFailureCodes.SendFailures, recorded.FailureCode);
            Assert.AreEqual(CallDeadLetterReplayer.MaxConsecutiveSendFailures, recorded.Result.SendFailed);
            Assert.AreEqual(20, unsendable.Queue.DeadLetters.Count);
        }

        [TestMethod]
        public async Task AStoreThatCantBeRead_IsWarnedAboutOnceAnHour_AndNothingRuns()
        {
            var importer = NewImporter(values: new UnreachableKeyValueStore());

            Assert.IsFalse(await importer.Poller.RunOnceAsync());
            importer.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.IsFalse(await importer.Poller.RunOnceAsync());
            importer.Clock.Advance(TimeSpan.FromHours(1));
            Assert.IsFalse(await importer.Poller.RunOnceAsync());

            Assert.AreEqual(2, importer.Logger.Entries.Count(e => e.Level == LogLevel.Warning), "The first failure, then one an hour later.");
            Assert.IsTrue(importer.Logger.Entries.All(e => !e.Message.Contains("Simulated")));
            Assert.AreEqual(0, importer.Opened);
        }

        /// <summary>The poller runs only where the portal can reach it: calls import on, Service Bus configured, durable state.</summary>
        [TestMethod]
        public void StartIfEnabled_DoesNotPoll_WithoutTheCallsImportServiceBusOrStorage()
        {
            Assert.IsNull(CallDeadLetterRequestPoller.StartIfEnabled(CallDeadLetterRequestTests.Config(CallDeadLetterRequestTests.StorageConnectionString, calls: false)));
            Assert.IsNull(CallDeadLetterRequestPoller.StartIfEnabled(CallDeadLetterRequestTests.Config(CallDeadLetterRequestTests.StorageConnectionString, serviceBus: null)));
            Assert.IsNull(CallDeadLetterRequestPoller.StartIfEnabled(CallDeadLetterRequestTests.Config(storage: null)),
                "Without Storage the request store is in memory, where the web app can't record anything this process would see.");
        }

        /// <summary>
        /// Holds the first <c>readers</c> reads until they have all been made, so each sees the row as it was before any of them
        /// wrote: two importer instances polling at the same moment.
        /// </summary>
        private sealed class RacingKeyValueStore : IConditionalKeyValueStore
        {
            private readonly IConditionalKeyValueStore _inner;
            private readonly int _readers;
            private readonly TaskCompletionSource<bool> _allRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _reads;

            public RacingKeyValueStore(IConditionalKeyValueStore inner, int readers)
            {
                _inner = inner;
                _readers = readers;
            }

            public string Description => _inner.Description;

            public async Task<VersionedValue> GetVersionedAsync(string key, CancellationToken cancellationToken = default)
            {
                var value = await _inner.GetVersionedAsync(key, cancellationToken);
                var read = Interlocked.Increment(ref _reads);
                if (read == _readers) _allRead.TrySetResult(true);
                if (read <= _readers) await _allRead.Task;
                return value;
            }

            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) => _inner.GetStringAsync(key, cancellationToken);
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) => _inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);
            public Task<bool> TrySetStringAsync(string key, string value, string expectedVersionToken, CancellationToken cancellationToken = default) => _inner.TrySetStringAsync(key, value, expectedVersionToken, cancellationToken);
            public Task<bool> TryDeleteAsync(string key, string expectedVersionToken, CancellationToken cancellationToken = default) => _inner.TryDeleteAsync(key, expectedVersionToken, cancellationToken);
        }
    }
}
