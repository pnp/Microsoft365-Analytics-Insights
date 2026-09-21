using System;
using System.Globalization;
using System.Linq;

namespace Common.Entities.SpoWebActivity
{
    /// <summary>
    /// The reporting window and options every SharePoint web-activity section is built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One window, unlike Teams Explorer.</b> Everything on this page comes from a single source -
    /// the Application Insights page-view import that fills <c>dbo.hits</c>, <c>dbo.sessions</c>,
    /// <c>dbo.searches</c> and <c>dbo.hits_clicked_elements</c>. That import is near real-time, so
    /// there is no Microsoft-usage-report lag to shift the window back for, and inventing a second
    /// window would only invite a reader to believe two figures covered different periods when they
    /// do not.
    /// </para>
    /// <para>
    /// Windows are snapped to <see cref="AllowedWindowDays"/> rather than validated, so a hand-edited
    /// URL cannot ask for an arbitrary range and force a huge scan of <c>dbo.hits</c> - the largest
    /// table in the product on a busy intranet. This mirrors <c>TeamsExplorerQuery</c> and
    /// <c>DlpAPIController</c>.
    /// </para>
    /// <para>
    /// <b>The window is a real cost lever here.</b> <c>IX_hits_hit_timestamp (hit_timestamp) INCLUDE
    /// (session_id)</c> exists (migration <c>IndexReportDateQueries</c>), so a shorter window really
    /// does read proportionally less. The queries that need more than <c>session_id</c> pay a key
    /// lookup per row, which is why the default is 28 days and not a year.
    /// </para>
    /// </remarks>
    public sealed class WebActivityQuery
    {
        /// <summary>Windows the UI offers. Anything else snaps to the nearest of these.</summary>
        public static readonly int[] AllowedWindowDays = { 7, 28, 90, 180, 365 };

        /// <summary>The window used when the caller does not ask for one.</summary>
        public const int DefaultWindowDays = 28;

        /// <summary>Rows returned by a ranked table. Small - these are leaderboards, not exports.</summary>
        public const int DefaultTop = 15;

        /// <summary>Hard ceiling on a ranked table, including for the CSV exports.</summary>
        public const int MaximumTop = 500;

        /// <summary>
        /// Page views a page needs before it is allowed into a quality ranking (slowest pages, longest
        /// dwell, highest bounce).
        /// </summary>
        /// <remarks>
        /// Without a floor these tables are always topped by a page that was viewed once, slowly. That
        /// is noise presented as a finding, and acting on it wastes an admin's afternoon.
        /// </remarks>
        public const int DefaultMinimumViews = 5;

        private WebActivityQuery()
        {
        }

        /// <summary>The snapped window length in days.</summary>
        public int Days { get; private set; }

        /// <summary>The "now" this window was computed from, so a cached result can be reproduced.</summary>
        public DateTime NowUtc { get; private set; }

        /// <summary>Inclusive first UTC date of the window.</summary>
        public DateTime FromUtc { get; private set; }

        /// <summary>Exclusive last UTC date of the window.</summary>
        public DateTime ToExclusiveUtc { get; private set; }

        /// <summary>Rows to return from a ranked table.</summary>
        public int Top { get; private set; }

        /// <summary>Minimum page views for a row to qualify for a quality ranking.</summary>
        public int MinimumViews { get; private set; }

        /// <summary>Inclusive last UTC date of the window, for display.</summary>
        public DateTime ToInclusiveUtc => ToExclusiveUtc.AddDays(-1);

        /// <summary>
        /// The Monday on or before <see cref="FromUtc"/>. Weekly buckets are Monday-aligned in SQL, so
        /// the first bucket a query can return starts here and not on <see cref="FromUtc"/> itself.
        /// </summary>
        public DateTime FirstWeekStartUtc => MondayOf(FromUtc);

        /// <summary>
        /// The midpoint of the window, splitting it into two comparable halves for the
        /// new-versus-returning and trend-direction measures.
        /// </summary>
        public DateTime MidpointUtc => FromUtc.AddDays(Days / 2);

        /// <summary>Builds a window. Every argument is normalised rather than rejected.</summary>
        public static WebActivityQuery Create(
            int days,
            DateTime nowUtc,
            int top = DefaultTop,
            int minimumViews = DefaultMinimumViews)
        {
            var snappedDays = SnapDays(days);
            var toExclusive = nowUtc.Date.AddDays(1);

            return new WebActivityQuery
            {
                Days = snappedDays,
                NowUtc = nowUtc,
                FromUtc = toExclusive.AddDays(-snappedDays),
                ToExclusiveUtc = toExclusive,
                Top = NormaliseTop(top),
                MinimumViews = NormaliseMinimumViews(minimumViews),
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

        /// <summary>The requested row count clamped to 1..<see cref="MaximumTop"/>.</summary>
        public static int NormaliseTop(int top)
        {
            if (top < 1) return DefaultTop;
            return top > MaximumTop ? MaximumTop : top;
        }

        /// <summary>The requested view floor clamped to 1..1000.</summary>
        public static int NormaliseMinimumViews(int minimumViews)
        {
            if (minimumViews < 1) return DefaultMinimumViews;
            return minimumViews > 1000 ? 1000 : minimumViews;
        }

        /// <summary>The Monday on or before <paramref name="value"/>, matching the SQL bucketing.</summary>
        /// <remarks>
        /// 1900-01-01 was a Monday, so "whole days since then, modulo 7" is 0 on a Monday. Computing it
        /// this way rather than from <see cref="DayOfWeek"/> keeps the C# spine and the SQL buckets
        /// identical by construction, and is independent of the connection's DATEFIRST.
        /// </remarks>
        public static DateTime MondayOf(DateTime value)
        {
            var date = value.Date;
            var offset = (int)(date - new DateTime(1900, 1, 1)).TotalDays % 7;
            return date.AddDays(-offset);
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
                "WebActivity::{0}::days={1}::top={2}::minViews={3}::asof={4:yyyy-MM-dd}",
                section ?? string.Empty,
                Days,
                Top,
                MinimumViews,
                NowUtc.Date);
        }

        /// <summary>True when <paramref name="days"/> is one of the offered windows.</summary>
        public static bool IsAllowedWindow(int days) => AllowedWindowDays.Contains(days);
    }
}
