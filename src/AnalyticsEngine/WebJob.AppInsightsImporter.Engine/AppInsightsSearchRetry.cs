using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>One UTC ingestion hour to reread from App Insights. No event, identity or search text is checkpointed.</summary>
    public sealed class AppInsightsSearchRetry
    {
        public AppInsightsSearchRetry(DateTime hourUtc, DateTime retryAfterUtc)
        {
            HourUtc = hourUtc;
            RetryAfterUtc = retryAfterUtc;
        }

        public DateTime HourUtc { get; }
        public DateTime RetryAfterUtc { get; }
        internal DateTime ExpiresUtc => DeadlineFor(HourUtc);
        internal static DateTime DeadlineFor(DateTime hourUtc) => hourUtc.AddHours(1) + AppInsightsIngestionCursorPolicy.LateArrivalBound;
    }

    internal sealed class AppInsightsSearchRetries
    {
        // Give a delayed page view longer than the normal ingestion overlap. A second, final pass covers
        // the importer's existing late-arrival allowance; neither pass holds up the ordinary ingestion cursor.
        private static readonly TimeSpan FirstDelay = AppInsightsIngestionCursorPolicy.Overlap + AppInsightsIngestionCursorPolicy.Overlap;
        private const int MaxHoursPerPass = 8;
        private readonly Dictionary<DateTime, AppInsightsSearchRetry> _hours;
        private readonly IAppInsightsIngestionCursorStore _store;
        private HashSet<DateTime> _replayHours;

        internal AppInsightsSearchRetries(IEnumerable<AppInsightsSearchRetry> saved, IAppInsightsIngestionCursorStore store)
        {
            _hours = saved.ToDictionary(h => h.HourUtc);
            _store = store;
        }

        internal IReadOnlyCollection<AppInsightsSearchRetry> Hours => _hours.Values;
        private bool Changed { get; set; }

        internal async Task PersistChangesAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Changed) await _store.WriteSearchRetriesAsync(Hours, token);
            token.ThrowIfCancellationRequested();
            Changed = false;
        }

        internal int RemoveOutsideRetention(DateTime now)
        {
            var lost = _hours.Keys.Where(h => h.AddHours(1) <= now - AppInsightsIngestionCursorPolicy.MaxLookback).ToList();
            foreach (var hour in lost) _hours.Remove(hour);
            if (lost.Count != 0) Changed = true;
            return lost.Count;
        }

        internal bool Observe(DateTime hour, DateTime now, bool newlyReadIngestion)
        {
            if (_hours.ContainsKey(hour)) return true;
            if (hour.AddHours(1) <= now - AppInsightsIngestionCursorPolicy.MaxLookback) return false;
            var expires = AppInsightsSearchRetry.DeadlineFor(hour);
            // Catch-up may first see an older search before a later timestamp day's page view is imported.
            // Give that newly covered ingestion one final post-import pass, not a new three-day lifetime.
            if (expires <= now && !newlyReadIngestion) return false;
            _hours.Add(hour, new AppInsightsSearchRetry(hour, now + FirstDelay < expires ? now + FirstDelay : expires));
            Changed = true;
            return true;
        }

        private IEnumerable<AppInsightsSearchRetry> DueHours(DateTime coveredThrough) =>
            _hours.Values.Where(h => h.RetryAfterUtc <= coveredThrough && h.HourUtc < coveredThrough);

        internal bool HasDue(DateTime coveredThrough) => DueHours(coveredThrough).Any();

        internal List<AppInsightsSearchRetry> Due(DateTime coveredThrough) =>
            DueHours(coveredThrough).OrderBy(h => h.RetryAfterUtc).ThenBy(h => h.HourUtc).Take(MaxHoursPerPass).ToList();

        internal void Unavailable(AppInsightsSearchRetry hour, DateTime coveredThrough)
        {
            // An outage did not check whether a visit exists, even on the final pass. Let other due hours go first.
            _hours[hour.HourUtc] = new AppInsightsSearchRetry(hour.HourUtc, coveredThrough + FirstDelay);
            Changed = true;
        }

        private async Task LoadReplayHoursAsync(DateTime now, CancellationToken token)
        {
            if (_replayHours == null)
            {
                var saved = await _store.ReadSearchReplayHoursAsync(token);
                token.ThrowIfCancellationRequested();
                _replayHours = new HashSet<DateTime>(saved);
            }
            // On a restore this set may have been read before a long normal import crossed the retention edge.
            var cutoff = now - AppInsightsIngestionCursorPolicy.MaxLookback;
            _replayHours.RemoveWhere(h => h.AddHours(1) <= cutoff);
        }

        internal async Task ReopenAfterRestoreAsync(DateTime now, CancellationToken token)
        {
            await LoadReplayHoursAsync(now, token);
            foreach (var hour in _replayHours)
            {
                if (!_hours.TryGetValue(hour, out var pending) || pending.RetryAfterUtc > now)
                {
                    _hours[hour] = new AppInsightsSearchRetry(hour, now);
                    Changed = true;
                }
            }
        }

        // SQL can commit just before the host stops; on restart an idempotent merge reports zero inserts.
        // Remember the source hours BEFORE the attempt, not just those which return a positive insert count.
        // This separate resource-local row stays dormant on healthy cycles with no due work. At most one
        // timestamp per retained ingestion hour (90 days plus the partial edge), independent of user count.
        internal async Task RememberBeforeReconciliationAsync(List<AppInsightsSearchRetry> due, DateTime now, CancellationToken token)
        {
            await LoadReplayHoursAsync(now, token);
            var changed = false;
            foreach (var hour in due) changed |= _replayHours.Add(hour.HourUtc);
            if (changed) await _store.WriteSearchReplayHoursAsync(_replayHours.OrderBy(h => h).ToList(), token);
            token.ThrowIfCancellationRequested();
        }

        internal void Finished(AppInsightsSearchRetry hour, bool stillMissing, DateTime now)
        {
            if (!stillMissing || now >= hour.ExpiresUtc) _hours.Remove(hour.HourUtc);
            else _hours[hour.HourUtc] = new AppInsightsSearchRetry(hour.HourUtc, hour.ExpiresUtc);
            Changed = true;
        }
    }
}
