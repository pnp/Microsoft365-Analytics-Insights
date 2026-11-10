using Common.Entities.Calls;
using Common.Entities.Config;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The record the portal and the importer share for a request to reprocess or clear the Teams calls dead-letter queue
    /// (<see cref="CallDeadLetterRequest"/>), its timing rules and its store. In memory throughout: the runtime state table's
    /// own behaviour is <c>StateStoreAzuriteTests</c>'.
    /// </summary>
    [TestClass]
    public class CallDeadLetterRequestTests
    {
        internal static readonly DateTime Now = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        internal const string RequestId = "00000000000000000000000000000001";

        internal static CallDeadLetterRequest Waiting(string action = CallDeadLetterActions.Reprocess, DateTime? requestedUtc = null, string id = RequestId)
        {
            return new CallDeadLetterRequest { Id = id, Action = action, RequestedUtc = requestedUtc ?? Now };
        }

        [TestMethod]
        public async Task ARequest_RoundTripsThroughTheStore_WithItsResult_UnderStablePropertyNames()
        {
            var values = new InMemoryKeyValueStore();
            var store = new CallDeadLetterRequestStore(values, isDurable: true);
            var request = Waiting(CallDeadLetterActions.Clear);
            request.ClaimedUtc = Now.AddMinutes(1);
            request.FinishedUtc = Now.AddMinutes(7);
            request.Outcome = CallDeadLetterOutcomes.Partial;
            request.FailureCode = CallDeadLetterFailureCodes.TimeBudget;
            request.Result = new CallDeadLetterRunResult
            {
                Passes = 2,
                Scanned = 1500,
                Replayed = 0,
                ReplayedButNotRemoved = 0,
                Deleted = 1490,
                SendFailed = 0,
                DeleteFailed = 10,
                Skipped = new Dictionary<string, int> { [CallDeadLetterSkipCodes.OtherReason] = 3 },
                Reasons = new List<CallDeadLetterReasonCount>
                {
                    new CallDeadLetterReasonCount { Reason = "CallImportRetriesExhausted", Deleted = 1000, Left = 4 },
                    new CallDeadLetterReasonCount { Reason = null, Deleted = 490, Left = 6 }
                },
                StopCode = CallDeadLetterFailureCodes.TimeBudget
            };

            Assert.IsTrue(await store.TryReplaceAsync(request, null));
            var read = (await store.ReadAsync()).Request;

            Assert.AreEqual(RequestId, read.Id);
            Assert.AreEqual(CallDeadLetterActions.Clear, read.Action);
            Assert.AreEqual(Now, read.RequestedUtc);
            Assert.AreEqual(DateTimeKind.Utc, read.RequestedUtc.Kind);
            Assert.AreEqual(Now.AddMinutes(1), read.ClaimedUtc);
            Assert.AreEqual(Now.AddMinutes(7), read.FinishedUtc);
            Assert.AreEqual(CallDeadLetterOutcomes.Partial, read.Outcome);
            Assert.AreEqual(CallDeadLetterFailureCodes.TimeBudget, read.FailureCode);
            Assert.AreEqual(2, read.Result.Passes);
            Assert.AreEqual(1500, read.Result.Scanned);
            Assert.AreEqual(1490, read.Result.Deleted);
            Assert.AreEqual(10, read.Result.DeleteFailed);
            Assert.AreEqual(3, read.Result.Skipped[CallDeadLetterSkipCodes.OtherReason]);
            Assert.AreEqual("CallImportRetriesExhausted", read.Result.Reasons[0].Reason);
            Assert.AreEqual(4, read.Result.Reasons[0].Left);
            Assert.IsNull(read.Result.Reasons[1].Reason, "Dead-lettered without a reason.");
            Assert.AreEqual(CallDeadLetterFailureCodes.TimeBudget, read.Result.StopCode);

            // The stored names are the contract between the web app and the importer, which may run different builds.
            var stored = JObject.Parse(await values.GetStringAsync(CallDeadLetterRequestStore.Key));
            CollectionAssert.AreEquivalent(new[] { "id", "action", "requestedUtc", "claimedUtc", "finishedUtc", "outcome", "failureCode", "result" },
                stored.Properties().Select(p => p.Name).ToList());
            CollectionAssert.AreEquivalent(
                new[] { "passes", "scanned", "replayed", "replayedButNotRemoved", "deleted", "sendFailed", "deleteFailed", "skipped", "reasons", "stopCode" },
                ((JObject)stored["result"]).Properties().Select(p => p.Name).ToList());
            CollectionAssert.AreEquivalent(new[] { "reason", "replayed", "deleted", "left" }, ((JObject)stored["result"]["reasons"][0]).Properties().Select(p => p.Name).ToList());

            // A waiting request carries only what it has.
            Assert.IsTrue(await store.TryReplaceAsync(Waiting(), (await store.ReadAsync()).VersionToken));
            CollectionAssert.AreEquivalent(new[] { "id", "action", "requestedUtc" },
                JObject.Parse(await values.GetStringAsync(CallDeadLetterRequestStore.Key)).Properties().Select(p => p.Name).ToList());
        }

        [TestMethod]
        public void StateOf_WaitingThenRunningThenFinished_AndStaleWhenNotPickedUpOrNotFinishedInTime()
        {
            var waiting = Waiting();
            Assert.AreEqual(CallDeadLetterRequestStates.Waiting, CallDeadLetterRequestRules.StateOf(waiting, Now.AddMinutes(15)));
            Assert.IsTrue(CallDeadLetterRequestRules.IsPending(waiting, Now.AddMinutes(15)));
            Assert.AreEqual(CallDeadLetterRequestStates.NotPickedUp, CallDeadLetterRequestRules.StateOf(waiting, Now.AddMinutes(15).AddSeconds(1)));
            Assert.IsFalse(CallDeadLetterRequestRules.IsPending(waiting, Now.AddMinutes(15).AddSeconds(1)), "Not picked up in 15 minutes: it may be replaced.");

            var running = Waiting();
            running.ClaimedUtc = Now.AddMinutes(14);
            Assert.AreEqual(CallDeadLetterRequestStates.Running, CallDeadLetterRequestRules.StateOf(running, Now.AddMinutes(14).AddHours(2)));
            Assert.IsTrue(CallDeadLetterRequestRules.IsPending(running, Now.AddMinutes(14).AddHours(2)));
            Assert.AreEqual(CallDeadLetterRequestStates.NotFinished, CallDeadLetterRequestRules.StateOf(running, Now.AddMinutes(14).AddHours(2).AddSeconds(1)));
            Assert.IsFalse(CallDeadLetterRequestRules.IsPending(running, Now.AddMinutes(14).AddHours(2).AddSeconds(1)), "Claimed and not finished in 2 hours: it may be replaced.");

            var finished = Waiting();
            finished.ClaimedUtc = Now.AddMinutes(1);
            finished.FinishedUtc = Now.AddMinutes(3);
            Assert.AreEqual(CallDeadLetterRequestStates.Finished, CallDeadLetterRequestRules.StateOf(finished, Now.AddDays(30)));
            Assert.IsFalse(CallDeadLetterRequestRules.IsPending(finished, Now.AddMinutes(3)));
            Assert.IsFalse(CallDeadLetterRequestRules.IsPending(null, Now));
        }

        [TestMethod]
        public async Task Store_AMissingKeyIsNoRequest_ADamagedRowIsNoneButReplaceable_AndAnUnreachableStoreThrows()
        {
            var values = new InMemoryKeyValueStore();
            var store = new CallDeadLetterRequestStore(values, isDurable: true);

            var empty = await store.ReadAsync();
            Assert.IsNull(empty.Request);
            Assert.IsNull(empty.VersionToken);

            await values.SetStringAsync(CallDeadLetterRequestStore.Key, "{ not a request");
            var damaged = await store.ReadAsync();
            Assert.IsNull(damaged.Request);
            Assert.IsTrue(await store.TryReplaceAsync(Waiting(), damaged.VersionToken), "A damaged row must not block every future request.");

            await values.SetStringAsync(CallDeadLetterRequestStore.Key, "{\"id\":\"1\",\"action\":\"dryRun\",\"requestedUtc\":\"2026-11-10T09:00:00Z\"}");
            Assert.IsNull((await store.ReadAsync()).Request, "An action this build doesn't know is not carried out.");

            var unreachable = new CallDeadLetterRequestStore(new UnreachableKeyValueStore(), isDurable: true);
            await Assert.ThrowsExceptionAsync<CallDeadLetterStateUnavailableException>(() => unreachable.ReadAsync());
            await Assert.ThrowsExceptionAsync<CallDeadLetterStateUnavailableException>(() => unreachable.TryReplaceAsync(Waiting(), null));
        }

        [TestMethod]
        public async Task Store_OfTwoWritesFromTheSameRead_OnlyTheFirstLands()
        {
            var store = new CallDeadLetterRequestStore(new InMemoryKeyValueStore(), isDurable: true);
            var read = await store.ReadAsync();

            Assert.IsTrue(await store.TryReplaceAsync(Waiting(CallDeadLetterActions.Reprocess, id: "a"), read.VersionToken));
            Assert.IsFalse(await store.TryReplaceAsync(Waiting(CallDeadLetterActions.Clear, id: "b"), read.VersionToken));

            Assert.AreEqual("a", (await store.ReadAsync()).Request.Id);
        }

        /// <summary>Without a Storage connection string the store is the process's in-memory fallback - and says it is not durable.</summary>
        [TestMethod]
        public async Task ForConfig_WithoutStorage_FallsBackToMemory_AndSaysSo()
        {
            var store = CallDeadLetterRequestStore.ForConfig(Config(storage: null));

            Assert.IsFalse(store.IsDurable);
            Assert.IsNull((await store.ReadAsync()).Request);
            Assert.IsTrue(CallDeadLetterRequestStore.ForConfig(Config(storage: StorageConnectionString)).IsDurable);
        }

        [TestMethod]
        public void Action_IsReadInAnyCase_AndNothingElseIsOne()
        {
            Assert.AreEqual(CallDeadLetterActions.Reprocess, CallDeadLetterActions.Parse(" Reprocess "));
            Assert.AreEqual(CallDeadLetterActions.Clear, CallDeadLetterActions.Parse("CLEAR"));
            Assert.IsNull(CallDeadLetterActions.Parse("purge"));
            Assert.IsNull(CallDeadLetterActions.Parse(null));
        }

        internal const string StorageConnectionString = "DefaultEndpointsProtocol=https;AccountName=contosoanalytics;AccountKey=FAKEFAKEFAKE==;EndpointSuffix=core.windows.net";

        /// <summary>An AppConfig built without reading the test App.config.</summary>
        internal static AppConfig Config(string storage, bool calls = true, string serviceBus = "Endpoint=sb://contoso.servicebus.windows.net/;EntityPath=contoso-calls")
        {
            var config = (AppConfig)FormatterServices.GetUninitializedObject(typeof(AppConfig));
            config.TenantGUID = Guid.Empty;
            config.ConnectionStrings = (AppConnectionStrings)FormatterServices.GetUninitializedObject(typeof(AppConnectionStrings));
            config.ConnectionStrings.StorageConnectionString = storage;
            config.ConnectionStrings.ServiceBusConnectionString = serviceBus;
            config.ImportJobSettings = new Common.Entities.ImportTaskSettings { Calls = calls };
            return config;
        }
    }

    /// <summary>A store whose every operation fails, as an unreachable storage account's do.</summary>
    internal sealed class UnreachableKeyValueStore : IConditionalKeyValueStore
    {
        public string Description => "an unreachable store";

        private static Exception Failure() => new Azure.RequestFailedException(503, "Simulated: the storage account can't be reached.");

        public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) => throw Failure();
        public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) => throw Failure();
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => throw Failure();
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => throw Failure();
        public Task<VersionedValue> GetVersionedAsync(string key, CancellationToken cancellationToken = default) => throw Failure();
        public Task<bool> TrySetStringAsync(string key, string value, string expectedVersionToken, CancellationToken cancellationToken = default) => throw Failure();
        public Task<bool> TryDeleteAsync(string key, string expectedVersionToken, CancellationToken cancellationToken = default) => throw Failure();
    }
}
