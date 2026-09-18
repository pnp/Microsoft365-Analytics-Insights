using System;

namespace Common.Entities.SpoWebActivity
{
    /// <summary>
    /// The shapes EF materialises the raw web-activity queries into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept separate from the API models on purpose. These mirror the SQL result sets exactly -
    /// including the aggregate types, which matters: EF's store-query shaper reads a column with a
    /// typed getter, so a <c>COUNT_BIG</c> (bigint) read into an <c>int</c> property throws at
    /// runtime rather than converting. Every property type here is chosen to match the SQL type the
    /// corresponding statement in <see cref="WebActivitySql"/> actually returns.
    /// </para>
    /// <para>
    /// They are public because EF6's <c>Database.SqlQuery&lt;T&gt;</c> materialises by reflection over
    /// public settable properties.
    /// </para>
    /// </remarks>
    public static class WebActivityRows
    {
        public class OverviewKpiRow
        {
            public long PageViews { get; set; }
            public long UniquePageViews { get; set; }
            public long Visits { get; set; }
            public int Visitors { get; set; }

            /// <summary>
            /// Visitors who are in the enabled directory population - the numerator of reach.
            /// </summary>
            /// <remarks>
            /// Separate from <see cref="Visitors"/> so reach cannot exceed 100%. A user who visited
            /// and was then disabled is a real visitor but is not in the denominator, so counting
            /// them in both would produce "104% of the organisation visited".
            /// </remarks>
            public int EnabledVisitors { get; set; }

            public int KnownUsers { get; set; }
            public int UniquePages { get; set; }
            public int Sites { get; set; }
            public long Bounces { get; set; }
            public double? AverageSecondsOnPage { get; set; }
            public double? AverageLoadSeconds { get; set; }
        }

        public class VisitorHalvesRow
        {
            public int NewVisitors { get; set; }
            public int ReturningVisitors { get; set; }
            public int LapsedVisitors { get; set; }
        }

        public class TrendRow
        {
            public DateTime WeekStart { get; set; }
            public long PageViews { get; set; }
            public long Visits { get; set; }
            public int Visitors { get; set; }
            public long Bounces { get; set; }
            public long Searches { get; set; }
        }

        public class ActiveDaysRow
        {
            public int ActiveDays { get; set; }
            public long Visitors { get; set; }
        }

        public class VisitDepthRow
        {
            /// <summary>Pages seen in the visit.</summary>
            public long Pages { get; set; }

            public long Visits { get; set; }

            /// <summary>Total dwell seconds across those visits, or null when never reported.</summary>
            public double? Seconds { get; set; }
        }

        public class HeatCellRow
        {
            public int Day { get; set; }
            public int Hour { get; set; }
            public long PageViews { get; set; }
            public long Visits { get; set; }
        }

        public class SiteRow
        {
            public string Name { get; set; }
            public string Url { get; set; }
            public long PageViews { get; set; }
            public long UniquePageViews { get; set; }
            public long Visits { get; set; }
            public int Visitors { get; set; }
        }

        public class VisitKpiRow
        {
            public long Visits { get; set; }
            public int Visitors { get; set; }
            public int UniquePages { get; set; }
            public int? EarliestVisitHour { get; set; }
            public int? LatestVisitHour { get; set; }
            public long OutOfHoursVisits { get; set; }
        }

        /// <summary>A generic "name and count" result, used by several leaderboards.</summary>
        public class NamedCountRow
        {
            public string Name { get; set; }
            public long Count { get; set; }
        }

        /// <summary>A weekly value for one named series.</summary>
        public class StackRow
        {
            public DateTime WeekStart { get; set; }
            public string Name { get; set; }
            public long Count { get; set; }
        }

        /// <summary>A weekly value bucketed by hour of day, stacked into periods of the day in C#.</summary>
        public class WeekHourRow
        {
            public DateTime WeekStart { get; set; }
            public int Hour { get; set; }
            public long Count { get; set; }
        }

        public class DayHourRow
        {
            public int Day { get; set; }
            public int Hour { get; set; }
            public long Count { get; set; }
        }

        public class PageRow
        {
            public string Title { get; set; }
            public string Url { get; set; }
            public string Site { get; set; }
            public long PageViews { get; set; }
            public long UniquePageViews { get; set; }
            public int Visitors { get; set; }
            public double? AverageSecondsOnPage { get; set; }
            public double? AverageLoadSeconds { get; set; }
            public long Entries { get; set; }
            public long Exits { get; set; }
            public long Bounces { get; set; }
        }

        public class PageViewDistributionRow
        {
            /// <summary>Page views a page received.</summary>
            public long Views { get; set; }

            /// <summary>How many pages received exactly that many views.</summary>
            public long Pages { get; set; }
        }

        public class TransitionRow
        {
            public string FromTitle { get; set; }
            public string FromUrl { get; set; }
            public string ToTitle { get; set; }
            public string ToUrl { get; set; }
            public long Count { get; set; }
            public double? SharePct { get; set; }
        }

        public class ClickCountRow
        {
            public long Clicks { get; set; }
        }

        public class PlaceRow
        {
            public string Name { get; set; }
            public string Country { get; set; }
            public long PageViews { get; set; }
            public long Visits { get; set; }
            public int Visitors { get; set; }
        }

        public class GeographyKpiRow
        {
            public int Countries { get; set; }
            public int Cities { get; set; }
            public int Provinces { get; set; }
            public long UnknownLocationPageViews { get; set; }
            public long PageViews { get; set; }
            public long Visits { get; set; }
            public int Visitors { get; set; }
        }

        public class SearchKpiRow
        {
            public long Searches { get; set; }
            public int Terms { get; set; }
            public int Searchers { get; set; }
            public long SessionsWithSearch { get; set; }
            public long StrugglingVisits { get; set; }
            public long DeadEndSearches { get; set; }
        }

        public class SearchTermRow
        {
            public string Term { get; set; }
            public long Searches { get; set; }
            public int Searchers { get; set; }
            public long DeadEnds { get; set; }
        }

        public class TechnologyKpiRow
        {
            public int Browsers { get; set; }
            public int OperatingSystems { get; set; }
            public int Devices { get; set; }
            public long UnknownBrowserPageViews { get; set; }
            public long PageViews { get; set; }
            public double? AverageLoadSeconds { get; set; }
        }

        public class PlatformRow
        {
            public string Name { get; set; }
            public long PageViews { get; set; }
            public long Visits { get; set; }
            public int Visitors { get; set; }
            public double? AverageLoadSeconds { get; set; }
            public double? AverageSecondsOnPage { get; set; }
        }

        public class LoadBucketRow
        {
            public int Bucket { get; set; }
            public long Count { get; set; }
        }

        public class TechnologyDetailRow
        {
            public string Browser { get; set; }
            public string Device { get; set; }
            public string OperatingSystem { get; set; }
            public string City { get; set; }
            public long Visits { get; set; }
            public int Visitors { get; set; }
            public long PageViews { get; set; }
            public double? AverageSecondsOnPage { get; set; }
            public double? AverageLoadSeconds { get; set; }
        }

        public class LatestHitRow
        {
            public DateTime? LastHitUtc { get; set; }
        }

        public class OptionalFeatureRow
        {
            public long AnySearches { get; set; }
            public long AnyClicks { get; set; }
        }
    }
}
