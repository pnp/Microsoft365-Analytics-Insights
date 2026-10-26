using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// Process-lifetime record of the import days that failed - to download or to save - so a failed day is retried
    /// before any later day, but a day that fails every time cannot stall the import on it for ever.
    ///
    /// Both halves matter. Carrying on past a failed day lets the cycle finish and move the import's resume point (the
    /// ingestion cursor, or before there is one the newest stored hit) past it, and what the day did not save is not
    /// read again. So the importer stops at a failed day and the next cycle starts there. But a deterministic failure
    /// would then hold the import on that one day indefinitely - the months-long stall that 895ff223 fixed by skipping
    /// failed days. So a day is attempted at most <see cref="MaxAttempts"/> times in a row; after that its forced
    /// retries stop and later days go ahead. That is not necessarily a permanent gap: rows for that day ingested
    /// later are still read, and the day is retried whenever they are.
    ///
    /// Held by the web-job host for the life of the process, because the host creates a new importer every cycle -
    /// as <see cref="AppInsightsScanWatermark"/> is. Not persisted: after a restart a failed day is retried only if
    /// the resume point has not moved past it, and its attempts start again from zero. Thread-safe.
    /// </summary>
    public sealed class AppInsightsDayFailureTracker
    {
        /// <summary>How many times in a row a day may fail to save before it is given up on.</summary>
        public const int DefaultMaxAttempts = 3;

        private readonly object _lock = new object();
        private readonly Dictionary<DateTime, int> _consecutiveFailuresByDay = new Dictionary<DateTime, int>();

        public AppInsightsDayFailureTracker() : this(DefaultMaxAttempts)
        {
        }

        public AppInsightsDayFailureTracker(int maxAttempts)
        {
            if (maxAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttempts), "A day needs at least one attempt.");
            }
            MaxAttempts = maxAttempts;
        }

        public int MaxAttempts { get; }

        /// <summary>
        /// Records that saving <paramref name="dayUtc"/> failed, and returns how many times in a row it now has.
        /// </summary>
        public int RecordFailure(DateTime dayUtc)
        {
            lock (_lock)
            {
                _consecutiveFailuresByDay.TryGetValue(dayUtc.Date, out var failures);
                failures++;
                _consecutiveFailuresByDay[dayUtc.Date] = failures;
                return failures;
            }
        }

        /// <summary>True once a day has failed <see cref="MaxAttempts"/> times in a row: carry on without it.</summary>
        public bool ShouldGiveUp(int consecutiveFailures) => consecutiveFailures >= MaxAttempts;

        /// <summary>
        /// True only for the failure that stops a day's forced retries, so "retries stopped" is reported once per
        /// transition rather than on every later failure of the same day.
        /// </summary>
        public bool StopsRetries(int consecutiveFailures) => consecutiveFailures == MaxAttempts;

        /// <summary><paramref name="dayUtc"/> was processed completely; any earlier failures no longer count.</summary>
        public void RecordSuccess(DateTime dayUtc)
        {
            lock (_lock)
            {
                _consecutiveFailuresByDay.Remove(dayUtc.Date);
            }
        }

        /// <summary>
        /// The earliest day that failed and has not been given up on, which the next run must start from even if
        /// the stored watermark has since moved past it. Null when there is none.
        /// </summary>
        public DateTime? EarliestDayAwaitingRetryUtc
        {
            get
            {
                lock (_lock)
                {
                    var awaitingRetry = _consecutiveFailuresByDay.Where(f => !ShouldGiveUp(f.Value)).Select(f => f.Key).ToList();
                    return awaitingRetry.Count == 0 ? (DateTime?)null : DateTime.SpecifyKind(awaitingRetry.Min(), DateTimeKind.Utc);
                }
            }
        }

        /// <summary>
        /// Used by an importer whose host supplied no tracker, so the retry limit still holds across that host's
        /// cycles. The web-job host supplies its own.
        /// </summary>
        internal static AppInsightsDayFailureTracker ProcessDefault { get; } = new AppInsightsDayFailureTracker();
    }
}
