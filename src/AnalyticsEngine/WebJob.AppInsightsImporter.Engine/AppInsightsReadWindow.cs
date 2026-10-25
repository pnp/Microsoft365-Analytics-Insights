using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>The two kinds of telemetry the importer reads.</summary>
    public enum AppInsightsTelemetryKind
    {
        PageViews,
        Events
    }

    /// <summary>
    /// What one query reads: rows whose <c>timestamp</c> is in [<see cref="FromUtc"/>, <see cref="ToUtc"/>) and, when
    /// set, whose <c>ingestion_time()</c> is in [<see cref="IngestedFromUtc"/>, <see cref="IngestedBeforeUtc"/>).
    /// Both ranges are half-open, so consecutive windows never return a row twice.
    /// </summary>
    public sealed class AppInsightsReadWindow
    {
        public AppInsightsReadWindow(DateTime fromUtc, DateTime toUtc, DateTime? ingestedFromUtc = null, DateTime? ingestedBeforeUtc = null)
        {
            if (toUtc <= fromUtc) throw new ArgumentException("A window must end after it starts.", nameof(toUtc));
            if (ingestedFromUtc.HasValue && ingestedBeforeUtc.HasValue && ingestedBeforeUtc.Value <= ingestedFromUtc.Value)
            {
                throw new ArgumentException("The ingestion range must end after it starts.", nameof(ingestedBeforeUtc));
            }
            FromUtc = fromUtc;
            ToUtc = toUtc;
            IngestedFromUtc = ingestedFromUtc;
            IngestedBeforeUtc = ingestedBeforeUtc;
        }

        public DateTime FromUtc { get; }
        public DateTime ToUtc { get; }
        public DateTime? IngestedFromUtc { get; }
        public DateTime? IngestedBeforeUtc { get; }

        /// <summary>The whole UTC day starting at <paramref name="dayUtc"/>, with no ingestion bounds.</summary>
        public static AppInsightsReadWindow WholeDay(DateTime dayUtc) => new AppInsightsReadWindow(dayUtc.Date, dayUtc.Date.AddDays(1));

        /// <summary>The same ingestion bounds over a different timestamp range.</summary>
        public AppInsightsReadWindow WithTimestamps(DateTime fromUtc, DateTime toUtc) =>
            new AppInsightsReadWindow(fromUtc, toUtc, IngestedFromUtc, IngestedBeforeUtc);

        public override string ToString() =>
            $"{FromUtc:yyyy-MM-dd HH:mm:ss} to {ToUtc:yyyy-MM-dd HH:mm:ss} UTC" +
            (IngestedFromUtc.HasValue || IngestedBeforeUtc.HasValue
                ? $", ingested {(IngestedFromUtc.HasValue ? IngestedFromUtc.Value.ToString("yyyy-MM-dd HH:mm:ss") : "any time")} to {(IngestedBeforeUtc.HasValue ? IngestedBeforeUtc.Value.ToString("yyyy-MM-dd HH:mm:ss") : "now")}"
                : string.Empty);
    }

    /// <summary>How many rows of one kind a range holds in one UTC hour (by <c>timestamp</c>).</summary>
    public sealed class AppInsightsHourlyRowCount
    {
        public AppInsightsHourlyRowCount(AppInsightsTelemetryKind kind, DateTime hourUtc, long count)
        {
            Kind = kind;
            HourUtc = DateTime.SpecifyKind(new DateTime(hourUtc.Year, hourUtc.Month, hourUtc.Day, hourUtc.Hour, 0, 0), DateTimeKind.Utc);
            Count = count;
        }

        public AppInsightsTelemetryKind Kind { get; }
        public DateTime HourUtc { get; }
        public long Count { get; }
    }

    /// <summary>
    /// Splits a day into the query windows the importer reads one at a time, from how many rows each hour holds. A
    /// window is fetched, parsed, staged and merged before the next is fetched, so its size is what bounds the
    /// importer's memory - a whole busy day used to be held at once. The App Insights client still halves a window
    /// whose result comes back partial, so a window planned from counts that has grown since is read completely too.
    /// </summary>
    public static class AppInsightsWindowPlanner
    {
        /// <summary>
        /// The most rows a planned window holds. Projected page-view rows are well under a kilobyte, so this keeps one
        /// window's response to tens of megabytes on the shared App Service plan, far below the API's own limits
        /// (500,000 rows / ~104 MB).
        /// </summary>
        public const int DefaultMaxRowsPerWindow = 20000;

        /// <summary>
        /// Contiguous windows, in time order, covering every hour of <paramref name="dayUtc"/> that has rows: hours are
        /// merged while the total stays within <paramref name="maxRowsPerWindow"/>, and an hour over it is cut into
        /// equal whole-second slices. Empty when the day has no rows.
        /// </summary>
        public static IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> PlanDay(DateTime dayUtc, IEnumerable<AppInsightsHourlyRowCount> hourCounts,
            int maxRowsPerWindow = DefaultMaxRowsPerWindow)
        {
            if (maxRowsPerWindow < 1) throw new ArgumentOutOfRangeException(nameof(maxRowsPerWindow));
            var day = DateTime.SpecifyKind(dayUtc.Date, DateTimeKind.Utc);
            var hours = (hourCounts ?? Enumerable.Empty<AppInsightsHourlyRowCount>())
                .Where(h => h.Count > 0 && h.HourUtc >= day && h.HourUtc < day.AddDays(1))
                .GroupBy(h => h.HourUtc)
                .Select(g => (Hour: g.Key, Count: g.Sum(h => h.Count)))
                .OrderBy(h => h.Hour)
                .ToList();

            var windows = new List<(DateTime FromUtc, DateTime ToUtc)>();
            DateTime? openFrom = null;
            var openTo = default(DateTime);
            long openRows = 0;

            void Close()
            {
                if (openFrom.HasValue)
                {
                    windows.Add((openFrom.Value, openTo));
                    openFrom = null;
                    openRows = 0;
                }
            }

            // Each window starts where the previous one ended, so a row that arrived in an hour the counts saw as empty
            // is still inside some window.
            DateTime NextStart(DateTime hourUtc) => windows.Count > 0 ? windows[windows.Count - 1].ToUtc : hourUtc;

            foreach (var hour in hours)
            {
                var hourEnd = hour.Hour.AddHours(1);
                if (hour.Count > maxRowsPerWindow)
                {
                    Close();
                    var slices = (int)Math.Min(3600, (hour.Count + maxRowsPerWindow - 1) / maxRowsPerWindow);
                    var sliceSeconds = 3600 / slices;
                    var from = NextStart(hour.Hour);
                    for (var i = 1; i < slices; i++)
                    {
                        var to = hour.Hour.AddSeconds(sliceSeconds * i);
                        windows.Add((from, to));
                        from = to;
                    }
                    windows.Add((from, hourEnd));
                    continue;
                }

                if (openFrom.HasValue && openRows + hour.Count > maxRowsPerWindow)
                {
                    Close();
                }
                if (!openFrom.HasValue)
                {
                    openFrom = NextStart(hour.Hour);
                }
                openRows += hour.Count;
                openTo = hourEnd;
            }
            Close();
            return windows;
        }
    }
}
