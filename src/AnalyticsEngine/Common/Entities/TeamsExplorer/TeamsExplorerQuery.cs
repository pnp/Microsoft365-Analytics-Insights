using System;
using System.Globalization;
using System.Linq;

namespace Common.Entities.TeamsExplorer
{
    /// <summary>
    /// The reporting window and options every Teams Explorer section is built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two windows, not one.</b> Teams Explorer reads from two kinds of source with different
    /// latency, and treating them as one window produces a chart that appears to collapse at the
    /// right-hand edge:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Call records</b> arrive from a Graph change-notification webhook within minutes, so
    /// they are reported right up to today (<see cref="FromUtc"/> / <see cref="ToExclusiveUtc"/>).</item>
    /// <item><b>Microsoft 365 usage reports</b> are published a couple of days in arrears and Graph
    /// keeps back-filling a report date for a short while after it first appears, so the usage window
    /// is shifted back by <see cref="UsageReportLagDays"/> (<see cref="UsageFromUtc"/> /
    /// <see cref="UsageToExclusiveUtc"/>).</item>
    /// </list>
    /// <para>
    /// The usage window is <i>shifted</i> rather than <i>truncated</i> so it still covers a full
    /// <see cref="Days"/> days. Truncating would silently compare a 25-day window against a 28-day one
    /// whenever the caller asked for 28 days, which is exactly the kind of quiet arithmetic error that
    /// makes a report untrustworthy.
    /// </para>
    /// <para>
    /// Windows are snapped to <see cref="AllowedWindowDays"/> rather than validated, so a hand-edited
    /// URL cannot ask for an arbitrary range and force a huge scan. This mirrors
    /// <c>DlpAPIController</c>. (<c>LicenceActivityQuery</c> throws instead, because that page offers a
    /// genuine custom range and rounding a user's explicit dates would be worse than refusing them.)
    /// </para>
    /// </remarks>
    public sealed class TeamsExplorerQuery
    {
        /// <summary>Windows the UI offers. Anything else snaps to the nearest of these.</summary>
        public static readonly int[] AllowedWindowDays = { 7, 28, 90, 180, 365 };

        /// <summary>The window used when the caller does not ask for one.</summary>
        public const int DefaultWindowDays = 28;

        /// <summary>
        /// How far behind "today" a Microsoft 365 usage report is considered settled. Matches
        /// <c>ReportsAPIController.UsageReportLagDays</c>; the two must agree or the Reports page and
        /// this page will disagree about the same week.
        /// </summary>
        public const int UsageReportLagDays = 3;

        /// <summary>Rows returned by a ranked table. Small - these are leaderboards, not exports.</summary>
        public const int DefaultTop = 20;

        /// <summary>Hard ceiling on a ranked table, including for the CSV exports.</summary>
        public const int MaximumTop = 500;

        /// <summary>Demographic dimensions the adoption breakdown can group by.</summary>
        public static readonly string[] Groupings = { "department", "country", "office", "jobTitle", "company" };

        /// <summary>The grouping used when the caller does not ask for one.</summary>
        public const string DefaultGrouping = "department";

        private TeamsExplorerQuery()
        {
        }

        /// <summary>The snapped window length in days.</summary>
        public int Days { get; private set; }

        /// <summary>The "now" this window was computed from, so a cached result can be reproduced.</summary>
        public DateTime NowUtc { get; private set; }

        /// <summary>Inclusive first UTC date of the live-data (call records) window.</summary>
        public DateTime FromUtc { get; private set; }

        /// <summary>Exclusive last UTC date of the live-data window.</summary>
        public DateTime ToExclusiveUtc { get; private set; }

        /// <summary>Inclusive first UTC report date of the usage-report window.</summary>
        public DateTime UsageFromUtc { get; private set; }

        /// <summary>Exclusive last UTC report date of the usage-report window.</summary>
        public DateTime UsageToExclusiveUtc { get; private set; }

        /// <summary>Demographic dimension for the adoption breakdown. Always one of <see cref="Groupings"/>.</summary>
        public string GroupBy { get; private set; }

        /// <summary>Rows to return from a ranked table.</summary>
        public int Top { get; private set; }

        /// <summary>Inclusive last UTC date of the live-data window, for display.</summary>
        public DateTime ToInclusiveUtc => ToExclusiveUtc.AddDays(-1);

        /// <summary>Inclusive last UTC report date of the usage-report window, for display.</summary>
        public DateTime UsageToInclusiveUtc => UsageToExclusiveUtc.AddDays(-1);

        /// <summary>
        /// Builds a window. <paramref name="days"/>, <paramref name="groupBy"/> and
        /// <paramref name="top"/> are all normalised rather than rejected.
        /// </summary>
        public static TeamsExplorerQuery Create(
            int days,
            DateTime nowUtc,
            string groupBy = DefaultGrouping,
            int top = DefaultTop)
        {
            var snappedDays = SnapDays(days);
            var today = nowUtc.Date;

            var toExclusive = today.AddDays(1);
            var usageToExclusive = today.AddDays(1 - UsageReportLagDays);

            return new TeamsExplorerQuery
            {
                Days = snappedDays,
                NowUtc = nowUtc,
                FromUtc = toExclusive.AddDays(-snappedDays),
                ToExclusiveUtc = toExclusive,
                UsageFromUtc = usageToExclusive.AddDays(-snappedDays),
                UsageToExclusiveUtc = usageToExclusive,
                GroupBy = NormaliseGrouping(groupBy),
                Top = NormaliseTop(top),
            };
        }

        /// <summary>
        /// The nearest allowed window to <paramref name="days"/>. Ties go to the SHORTER window: an
        /// ambiguous request should cost less, not more.
        /// </summary>
        public static int SnapDays(int days)
        {
            if (days <= 0) return DefaultWindowDays;

            var best = AllowedWindowDays[0];
            var bestDistance = Math.Abs(days - best);

            foreach (var candidate in AllowedWindowDays)
            {
                var distance = Math.Abs(days - candidate);
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>The requested grouping if it is supported, otherwise <see cref="DefaultGrouping"/>.</summary>
        public static string NormaliseGrouping(string groupBy)
        {
            if (string.IsNullOrWhiteSpace(groupBy)) return DefaultGrouping;

            return Groupings.FirstOrDefault(
                g => string.Equals(g, groupBy.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? DefaultGrouping;
        }

        /// <summary>The requested row count clamped to 1..<see cref="MaximumTop"/>.</summary>
        public static int NormaliseTop(int top)
        {
            if (top < 1) return DefaultTop;
            return top > MaximumTop ? MaximumTop : top;
        }

        /// <summary>
        /// Cache key for one section of the page. Includes the UTC date rather than the full
        /// timestamp, so a cached entry is reused across a day but can never be served after the
        /// window has rolled over to a new day.
        /// </summary>
        public string CacheKey(string section)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "TeamsExplorer::{0}::days={1}::group={2}::top={3}::asof={4:yyyy-MM-dd}",
                section ?? string.Empty,
                Days,
                GroupBy,
                Top,
                NowUtc.Date);
        }
    }
}
