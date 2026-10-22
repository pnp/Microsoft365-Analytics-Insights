using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// Process-lifetime memory of the query windows each day's telemetry needed. When a day is over the App Insights
    /// API's row or size limits, <see cref="AppInsightsAPIClient"/> splits its query until every window comes back
    /// complete. A day's telemetry only grows, so the next cycle starts from the windows that last came back
    /// complete instead of splitting from the whole day again - which downloaded and threw away several capped
    /// responses, each up to ~104 MB, every cycle. A remembered window that has since grown past the limits is split
    /// further in exactly the same way, so what is read is identical either way.
    ///
    /// Held by the web-job host for the life of the process - like <see cref="AppInsightsDayFailureTracker"/> - and
    /// handed to the client the importer creates each cycle. Thread-safe: a day's page views and custom events are
    /// read in parallel.
    /// </summary>
    public sealed class AppInsightsQueryWindowCache
    {
        private readonly object _lock = new object();
        private readonly Dictionary<(string Query, DateTime DayUtc), IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)>> _windowsByDay =
            new Dictionary<(string Query, DateTime DayUtc), IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)>>();

        /// <summary>
        /// The windows the <paramref name="query"/> for the day starting at <paramref name="dayUtc"/> last needed, in
        /// time order, or null when it has not needed splitting: start from the whole day.
        /// </summary>
        public IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> WindowsFor(string query, DateTime dayUtc)
        {
            lock (_lock)
            {
                return _windowsByDay.TryGetValue((query, dayUtc), out var windows) ? windows : null;
            }
        }

        /// <summary>
        /// Remembers the windows that came back complete for the <paramref name="query"/> for the day starting at
        /// <paramref name="dayUtc"/>. Only a split day is remembered - a single window is the whole day, the default -
        /// and only windows that exactly cover the day, in order and without gaps, so a remembered plan can never skip
        /// or repeat a row.
        /// </summary>
        public void Remember(string query, DateTime dayUtc, IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> completeWindows)
        {
            lock (_lock)
            {
                if (completeWindows != null && completeWindows.Count > 1 && CoverExactly(completeWindows, dayUtc, dayUtc.AddDays(1)))
                {
                    _windowsByDay[(query, dayUtc)] = completeWindows.ToList();
                }
                else
                {
                    _windowsByDay.Remove((query, dayUtc));
                }
            }
        }

        /// <summary>Forgets every day that starts before <paramref name="dayUtc"/>: days the import no longer reads.</summary>
        public void ForgetDaysBefore(DateTime dayUtc)
        {
            lock (_lock)
            {
                foreach (var key in _windowsByDay.Keys.Where(k => k.DayUtc < dayUtc).ToList())
                {
                    _windowsByDay.Remove(key);
                }
            }
        }

        private static bool CoverExactly(IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> windows, DateTime fromUtc, DateTime toUtc)
        {
            var expectedFrom = fromUtc;
            foreach (var window in windows)
            {
                if (window.FromUtc != expectedFrom || window.ToUtc <= window.FromUtc)
                {
                    return false;
                }
                expectedFrom = window.ToUtc;
            }
            return expectedFrom == toUtc;
        }

        /// <summary>
        /// Used by an importer whose host supplied no cache. The web-job host supplies its own.
        /// </summary>
        internal static AppInsightsQueryWindowCache ProcessDefault { get; } = new AppInsightsQueryWindowCache();
    }
}
