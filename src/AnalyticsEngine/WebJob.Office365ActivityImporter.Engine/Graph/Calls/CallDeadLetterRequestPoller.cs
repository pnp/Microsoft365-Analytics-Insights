using Common.Entities.Calls;
using Common.Entities.Config;
using Common.Entities.State;
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
    /// <summary>
    /// Carries out the requests administrators record on the portal's Service configuration page to reprocess or clear the
    /// Teams calls dead-letter queue (<see cref="CallDeadLetterRequestStore"/>): looks for a new one about once a minute,
    /// claims it, runs <see cref="CallDeadLetterReplayer"/> - the same replay as the operator's <c>--replayCallDeadLetters</c>
    /// mode, with the same log lines - and records the result for the portal to show.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reprocess</b> is the replayer's default options (replay at most <see cref="CallDeadLetterReplayOptions.DefaultMaxMessages"/>,
    /// looking at no more than its default scan limit, leaving what can never be imported and notifications Graph no
    /// longer has), repeated while a pass stopped at one of those limits having replayed something. <b>Clear</b> is
    /// <see cref="CallDeadLetterReplayOptions.PurgeAll"/>, repeated the same way. Either stops at
    /// <see cref="CallDeadLetterRequestRules.RunBudget"/>, or once it has looked at
    /// <see cref="CallDeadLetterReplayOptions.ScanLimitLimit"/> messages in all, and records what it did.
    /// </para>
    /// <para>
    /// Two importer instances may poll the same store. The claim is a compare-and-set on the row's version, so only one of
    /// them carries a request out; and were both to, no harm is done: a replay is at-least-once, and saving a call replaces
    /// any copy already saved.
    /// </para>
    /// <para>
    /// Only a request that is waiting (<see cref="CallDeadLetterRequestStates.Waiting"/>) is claimed. One no importer picked up
    /// within <see cref="CallDeadLetterRequestRules.PickUpWithin"/> is left alone - a deletion the administrator asked for
    /// hours ago is not carried out by surprise - and so is one claimed and not finished within
    /// <see cref="CallDeadLetterRequestRules.FinishWithin"/>. The portal may replace either.
    /// </para>
    /// <para>
    /// Shutdown cancels a run as it cancels the operator's: the message in hand is finished, and what the run holds goes back
    /// to the dead-letter queue when its lock lapses. Nothing is recorded, so the request stays claimed and becomes stale
    /// (<see cref="CallDeadLetterRequestStates.NotFinished"/>), when it can be requested again.
    /// </para>
    /// <para>Log lines carry the action, counts, codes and the request's id - never a message, call id, identity or URL.</para>
    /// </remarks>
    public sealed class CallDeadLetterRequestPoller : IDisposable
    {
        private const string LogPrefix = "Teams calls dead-letter request from the portal";
        private const string RequestsLogPrefix = "Teams calls dead-letter requests from the portal";

        /// <summary>A failure to read the request is logged as a warning at most this often; it is retried every poll.</summary>
        public static readonly TimeSpan ReadFailureLogInterval = TimeSpan.FromHours(1);

        private static readonly TimeSpan RecordRetryDelay = TimeSpan.FromSeconds(2);
        private const int RecordAttempts = 3;

        private readonly CallDeadLetterRequestStore _store;
        private readonly Func<ICallDeadLetterQueue> _openQueue;
        private readonly ILogger _logger;
        private readonly IClock _clock;
        private readonly TimeSpan _budget;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private DateTime? _readFailureLoggedUtc;

        /// <param name="openQueue">Opens the calls queue; called once per request carried out, and disposed after it.</param>
        /// <param name="budget">How long one request may take; <see cref="CallDeadLetterRequestRules.RunBudget"/> by default.</param>
        public CallDeadLetterRequestPoller(CallDeadLetterRequestStore store, Func<ICallDeadLetterQueue> openQueue, ILogger logger, IClock clock = null,
            TimeSpan? budget = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _openQueue = openQueue ?? throw new ArgumentNullException(nameof(openQueue));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? SystemClock.Instance;
            _budget = budget ?? CallDeadLetterRequestRules.RunBudget;
        }

        /// <summary>
        /// Starts polling when the portal can reach this importer with a request: the calls import is on, Service Bus is
        /// configured, and requests are kept in the runtime state table (a Storage connection string). Returns null otherwise.
        /// Call once per process.
        /// </summary>
        public static CallDeadLetterRequestPoller StartIfEnabled(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (config.ImportJobSettings == null || !config.ImportJobSettings.Calls) return null;
            if (string.IsNullOrWhiteSpace(config.ConnectionStrings?.ServiceBusConnectionString)) return null;

            // The calls queue's own App Insights operation, as for the operator's replay, so it is found alongside the call imports.
            var logger = new AnalyticsLogger(config.AppInsightsConnectionString, "Office365CallsImporter");
            var store = CallDeadLetterRequestStore.ForConfig(config, logger);
            if (!store.IsDurable)
            {
                logger.LogInformation($"{RequestsLogPrefix}: not checked. There is no Storage connection string, so the portal can't record a request this " +
                    $"importer would see. To reprocess or clear the dead-letter queue, run the importer with {CallDeadLetterReplayCommand.Switch} from the WebJob's console.");
                return null;
            }

            var poller = new CallDeadLetterRequestPoller(store, () => ServiceBusCallDeadLetterQueue.Open(config), logger, SystemClock.Instance);
            poller.Start(() => HostRuntime.ShutdownRequested);
            logger.LogInformation($"{RequestsLogPrefix}: checked every {CallDeadLetterRequestRules.PollInterval.TotalMinutes:0} minute(s) in partition " +
                $"'{StatePartitions.CallDeadLetters}' of the runtime state table; each is worked on for at most {CallDeadLetterRequestRules.RunBudget.TotalMinutes:0} minutes.");
            return poller;
        }

        /// <param name="shutdownRequested">Polled every second; once true, the loop and any run in progress are cancelled.</param>
        public void Start(Func<bool> shutdownRequested)
        {
            var stop = _stop.Token;
            if (shutdownRequested != null)
            {
                Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        if (shutdownRequested())
                        {
                            _stop.Cancel();
                            return;
                        }
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                });
            }
            Task.Run(() => LoopAsync(stop));
        }

        private async Task LoopAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // RunOnceAsync handles its own failures; this only keeps the poller alive through a bug.
                    _logger.LogError(ex, $"{RequestsLogPrefix}: the check failed unexpectedly ({ex.GetType().Name}); it carries on.");
                }

                try
                {
                    await Task.Delay(CallDeadLetterRequestRules.PollInterval, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Looks for a waiting request and, when this instance claims it, carries it out and records the result. True when it
        /// claimed one. Never throws for a store or Service Bus failure: those are logged and, where the request was claimed,
        /// recorded.
        /// </summary>
        public async Task<bool> RunOnceAsync(CancellationToken stop = default(CancellationToken))
        {
            CallDeadLetterRequestSnapshot snapshot;
            try
            {
                snapshot = await _store.ReadAsync(stop).ConfigureAwait(false);
            }
            catch (CallDeadLetterStateUnavailableException ex)
            {
                LogStoreFailure("couldn't check for one", ex);
                return false;
            }

            var waiting = snapshot.Request;
            var claimedUtc = _clock.UtcNow;
            if (waiting == null || CallDeadLetterRequestRules.StateOf(waiting, claimedUtc) != CallDeadLetterRequestStates.Waiting)
            {
                return false;
            }

            var claimed = waiting.Clone();
            claimed.ClaimedUtc = claimedUtc;
            try
            {
                if (!await _store.TryReplaceAsync(claimed, snapshot.VersionToken, stop).ConfigureAwait(false))
                {
                    _logger.LogDebug($"{LogPrefix} {waiting.Id}: another importer instance claimed it first, or it was replaced.");
                    return false;
                }
            }
            catch (CallDeadLetterStateUnavailableException ex)
            {
                LogStoreFailure($"couldn't claim request {waiting.Id}", ex);
                return false;
            }

            _logger.LogInformation($"{LogPrefix}: {Describe(claimed.Action)} (request {claimed.Id}, recorded {claimed.RequestedUtc.ToString("u", CultureInfo.InvariantCulture)}), " +
                $"claimed by this importer instance and worked on for at most {_budget.TotalMinutes:0.#} minutes. The replay's summary follows.");

            var finished = await CarryOutAsync(claimed, stop).ConfigureAwait(false);
            if (finished == null)
            {
                _logger.LogWarning($"{LogPrefix} {claimed.Id} ({claimed.Action}) was interrupted: the importer is stopping. It stays claimed, and the portal shows it " +
                    $"as not finished {CallDeadLetterRequestRules.FinishWithin.TotalHours:0} hours after it was claimed, when it can be requested again.");
                return true;
            }

            await RecordAsync(claimed, finished).ConfigureAwait(false);
            return true;
        }

        /// <summary>The finished request, or null when the run was interrupted by <paramref name="stop"/> and nothing is to be recorded.</summary>
        private async Task<CallDeadLetterRequest> CarryOutAsync(CallDeadLetterRequest claimed, CancellationToken stop)
        {
            ICallDeadLetterQueue queue;
            try
            {
                queue = _openQueue();
            }
            catch (Exception ex)
            {
                _logger.LogError($"{LogPrefix} {claimed.Id}: couldn't open the Teams calls queue ({DescribeFailure(ex)}), so nothing was done.");
                return Finish(claimed, CallDeadLetterOutcomes.Failed, CallDeadLetterFailureCodes.QueueUnavailable, null);
            }

            var totals = new RunTotals();
            var startedUtc = _clock.UtcNow;
            try
            {
                using (var budget = new CancellationTokenSource())
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, budget.Token))
                {
                    budget.CancelAfter(_budget);
                    while (true)
                    {
                        var options = claimed.Action == CallDeadLetterActions.Clear
                            ? new CallDeadLetterReplayOptions { PurgeAll = true }
                            : new CallDeadLetterReplayOptions();
                        var summary = await new CallDeadLetterReplayer(queue, queue, _logger, _clock).RunAsync(options, linked.Token).ConfigureAwait(false);
                        totals.Add(summary);

                        if (stop.IsCancellationRequested)
                        {
                            return null;
                        }
                        if (summary.Cancelled)
                        {
                            totals.StopCode = CallDeadLetterFailureCodes.TimeBudget;
                            break;
                        }
                        if (summary.StoppedEarly != null)
                        {
                            totals.StopCode = summary.StopCode ?? CallDeadLetterFailureCodes.Unexpected;
                            break;
                        }
                        if (!summary.ReachedMaxMessages && !summary.ReachedScanLimit)
                        {
                            break;
                        }

                        // A pass that stopped at a limit having replayed or deleted nothing would only look at the same messages
                        // again: what it left stays at the head of the dead-letter queue.
                        if (summary.Replayed + summary.Purged == 0 || totals.Scanned >= CallDeadLetterReplayOptions.ScanLimitLimit)
                        {
                            totals.StopCode = CallDeadLetterFailureCodes.ScanLimit;
                            break;
                        }
                        if (budget.IsCancellationRequested || _clock.UtcNow - startedUtc >= _budget)
                        {
                            totals.StopCode = CallDeadLetterFailureCodes.TimeBudget;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                // The replayer handles Service Bus failures itself, so this is a bug: record it rather than leave the request to go stale.
                _logger.LogError(ex, $"{LogPrefix} {claimed.Id}: the replay failed unexpectedly ({ex.GetType().Name}).");
                totals.StopCode = CallDeadLetterFailureCodes.Unexpected;
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                try
                {
                    await queue.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"{LogPrefix} {claimed.Id}: couldn't close the Service Bus connection cleanly ({DescribeFailure(ex)}).");
                }
            }

            var (outcome, failureCode) = Conclude(totals);
            return Finish(claimed, outcome, failureCode, totals.ToResult());
        }

        /// <summary>What a run's totals amount to: <see cref="CallDeadLetterOutcomes"/> and, unless it succeeded, why not.</summary>
        internal static (string Outcome, string FailureCode) Conclude(RunTotals totals)
        {
            var didSomething = totals.Replayed + totals.Deleted > 0;
            switch (totals.StopCode)
            {
                case null:
                    break;
                case CallDeadLetterFailureCodes.ReadFailed:
                case CallDeadLetterFailureCodes.SendFailures:
                case CallDeadLetterFailureCodes.Unexpected:
                    return (didSomething ? CallDeadLetterOutcomes.Partial : CallDeadLetterOutcomes.Failed, totals.StopCode);
                default:
                    return (CallDeadLetterOutcomes.Partial, totals.StopCode);
            }
            if (totals.SendFailed > 0) return (CallDeadLetterOutcomes.Partial, CallDeadLetterFailureCodes.SendFailed);
            if (totals.DeleteFailed > 0) return (CallDeadLetterOutcomes.Partial, CallDeadLetterFailureCodes.DeleteFailed);
            return (CallDeadLetterOutcomes.Succeeded, null);
        }

        private CallDeadLetterRequest Finish(CallDeadLetterRequest claimed, string outcome, string failureCode, CallDeadLetterRunResult result)
        {
            var finished = claimed.Clone();
            finished.FinishedUtc = _clock.UtcNow;
            finished.Outcome = outcome;
            finished.FailureCode = failureCode;
            finished.Result = result;
            return finished;
        }

        /// <summary>
        /// Records the result over this instance's claim. Not cancelled by shutdown: the work is done, and saying so takes a
        /// single write. Nothing is written when the request was replaced meanwhile.
        /// </summary>
        private async Task RecordAsync(CallDeadLetterRequest claimed, CallDeadLetterRequest finished)
        {
            var result = finished.Result;
            var summary = $"{LogPrefix} {finished.Id} ({finished.Action}) finished: {finished.Outcome}" +
                (finished.FailureCode == null ? string.Empty : $" ({finished.FailureCode})") +
                (result == null ? "." : $" after {result.Passes} pass(es); replayed {result.Replayed}, deleted {result.Deleted}, " +
                    $"left in the dead-letter queue {result.Reasons.Sum(r => r.Left)}.");

            for (var attempt = 1; attempt <= RecordAttempts; attempt++)
            {
                try
                {
                    var current = await _store.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                    if (current.Request == null || current.Request.Id != claimed.Id || current.Request.ClaimedUtc != claimed.ClaimedUtc || current.Request.FinishedUtc.HasValue)
                    {
                        _logger.LogWarning(summary + " The request was replaced while it ran, so this result is in this log only.");
                        return;
                    }
                    if (await _store.TryReplaceAsync(finished, current.VersionToken, CancellationToken.None).ConfigureAwait(false))
                    {
                        if (finished.Outcome == CallDeadLetterOutcomes.Succeeded)
                        {
                            _logger.LogInformation(summary + " Recorded for the portal.");
                        }
                        else
                        {
                            _logger.LogWarning(summary + " Recorded for the portal.");
                        }
                        return;
                    }
                }
                catch (CallDeadLetterStateUnavailableException ex)
                {
                    if (attempt == RecordAttempts)
                    {
                        _logger.LogWarning(summary + $" Couldn't record it for the portal ({DescribeFailure(ex.InnerException ?? ex)}), so the portal shows the " +
                            $"request as not finished {CallDeadLetterRequestRules.FinishWithin.TotalHours:0} hours after it was claimed.");
                        return;
                    }
                }
                await Task.Delay(RecordRetryDelay).ConfigureAwait(false);
            }
            _logger.LogWarning(summary + " The request kept changing while the result was being recorded, so it is in this log only.");
        }

        private void LogStoreFailure(string what, CallDeadLetterStateUnavailableException ex)
        {
            var now = _clock.UtcNow;
            var message = $"{RequestsLogPrefix}: {what} in the runtime state table ({DescribeFailure(ex.InnerException ?? ex)}). Tried again every " +
                $"{CallDeadLetterRequestRules.PollInterval.TotalMinutes:0} minute(s).";
            if (_readFailureLoggedUtc == null || now - _readFailureLoggedUtc.Value >= ReadFailureLogInterval)
            {
                _readFailureLoggedUtc = now;
                _logger.LogWarning(message);
            }
            else
            {
                _logger.LogDebug(message);
            }
        }

        private static string Describe(string action)
        {
            return action == CallDeadLetterActions.Clear
                ? "clear (DELETE every dead-lettered call, whatever its reason or age)"
                : "reprocess (send the dead-lettered calls back to the queue)";
        }

        /// <summary>The exception's type and, for an Azure SDK failure, its status and error code: no message, which can carry a URL.</summary>
        private static string DescribeFailure(Exception ex)
        {
            if (ex is Azure.RequestFailedException requestFailed)
            {
                return $"{ex.GetType().Name}, HTTP {requestFailed.Status}" + (string.IsNullOrEmpty(requestFailed.ErrorCode) ? string.Empty : $", {requestFailed.ErrorCode}");
            }
            if (ex is Azure.Messaging.ServiceBus.ServiceBusException serviceBus)
            {
                return $"{ex.GetType().Name}, {serviceBus.Reason}";
            }
            return ex.GetType().Name;
        }

        public void Dispose()
        {
            _stop.Cancel();
        }

        /// <summary>
        /// One request's passes over the dead-letter queue, added up. Replays and deletes add up; what a pass left comes round
        /// again on the next, so for that the most any pass left is kept.
        /// </summary>
        internal sealed class RunTotals
        {
            private readonly Dictionary<string, CallDeadLetterReasonCount> _reasons = new Dictionary<string, CallDeadLetterReasonCount>(StringComparer.Ordinal);

            public int Passes { get; private set; }
            public int Scanned { get; private set; }
            public int Replayed { get; private set; }
            public int ReplayedButNotRemoved { get; private set; }
            public int Deleted { get; private set; }
            public int SendFailed { get; private set; }
            public int DeleteFailed { get; private set; }
            public Dictionary<string, int> Skipped { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
            public string StopCode { get; set; }

            public void Add(CallDeadLetterReplaySummary summary)
            {
                Passes++;
                Scanned += summary.Scanned;
                Replayed += summary.Replayed;
                ReplayedButNotRemoved += summary.ReplayedButNotRemoved;
                Deleted += summary.Purged;
                SendFailed += summary.Failed;
                DeleteFailed += summary.PurgeFailed;

                foreach (var skipped in summary.SkippedByReason)
                {
                    var code = SkipCode(skipped.Key);
                    Skipped.TryGetValue(code, out var most);
                    Skipped[code] = Math.Max(most, skipped.Value);
                }

                foreach (var byReason in summary.ByDeadLetterReason)
                {
                    var reason = byReason.Key == CallDeadLetterReplaySummary.NoReason ? string.Empty : byReason.Key;
                    if (!_reasons.TryGetValue(reason, out var count))
                    {
                        count = new CallDeadLetterReasonCount { Reason = reason.Length == 0 ? null : Truncate(reason) };
                        _reasons[reason] = count;
                    }
                    count.Replayed += byReason.Value.Replayed;
                    count.Deleted += byReason.Value.Deleted;
                    count.Left = Math.Max(count.Left, byReason.Value.Count - byReason.Value.Replayed - byReason.Value.Deleted);
                }
            }

            public CallDeadLetterRunResult ToResult()
            {
                return new CallDeadLetterRunResult
                {
                    Passes = Passes,
                    Scanned = Scanned,
                    Replayed = Replayed,
                    ReplayedButNotRemoved = ReplayedButNotRemoved,
                    Deleted = Deleted,
                    SendFailed = SendFailed,
                    DeleteFailed = DeleteFailed,
                    Skipped = new Dictionary<string, int>(Skipped, StringComparer.Ordinal),
                    Reasons = _reasons.Values
                        .OrderByDescending(r => r.Replayed + r.Deleted + r.Left)
                        .ThenBy(r => r.Reason, StringComparer.Ordinal)
                        .Take(CallDeadLetterRequestRules.MaxReasons)
                        .ToList(),
                    StopCode = StopCode
                };
            }

            private static string Truncate(string reason)
            {
                return reason.Length <= CallDeadLetterRequestRules.MaxReasonLength ? reason : reason.Substring(0, CallDeadLetterRequestRules.MaxReasonLength);
            }

            private static string SkipCode(CallDeadLetterSkipReason reason)
            {
                switch (reason)
                {
                    case CallDeadLetterSkipReason.OtherReason: return CallDeadLetterSkipCodes.OtherReason;
                    case CallDeadLetterSkipReason.NeverImportable: return CallDeadLetterSkipCodes.NeverImportable;
                    case CallDeadLetterSkipReason.CallRecordNoLongerInGraph: return CallDeadLetterSkipCodes.CallRecordNoLongerInGraph;
                    case CallDeadLetterSkipReason.ReplayLimitReached: return CallDeadLetterSkipCodes.ReplayLimitReached;
                    case CallDeadLetterSkipReason.ReplayedInThisRun: return CallDeadLetterSkipCodes.ReplayedInThisRun;
                    default: return char.ToLowerInvariant(reason.ToString()[0]) + reason.ToString().Substring(1);
                }
            }
        }
    }
}
