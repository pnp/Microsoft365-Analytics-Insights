using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Pauses the calls queue during an outage that every call would hit (<see cref="CallImportFailureKind.Systemic"/>
    /// failures), such as SQL being unavailable.
    /// </summary>
    /// <remarks>
    /// Without it the processor keeps taking notifications off the queue and failing them one after another, spending
    /// every one's retries - and its Graph calls - on an outage that has nothing to do with the call. While paused, a
    /// message handler waits before importing (Service Bus keeps renewing its lock), so at most the processor's
    /// concurrent handlers hold a message and the rest of the backlog is left untouched in the queue.
    /// <para>
    /// "Systemic" is judged from one error, and one call can produce such errors on its own - a 504 on its own
    /// oversized call record, say. So it takes failures from <see cref="DistinctCallsToPause"/> different calls within
    /// <see cref="FailureWindow"/>, with no call saved in between, to pause the queue; one call can never pause it alone.
    /// </para>
    /// <para>
    /// When a pause ends one message is let through as a probe. Only a call that is actually saved ends the outage:
    /// one processed without saving (out of <c>UserGroupsFilter</c>, no organiser) never touched SQL, so it just lets
    /// the next message probe. Failures from different calls during the probing pause the queue again, for longer
    /// (1, 2, 4 ... minutes, at most 15). Pure state: callers pass the time in.
    /// </para>
    /// </remarks>
    public sealed class CallImportCircuitBreaker
    {
        public static readonly TimeSpan DefaultInitialPause = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan DefaultMaxPause = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan DefaultFailureWindow = TimeSpan.FromMinutes(5);
        public const int DefaultDistinctCallsToPause = 2;

        /// <summary>How often a handler waiting behind a probe asks again.</summary>
        public static readonly TimeSpan ProbePollInterval = TimeSpan.FromSeconds(5);

        private readonly object _lock = new object();

        // Call id -> its latest systemic failure, within FailureWindow and since the last save.
        private readonly Dictionary<string, DateTime> _recentSystemicFailures = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DateTime? _pausedUntilUtc;
        private bool _probeInFlight;
        private int _consecutivePauses;

        public CallImportCircuitBreaker()
            : this(DefaultInitialPause, DefaultMaxPause, DefaultFailureWindow, DefaultDistinctCallsToPause) { }

        public CallImportCircuitBreaker(TimeSpan initialPause, TimeSpan maxPause, TimeSpan failureWindow, int distinctCallsToPause)
        {
            if (initialPause <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initialPause));
            if (maxPause < initialPause) throw new ArgumentOutOfRangeException(nameof(maxPause));
            if (failureWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(failureWindow));
            if (distinctCallsToPause < 1) throw new ArgumentOutOfRangeException(nameof(distinctCallsToPause));

            InitialPause = initialPause;
            MaxPause = maxPause;
            FailureWindow = failureWindow;
            DistinctCallsToPause = distinctCallsToPause;
        }

        public TimeSpan InitialPause { get; }
        public TimeSpan MaxPause { get; }

        /// <summary>How far back systemic failures of other calls count towards a pause.</summary>
        public TimeSpan FailureWindow { get; }

        /// <summary>How many different calls must fail systemically, with none saved in between, to pause the queue.</summary>
        public int DistinctCallsToPause { get; }

        /// <summary>True from a pause until a call is saved.</summary>
        public bool IsPaused
        {
            get { lock (_lock) { return _pausedUntilUtc.HasValue; } }
        }

        /// <summary>
        /// Whether a handler may import now. <see cref="TimeSpan.Zero"/> means go ahead - and if
        /// <paramref name="isProbe"/> is set, this import decides whether the queue resumes, so its outcome must be
        /// reported. Otherwise, how long to wait before asking again.
        /// </summary>
        public TimeSpan TryEnter(DateTime nowUtc, out bool isProbe)
        {
            lock (_lock)
            {
                isProbe = false;
                if (!_pausedUntilUtc.HasValue)
                {
                    return TimeSpan.Zero;
                }
                if (nowUtc < _pausedUntilUtc.Value)
                {
                    return _pausedUntilUtc.Value - nowUtc;
                }
                if (_probeInFlight)
                {
                    return ProbePollInterval;
                }

                _probeInFlight = true;
                isProbe = true;
                return TimeSpan.Zero;
            }
        }

        /// <summary>A call was saved, so SQL and Graph both work: the queue resumes and past failures are forgotten.</summary>
        /// <returns>True when this ended a pause.</returns>
        public bool RecordSaved()
        {
            lock (_lock)
            {
                var wasPaused = _pausedUntilUtc.HasValue;
                _pausedUntilUtc = null;
                _probeInFlight = false;
                _consecutivePauses = 0;
                _recentSystemicFailures.Clear();
                return wasPaused;
            }
        }

        /// <summary>
        /// A call was processed without being saved. It never reached SQL, so it proves nothing about an outage: a pause
        /// stays as it is, and a probe just makes way for the next one.
        /// </summary>
        public void RecordProcessedWithoutSaving(bool wasProbe)
        {
            lock (_lock)
            {
                if (wasProbe)
                {
                    _probeInFlight = false;
                }
            }
        }

        /// <summary>An import failed.</summary>
        /// <returns>The pause this started, or null when it started none.</returns>
        public TimeSpan? RecordFailure(CallImportFailureKind kind, string callId, bool wasProbe, DateTime nowUtc)
        {
            lock (_lock)
            {
                if (wasProbe)
                {
                    _probeInFlight = false;
                }

                if (kind != CallImportFailureKind.Systemic)
                {
                    // Says nothing about an outage, so the next waiting handler probes instead.
                    return null;
                }

                var windowStart = nowUtc - FailureWindow;
                foreach (var expired in _recentSystemicFailures.Where(f => f.Value < windowStart).Select(f => f.Key).ToList())
                {
                    _recentSystemicFailures.Remove(expired);
                }
                _recentSystemicFailures[callId ?? string.Empty] = nowUtc;

                if (_pausedUntilUtc.HasValue && nowUtc < _pausedUntilUtc.Value)
                {
                    // Another import that was already running when the pause began: the same outage, not a new one.
                    return null;
                }

                if (_recentSystemicFailures.Count < DistinctCallsToPause)
                {
                    // Only this call is failing so far: that may well be a problem with the call, not an outage.
                    return null;
                }

                _consecutivePauses++;
                var pause = GetPause(_consecutivePauses);
                _pausedUntilUtc = nowUtc + pause;
                _probeInFlight = false;
                return pause;
            }
        }

        private TimeSpan GetPause(int consecutivePauses)
        {
            var doublings = Math.Max(0, Math.Min(consecutivePauses - 1, 30));
            var ticks = InitialPause.Ticks * Math.Pow(2, doublings);
            return ticks >= MaxPause.Ticks ? MaxPause : TimeSpan.FromTicks((long)ticks);
        }
    }
}
