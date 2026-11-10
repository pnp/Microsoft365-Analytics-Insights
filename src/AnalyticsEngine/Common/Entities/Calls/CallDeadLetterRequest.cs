using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.Calls
{
    /// <summary>What an administrator can ask the importer to do with the Teams calls queue's dead-letter queue.</summary>
    public static class CallDeadLetterActions
    {
        /// <summary>
        /// Send the dead-lettered call notifications back to the calls queue, for the importer to try again: the replayer's
        /// default options, which leave what can never be imported where it is.
        /// </summary>
        public const string Reprocess = "reprocess";

        /// <summary>Delete every dead-lettered call notification, whatever its reason or age. Permanent: those calls are never imported.</summary>
        public const string Clear = "clear";

        /// <summary>The action <paramref name="value"/> names, in any case; null for anything else.</summary>
        public static string Parse(string value)
        {
            var trimmed = value?.Trim();
            if (string.Equals(trimmed, Reprocess, StringComparison.OrdinalIgnoreCase)) return Reprocess;
            if (string.Equals(trimmed, Clear, StringComparison.OrdinalIgnoreCase)) return Clear;
            return null;
        }
    }

    /// <summary>How a request the importer finished went.</summary>
    public static class CallDeadLetterOutcomes
    {
        /// <summary>It went through the whole dead-letter queue, and everything it looked at was replayed, deleted or deliberately left.</summary>
        public const string Succeeded = "succeeded";

        /// <summary>It did some of the work, then stopped or failed part-way; <see cref="CallDeadLetterRequest.FailureCode"/> says why.</summary>
        public const string Partial = "partial";

        /// <summary>It did nothing; <see cref="CallDeadLetterRequest.FailureCode"/> says why.</summary>
        public const string Failed = "failed";
    }

    /// <summary>Where a request stands, worked out from its times (<see cref="CallDeadLetterRequestRules.StateOf"/>).</summary>
    public static class CallDeadLetterRequestStates
    {
        /// <summary>Recorded, and waiting for the importer to pick it up.</summary>
        public const string Waiting = "waiting";

        /// <summary>Claimed by an importer instance, which is carrying it out.</summary>
        public const string Running = "running";

        /// <summary>The importer recorded its outcome.</summary>
        public const string Finished = "finished";

        /// <summary>Stale: no importer picked it up within <see cref="CallDeadLetterRequestRules.PickUpWithin"/>, so none will.</summary>
        public const string NotPickedUp = "notPickedUp";

        /// <summary>
        /// Stale: claimed, but not finished within <see cref="CallDeadLetterRequestRules.FinishWithin"/> - the importer was
        /// stopped or restarted while carrying it out.
        /// </summary>
        public const string NotFinished = "notFinished";
    }

    /// <summary>
    /// Why a request did not succeed, or why a run stopped early. Stable codes: the portal maps each to a sentence in the
    /// reader's language and shows one it does not know as it is.
    /// </summary>
    public static class CallDeadLetterFailureCodes
    {
        /// <summary>The importer couldn't open the Teams calls queue (Service Bus unreachable, or the runtime identity refused).</summary>
        public const string QueueUnavailable = "queueUnavailable";

        /// <summary>Reading the dead-letter queue failed.</summary>
        public const string ReadFailed = "readFailed";

        /// <summary>Several sends back to the calls queue failed in a row, so the run stopped.</summary>
        public const string SendFailures = "sendFailures";

        /// <summary>Some sends back to the calls queue failed; those messages stay in the dead-letter queue.</summary>
        public const string SendFailed = "sendFailed";

        /// <summary>Some deletes failed; those messages stay in the dead-letter queue.</summary>
        public const string DeleteFailed = "deleteFailed";

        /// <summary>Messages the run has not read are locked by something else - another replay, or Service Bus Explorer.</summary>
        public const string LockedElsewhere = "lockedElsewhere";

        /// <summary>The messages a run holds at the head of the dead-letter queue kept losing their locks before it got past them.</summary>
        public const string LocksLapsing = "locksLapsing";

        /// <summary>A dry run's browse returned only messages it had already looked at.</summary>
        public const string BrowseRepeated = "browseRepeated";

        /// <summary>The run was cancelled.</summary>
        public const string Cancelled = "cancelled";

        /// <summary>The run used its time budget (<see cref="CallDeadLetterRequestRules.RunBudget"/>) with more left to do.</summary>
        public const string TimeBudget = "timeBudget";

        /// <summary>The run looked at as many messages as it may before reaching the end of the dead-letter queue.</summary>
        public const string ScanLimit = "scanLimit";

        /// <summary>Anything else: the importer's log says what.</summary>
        public const string Unexpected = "unexpectedError";
    }

    /// <summary>Why the portal can't offer the actions. Stable codes, as <see cref="CallDeadLetterFailureCodes"/>.</summary>
    public static class CallDeadLetterUnavailableReasons
    {
        /// <summary>The Teams calls import is switched off, so there is no calls queue to work on.</summary>
        public const string CallsImportDisabled = "callsImportDisabled";

        /// <summary>No <c>ServiceBus</c> connection string: there is no calls queue.</summary>
        public const string ServiceBusNotConfigured = "serviceBusNotConfigured";

        /// <summary>
        /// No <c>Storage</c> connection string, so the web app could only record a request in its own memory, where the
        /// importer - another process - can't see it.
        /// </summary>
        public const string StateStoreInMemory = "stateStoreInMemory";
    }

    /// <summary>Why a run left a dead-lettered message where it is. Stable codes for <see cref="CallDeadLetterRunResult.Skipped"/>.</summary>
    public static class CallDeadLetterSkipCodes
    {
        public const string OtherReason = "otherReason";
        public const string NeverImportable = "neverImportable";
        public const string CallRecordNoLongerInGraph = "callRecordNoLongerInGraph";
        public const string ReplayLimitReached = "replayLimitReached";
        public const string ReplayedInThisRun = "replayedInThisRun";
    }

    /// <summary>
    /// An administrator's request, from the portal, to reprocess or clear the Teams calls dead-letter queue - and, once the
    /// importer has carried it out, what happened. The latest one is the only one kept (<see cref="CallDeadLetterRequestStore"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The portal records it; the importer, which owns the Service Bus client and the replay, claims it, carries it out and
    /// records the result; the portal shows it. Holds no message body, call id, message id, user name or other tenant data:
    /// times, an action, counts and stable codes only. Not who asked, either.
    /// </para>
    /// <para>Stored as JSON. Property names are part of the stored data: add new ones, never rename.</para>
    /// </remarks>
    public sealed class CallDeadLetterRequest
    {
        /// <summary>Identifies the request: a new GUID ("N" format) each time one is recorded.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary><see cref="CallDeadLetterActions.Reprocess"/> or <see cref="CallDeadLetterActions.Clear"/>.</summary>
        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("requestedUtc")]
        public DateTime RequestedUtc { get; set; }

        /// <summary>When an importer instance claimed it; null while it waits.</summary>
        [JsonProperty("claimedUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTime? ClaimedUtc { get; set; }

        /// <summary>When the importer recorded its outcome; null until then.</summary>
        [JsonProperty("finishedUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTime? FinishedUtc { get; set; }

        /// <summary>One of <see cref="CallDeadLetterOutcomes"/> once finished.</summary>
        [JsonProperty("outcome", NullValueHandling = NullValueHandling.Ignore)]
        public string Outcome { get; set; }

        /// <summary>One of <see cref="CallDeadLetterFailureCodes"/> when the outcome is not <see cref="CallDeadLetterOutcomes.Succeeded"/>.</summary>
        [JsonProperty("failureCode", NullValueHandling = NullValueHandling.Ignore)]
        public string FailureCode { get; set; }

        /// <summary>What the run did; null until finished, and when it failed before it looked at anything.</summary>
        [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
        public CallDeadLetterRunResult Result { get; set; }

        public CallDeadLetterRequest Clone()
        {
            return JsonConvert.DeserializeObject<CallDeadLetterRequest>(JsonConvert.SerializeObject(this));
        }
    }

    /// <summary>
    /// The counts a finished request reports, from the replayer's summaries. A request may take several passes over the
    /// dead-letter queue; replays and deletes add up across them, but a message left where it is comes round again on every
    /// pass, so what was left is the most any pass left rather than a sum.
    /// </summary>
    public sealed class CallDeadLetterRunResult
    {
        /// <summary>How many times the replayer went over the dead-letter queue.</summary>
        [JsonProperty("passes")]
        public int Passes { get; set; }

        /// <summary>Dead-lettered messages looked at, added up across passes: a message left where it is is counted once per pass.</summary>
        [JsonProperty("scanned")]
        public int Scanned { get; set; }

        /// <summary>Sent back to the calls queue.</summary>
        [JsonProperty("replayed")]
        public int Replayed { get; set; }

        /// <summary>Of <see cref="Replayed"/>, those whose dead-lettered original couldn't then be deleted: a later run may replay them again, harmlessly.</summary>
        [JsonProperty("replayedButNotRemoved")]
        public int ReplayedButNotRemoved { get; set; }

        /// <summary>Deleted from the dead-letter queue.</summary>
        [JsonProperty("deleted")]
        public int Deleted { get; set; }

        /// <summary>Couldn't be sent back, so left in the dead-letter queue.</summary>
        [JsonProperty("sendFailed")]
        public int SendFailed { get; set; }

        /// <summary>Couldn't be deleted, so left in the dead-letter queue.</summary>
        [JsonProperty("deleteFailed")]
        public int DeleteFailed { get; set; }

        /// <summary>Left in the dead-letter queue on purpose, by <see cref="CallDeadLetterSkipCodes"/> code.</summary>
        [JsonProperty("skipped")]
        public Dictionary<string, int> Skipped { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// What became of the messages, by the reason they were dead-lettered with - at most
        /// <see cref="CallDeadLetterRequestRules.MaxReasons"/> reasons, most messages first.
        /// </summary>
        [JsonProperty("reasons")]
        public List<CallDeadLetterReasonCount> Reasons { get; set; } = new List<CallDeadLetterReasonCount>();

        /// <summary>Why the last pass stopped before it had looked at all it should have (<see cref="CallDeadLetterFailureCodes"/>); null when it didn't.</summary>
        [JsonProperty("stopCode", NullValueHandling = NullValueHandling.Ignore)]
        public string StopCode { get; set; }
    }

    /// <summary>The messages a request dealt with that were dead-lettered with one reason.</summary>
    public sealed class CallDeadLetterReasonCount
    {
        /// <summary>
        /// The dead-letter reason as Service Bus holds it: one of the importer's (<c>CallImportRetriesExhausted</c>, ...) or
        /// Service Bus's own (<c>MaxDeliveryCountExceeded</c>). Null for messages dead-lettered without one. A code, not text.
        /// </summary>
        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("replayed")]
        public int Replayed { get; set; }

        [JsonProperty("deleted")]
        public int Deleted { get; set; }

        /// <summary>Left in the dead-letter queue, skipped or failed: the most any one pass left.</summary>
        [JsonProperty("left")]
        public int Left { get; set; }
    }

    /// <summary>The timing rules both the portal and the importer apply to a request.</summary>
    public static class CallDeadLetterRequestRules
    {
        /// <summary>A request no importer picked up in this time is stale: no importer will run it, and it may be replaced.</summary>
        public static readonly TimeSpan PickUpWithin = TimeSpan.FromMinutes(15);

        /// <summary>A claimed request not finished in this time is stale: its importer was stopped, and it may be replaced.</summary>
        public static readonly TimeSpan FinishWithin = TimeSpan.FromHours(2);

        /// <summary>How long the importer works on one request before it stops and records what it did.</summary>
        public static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(15);

        /// <summary>How often the importer looks for a new request.</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

        /// <summary>The most dead-letter reasons a result lists; a longer reason is cut to <see cref="MaxReasonLength"/>.</summary>
        public const int MaxReasons = 20;

        public const int MaxReasonLength = 100;

        /// <summary>Where <paramref name="request"/> stands at <paramref name="nowUtc"/>: one of <see cref="CallDeadLetterRequestStates"/>.</summary>
        public static string StateOf(CallDeadLetterRequest request, DateTime nowUtc)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.FinishedUtc.HasValue) return CallDeadLetterRequestStates.Finished;
            if (!request.ClaimedUtc.HasValue)
            {
                return nowUtc - request.RequestedUtc > PickUpWithin ? CallDeadLetterRequestStates.NotPickedUp : CallDeadLetterRequestStates.Waiting;
            }
            return nowUtc - request.ClaimedUtc.Value > FinishWithin ? CallDeadLetterRequestStates.NotFinished : CallDeadLetterRequestStates.Running;
        }

        /// <summary>Waiting or running, and not stale: another request must wait for it.</summary>
        public static bool IsPending(CallDeadLetterRequest request, DateTime nowUtc)
        {
            if (request == null) return false;
            var state = StateOf(request, nowUtc);
            return state == CallDeadLetterRequestStates.Waiting || state == CallDeadLetterRequestStates.Running;
        }
    }
}
