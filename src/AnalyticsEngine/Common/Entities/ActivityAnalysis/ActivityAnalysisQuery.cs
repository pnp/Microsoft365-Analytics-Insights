using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Entities.ActivityAnalysis
{
    /// <summary>The stable error codes the Activity analysis API answers with. The portal words them; never rename one.</summary>
    public static class ActivityAnalysisErrorCodes
    {
        public const string InvalidPeriod = "invalidPeriod";
        public const string InvalidMetric = "invalidMetric";
        public const string InvalidRange = "invalidRange";
        public const string InvalidFilter = "invalidFilter";
        public const string NotInstalled = "notInstalled";
        public const string Busy = "activityAnalysisBusy";
    }

    /// <summary>Why the page cannot show figures. Stable keys, translated by the portal.</summary>
    public static class ActivityAnalysisReasons
    {
        /// <summary><c>profiling.ActivitiesWeeklyColumns</c> or <c>profiling.users</c> does not exist.</summary>
        public const string NotInstalled = "notInstalled";

        /// <summary>The weekly table exists but the runbooks have not compiled a week into it yet.</summary>
        public const string NoData = "noData";
    }

    /// <summary>A request the API refuses with a 400: the code says which part, the message is English for anyone else.</summary>
    public sealed class ActivityAnalysisQueryException : ArgumentException
    {
        public ActivityAnalysisQueryException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>ISO weeks, identified by their Monday - the bucketing <c>profiling.udf_GetMonday</c> uses.</summary>
    public static class ActivityAnalysisWeeks
    {
        public const string DateFormat = "yyyy-MM-dd";

        /// <summary>The earliest and latest dates the API accepts, so no week arithmetic can leave the calendar.</summary>
        public static readonly DateTime EarliestDate = new DateTime(1900, 1, 1);
        public static readonly DateTime LatestDate = new DateTime(9998, 12, 31);

        /// <summary>The Monday on or before <paramref name="date"/>.</summary>
        public static DateTime MondayOnOrBefore(DateTime date)
        {
            var day = date.Date;
            var offset = ((int)day.DayOfWeek + 6) % 7;
            return DateTime.SpecifyKind(day.AddDays(-offset), DateTimeKind.Unspecified);
        }

        public static string Format(DateTime date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

        public static string Format(DateTime? date) => date.HasValue ? Format(date.Value) : null;

        /// <summary>A <c>yyyy-MM-dd</c> date, exactly. Anything else - a time, another format - is not a date here.</summary>
        public static bool TryParse(string text, out DateTime date)
        {
            return DateTime.TryParseExact(
                text?.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
    }

    /// <summary>
    /// A run of whole ISO weeks, <c>[From, To]</c> inclusive of both Mondays.
    /// </summary>
    public sealed class ActivityAnalysisPeriod
    {
        /// <summary>The longest period one request may cover: two years and a week, so "last two years" always fits.</summary>
        public const int MaximumWeeks = 105;

        private ActivityAnalysisPeriod(DateTime from, DateTime to)
        {
            From = from;
            To = to;
            Weeks = (int)((to - from).TotalDays / 7) + 1;
            WeekStarts = Enumerable.Range(0, Weeks).Select(i => from.AddDays(7 * i)).ToList().AsReadOnly();
        }

        /// <summary>The first Monday.</summary>
        public DateTime From { get; }

        /// <summary>The last Monday.</summary>
        public DateTime To { get; }

        public int Weeks { get; }

        /// <summary>Every Monday in the period, oldest first.</summary>
        public IReadOnlyList<DateTime> WeekStarts { get; }

        /// <summary>A stable identity for cache keys.</summary>
        public string Key => ActivityAnalysisWeeks.Format(From) + ".." + ActivityAnalysisWeeks.Format(To);

        /// <summary>
        /// The week a date falls in, or -1 outside the period. Any day of a week maps to it, although the runbooks
        /// only ever store the Monday.
        /// </summary>
        public int WeekIndexOf(DateTime date)
        {
            var monday = ActivityAnalysisWeeks.MondayOnOrBefore(date);
            if (monday < From || monday > To) return -1;
            return (int)((monday - From).TotalDays / 7);
        }

        /// <summary>The period between two dates, each snapped to the Monday on or before it.</summary>
        /// <exception cref="ActivityAnalysisQueryException"><c>invalidPeriod</c> - reversed, too long or off the calendar.</exception>
        public static ActivityAnalysisPeriod Create(DateTime from, DateTime to)
        {
            if (from.Date < ActivityAnalysisWeeks.EarliestDate || to.Date < ActivityAnalysisWeeks.EarliestDate
                || from.Date > ActivityAnalysisWeeks.LatestDate || to.Date > ActivityAnalysisWeeks.LatestDate)
            {
                throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidPeriod,
                    "Choose dates between 1900-01-01 and 9998-12-31.");
            }

            var first = ActivityAnalysisWeeks.MondayOnOrBefore(from);
            var last = ActivityAnalysisWeeks.MondayOnOrBefore(to);
            if (first > last)
            {
                throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidPeriod,
                    "The start of the period must be on or before its end.");
            }

            if ((last - first).TotalDays / 7 + 1 > MaximumWeeks)
            {
                throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidPeriod,
                    "Choose a period of at most " + MaximumWeeks.ToString(CultureInfo.InvariantCulture) + " weeks.");
            }

            return new ActivityAnalysisPeriod(first, last);
        }
    }

    /// <summary>
    /// A min/max condition on one metric's per-person total over the period. Inclusive; a missing bound is open.
    /// </summary>
    public sealed class ActivityAnalysisRange
    {
        internal ActivityAnalysisRange(ActivityAnalysisMetric metric, long? minimum, long? maximum)
        {
            Metric = metric;
            Minimum = minimum;
            Maximum = maximum;
        }

        public ActivityAnalysisMetric Metric { get; }

        /// <summary>The lowest total admitted, rounded up to a whole number, or <c>null</c> for no lower bound.</summary>
        public long? Minimum { get; }

        /// <summary>The highest total admitted, rounded down to a whole number, or <c>null</c> for no upper bound.</summary>
        public long? Maximum { get; }

        public bool Admits(long total)
        {
            return (!Minimum.HasValue || total >= Minimum.Value) && (!Maximum.HasValue || total <= Maximum.Value);
        }
    }

    /// <summary>
    /// One report or people request: the period, the selected metrics, and the reader's licence and activity-range
    /// conditions. The reader's user filter is resolved separately, with every other report's.
    /// </summary>
    /// <remarks>
    /// Parsed in two steps. <see cref="Parse"/> checks everything that needs no database - formats and metric keys -
    /// so a malformed request is refused before any SQL runs; <see cref="Resolve"/> then checks it against what the
    /// database actually holds: which columns exist, and which weeks have been compiled.
    /// </remarks>
    public sealed class ActivityAnalysisQuery
    {
        /// <summary>The default period: the latest compiled week and the 51 before it.</summary>
        public const int DefaultWeeks = 52;

        /// <summary>How many licences one request may name - every imported licence type, on any realistic tenant.</summary>
        public const int MaximumLicences = 500;

        private ActivityAnalysisQuery(
            DateTime? requestedFrom, DateTime? requestedTo, IReadOnlyList<ActivityAnalysisMetric> metrics,
            IReadOnlyList<ActivityAnalysisRange> ranges, IReadOnlyList<int> licenceTypeIds, ActivityAnalysisPeriod period)
        {
            RequestedFrom = requestedFrom;
            RequestedTo = requestedTo;
            Metrics = metrics;
            Ranges = ranges;
            LicenceTypeIds = licenceTypeIds;
            Period = period;
        }

        public DateTime? RequestedFrom { get; }

        public DateTime? RequestedTo { get; }

        /// <summary>The selected metrics, in the order requested, each once.</summary>
        public IReadOnlyList<ActivityAnalysisMetric> Metrics { get; }

        public IReadOnlyList<ActivityAnalysisRange> Ranges { get; }

        /// <summary>The licences a person must hold at least one of. Empty for no licence condition.</summary>
        public IReadOnlyList<int> LicenceTypeIds { get; }

        /// <summary>The weeks covered. <c>null</c> until <see cref="Resolve"/> has run.</summary>
        public ActivityAnalysisPeriod Period { get; }

        /// <summary>True when the reader narrowed the people by licence or activity, on top of any user filter.</summary>
        public bool HasConditions => LicenceTypeIds.Count > 0 || Ranges.Count > 0;

        /// <summary>Checks everything that needs no database.</summary>
        /// <exception cref="ActivityAnalysisQueryException">The code names the parameter at fault.</exception>
        public static ActivityAnalysisQuery Parse(string from, string to, string metrics, string ranges, string licences)
        {
            var requestedFrom = ParseDate(from);
            var requestedTo = ParseDate(to);
            return new ActivityAnalysisQuery(
                requestedFrom, requestedTo, ParseMetrics(metrics), ParseRanges(ranges), ParseLicences(licences), null);
        }

        /// <summary>
        /// Checks the query against the database's schema and fills in the period: the requested one, or the default
        /// one ending at the latest compiled week.
        /// </summary>
        /// <param name="nowUtc">Today, for the default period of a database with no compiled week yet.</param>
        public ActivityAnalysisQuery Resolve(ActivityAnalysisSchema schema, DateTime nowUtc)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));

            if (Metrics.Any(m => !schema.IsAvailable(m)))
            {
                throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidMetric,
                    "One or more of the chosen metrics is not compiled in this database. Choose from the metrics the page offers.");
            }

            if (Ranges.Any(r => !schema.IsAvailable(r.Metric)))
            {
                throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidRange,
                    "An activity range names a metric that is not compiled in this database.");
            }

            var latest = schema.LatestWeek ?? ActivityAnalysisWeeks.MondayOnOrBefore(nowUtc);
            var earliest = schema.EarliestWeek;

            DateTime periodTo;
            if (RequestedTo.HasValue) periodTo = RequestedTo.Value;
            else if (RequestedFrom.HasValue) periodTo = Max(ActivityAnalysisWeeks.MondayOnOrBefore(RequestedFrom.Value), latest);
            else periodTo = latest;

            DateTime periodFrom;
            if (RequestedFrom.HasValue)
            {
                periodFrom = RequestedFrom.Value;
            }
            else
            {
                var end = ActivityAnalysisWeeks.MondayOnOrBefore(periodTo);
                periodFrom = end.AddDays(-7 * (DefaultWeeks - 1));
                if (earliest.HasValue && earliest.Value <= end && periodFrom < earliest.Value) periodFrom = earliest.Value;
                if (periodFrom < ActivityAnalysisWeeks.EarliestDate) periodFrom = end;
            }

            return new ActivityAnalysisQuery(RequestedFrom, RequestedTo, Metrics, Ranges, LicenceTypeIds,
                ActivityAnalysisPeriod.Create(periodFrom, periodTo));
        }

        /// <summary>A comma-separated list of metric keys: required, each from the catalogue, de-duplicated in order.</summary>
        public static IReadOnlyList<ActivityAnalysisMetric> ParseMetrics(string metrics)
        {
            var selected = new List<ActivityAnalysisMetric>();
            var seen = new HashSet<int>();
            foreach (var key in Split(metrics))
            {
                if (!ActivityAnalysisMetricCatalogue.TryGet(key, out var metric))
                {
                    throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidMetric,
                        "One or more of the chosen metrics is not recognised. Choose from the metrics the page offers.");
                }

                if (seen.Add(metric.Index)) selected.Add(metric);
            }

            if (selected.Count == 0)
            {
                throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidMetric, "Choose at least one metric.");
            }

            return selected.AsReadOnly();
        }

        /// <summary>
        /// <c>key:min:max</c> conditions separated by commas, either bound optional - <c>teams.calls:5:</c>,
        /// <c>outlook.emailsSent::100</c>. Bounds are non-negative numbers; a fractional one is rounded inwards, which
        /// is exact for whole-number totals (a total of at least 5.5 is a total of at least 6).
        /// </summary>
        public static IReadOnlyList<ActivityAnalysisRange> ParseRanges(string ranges)
        {
            var parsed = new List<ActivityAnalysisRange>();
            var seen = new HashSet<int>();
            foreach (var entry in Split(ranges))
            {
                var parts = entry.Split(':');
                if (parts.Length != 3 || !ActivityAnalysisMetricCatalogue.TryGet(parts[0].Trim(), out var metric))
                {
                    throw InvalidRange("Write each activity range as metric:minimum:maximum, with a metric the page offers.");
                }

                if (!seen.Add(metric.Index))
                {
                    throw InvalidRange("Give each metric at most one activity range.");
                }

                var minimum = ParseBound(parts[1]);
                var maximum = ParseBound(parts[2]);
                if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
                {
                    throw InvalidRange("An activity range's minimum must not be above its maximum.");
                }

                if (!minimum.HasValue && !maximum.HasValue) continue;

                parsed.Add(new ActivityAnalysisRange(
                    metric,
                    minimum.HasValue ? (long?)decimal.Ceiling(minimum.Value) : null,
                    maximum.HasValue ? (long?)decimal.Floor(maximum.Value) : null));
            }

            return parsed.AsReadOnly();
        }

        /// <summary>Licence type ids (<c>dbo.license_types.id</c>) separated by commas.</summary>
        public static IReadOnlyList<int> ParseLicences(string licences)
        {
            var ids = new List<int>();
            var seen = new HashSet<int>();
            foreach (var entry in Split(licences))
            {
                if (entry.Length > 10 || !int.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                {
                    throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidFilter,
                        "Licences must be licence type ids separated by commas.");
                }

                if (seen.Add(id)) ids.Add(id);
            }

            if (ids.Count > MaximumLicences)
            {
                throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidFilter,
                    "Choose at most " + MaximumLicences.ToString(CultureInfo.InvariantCulture) + " licences.");
            }

            return ids.AsReadOnly();
        }

        private static DateTime? ParseDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            if (!ActivityAnalysisWeeks.TryParse(text, out var date))
            {
                throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidPeriod, "Dates must use the YYYY-MM-DD format.");
            }

            if (date < ActivityAnalysisWeeks.EarliestDate || date > ActivityAnalysisWeeks.LatestDate)
            {
                throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidPeriod, "Choose dates between 1900-01-01 and 9998-12-31.");
            }

            return date;
        }

        /// <summary>
        /// A bound: digits with an optional decimal point, or nothing for an open bound. Signs, exponents, thousands
        /// separators and absurdly large numbers are refused - a per-person total never exceeds a 32-bit integer.
        /// </summary>
        private static decimal? ParseBound(string text)
        {
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;

            if (trimmed.Length > 32
                || !decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || value > MaximumBound)
            {
                throw InvalidRange("Activity range bounds must be numbers from 0 to 1,000,000,000,000,000.");
            }

            return value;
        }

        private const decimal MaximumBound = 1000000000000000m;

        private static ActivityAnalysisQueryException InvalidRange(string message) =>
            new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidRange, message);

        private static IEnumerable<string> Split(string list)
        {
            if (string.IsNullOrWhiteSpace(list)) yield break;

            foreach (var part in list.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) yield return trimmed;
            }
        }

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    }

    /// <summary>
    /// What the database holds for the Activity analysis page: whether the profiling tables exist, which metric
    /// columns they have, and which weeks are compiled.
    /// </summary>
    public sealed class ActivityAnalysisSchema
    {
        private readonly bool[] _available;

        public ActivityAnalysisSchema(bool installed, IEnumerable<string> columns, DateTime? earliestDate, DateTime? latestDate)
        {
            Installed = installed;
            _available = new bool[ActivityAnalysisMetricCatalogue.Count];
            if (installed && columns != null)
            {
                foreach (var column in columns)
                {
                    if (ActivityAnalysisMetricCatalogue.TryGetByColumn(column, out var metric)) _available[metric.Index] = true;
                }
            }

            if (installed && earliestDate.HasValue && latestDate.HasValue)
            {
                EarliestWeek = ActivityAnalysisWeeks.MondayOnOrBefore(earliestDate.Value);
                LatestWeek = ActivityAnalysisWeeks.MondayOnOrBefore(latestDate.Value);
            }
        }

        /// <summary>Neither table, so nothing to show.</summary>
        public static ActivityAnalysisSchema NotInstalled => new ActivityAnalysisSchema(false, null, null, null);

        /// <summary>Every catalogue column, for tests and for a fully upgraded database.</summary>
        public static ActivityAnalysisSchema Complete(DateTime? earliestDate, DateTime? latestDate) =>
            new ActivityAnalysisSchema(true, ActivityAnalysisMetricCatalogue.All.Select(m => m.Column), earliestDate, latestDate);

        public bool Installed { get; }

        public bool HasData => Installed && LatestWeek.HasValue;

        public DateTime? EarliestWeek { get; }

        public DateTime? LatestWeek { get; }

        /// <summary>The latest compiled week and the 51 before it, but never before the earliest compiled week.</summary>
        public DateTime? DefaultFrom
        {
            get
            {
                if (!LatestWeek.HasValue) return null;
                var from = LatestWeek.Value.AddDays(-7 * (ActivityAnalysisQuery.DefaultWeeks - 1));
                return EarliestWeek.HasValue && from < EarliestWeek.Value ? EarliestWeek.Value : from;
            }
        }

        public DateTime? DefaultTo => LatestWeek;

        /// <summary><see cref="ActivityAnalysisReasons"/>, or <c>null</c> when there are figures to show.</summary>
        public string Reason => !Installed ? ActivityAnalysisReasons.NotInstalled : !HasData ? ActivityAnalysisReasons.NoData : null;

        public bool IsAvailable(ActivityAnalysisMetric metric) => metric != null && _available[metric.Index];

        /// <summary>The metrics whose columns exist, in catalogue order.</summary>
        public IReadOnlyList<ActivityAnalysisMetric> AvailableMetrics =>
            ActivityAnalysisMetricCatalogue.All.Where(m => _available[m.Index]).ToList().AsReadOnly();
    }
}
