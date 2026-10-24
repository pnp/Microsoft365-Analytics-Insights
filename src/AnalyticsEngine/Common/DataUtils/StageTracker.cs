using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace DataUtils
{
    /// <summary>
    /// The two places a host can be working at once: the import cycle, and the background pass that outlives it.
    /// </summary>
    public enum StageSlot
    {
        Cycle,
        Background
    }

    /// <summary>How a stage ended.</summary>
    public enum StageOutcome
    {
        Succeeded,
        Failed,
        Skipped
    }

    /// <summary>What a slot is doing now: the stage path (outermost first, joined with '/') and when its innermost stage began.</summary>
    public sealed class StageSnapshot
    {
        public string Stage { get; set; }
        public DateTime? StartedUtc { get; set; }
    }

    /// <summary>
    /// Which phase of the import a process is in, so that a hang or a slow cycle can be located from a log export
    /// alone. A stage logs an <c>ImportStage start</c> trace when it begins and an <c>ImportStage</c> custom event when
    /// it ends, <b>on failure too</b>, with the outcome, the duration and the failure classification; and the heartbeat
    /// reports the stage in progress every minute.
    /// </summary>
    public sealed class StageTracker
    {
        /// <summary>The tracker every host and the heartbeat share.</summary>
        public static StageTracker Process { get; } = new StageTracker();

        private readonly object _gate = new object();
        private readonly Dictionary<StageSlot, List<StageScope>> _active = new Dictionary<StageSlot, List<StageScope>>
        {
            { StageSlot.Cycle, new List<StageScope>() },
            { StageSlot.Background, new List<StageScope>() },
        };

        /// <summary>Starts a stage. Record its end with <see cref="StageScope.Succeeded"/>, <see cref="StageScope.Skipped"/> or <see cref="StageScope.Failed"/>.</summary>
        public StageScope Begin(AnalyticsLogger logger, string stage, StageSlot slot = StageSlot.Cycle)
        {
            if (string.IsNullOrWhiteSpace(stage)) throw new ArgumentException("A stage needs a name.", nameof(stage));
            var scope = new StageScope(this, logger, stage, slot);
            lock (_gate)
            {
                _active[slot].Add(scope);
            }
            logger?.LogInformation($"ImportStage start: Stage={stage}; Slot={slot}.");
            return scope;
        }

        /// <summary>
        /// Runs <paramref name="work"/> as a stage: success when it returns, failure (with the exception, which is then
        /// rethrown unchanged) when it throws.
        /// </summary>
        public async System.Threading.Tasks.Task RunAsync(AnalyticsLogger logger, string stage, Func<System.Threading.Tasks.Task> work, StageSlot slot = StageSlot.Cycle)
        {
            var scope = Begin(logger, stage, slot);
            try
            {
                await work().ConfigureAwait(false);
                scope.Succeeded();
            }
            catch (Exception ex)
            {
                scope.Failed(ex);
                throw;
            }
        }

        /// <summary>The innermost stage of a slot, or null when the slot is idle.</summary>
        public StageSnapshot Snapshot(StageSlot slot = StageSlot.Cycle)
        {
            lock (_gate)
            {
                var active = _active[slot];
                if (active.Count == 0) return null;
                return new StageSnapshot
                {
                    Stage = string.Join("/", active.Select(s => s.Stage)),
                    StartedUtc = active[active.Count - 1].StartedUtc,
                };
            }
        }

        internal void Remove(StageScope scope)
        {
            lock (_gate)
            {
                _active[scope.Slot].Remove(scope);
            }
        }
    }

    /// <summary>One stage in progress. Exactly one outcome is recorded; a later one is ignored.</summary>
    public sealed class StageScope : IDisposable
    {
        private readonly StageTracker _tracker;
        private readonly AnalyticsLogger _logger;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _finished;

        internal StageScope(StageTracker tracker, AnalyticsLogger logger, string stage, StageSlot slot)
        {
            _tracker = tracker;
            _logger = logger;
            Stage = stage;
            Slot = slot;
            StartedUtc = DateTime.UtcNow;
        }

        public string Stage { get; }
        public StageSlot Slot { get; }
        public DateTime StartedUtc { get; }

        public void Succeeded() => Finish(StageOutcome.Succeeded, null, null);

        /// <param name="reason">Why nothing was done, e.g. a stable key such as "throttled".</param>
        public void Skipped(string reason = null) => Finish(StageOutcome.Skipped, null, reason);

        /// <param name="exception">The failure, when there is one; a stage can also fail by reporting that it did not complete.</param>
        /// <param name="reason">Why it failed when there is no exception.</param>
        public void Failed(Exception exception = null, string reason = null) => Finish(StageOutcome.Failed, exception, reason);

        /// <summary>A stage left without an outcome did not finish: it is recorded as failed rather than dropped.</summary>
        public void Dispose() => Finish(StageOutcome.Failed, null, "NoOutcomeRecorded");

        private void Finish(StageOutcome outcome, Exception exception, string reason)
        {
            if (System.Threading.Interlocked.Exchange(ref _finished, 1) != 0) return;
            _stopwatch.Stop();
            _tracker.Remove(this);

            var context = new Dictionary<string, string>
            {
                { "Stage", Stage },
                { "Slot", Slot.ToString() },
                { "Outcome", outcome.ToString() },
            };
            if (!string.IsNullOrEmpty(reason)) context["Reason"] = reason;
            if (exception != null)
            {
                foreach (var dimension in ExceptionDescriber.Dimensions(exception)) context[dimension.Key] = dimension.Value;
            }
            var metrics = new Dictionary<string, double> { { "DurationMs", _stopwatch.Elapsed.TotalMilliseconds } };

            if (_logger == null) return;
            _logger.TrackEvent(AnalyticsLogger.AnalyticsEvent.ImportStage, context, metrics);
            if (outcome == StageOutcome.Failed)
            {
                // The exception itself is tracked by whoever handles it; this is the unit-of-work outcome line.
                _logger.LogError($"ImportStage finish: Stage={Stage}; Outcome=Failed; DurationMs={_stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}"
                    + (exception != null ? $"; {ExceptionDescriber.Chain(exception)}" : string.Empty)
                    + (!string.IsNullOrEmpty(reason) ? $"; Reason={reason}" : string.Empty) + ".");
            }
        }
    }
}
