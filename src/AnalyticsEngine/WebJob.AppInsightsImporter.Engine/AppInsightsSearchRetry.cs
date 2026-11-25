using System;
using System.Collections.Generic;
using System.Linq;

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
        private const int MaxHoursPerCycle = 8;
        private readonly Dictionary<DateTime, AppInsightsSearchRetry> _hours;

        internal AppInsightsSearchRetries(IEnumerable<AppInsightsSearchRetry> saved)
        {
            _hours = saved.ToDictionary(h => h.HourUtc);
        }

        internal IReadOnlyCollection<AppInsightsSearchRetry> Hours => _hours.Values;
        internal bool Changed { get; private set; }

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

        internal List<AppInsightsSearchRetry> Due(DateTime now) =>
            _hours.Values.Where(h => h.RetryAfterUtc <= now).OrderBy(h => h.RetryAfterUtc)
                .ThenBy(h => h.HourUtc).Take(MaxHoursPerCycle).ToList();

        internal void Finished(AppInsightsSearchRetry hour, bool stillMissing, DateTime now)
        {
            if (!stillMissing || now >= hour.ExpiresUtc) _hours.Remove(hour.HourUtc);
            else _hours[hour.HourUtc] = new AppInsightsSearchRetry(hour.HourUtc, hour.ExpiresUtc);
            Changed = true;
        }
    }
}
