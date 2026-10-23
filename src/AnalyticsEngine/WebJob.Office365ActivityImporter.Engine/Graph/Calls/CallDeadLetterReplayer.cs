using Azure.Messaging.ServiceBus;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>A dead-lettered call notification, as <see cref="CallDeadLetterReplayer"/> sees it.</summary>
    /// <remarks>
    /// <see cref="Body"/> is the notification Graph posted, which carries the webhook subscription's <c>clientState</c>:
    /// a replay copies it unchanged and never reads or logs it.
    /// </remarks>
    public sealed class CallDeadLetterMessage
    {
        public string MessageId { get; set; }

        /// <summary>Identifies the message within the dead-letter queue.</summary>
        public long SequenceNumber { get; set; }

        public DateTimeOffset EnqueuedTime { get; set; }

        /// <summary>
        /// Why it was dead-lettered: one of the importer's reasons, Service Bus's own (<c>MaxDeliveryCountExceeded</c>), or
        /// null when it was dead-lettered without one, as the importer did before it gave reasons.
        /// </summary>
        public string DeadLetterReason { get; set; }

        public BinaryData Body { get; set; }
        public string ContentType { get; set; }
        public string CorrelationId { get; set; }
        public string Subject { get; set; }

        public IReadOnlyDictionary<string, object> ApplicationProperties { get; set; } = new Dictionary<string, object>();

        /// <summary>Identifies this receipt's lock: each time a message is received it is locked afresh, with a new token.</summary>
        public string LockToken { get; set; }

        /// <summary>When this receipt's lock lapses and the message is back in the dead-letter queue (default when peeked).</summary>
        public DateTimeOffset LockedUntil { get; set; }

        /// <summary>The message as Service Bus delivered it, for <see cref="ServiceBusCallDeadLetterQueue"/> to settle.</summary>
        internal ServiceBusReceivedMessage Received { get; set; }
    }

    /// <summary>The new message a replay sends to the calls queue.</summary>
    public sealed class CallReplayMessage
    {
        public string MessageId { get; set; }
        public BinaryData Body { get; set; }
        public string ContentType { get; set; }
        public string CorrelationId { get; set; }
        public string Subject { get; set; }
        public IDictionary<string, object> ApplicationProperties { get; } = new Dictionary<string, object>();
    }

    /// <summary>The calls queue's dead-letter queue - never the queue itself, whose messages the importer is processing.</summary>
    public interface ICallDeadLetterReceiver
    {
        /// <summary>
        /// Up to <paramref name="maxMessages"/> dead-lettered messages from <paramref name="fromSequenceNumber"/> on, locked or
        /// not, without locking or changing them. Empty when there are none. Always given a start: Service Bus's own
        /// "carry on from the last peek" is moved by every peek, whatever it was for.
        /// </summary>
        Task<IReadOnlyList<CallDeadLetterMessage>> PeekAsync(long fromSequenceNumber, int maxMessages, CancellationToken cancellationToken);

        /// <summary>
        /// Receives up to <paramref name="maxMessages"/> dead-lettered messages in PeekLock mode: each stays in the
        /// dead-letter queue, locked, until it is completed or abandoned, or its lock expires. Empty when none are available.
        /// </summary>
        Task<IReadOnlyList<CallDeadLetterMessage>> ReceiveAsync(int maxMessages, CancellationToken cancellationToken);

        /// <summary>Deletes a received message from the dead-letter queue.</summary>
        Task CompleteAsync(CallDeadLetterMessage message, CancellationToken cancellationToken);

        /// <summary>Releases a received message's lock, leaving it in the dead-letter queue.</summary>
        Task AbandonAsync(CallDeadLetterMessage message, CancellationToken cancellationToken);

        /// <summary>
        /// Whether the dead-letter queue holds any message after <paramref name="sequenceNumber"/>, locked or not: anything a
        /// run that has looked at messages up to it has yet to reach. Browses a single message; changes nothing.
        /// </summary>
        Task<bool> HasMessagesAfterAsync(long sequenceNumber, CancellationToken cancellationToken);
    }

    /// <summary>Sends to the calls queue itself, where the importer picks messages up.</summary>
    public interface ICallReplaySender
    {
        Task SendAsync(CallReplayMessage message, CancellationToken cancellationToken);
    }

    /// <summary>The calls queue as a replay uses it: its dead-letter queue to read, the queue itself to send to.</summary>
    public interface ICallDeadLetterQueue : ICallDeadLetterReceiver, ICallReplaySender, IAsyncDisposable
    {
        string QueueName { get; }
    }

    /// <summary>What one replay run should do.</summary>
    public sealed class CallDeadLetterReplayOptions
    {
        public const int DefaultMaxMessages = 1000;
        public const int MaxMessagesLimit = 10000;
        public const int MinimumDefaultScanLimit = 10000;
        public const int ScanLimitLimit = 100000;

        /// <summary>
        /// The most dead-lettered messages to replay - or try to - in one run. Messages skipped or deleted don't count, so
        /// a run walks past them to the calls behind.
        /// </summary>
        public int MaxMessages { get; set; } = DefaultMaxMessages;

        /// <summary>
        /// The most dead-lettered messages to look at in one run, whatever becomes of them; null for
        /// <see cref="EffectiveScanLimit"/>'s default.
        /// </summary>
        public int? ScanLimit { get; set; }

        /// <summary><see cref="ScanLimit"/>, or by default ten times <see cref="MaxMessages"/> and at least 10,000.</summary>
        public int EffectiveScanLimit => ScanLimit ?? Math.Min(ScanLimitLimit, Math.Max(MinimumDefaultScanLimit, MaxMessages * 10));

        /// <summary>Only count and summarise: nothing is sent, deleted or abandoned, and no message is locked.</summary>
        public bool DryRun { get; set; }

        /// <summary>Only messages dead-lettered with this reason (any case); null for every reason.</summary>
        public string Reason { get; set; }

        /// <summary>Also replay messages the importer found can never be imported (<see cref="CallDeadLetterReplayer.IsNeverImportable"/>).</summary>
        public bool IncludeInvalid { get; set; }

        /// <summary>
        /// Delete dead-lettered notifications first queued over 30 days ago: Graph no longer has their call records, so they
        /// can never be imported. Never the default.
        /// </summary>
        public bool PurgeUnimportable { get; set; }

        /// <summary>
        /// Delete dead-lettered notifications the importer found can never be imported
        /// (<see cref="CallDeadLetterReplayer.IsNeverImportable"/>). Never the default.
        /// </summary>
        public bool PurgeInvalid { get; set; }
    }

    /// <summary>Why a dead-lettered message was left where it is.</summary>
    public enum CallDeadLetterSkipReason
    {
        /// <summary>Dead-lettered for a reason other than the one asked for.</summary>
        OtherReason,

        /// <summary>The importer found it can never be imported as it stands; <c>--include-invalid</c> replays it anyway.</summary>
        NeverImportable,

        /// <summary>First queued over 30 days ago: Graph keeps a call record for 30 days, so its call can't be read any more.</summary>
        CallRecordNoLongerInGraph,

        /// <summary>Already replayed <see cref="CallDeadLetterReplayer.MaxReplays"/> times.</summary>
        ReplayLimitReached,

        /// <summary>A copy this run sent, which the importer dead-lettered again while the run was still going.</summary>
        ReplayedInThisRun
    }

    /// <summary>The dead-lettered messages one run looked at that had one dead-letter reason.</summary>
    public sealed class CallDeadLetterReasonSummary
    {
        public int Count { get; internal set; }
        public DateTime? OldestFirstQueuedUtc { get; internal set; }
        public DateTime? NewestFirstQueuedUtc { get; internal set; }
    }

    /// <summary>What one replay run did - or, for a dry run, would do.</summary>
    public sealed class CallDeadLetterReplaySummary
    {
        public const string NoReason = "(none)";

        internal CallDeadLetterReplaySummary(CallDeadLetterReplayOptions options)
        {
            DryRun = options.DryRun;
            MaxMessages = options.MaxMessages;
            ScanLimit = options.EffectiveScanLimit;
            PurgeRequested = options.PurgeUnimportable || options.PurgeInvalid;
        }

        public bool DryRun { get; }
        public int MaxMessages { get; }

        /// <summary>Dead-lettered messages looked at.</summary>
        public int Scanned { get; internal set; }

        /// <summary>Sent back to the calls queue (in a dry run: would be).</summary>
        public int Replayed { get; internal set; }

        /// <summary>
        /// Of <see cref="Replayed"/>, those whose dead-lettered original could not then be deleted. It goes back to the
        /// dead-letter queue when its lock expires, so a later run can replay it again - harmless, as saving a call
        /// replaces any copy already saved.
        /// </summary>
        public int ReplayedButNotRemoved { get; internal set; }

        /// <summary>Could not be sent, so left in the dead-letter queue.</summary>
        public int Failed { get; internal set; }

        public IDictionary<CallDeadLetterSkipReason, int> SkippedByReason { get; } = new SortedDictionary<CallDeadLetterSkipReason, int>();
        public int Skipped => SkippedByReason.Values.Sum();

        /// <summary>The messages looked at, by dead-letter reason (<see cref="NoReason"/> for none).</summary>
        public IDictionary<string, CallDeadLetterReasonSummary> ByDeadLetterReason { get; } =
            new SortedDictionary<string, CallDeadLetterReasonSummary>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The messages looked at, by days since they were first queued: under 1, 1-7, 7-30, over 30, unknown.</summary>
        public int[] ByAge { get; } = new int[5];

        /// <summary>Messages dead-lettered again after <see cref="CallDeadLetterReplayer.MaxReplays"/> replays, left for someone to look at.</summary>
        public IList<string> ReplayLimitReachedMessageIds { get; } = new List<string>();

        /// <summary>Sending failed so many times in a row that the run stopped.</summary>
        public bool StoppedAfterSendFailures { get; internal set; }

        public bool Cancelled { get; internal set; }

        /// <summary>Why reading the dead-letter queue stopped the run, if it did.</summary>
        public string Error { get; internal set; }

        /// <summary>Why the run gave up before it had looked at all it should have; null when it didn't.</summary>
        public string StoppedEarly { get; internal set; }

        /// <summary>The run stopped because it had replayed (or tried to replay) <see cref="MaxMessages"/>.</summary>
        public bool ReachedMaxMessages { get; internal set; }

        /// <summary>The run stopped because it had looked at <see cref="ScanLimit"/> messages.</summary>
        public bool ReachedScanLimit { get; internal set; }

        /// <summary>
        /// Set when the run found it had been through the whole queue while the messages it left there were still coming
        /// round as their locks lapsed: how many there were.
        /// </summary>
        public int? ReachedEndHolding { get; internal set; }

        public int ScanLimit { get; internal set; }

        /// <summary>Whether the run was asked to delete what can never be imported.</summary>
        public bool PurgeRequested { get; internal set; }

        /// <summary>Deleted from the dead-letter queue (in a dry run: would be), by why they can never be imported.</summary>
        public IDictionary<CallDeadLetterSkipReason, int> PurgedByReason { get; } = new SortedDictionary<CallDeadLetterSkipReason, int>();
        public int Purged => PurgedByReason.Values.Sum();

        /// <summary>Could not be deleted, so left in the dead-letter queue.</summary>
        public int PurgeFailed { get; internal set; }

        /// <summary>True when every message looked at was replayed, deleted or deliberately skipped, and the run did not give up.</summary>
        public bool Succeeded => Failed == 0 && PurgeFailed == 0 && !StoppedAfterSendFailures && !Cancelled && Error == null && StoppedEarly == null;

        internal void AddSkipped(CallDeadLetterSkipReason reason) => Increment(SkippedByReason, reason);

        internal void AddPurged(CallDeadLetterSkipReason reason) => Increment(PurgedByReason, reason);

        /// <summary>Records that the run gave up, and why; the first reason is the one reported.</summary>
        internal void StopEarly(string reason)
        {
            if (StoppedEarly == null)
            {
                StoppedEarly = reason;
            }
        }

        internal void Record(string deadLetterReason, DateTime? firstQueuedUtc, DateTime nowUtc)
        {
            var key = string.IsNullOrEmpty(deadLetterReason) ? NoReason : deadLetterReason;
            if (!ByDeadLetterReason.TryGetValue(key, out var reason))
            {
                reason = new CallDeadLetterReasonSummary();
                ByDeadLetterReason[key] = reason;
            }
            reason.Count++;

            if (!firstQueuedUtc.HasValue)
            {
                ByAge[4]++;
                return;
            }
            if (!reason.OldestFirstQueuedUtc.HasValue || firstQueuedUtc < reason.OldestFirstQueuedUtc) reason.OldestFirstQueuedUtc = firstQueuedUtc;
            if (!reason.NewestFirstQueuedUtc.HasValue || firstQueuedUtc > reason.NewestFirstQueuedUtc) reason.NewestFirstQueuedUtc = firstQueuedUtc;

            var days = (nowUtc - firstQueuedUtc.Value).TotalDays;
            ByAge[days < 1 ? 0 : days < 7 ? 1 : days <= 30 ? 2 : 3]++;
        }

        /// <summary>The summary an operator reads at the end of a run.</summary>
        public IEnumerable<string> Describe()
        {
            yield return DryRun
                ? "Teams calls dead-letter replay - DRY RUN: nothing was sent, deleted or locked."
                : StoppedEarly != null ? "Teams calls dead-letter replay STOPPED EARLY (see below)." : "Teams calls dead-letter replay finished.";
            yield return $"Scanned: {Scanned} dead-lettered message(s) (scan limit {ScanLimit}).";
            yield return (DryRun ? "Would replay: " : "Replayed: ") + Replayed + $" (limit {MaxMessages})" +
                (ReplayedButNotRemoved > 0 ? $"; {ReplayedButNotRemoved} of them could not then be deleted from the dead-letter queue, so a later run may replay them again" : string.Empty) + ".";
            if (PurgeRequested || Purged > 0 || PurgeFailed > 0)
            {
                yield return (DryRun ? "Would delete: " : "Deleted: ") + Purged + ".";
                foreach (var purged in PurgedByReason)
                {
                    yield return $"  {purged.Value} x {DescribePurge(purged.Key)}";
                }
                if (PurgeFailed > 0)
                {
                    yield return $"Couldn't delete (left in the dead-letter queue): {PurgeFailed}.";
                }
            }
            yield return $"Skipped (left in the dead-letter queue): {Skipped}.";
            foreach (var skipped in SkippedByReason)
            {
                yield return $"  {skipped.Value} x {DescribeSkip(skipped.Key)}";
            }
            yield return $"Failed (left in the dead-letter queue): {Failed}.";
            if (ReachedEndHolding.HasValue)
            {
                yield return $"Reached the end of the dead-letter queue. The {ReachedEndHolding} message(s) it left there kept coming round as their " +
                    "locks lapsed, as they do on a run longer than the queue's lock duration.";
            }
            if (StoppedEarly != null)
            {
                yield return $"Stopped early: {StoppedEarly}";
            }
            if (ReachedMaxMessages)
            {
                yield return $"Reached --max ({MaxMessages}): there may be more to replay - run it again.";
            }
            if (ReachedScanLimit)
            {
                yield return $"Reached the scan limit ({ScanLimit}) before the end of the dead-letter queue: run it again - and delete what can never be " +
                    "imported (--purge-unimportable, --purge-invalid), so that runs stop having to look past it.";
            }
            if (ReplayLimitReachedMessageIds.Count > 0)
            {
                const int listed = 20;
                yield return $"Replayed {CallDeadLetterReplayer.MaxReplays} times already, so not replayed again (message ids): " +
                    string.Join(", ", ReplayLimitReachedMessageIds.Take(listed)) +
                    (ReplayLimitReachedMessageIds.Count > listed ? $" and {ReplayLimitReachedMessageIds.Count - listed} more." : ".");
            }
            if (ByDeadLetterReason.Count > 0)
            {
                yield return "By dead-letter reason:";
                foreach (var reason in ByDeadLetterReason)
                {
                    var range = reason.Value.OldestFirstQueuedUtc.HasValue
                        ? $", first queued {FormatUtc(reason.Value.OldestFirstQueuedUtc.Value)} to {FormatUtc(reason.Value.NewestFirstQueuedUtc.Value)} UTC"
                        : string.Empty;
                    yield return $"  {reason.Key}: {reason.Value.Count}{range}";
                }
                yield return $"By age since first queued: under 1 day {ByAge[0]}; 1-7 days {ByAge[1]}; 7-30 days {ByAge[2]}; over 30 days {ByAge[3]}" +
                    (ByAge[4] > 0 ? $"; unknown {ByAge[4]}." : ".");
            }
        }

        private static void Increment(IDictionary<CallDeadLetterSkipReason, int> counts, CallDeadLetterSkipReason reason)
        {
            counts.TryGetValue(reason, out var count);
            counts[reason] = count + 1;
        }

        private static string DescribeSkip(CallDeadLetterSkipReason reason)
        {
            switch (reason)
            {
                case CallDeadLetterSkipReason.OtherReason: return "dead-lettered for a reason other than --reason";
                case CallDeadLetterSkipReason.NeverImportable:
                    return "never importable as it stands (malformed, no call id, or not retryable) - --include-invalid replays these, --purge-invalid deletes them";
                case CallDeadLetterSkipReason.CallRecordNoLongerInGraph:
                    return "first queued over 30 days ago - Graph no longer has the call record; --purge-unimportable deletes these";
                case CallDeadLetterSkipReason.ReplayLimitReached: return $"already replayed {CallDeadLetterReplayer.MaxReplays} times";
                case CallDeadLetterSkipReason.ReplayedInThisRun: return "replayed by this run and dead-lettered again already";
                default: return reason.ToString();
            }
        }

        private static string DescribePurge(CallDeadLetterSkipReason reason)
        {
            switch (reason)
            {
                case CallDeadLetterSkipReason.CallRecordNoLongerInGraph: return "first queued over 30 days ago - Graph no longer has the call record (--purge-unimportable)";
                case CallDeadLetterSkipReason.NeverImportable: return "never importable as it stands (--purge-invalid)";
                default: return reason.ToString();
            }
        }

        private static string FormatUtc(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Replays the Teams calls queue's dead-letter queue: sends each dead-lettered notification back to the queue as a new
    /// message, for the importer to try again - after an outage such as a SQL failover or an expired Graph permission
    /// has dead-lettered calls that would now import.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At-least-once: a dead-lettered message is deleted only once its copy has been sent, and released back to the
    /// dead-letter queue if the send fails. A message can therefore be replayed twice (a send that succeeded but reported
    /// failure, or a delete that failed), which is safe: saving a call replaces any copy of it already saved, by Graph
    /// call id (<c>call_records.graph_id</c> is unique).
    /// </para>
    /// <para>
    /// The copy carries the original's body, content type and application properties, plus a replay count, but starts
    /// the importer's retry bookkeeping (<see cref="CallQueueRetryState"/>) afresh: otherwise a replayed call would be
    /// dead-lettered again at its first failure, its retries already spent. <see cref="MaxReplays"/> bounds how often
    /// that can happen. When a replayed message fails again it comes back to the dead-letter queue with its count, so it
    /// is not replayed forever.
    /// </para>
    /// <para>
    /// Skipped and left in the dead-letter queue: messages the importer found can never be imported
    /// (<see cref="IsNeverImportable"/>, unless asked for), notifications first queued over 30 days ago (Graph deletes a call
    /// record 30 days after the call, so it could only fail), and messages replayed <see cref="MaxReplays"/> times. Nothing
    /// else ever removes dead letters, so when asked - never by default - a run deletes the first two kinds instead
    /// (<see cref="CallDeadLetterReplayOptions.PurgeUnimportable"/>, <see cref="CallDeadLetterReplayOptions.PurgeInvalid"/>).
    /// </para>
    /// <para>
    /// Skipped messages stay at the head of the dead-letter queue, so they don't count towards how many a run replays: it
    /// walks past them, as far as its scan limit, to the calls behind.
    /// </para>
    /// <para>
    /// Message bodies are never logged: they carry the webhook subscription's <c>clientState</c>. Logs carry counts,
    /// reasons, message ids and times only.
    /// </para>
    /// </remarks>
    public sealed class CallDeadLetterReplayer
    {
        /// <summary>A message is not replayed again once it has been replayed this many times.</summary>
        public const int MaxReplays = 3;

        /// <summary>Application property: how many times the notification has been replayed from the dead-letter queue.</summary>
        public const string ReplayCountProperty = "CallImportReplayCount";

        /// <summary>
        /// Application property: when the notification was first queued, as far as is known (UTC, round-trip format). Kept
        /// across replays, as each replay is a new message and starts the retry bookkeeping afresh.
        /// </summary>
        public const string FirstQueuedUtcProperty = "CallImportFirstQueuedUtc";

        /// <summary>How long Graph keeps a call record after the call ends.</summary>
        public static readonly TimeSpan GraphCallRecordRetention = TimeSpan.FromDays(30);

        internal const int BatchSize = 100;
        internal const int MaxConsecutiveSendFailures = 5;

        // Dead-letter reasons CallQueueMessageHandler gives a notification that can never be imported as it stands.
        private static readonly HashSet<string> NeverImportableReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CallQueueMessageHandler.InvalidNotificationReason,
            CallQueueMessageHandler.MissingCallIdReason,
            CallQueueMessageHandler.NotRetryableReason
        };

        // Not copied to the replay: what Service Bus set when it dead-lettered the message, the trace id of the original
        // send, and the importer's retry bookkeeping, which restarts.
        private static readonly HashSet<string> PropertiesNotCopied = new HashSet<string>(StringComparer.Ordinal)
        {
            "DeadLetterReason",
            "DeadLetterErrorDescription",
            "Diagnostic-Id",
            CallQueueRetryState.FailuresProperty,
            CallQueueRetryState.SystemicFailuresProperty,
            CallQueueRetryState.RetriesProperty,
            CallQueueRetryState.FirstFailureUtcProperty
        };

        private static readonly IReadOnlyDictionary<string, object> NoProperties = new Dictionary<string, object>();

        private readonly ICallDeadLetterReceiver _deadLetters;
        private readonly ICallReplaySender _queue;
        private readonly ILogger _logger;
        private readonly IClock _clock;

        public CallDeadLetterReplayer(ICallDeadLetterReceiver deadLetters, ICallReplaySender queue, ILogger logger, IClock clock = null)
        {
            _deadLetters = deadLetters ?? throw new ArgumentNullException(nameof(deadLetters));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? SystemClock.Instance;
        }

        /// <summary>
        /// Whether a dead-letter reason is one the importer gives a notification that can never be imported as it stands:
        /// a body that isn't a Graph change notification, one with no call id, or a call whose import fails the same way
        /// every time.
        /// </summary>
        public static bool IsNeverImportable(string deadLetterReason)
        {
            return !string.IsNullOrEmpty(deadLetterReason) && NeverImportableReasons.Contains(deadLetterReason);
        }

        public async Task<CallDeadLetterReplaySummary> RunAsync(CallDeadLetterReplayOptions options, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.MaxMessages < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxMessages must be at least 1.");
            if (options.ScanLimit.HasValue && options.ScanLimit < 1) throw new ArgumentOutOfRangeException(nameof(options), "ScanLimit must be at least 1.");
            if (options.IncludeInvalid && options.PurgeInvalid) throw new ArgumentException("IncludeInvalid replays what PurgeInvalid deletes.", nameof(options));

            var summary = new CallDeadLetterReplaySummary(options);
            var nowUtc = _clock.UtcNow;

            _logger.LogInformation($"Teams calls dead-letter replay starting{(options.DryRun ? " (dry run)" : string.Empty)}: replaying at most {options.MaxMessages} " +
                $"message(s) and looking at no more than {summary.ScanLimit}" +
                $"{(string.IsNullOrEmpty(options.Reason) ? string.Empty : $", only those dead-lettered as '{options.Reason}'")}" +
                $"{(options.IncludeInvalid ? ", including those the importer found can never be imported" : string.Empty)}" +
                $"{(options.PurgeUnimportable ? ", DELETING those first queued over 30 days ago" : string.Empty)}" +
                $"{(options.PurgeInvalid ? ", DELETING those the importer found can never be imported" : string.Empty)}.");
            if (!string.IsNullOrEmpty(options.Reason) && !options.IncludeInvalid && !options.PurgeInvalid && IsNeverImportable(options.Reason))
            {
                _logger.LogWarning($"'{options.Reason}' is a reason the importer gives to notifications that can never be imported as they stand, " +
                    "so they are all skipped. --include-invalid replays them anyway; --purge-invalid deletes them.");
            }

            if (options.DryRun)
            {
                await DryRunAsync(options, summary, nowUtc, cancellationToken);
            }
            else
            {
                await ReplayAsync(options, summary, nowUtc, cancellationToken);
            }

            foreach (var line in summary.Describe())
            {
                _logger.LogInformation(line);
            }
            return summary;
        }

        private async Task DryRunAsync(CallDeadLetterReplayOptions options, CallDeadLetterReplaySummary summary, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var seen = new HashSet<long>();
            var browseFrom = 0L;
            while (summary.StoppedEarly == null && !LimitReached(options, summary))
            {
                var from = browseFrom;
                var batch = await ReadAsync(summary, cancellationToken,
                    () => _deadLetters.PeekAsync(from, Math.Min(BatchSize, summary.ScanLimit - summary.Scanned), cancellationToken));
                if (batch.Count == 0)
                {
                    break;
                }
                browseFrom = Math.Max(browseFrom, batch.Max(m => m.SequenceNumber) + 1);

                var examined = 0;
                foreach (var message in batch)
                {
                    if (LimitReached(options, summary))
                    {
                        break;
                    }
                    if (!seen.Add(message.SequenceNumber))
                    {
                        continue;
                    }
                    examined++;
                    var reason = Examine(message, options, summary, nowUtc, replayedThisRun: null, purge: out var purge);
                    if (!reason.HasValue)
                    {
                        summary.Replayed++;
                    }
                    else if (purge)
                    {
                        summary.AddPurged(reason.Value);
                    }
                }
                if (examined == 0)
                {
                    // Browsing moves on by sequence number, so this is not the end of the queue but a browse going wrong.
                    summary.StopEarly("browsing the dead-letter queue returned only messages it had already looked at.");
                }
            }
        }

        private async Task ReplayAsync(CallDeadLetterReplayOptions options, CallDeadLetterReplaySummary summary, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var seen = new HashSet<long>();
            var replayedThisRun = new HashSet<string>(StringComparer.Ordinal);

            // A received message that is not replayed or deleted stays locked until the run ends: released straight away, it
            // would be back at the head of the dead-letter queue, and the next receive would return it again instead of the
            // messages behind it. Only its latest receipt is kept: an earlier receipt's lock has lapsed or been superseded.
            var held = new Dictionary<long, CallDeadLetterMessage>();

            // Messages looked at that are still in the dead-letter queue, so may come round again when their locks lapse.
            var outstanding = new HashSet<long>();
            var lastLookedAt = -1L;

            // Messages behind the last one looked at that the run has never received, found by one browse from the start.
            HashSet<long> passedUnread = null;
            var sendFailuresInARow = 0;
            var cameRoundInARow = 0;
            try
            {
                while (summary.StoppedEarly == null && !LimitReached(options, summary))
                {
                    var batch = await ReadAsync(summary, cancellationToken,
                        () => _deadLetters.ReceiveAsync(Math.Min(BatchSize, summary.ScanLimit - summary.Scanned), cancellationToken));
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    var examined = 0;
                    foreach (var message in batch)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            MarkCancelled(summary);
                        }
                        if (summary.StoppedEarly != null || LimitReached(options, summary))
                        {
                            Hold(message);
                            continue;
                        }

                        if (!seen.Add(message.SequenceNumber))
                        {
                            // Held, its lock lapsed, and it came round again - which on a run longer than the queue's lock
                            // duration is expected, and means carrying on past it.
                            Hold(message);
                            cameRoundInARow++;
                            continue;
                        }
                        cameRoundInARow = 0;
                        examined++;
                        lastLookedAt = Math.Max(lastLookedAt, message.SequenceNumber);

                        var reason = Examine(message, options, summary, nowUtc, replayedThisRun, out var purge);
                        if (purge)
                        {
                            try
                            {
                                await _deadLetters.CompleteAsync(message, CancellationToken.None);
                                summary.AddPurged(reason.Value);
                                _logger.LogInformation($"Teams calls dead-letter replay: deleted dead-lettered message '{message.MessageId}' " +
                                    $"({DescribeReason(message.DeadLetterReason)}, first queued {DescribeTime(GetFirstQueuedUtc(message))}): {DescribeCannotImport(reason.Value)}.");
                            }
                            catch (Exception ex)
                            {
                                summary.PurgeFailed++;
                                Hold(message);
                                _logger.LogError($"Teams calls dead-letter replay: couldn't delete dead-lettered message '{message.MessageId}' " +
                                    $"({DescribeReason(message.DeadLetterReason)}), so it stays in the dead-letter queue. {ex.GetType().Name}: {ex.Message}");
                            }
                            continue;
                        }
                        if (reason.HasValue)
                        {
                            Hold(message);
                            continue;
                        }

                        var replay = BuildReplayMessage(message, nowUtc);
                        try
                        {
                            await _queue.SendAsync(replay, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            // Perhaps sent, perhaps not: it stays in the dead-letter queue, so at worst it is replayed twice.
                            MarkCancelled(summary);
                            Hold(message);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            summary.Failed++;
                            Hold(message);
                            _logger.LogError($"Teams calls dead-letter replay: couldn't send dead-lettered message '{message.MessageId}' " +
                                $"({DescribeReason(message.DeadLetterReason)}) back to the queue, so it stays in the dead-letter queue. {ex.GetType().Name}: {ex.Message}");
                            if (++sendFailuresInARow >= MaxConsecutiveSendFailures)
                            {
                                summary.StoppedAfterSendFailures = true;
                                summary.StopEarly($"{MaxConsecutiveSendFailures} sends to the queue failed in a row.");
                            }
                            continue;
                        }

                        sendFailuresInARow = 0;
                        summary.Replayed++;
                        replayedThisRun.Add(replay.MessageId);
                        try
                        {
                            await _deadLetters.CompleteAsync(message, CancellationToken.None);
                            _logger.LogInformation($"Teams calls dead-letter replay: replayed message '{message.MessageId}' ({DescribeReason(message.DeadLetterReason)}, " +
                                $"enqueued {message.EnqueuedTime.UtcDateTime:u}) as '{replay.MessageId}' - replay {replay.ApplicationProperties[ReplayCountProperty]} of at most {MaxReplays}.");
                        }
                        catch (Exception ex)
                        {
                            // Its lock lapses and it returns to the dead-letter queue, where a later run may replay it again.
                            summary.ReplayedButNotRemoved++;
                            outstanding.Add(message.SequenceNumber);
                            _logger.LogWarning($"Teams calls dead-letter replay: replayed message '{message.MessageId}' as '{replay.MessageId}', but couldn't remove it " +
                                $"from the dead-letter queue, so a later run may replay it again - harmless, as saving a call replaces any copy already saved. {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    if (examined == 0 && summary.StoppedEarly == null && !LimitReached(options, summary))
                    {
                        // Only messages it had looked at already came round. Once a run lasts longer than the lock duration,
                        // what it holds lapses a few seconds apart, so no receive need ever come back empty to say it has
                        // read everything: whether anything lies beyond the last message it looked at says so instead.
                        var anythingLeft = await HasMessagesAfterAsync(lastLookedAt, summary, cancellationToken);
                        if (anythingLeft == false)
                        {
                            // A message another receiver had locked when the run went past it would lie behind instead. One
                            // browse from the start finds any such; after that the run waits only for those it found.
                            if (passedUnread == null)
                            {
                                passedUnread = await FindUnreadAsync(seen, summary, cancellationToken);
                            }
                            passedUnread?.RemoveWhere(seen.Contains);
                            anythingLeft = passedUnread == null ? (bool?)null : passedUnread.Count > 0;
                        }
                        if (anythingLeft == false)
                        {
                            summary.ReachedEndHolding = outstanding.Count;
                            break;
                        }

                        // Counted in messages, not receives, as Service Bus need not return full batches: more in a row than
                        // could come round at all means some came round twice before the run got past them.
                        if (cameRoundInARow > outstanding.Count)
                        {
                            summary.StopEarly(passedUnread != null && passedUnread.Count > 0
                                ? $"{passedUnread.Count} message(s) it hasn't read are locked by something else - another replay, or Service Bus " +
                                    "Explorer, say. Run it again once they are free."
                                : $"the {outstanding.Count} message(s) it holds at the head of the dead-letter queue keep losing their locks " +
                                    "before it can get past them to the messages behind. Delete what can never be imported " +
                                    "(--purge-unimportable, --purge-invalid) or narrow the run with --reason, then run it again.");
                        }
                    }
                }
            }
            finally
            {
                await ReleaseAsync(held.Values, summary, cancellationToken);
            }

            void Hold(CallDeadLetterMessage message)
            {
                held[message.SequenceNumber] = message;
                outstanding.Add(message.SequenceNumber);
            }
        }

        /// <summary>Whether the run has replayed (or tried to replay) all it may, or looked at all it may - and if so, notes which.</summary>
        private static bool LimitReached(CallDeadLetterReplayOptions options, CallDeadLetterReplaySummary summary)
        {
            if (summary.Replayed + summary.Failed >= options.MaxMessages)
            {
                summary.ReachedMaxMessages = true;
                return true;
            }
            if (summary.Scanned >= summary.ScanLimit)
            {
                summary.ReachedScanLimit = true;
                return true;
            }
            return false;
        }

        private static void MarkCancelled(CallDeadLetterReplaySummary summary)
        {
            summary.Cancelled = true;
            summary.StopEarly("cancelled.");
        }

        /// <summary>Whether anything lies beyond <paramref name="sequenceNumber"/>; null when that can't be told.</summary>
        private async Task<bool?> HasMessagesAfterAsync(long sequenceNumber, CallDeadLetterReplaySummary summary, CancellationToken cancellationToken)
        {
            try
            {
                return await _deadLetters.HasMessagesAfterAsync(sequenceNumber, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkCancelled(summary);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Teams calls dead-letter replay: couldn't check for messages beyond those it has looked at. {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Browses the whole dead-letter queue from its start, locked messages included, for any the run has not looked at;
        /// null when that can't be told. Each attempt keeps its own place, starting from the beginning.
        /// </summary>
        private async Task<HashSet<long>> FindUnreadAsync(HashSet<long> seen, CallDeadLetterReplaySummary summary, CancellationToken cancellationToken)
        {
            var unread = new HashSet<long>();
            var browseFrom = 0L;
            try
            {
                while (true)
                {
                    var batch = await _deadLetters.PeekAsync(browseFrom, BatchSize, cancellationToken);
                    if (batch == null || batch.Count == 0)
                    {
                        return unread;
                    }
                    var browsedTo = batch.Max(m => m.SequenceNumber);
                    if (browsedTo < browseFrom)
                    {
                        return unread;
                    }
                    browseFrom = browsedTo + 1;
                    unread.UnionWith(batch.Select(m => m.SequenceNumber).Where(n => !seen.Contains(n)));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkCancelled(summary);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Teams calls dead-letter replay: couldn't browse the dead-letter queue for messages it has not read. {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }

        private async Task<IReadOnlyList<CallDeadLetterMessage>> ReadAsync(CallDeadLetterReplaySummary summary, CancellationToken cancellationToken,
            Func<Task<IReadOnlyList<CallDeadLetterMessage>>> read)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                MarkCancelled(summary);
                return Array.Empty<CallDeadLetterMessage>();
            }
            try
            {
                return await read() ?? Array.Empty<CallDeadLetterMessage>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkCancelled(summary);
            }
            catch (Exception ex)
            {
                summary.Error = $"{ex.GetType().Name}: {ex.Message}";
                summary.StopEarly($"couldn't read the dead-letter queue. {summary.Error}");
                _logger.LogError($"Teams calls dead-letter replay: couldn't read the dead-letter queue. {summary.Error}");
            }
            return Array.Empty<CallDeadLetterMessage>();
        }

        /// <summary>
        /// Puts back what the run still holds, so it is available again at once. Each release is a round trip, so: one per
        /// message (its latest receipt), newest lock first, none whose lock has lapsed already - which is then back in the
        /// queue anyway - and none once cancelled. That bounds it by the queue's lock duration, as by then every lock it
        /// holds has lapsed.
        /// </summary>
        private async Task ReleaseAsync(IEnumerable<CallDeadLetterMessage> held, CallDeadLetterReplaySummary summary, CancellationToken cancellationToken)
        {
            var leftLocked = new List<CallDeadLetterMessage>();
            foreach (var message in held.OrderByDescending(m => m.LockedUntil).ToList())
            {
                if (message.LockedUntil != default(DateTimeOffset) && message.LockedUntil.UtcDateTime <= _clock.UtcNow)
                {
                    continue;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    leftLocked.Add(message);
                    continue;
                }
                try
                {
                    await _deadLetters.AbandonAsync(message, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    leftLocked.Add(message);
                }
                catch (Exception ex)
                {
                    // Its lock lapses instead, after which it is back in the dead-letter queue all the same.
                    _logger.LogDebug($"Teams calls dead-letter replay: couldn't release dead-lettered message '{message.MessageId}': {ex.Message}");
                }
            }

            if (leftLocked.Count > 0)
            {
                MarkCancelled(summary);
                _logger.LogWarning($"Teams calls dead-letter replay: cancelled while putting back the messages it held. {leftLocked.Count} stay locked, " +
                    $"and go back to the dead-letter queue by themselves when their locks lapse, by {leftLocked.Max(m => m.LockedUntil).UtcDateTime:u}.");
            }
        }

        /// <summary>Counts a message in <paramref name="summary"/>, and says what becomes of it (see <see cref="Decide"/>).</summary>
        private CallDeadLetterSkipReason? Examine(CallDeadLetterMessage message, CallDeadLetterReplayOptions options, CallDeadLetterReplaySummary summary,
            DateTime nowUtc, ICollection<string> replayedThisRun, out bool purge)
        {
            summary.Scanned++;
            var firstQueuedUtc = GetFirstQueuedUtc(message);
            summary.Record(message.DeadLetterReason, firstQueuedUtc, nowUtc);

            var reason = Decide(message, options, firstQueuedUtc, nowUtc, replayedThisRun, out purge);
            if (reason.HasValue && !purge)
            {
                summary.AddSkipped(reason.Value);
                if (reason == CallDeadLetterSkipReason.ReplayLimitReached)
                {
                    summary.ReplayLimitReachedMessageIds.Add(message.MessageId);
                    _logger.LogWarning($"Teams calls dead-letter replay: message '{message.MessageId}' ({DescribeReason(message.DeadLetterReason)}) has been replayed " +
                        $"{GetReplayCount(message)} times and dead-lettered again each time, so it is left in the dead-letter queue.");
                }
            }
            return reason;
        }

        /// <summary>
        /// What becomes of a dead-lettered message: null to replay it; otherwise why it is left in the dead-letter queue - or,
        /// with <paramref name="purge"/> set, deleted from it.
        /// </summary>
        internal static CallDeadLetterSkipReason? Decide(CallDeadLetterMessage message, CallDeadLetterReplayOptions options, DateTime? firstQueuedUtc,
            DateTime nowUtc, ICollection<string> replayedThisRun, out bool purge)
        {
            purge = false;
            if (replayedThisRun != null && message.MessageId != null && replayedThisRun.Contains(message.MessageId))
            {
                return CallDeadLetterSkipReason.ReplayedInThisRun;
            }
            if (!string.IsNullOrEmpty(options.Reason) && !string.Equals(options.Reason, message.DeadLetterReason, StringComparison.OrdinalIgnoreCase))
            {
                return CallDeadLetterSkipReason.OtherReason;
            }

            var noLongerInGraph = firstQueuedUtc.HasValue && nowUtc - firstQueuedUtc.Value > GraphCallRecordRetention;
            var neverImportable = IsNeverImportable(message.DeadLetterReason);
            if (noLongerInGraph && options.PurgeUnimportable)
            {
                purge = true;
                return CallDeadLetterSkipReason.CallRecordNoLongerInGraph;
            }
            if (neverImportable && options.PurgeInvalid)
            {
                purge = true;
                return CallDeadLetterSkipReason.NeverImportable;
            }
            if (neverImportable && !options.IncludeInvalid)
            {
                return CallDeadLetterSkipReason.NeverImportable;
            }
            if (noLongerInGraph)
            {
                return CallDeadLetterSkipReason.CallRecordNoLongerInGraph;
            }
            if (GetReplayCount(message) >= MaxReplays)
            {
                return CallDeadLetterSkipReason.ReplayLimitReached;
            }
            return null;
        }
        /// <summary>
        /// The copy to send: a new message id (were duplicate detection enabled on the queue, the original's id could be
        /// discarded as a duplicate), the original's body, content type and application properties, the replay count one
        /// higher, and the retry bookkeeping removed so the importer retries it as a new notification.
        /// </summary>
        internal static CallReplayMessage BuildReplayMessage(CallDeadLetterMessage message, DateTime nowUtc)
        {
            var replay = new CallReplayMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                Body = message.Body,
                ContentType = message.ContentType,
                CorrelationId = message.CorrelationId,
                Subject = message.Subject
            };

            foreach (var property in message.ApplicationProperties ?? NoProperties)
            {
                if (!PropertiesNotCopied.Contains(property.Key))
                {
                    replay.ApplicationProperties[property.Key] = property.Value;
                }
            }

            replay.ApplicationProperties[ReplayCountProperty] = GetReplayCount(message) + 1;
            if (string.IsNullOrEmpty(ReadString(message.ApplicationProperties, CallQueueRetryState.OriginalMessageIdProperty)) && !string.IsNullOrEmpty(message.MessageId))
            {
                replay.ApplicationProperties[CallQueueRetryState.OriginalMessageIdProperty] = message.MessageId;
            }

            var firstQueuedUtc = GetFirstQueuedUtc(message) ?? nowUtc;
            replay.ApplicationProperties[FirstQueuedUtcProperty] = firstQueuedUtc.ToString("o", CultureInfo.InvariantCulture);
            return replay;
        }

        /// <summary>How many times the message has been replayed already.</summary>
        internal static int GetReplayCount(CallDeadLetterMessage message)
        {
            var properties = message?.ApplicationProperties ?? NoProperties;
            if (properties.TryGetValue(ReplayCountProperty, out var value) && value != null)
            {
                try
                {
                    return Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                }
                catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
                {
                }
            }
            return 0;
        }

        /// <summary>
        /// When the notification was first queued, as far as the message shows: the earliest of when it was enqueued, its
        /// first failed import and what an earlier replay recorded. Never earlier than the truth - each is a time the
        /// notification was already on the queue - so a notification is never wrongly taken to be past Graph's retention.
        /// </summary>
        internal static DateTime? GetFirstQueuedUtc(CallDeadLetterMessage message)
        {
            DateTime? earliest = null;
            if (message.EnqueuedTime != default(DateTimeOffset))
            {
                earliest = message.EnqueuedTime.UtcDateTime;
            }
            foreach (var name in new[] { FirstQueuedUtcProperty, CallQueueRetryState.FirstFailureUtcProperty })
            {
                var value = ReadUtc(message.ApplicationProperties ?? NoProperties, name);
                if (value.HasValue && (!earliest.HasValue || value.Value < earliest.Value))
                {
                    earliest = value;
                }
            }
            return earliest;
        }

        private static string DescribeReason(string deadLetterReason)
        {
            return string.IsNullOrEmpty(deadLetterReason) ? "no dead-letter reason" : $"dead-lettered as '{deadLetterReason}'";
        }

        private static string DescribeTime(DateTime? utc)
        {
            return utc.HasValue ? utc.Value.ToString("u", CultureInfo.InvariantCulture) : "unknown";
        }

        private static string DescribeCannotImport(CallDeadLetterSkipReason reason)
        {
            return reason == CallDeadLetterSkipReason.CallRecordNoLongerInGraph
                ? "Graph no longer has its call record"
                : "the importer found it can never be imported";
        }

        private static string ReadString(IReadOnlyDictionary<string, object> properties, string name)
        {
            return properties != null && properties.TryGetValue(name, out var value) ? value?.ToString() : null;
        }

        private static DateTime? ReadUtc(IReadOnlyDictionary<string, object> properties, string name)
        {
            if (!properties.TryGetValue(name, out var value) || value == null)
            {
                return null;
            }
            if (value is DateTime dateTime)
            {
                return dateTime.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc) : dateTime.ToUniversalTime();
            }
            if (value is DateTimeOffset offset)
            {
                return offset.UtcDateTime;
            }
            if (!DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return null;
            }
            return parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
        }
    }
}
