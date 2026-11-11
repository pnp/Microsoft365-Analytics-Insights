using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.SpoWebActivity
{
    /// <summary>
    /// The small-group rules the web activity sections share, for a reader without the portal's See PII permission (#844).
    /// </summary>
    public static class WebActivitySmallGroups
    {
        /// <summary>
        /// <paramref name="rows"/> without any row of fewer than <paramref name="minimumPeople"/> people. A row whose people
        /// were not counted is treated as small. When a row is left out, the smallest row shown is left out with it, so what
        /// the list leaves out - the section's total less the rows shown, which every share on the page gives away - always
        /// includes a row of at least the minimum and is never one small row's figures. When no row is big enough, none is
        /// shown. The smallest row is chosen by people and then by <paramref name="name"/>, never by the list's own order, so
        /// two lists of the same rows ranked differently (by visits, by page views) leave out the same one.
        /// A new list: the rows themselves are cached and shared between readers, and are never edited.
        /// </summary>
        /// <param name="leftOut">How many rows were left out.</param>
        public static List<T> WithoutSmallRows<T>(
            IEnumerable<T> rows, Func<T, int?> people, Func<T, string> name, int minimumPeople, out int leftOut)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var all = rows?.ToList() ?? new List<T>();
            var shown = all.Where(r => (people(r) ?? 0) >= minimumPeople).ToList();
            leftOut = all.Count - shown.Count;
            if (leftOut == 0 || shown.Count == 0) return shown;

            var smallest = shown
                .OrderBy(r => people(r) ?? 0)
                .ThenBy(r => name(r) ?? string.Empty, StringComparer.Ordinal)
                .First();
            shown.Remove(smallest);
            leftOut++;
            return shown;
        }

        /// <summary>
        /// <see cref="WithoutSmallRows"/> over the first <see cref="WebActivityQuery.MaximumTop"/> rows of a list - the most
        /// any reader is shown - fetched with one row more (<see cref="WebActivityQuery.ForFloor"/>) to tell whether rows lie
        /// beyond them (#844). Rows past the cap are never shown. When there are any, they are in the section's total but not
        /// in the list, just as a row left out is, and they can be one small row's, so they count as a row left out: when
        /// nothing else is, the smallest row shown goes too, and the total less the rows shown always includes a row of at
        /// least <paramref name="minimumPeople"/>. A list that ends inside the cap is floored as
        /// <see cref="WithoutSmallRows"/> floors it. A new list.
        /// </summary>
        public static List<T> WithoutSmallRowsWithinCap<T>(
            IList<T> rows, Func<T, int?> people, Func<T, string> name, int minimumPeople)
            where T : class
        {
            var all = rows ?? new List<T>();
            var shown = WithoutSmallRows(all.Take(WebActivityQuery.MaximumTop), people, name, minimumPeople, out var leftOut);
            if (leftOut == 0 && all.Count > WebActivityQuery.MaximumTop) RemoveSmallest(shown, people, name);
            return shown;
        }

        /// <summary>The first <see cref="WebActivityQuery.MaximumTop"/> rows of <paramref name="rows"/>: those a floor decides over.</summary>
        public static IEnumerable<T> WithinCap<T>(IEnumerable<T> rows) =>
            (rows ?? Enumerable.Empty<T>()).Take(WebActivityQuery.MaximumTop);

        /// <summary>Leaves out the smallest row of <paramref name="shown"/>, chosen as <see cref="WithoutSmallRows"/> chooses it.</summary>
        private static void RemoveSmallest<T>(List<T> shown, Func<T, int?> people, Func<T, string> name)
            where T : class
        {
            if (shown.Count == 0) return;
            shown.Remove(shown
                .OrderBy(r => people(r) ?? 0)
                .ThenBy(r => name(r) ?? string.Empty, StringComparer.Ordinal)
                .First());
        }

        /// <summary>
        /// <see cref="WithoutSmallRows"/> within each <paramref name="group"/> of <paramref name="rows"/> separately, for a
        /// list whose shares are each against their own group's total - the steps out of one page, as a share of all the
        /// steps out of it. A row's share and count give its group's total away, so what a group leaves out has to include
        /// one of that group's own rows of at least the minimum, not some other group's. The rows keep their order. A new
        /// list: the rows themselves are cached and shared between readers, and are never edited.
        /// </summary>
        /// <param name="leftOut">How many rows were left out, over every group.</param>
        public static List<T> WithoutSmallRowsInEachGroup<T>(
            IEnumerable<T> rows, Func<T, string> group, Func<T, int?> people, Func<T, string> name, int minimumPeople, out int leftOut) =>
            WithoutSmallRowsInEachGroup(rows, group, people, name, minimumPeople, _ => false, out leftOut);

        /// <summary>
        /// The same floor within each group, over a list that may stop before some group's last row (#844): a group whose
        /// rows <paramref name="goesOnPastTheList"/> says continue beyond the list counts those rows as a row left out, so
        /// when none of its own rows is, its smallest row shown goes too - as <see cref="WithoutSmallRowsWithinCap"/> does
        /// for a whole list.
        /// </summary>
        public static List<T> WithoutSmallRowsInEachGroup<T>(
            IEnumerable<T> rows,
            Func<T, string> group,
            Func<T, int?> people,
            Func<T, string> name,
            int minimumPeople,
            Func<IReadOnlyList<T>, bool> goesOnPastTheList,
            out int leftOut)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var all = (rows ?? Enumerable.Empty<T>()).Select((row, index) => new { Row = row, Index = index }).ToList();
            var shown = new HashSet<int>();
            leftOut = 0;
            foreach (var rowsOfGroup in all.GroupBy(r => group(r.Row) ?? string.Empty, StringComparer.Ordinal))
            {
                var kept = WithoutSmallRows(rowsOfGroup, r => people(r.Row), r => name(r.Row), minimumPeople, out var groupLeftOut);
                if (groupLeftOut == 0 && kept.Count > 0 && goesOnPastTheList(rowsOfGroup.Select(r => r.Row).ToList()))
                {
                    RemoveSmallest(kept, r => people(r.Row), r => name(r.Row));
                    groupLeftOut = 1;
                }

                leftOut += groupLeftOut;
                foreach (var row in kept) shown.Add(row.Index);
            }

            return all.Where(r => shown.Contains(r.Index)).Select(r => r.Row).ToList();
        }

        /// <summary>
        /// The name a page row is told apart by when two rows have as few people: its title - the same text a list of pages
        /// by name shows, so two lists of the same pages pick the same one - then its URL.
        /// </summary>
        internal static string PageName(WebActivityPageRow page) => page.Title + "\u0000" + page.Url;

        /// <summary>
        /// The first <paramref name="top"/> rows of <paramref name="shown"/> - what a floor kept of <paramref name="all"/>, in
        /// the same order - so that a list floored over every row a section holds and then cut to the rows a reader asked for
        /// is always the first rows of the same floored list, whatever they asked for (#844). Floored over only the rows
        /// asked for, a short list with no small row would show a row that a longer list leaves out as its smallest shown,
        /// and that row's figures, taken off the longer list's remainder, would leave the small rows'.
        /// </summary>
        /// <param name="leftOutInView">
        /// How many of the first <paramref name="top"/> rows of <paramref name="all"/> were left out: the rows missing from
        /// what the reader sees, not those that would have ranked below it anyway.
        /// </param>
        public static List<T> FirstRows<T>(IList<T> all, IList<T> shown, int top, out int leftOutInView)
            where T : class
        {
            if (top < 1) throw new ArgumentOutOfRangeException(nameof(top));

            var rows = all ?? new List<T>();
            var floored = shown ?? new List<T>();
            var shownRows = new HashSet<T>(floored, ReferenceComparer<T>.Instance);
            leftOutInView = rows.Take(top).Count(r => !shownRows.Contains(r));
            return floored.Take(top).ToList();
        }

        /// <summary>Rows told apart by identity: a cached row is the same row wherever a list holds it.</summary>
        private sealed class ReferenceComparer<T> : IEqualityComparer<T>
            where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

            public bool Equals(T x, T y) => ReferenceEquals(x, y);

            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        /// <summary>
        /// The weekly points of a "busiest N over time" chart, with every named series the list beside it does not show
        /// folded into <paramref name="otherName"/> week by week. The chart still stacks to each week's total, but draws no
        /// series of its own for a row the list left out. The query's own <paramref name="otherName"/> and
        /// <paramref name="unknownName"/> bands are kept. New points: the section is cached and shared between readers.
        /// </summary>
        public static List<WebActivityStackPoint> WithSeriesFolded(
            IEnumerable<WebActivityStackPoint> points, IEnumerable<string> shownNames, string otherName, string unknownName)
        {
            var shown = new HashSet<string>(shownNames.Where(n => n != null), StringComparer.Ordinal);
            return (points ?? Enumerable.Empty<WebActivityStackPoint>())
                .Select(p => new
                {
                    p.WeekStart,
                    Name = p.Name != null && (shown.Contains(p.Name) || p.Name == otherName || p.Name == unknownName) ? p.Name : otherName,
                    p.Count,
                })
                .GroupBy(p => new { p.WeekStart, p.Name })
                .Select(g => new WebActivityStackPoint { WeekStart = g.Key.WeekStart, Name = g.Key.Name, Count = g.Sum(p => p.Count) })
                .OrderBy(p => p.WeekStart)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// <see cref="WithSeriesFolded"/>, matching each series to the list beside it by its <see cref="WebActivityStackPoint.Key"/>
        /// - the site itself - rather than by its title, which two sites can share: a site the list does not show keeps no
        /// series of its own however it is named. A named series with no key is folded too. New points.
        /// </summary>
        public static List<WebActivityStackPoint> WithSeriesFoldedByKey(
            IEnumerable<WebActivityStackPoint> points, IEnumerable<int?> shownKeys, string otherName, string unknownName)
        {
            var shown = new HashSet<int>(shownKeys.Where(k => k.HasValue).Select(k => k.Value));
            return (points ?? Enumerable.Empty<WebActivityStackPoint>())
                .Select(p =>
                {
                    var keep = p.Key.HasValue
                        ? shown.Contains(p.Key.Value)
                        : p.Name == otherName || p.Name == unknownName;
                    return new { p.WeekStart, Name = keep ? p.Name : otherName, Key = keep ? p.Key : null, p.Count };
                })
                .GroupBy(p => new { p.WeekStart, p.Name, p.Key })
                .Select(g => new WebActivityStackPoint { WeekStart = g.Key.WeekStart, Name = g.Key.Name, Key = g.Key.Key, Count = g.Sum(p => p.Count) })
                .OrderBy(p => p.WeekStart)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// One executed query, so the page can show the SQL behind a figure and report a failure per
    /// section rather than failing the whole tab.
    /// </summary>
    /// <remarks>
    /// <c>dbo.hits</c> is the largest table in the product on a busy intranet and only carries a date
    /// index - a leaderboard that has to key-look-up a million rows can genuinely time out. A tab that
    /// renders eight sections and says why the ninth is missing is far more useful than one that
    /// returns a 500, so failures are captured here rather than thrown.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityQueryInfo
    {
        /// <summary>Section identifier, matching the UI's lookup key.</summary>
        public string Key { get; set; }

        /// <summary>The SQL that produced the section, with its parameters declared, ready to paste into SSMS.</summary>
        public string Sql { get; set; }

        /// <summary>
        /// When the section failed, what its reader is told: the reason in English and an error reference, never the
        /// exception's text (<see cref="ReportQueryFailure"/>). Null when it succeeded.
        /// </summary>
        public string Error { get; set; }

        /// <summary>The failure's stable reason (<see cref="ReportQueryFailure.Reason"/>), which the portal words.</summary>
        public string ErrorReason { get; set; }

        /// <summary>The error reference the server log carries for this failure.</summary>
        public string ErrorReference { get; set; }

        /// <summary>How long the query took, for spotting the section that needs an index.</summary>
        public long ElapsedMs { get; set; }
    }

    /// <summary>The reporting window every tab is built from.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityWindow
    {
        public int Days { get; set; }

        /// <summary>Inclusive first UTC date covered.</summary>
        public DateTime FromUtc { get; set; }

        /// <summary>Inclusive last UTC date covered.</summary>
        public DateTime ToUtc { get; set; }

        /// <summary>Working days (Mon-Fri) in the window - the denominator of the visitor segments.</summary>
        public int WorkingDays { get; set; }

        /// <summary>Rows a ranked table returns, so the UI can caption "top N" honestly.</summary>
        public int Top { get; set; }

        /// <summary>Page-view floor a row needs to enter a quality ranking.</summary>
        public int MinimumViews { get; set; }

        /// <summary>
        /// True when this window is long enough for every visitor-engagement band to be reachable.
        /// </summary>
        /// <remarks>
        /// Over a short window there are too few possible active-day counts to fill the lower bands,
        /// so an empty "Occasional" band means "this period cannot tell", not "nobody is occasional".
        /// The UI says which when this is false.
        /// </remarks>
        public bool SegmentsFullyReachable { get; set; }

        /// <summary>
        /// Distinct viewed pages needed before a "busiest 10% of pages" share means anything.
        /// </summary>
        /// <remarks>
        /// Carried on the window so the server owns the threshold and the UI cannot drift from it:
        /// the Overview tab's judgement and the Page views tab's chart are suppressed together or
        /// not at all.
        /// </remarks>
        public int MinimumPagesForDecile { get; set; }

        /// <summary>This window, captioning <paramref name="top"/> rows per ranked table. A copy: the window is cached.</summary>
        public WebActivityWindow WithTop(int top)
        {
            var copy = (WebActivityWindow)MemberwiseClone();
            copy.Top = top;
            return copy;
        }

        public static WebActivityWindow From(WebActivityQuery query)
        {
            return new WebActivityWindow
            {
                Days = query.Days,

                // Stamped UTC so the serialised ISO string carries a "Z". Without it JavaScript's
                // Date parses the value as local time and the window caption shifts by a day.
                FromUtc = DateTime.SpecifyKind(query.FromUtc, DateTimeKind.Utc),
                ToUtc = DateTime.SpecifyKind(query.ToInclusiveUtc, DateTimeKind.Utc),
                WorkingDays = WebActivityScoring.WorkingDaysBetween(query.FromUtc, query.ToExclusiveUtc),
                Top = query.Top,
                MinimumViews = query.MinimumViews,

                // Whether the engagement mix can distinguish its lower bands at this window length.
                SegmentsFullyReachable = WebActivityScoring.SegmentsFullyReachable(query.Days),
                MinimumPagesForDecile = WebActivityScoring.MinimumPagesForDecile,
            };
        }
    }

    /// <summary>Base class carrying the window and the per-section query diagnostics.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public abstract class WebActivitySection
    {
        public WebActivityWindow Window { get; set; }

        public List<WebActivityQueryInfo> Queries { get; set; } = new List<WebActivityQueryInfo>();

        /// <summary>
        /// The window a copy of this section cut to <paramref name="top"/> rows reports: a section built for more rows than a
        /// reader asked for (#844) still captions the rows they see.
        /// </summary>
        protected WebActivityWindow WindowFor(int top) =>
            Window != null && top < Window.Top ? Window.WithTop(top) : Window;
    }

    /// <summary>A verdict on one headline figure, in words an intranet owner can act on.</summary>
    /// <remarks>
    /// The portal words the verdict itself, in the reader's language, from
    /// <see cref="HeadlineKey"/>, <see cref="DetailKey"/> and the unformatted <see cref="Values"/>.
    /// <see cref="Headline"/> and <see cref="Detail"/> are the English fallback for a key the portal
    /// does not recognise; they are never shown when it does.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityJudgement
    {
        public string Key { get; set; }

        /// <summary>One of good / neutral / warning / critical - drives the message-bar intent.</summary>
        public string Tone { get; set; }

        /// <summary>Stable id of the headline sentence, <c>{Key}.headline</c> or <c>{Key}.headline.{variant}</c>.</summary>
        public string HeadlineKey { get; set; }

        /// <summary>Stable id of the detail sentence, <c>{Key}.detail</c> or <c>{Key}.detail.{variant}</c>.</summary>
        public string DetailKey { get; set; }

        /// <summary>
        /// The figures the sentences quote, unformatted, so the portal formats them for the reader's
        /// locale. Keyed by placeholder name; empty when the sentences quote no figure.
        /// </summary>
        public Dictionary<string, double> Values { get; set; } = new Dictionary<string, double>();

        public string Headline { get; set; }

        public string Detail { get; set; }
    }

    /// <summary>A named value in a ranked list.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityNamedCount
    {
        public string Name { get; set; }
        public long Count { get; set; }

        /// <summary>Share of the section's total, 0-100. Null where a share is meaningless.</summary>
        public double? SharePct { get; set; }

        /// <summary>
        /// Distinct people behind the row, where the query counts them; null where it does not. Only decides what a reader
        /// without See PII is shown (<see cref="WebActivitySmallGroups.WithoutSmallRows"/>), so it is never sent.
        /// </summary>
        [JsonIgnore]
        public int? People { get; set; }

        /// <summary>
        /// The id of what the row is about, where the query returns one - a site's web id - so a series over time of the
        /// same thing can be matched to the row by more than a title another site may share (#844). Never sent.
        /// </summary>
        [JsonIgnore]
        public int? Key { get; set; }
    }

    /// <summary>One bucket of a distribution (period of day, session depth, load-time band...).</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityBucket
    {
        /// <summary>Stable ordering/identity key.</summary>
        public string Key { get; set; }
        public string Label { get; set; }
        public long Count { get; set; }
        public double SharePct { get; set; }
    }

    /// <summary>One point of a weekly series.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityTrendPoint
    {
        public DateTime WeekStart { get; set; }
        public long PageViews { get; set; }
        public long Visits { get; set; }
        public int Visitors { get; set; }

        /// <summary>Visits that saw exactly one page - plotted so a bounce spike is visible in context.</summary>
        public long Bounces { get; set; }

        public long Searches { get; set; }
    }

    /// <summary>A weekly value for one named series, for the stacked "by site over time" charts.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityStackPoint
    {
        public DateTime WeekStart { get; set; }
        public string Name { get; set; }
        public long Count { get; set; }

        /// <summary>
        /// The id of the series' subject where the query returns one - a site's web id; null for the catch-all bands. Matches
        /// the series to <see cref="WebActivityNamedCount.Key"/> (#844). Never sent.
        /// </summary>
        [JsonIgnore]
        public int? Key { get; set; }
    }

    /// <summary>One cell of the day-of-week x hour-of-day grid.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityHeatCell
    {
        /// <summary>0 = Monday ... 6 = Sunday.</summary>
        public int Day { get; set; }

        /// <summary>0-23, in UTC - the timestamps are stored in UTC and are not converted.</summary>
        public int Hour { get; set; }

        public long PageViews { get; set; }

        /// <summary>Visits that STARTED in this cell, so the cells sum to the window's total visits.</summary>
        public long Visits { get; set; }
    }

    #region Overview

    /// <summary>The Overview tab's headline figures.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityOverviewKpis
    {
        public long PageViews { get; set; }

        /// <summary>Distinct (visit, page) pairs - a page counted once per visit, as Google Analytics does.</summary>
        public long UniquePageViews { get; set; }

        public long Visits { get; set; }
        public int Visitors { get; set; }

        /// <summary>Enabled directory users, the denominator of <see cref="ReachPct"/>.</summary>
        public int KnownUsers { get; set; }

        /// <summary>Visitors in the enabled directory, as a share of it. Null when it cannot be measured.</summary>
        public double? ReachPct { get; set; }

        /// <summary>
        /// True when the Graph user-metadata import is on, so the directory is a real denominator.
        /// </summary>
        /// <remarks>
        /// Without it <c>dbo.users</c> still fills up - the page-view importer inserts a row for every
        /// visitor it sees - so <see cref="KnownUsers"/> would be positive and reach would be a
        /// circular ~100%: the share of people we have seen, who visited.
        /// </remarks>
        public bool DirectoryImported { get; set; }

        /// <summary>Distinct pages that were viewed at least once.</summary>
        public int UniquePages { get; set; }

        /// <summary>Distinct sites (SharePoint webs) that were visited at least once.</summary>
        public int Sites { get; set; }

        public double PagesPerVisit { get; set; }

        /// <summary>Visits that saw exactly one page, as a percentage of all visits.</summary>
        public double BouncePct { get; set; }

        /// <summary>Mean dwell seconds, or null when the tracker never reported one.</summary>
        public double? AverageSecondsOnPage { get; set; }

        /// <summary>Mean page load seconds, or null when the browser never reported one.</summary>
        public double? AverageLoadSeconds { get; set; }

        /// <summary>Visitors seen only in the second half of THIS WINDOW, not first-ever visitors.</summary>
        public int NewVisitors { get; set; }

        /// <summary>Visitors seen in both halves of the window.</summary>
        public int ReturningVisitors { get; set; }

        /// <summary>
        /// Share of page views from a device classed as mobile, over the page views whose device is
        /// KNOWN. Null when no page view carried a device.
        /// </summary>
        public double? MobilePageViewPct { get; set; }
    }

    /// <summary>The Overview tab.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityOverview : WebActivitySection
    {
        public WebActivityOverviewKpis Kpis { get; set; } = new WebActivityOverviewKpis();

        public List<WebActivityTrendPoint> Trend { get; set; } = new List<WebActivityTrendPoint>();

        /// <summary>Visitors by how habitually they visit - the engagement mix.</summary>
        public List<WebActivityBucket> VisitorSegments { get; set; } = new List<WebActivityBucket>();

        /// <summary>Visits by number of pages seen, capped into bands.</summary>
        public List<WebActivityBucket> VisitDepth { get; set; } = new List<WebActivityBucket>();

        public List<WebActivityHeatCell> Heatmap { get; set; } = new List<WebActivityHeatCell>();

        public List<WebActivityNamedCount> TopSites { get; set; } = new List<WebActivityNamedCount>();

        public List<WebActivityJudgement> Judgements { get; set; } = new List<WebActivityJudgement>();

        /// <summary>
        /// The fewest people a row of this section describes, for a reader without See PII; <c>null</c> when nothing is held
        /// back for the reader. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>Sites left out of <see cref="TopSites"/> for a reader without See PII.</summary>
        public int WithheldSites { get; set; }

        /// <summary>Engagement segments left out of <see cref="VisitorSegments"/> for a reader without See PII.</summary>
        public int WithheldSegments { get; set; }

        /// <summary>
        /// This section for a reader who does not hold the portal's See PII permission (#844): a site visited by fewer than
        /// <paramref name="minimumPeople"/> people is left out of the busiest sites, with the smallest site shown
        /// (<see cref="WebActivitySmallGroups.WithoutSmallRows"/>), and so is an engagement segment of fewer than
        /// <paramref name="minimumPeople"/> visitors, with the smallest segment shown, because the segments add up to the
        /// visitors on the same page. An empty segment describes no one and is not listed either, uncounted, so the segments
        /// listed never say which ones were left out. The headline figures, trend, heatmap and visit depth are over the
        /// reader's whole scope and stay. A shallow copy: the section is cached and shared between readers.
        /// </summary>
        public WebActivityOverview WithoutSmallGroups(int minimumPeople) => WithoutSmallGroups(minimumPeople, WebActivityQuery.MaximumTop);

        /// <summary>
        /// <see cref="WithoutSmallGroups(int)"/>, with the busiest sites floored over the first
        /// <see cref="WebActivityQuery.MaximumTop"/> sites the section holds - any site past them counting as one left out
        /// (<see cref="WebActivitySmallGroups.WithoutSmallRowsWithinCap"/>) - and then cut to the first <paramref name="top"/>
        /// (<see cref="WebActivitySmallGroups.FirstRows"/>), so the list a reader sees is the first rows of the same floored
        /// list whatever <paramref name="top"/> they asked for.
        /// </summary>
        public WebActivityOverview WithoutSmallGroups(int minimumPeople, int top)
        {
            var copy = (WebActivityOverview)MemberwiseClone();
            copy.Window = WindowFor(top);
            copy.MinimumGroupSize = minimumPeople;
            copy.TopSites = WebActivitySmallGroups.FirstRows(
                TopSites, WebActivitySmallGroups.WithoutSmallRowsWithinCap(TopSites, s => s.People, s => s.Name, minimumPeople), top, out var sites);
            copy.WithheldSites = sites;
            copy.VisitorSegments = WebActivitySmallGroups.WithoutSmallRows(
                VisitorSegments.Where(b => b.Count > 0), b => (int)Math.Min(b.Count, int.MaxValue), b => b.Key, minimumPeople, out var segments);
            copy.WithheldSegments = segments;
            return copy;
        }
    }

    #endregion

    #region Visits

    /// <summary>The Visits tab's headline figures, mirroring the Power BI report's "by STATS" panel.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityVisitKpis
    {
        public long Visits { get; set; }
        public int Visitors { get; set; }
        public int UniquePages { get; set; }
        public double VisitsPerVisitor { get; set; }

        /// <summary>Hour of day (UTC) of the earliest visit start in the window, or null when there were none.</summary>
        public int? EarliestVisitHour { get; set; }

        /// <summary>Hour of day (UTC) of the latest visit start in the window, or null when there were none.</summary>
        public int? LatestVisitHour { get; set; }

        /// <summary>Visits that started outside 07:00-19:00 UTC or at the weekend.</summary>
        public long OutOfHoursVisits { get; set; }

        public double OutOfHoursPct { get; set; }
    }

    /// <summary>The Visits tab.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityVisits : WebActivitySection
    {
        public WebActivityVisitKpis Kpis { get; set; } = new WebActivityVisitKpis();

        public List<WebActivityTrendPoint> Trend { get; set; } = new List<WebActivityTrendPoint>();

        public List<WebActivityNamedCount> BySite { get; set; } = new List<WebActivityNamedCount>();

        public List<WebActivityNamedCount> ByPage { get; set; } = new List<WebActivityNamedCount>();

        public List<WebActivityNamedCount> ByDevice { get; set; } = new List<WebActivityNamedCount>();

        public List<WebActivityNamedCount> ByBrowser { get; set; } = new List<WebActivityNamedCount>();

        /// <summary>Visits by day of week, 0 = Monday.</summary>
        public List<WebActivityBucket> ByDay { get; set; } = new List<WebActivityBucket>();

        public List<WebActivityBucket> ByPeriodOfDay { get; set; } = new List<WebActivityBucket>();

        /// <summary>Visits by hour of day (UTC), 0-23 - the "popular hours" list.</summary>
        public List<WebActivityBucket> ByHour { get; set; } = new List<WebActivityBucket>();

        /// <summary>Weekly page views for each of the busiest sites, for the stacked site-over-time chart.</summary>
        public List<WebActivityStackPoint> SiteOverTime { get; set; } = new List<WebActivityStackPoint>();

        /// <summary>
        /// The fewest people a row of this section describes, for a reader without See PII; <c>null</c> when nothing is held
        /// back for the reader. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>Sites left out of <see cref="BySite"/> for a reader without See PII.</summary>
        public int WithheldSites { get; set; }

        /// <summary>Devices left out of <see cref="ByDevice"/> for a reader without See PII.</summary>
        public int WithheldDevices { get; set; }

        /// <summary>Browsers left out of <see cref="ByBrowser"/> for a reader without See PII.</summary>
        public int WithheldBrowsers { get; set; }

        /// <summary>Pages left out of <see cref="ByPage"/> for a reader without See PII.</summary>
        public int WithheldPages { get; set; }

        /// <summary>
        /// This section for a reader who does not hold the portal's See PII permission (#844): sites, devices, browsers and
        /// pages of fewer than <paramref name="minimumPeople"/> visitors are left out, each list with its smallest row
        /// shown (<see cref="WebActivitySmallGroups.WithoutSmallRows"/>); a site the site list does not show - matched by its
        /// web id, not its title (<see cref="WebActivitySmallGroups.WithSeriesFoldedByKey"/>) - is folded into
        /// "(other sites)" over time. The headline figures, trend and time-of-day buckets are over the reader's whole scope
        /// and stay. A shallow copy: the section is cached and shared between readers.
        /// </summary>
        public WebActivityVisits WithoutSmallGroups(int minimumPeople) => WithoutSmallGroups(minimumPeople, WebActivityQuery.MaximumTop);

        /// <summary>
        /// <see cref="WithoutSmallGroups(int)"/>, with each list floored over the first <see cref="WebActivityQuery.MaximumTop"/>
        /// rows the section holds - any row past them counting as one left out
        /// (<see cref="WebActivitySmallGroups.WithoutSmallRowsWithinCap"/>) - and then cut to its first <paramref name="top"/>
        /// rows (<see cref="WebActivitySmallGroups.FirstRows"/>); the sites over time are folded to the sites the cut list shows.
        /// </summary>
        public WebActivityVisits WithoutSmallGroups(int minimumPeople, int top)
        {
            var copy = (WebActivityVisits)MemberwiseClone();
            copy.Window = WindowFor(top);
            copy.MinimumGroupSize = minimumPeople;
            copy.BySite = WebActivitySmallGroups.FirstRows(
                BySite, WebActivitySmallGroups.WithoutSmallRowsWithinCap(BySite, s => s.People, s => s.Name, minimumPeople), top, out var sites);
            copy.WithheldSites = sites;
            copy.ByDevice = WebActivitySmallGroups.FirstRows(
                ByDevice, WebActivitySmallGroups.WithoutSmallRowsWithinCap(ByDevice, d => d.People, d => d.Name, minimumPeople), top, out var devices);
            copy.WithheldDevices = devices;
            copy.ByBrowser = WebActivitySmallGroups.FirstRows(
                ByBrowser, WebActivitySmallGroups.WithoutSmallRowsWithinCap(ByBrowser, b => b.People, b => b.Name, minimumPeople), top, out var browsers);
            copy.WithheldBrowsers = browsers;
            copy.ByPage = WebActivitySmallGroups.FirstRows(
                ByPage, WebActivitySmallGroups.WithoutSmallRowsWithinCap(ByPage, p => p.People, p => p.Name, minimumPeople), top, out var pages);
            copy.WithheldPages = pages;
            copy.SiteOverTime = WebActivitySmallGroups.WithSeriesFoldedByKey(
                SiteOverTime, copy.BySite.Select(s => s.Key), OtherSites, UnknownSite);
            return copy;
        }

        /// <summary>The band <see cref="WebActivitySql.SiteOverTime"/> rolls the sites beyond the busiest into.</summary>
        internal const string OtherSites = "(other sites)";

        /// <summary>The band <see cref="WebActivitySql.SiteOverTime"/> puts page views with no site into.</summary>
        internal const string UnknownSite = "(unknown site)";
    }

    #endregion

    #region Pages

    /// <summary>A page and everything the report knows about how it performed.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityPageRow
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string Site { get; set; }
        public long PageViews { get; set; }
        public long UniquePageViews { get; set; }
        public int Visitors { get; set; }

        /// <summary>Mean dwell time in seconds, or null when the tracker never reported one.</summary>
        public double? AverageSecondsOnPage { get; set; }

        /// <summary>Mean load time in seconds, or null when the tracker never reported one.</summary>
        public double? AverageLoadSeconds { get; set; }

        /// <summary>Visits that entered the site on this page.</summary>
        public long Entries { get; set; }

        /// <summary>Visits that ended on this page.</summary>
        public long Exits { get; set; }

        /// <summary>Visits that both started and ended here, having seen nothing else.</summary>
        public long Bounces { get; set; }

        /// <summary>Bounces as a share of entries, 0-100. Null when the page was never an entry page.</summary>
        public double? BouncePct { get; set; }

        /// <summary>
        /// True on a copy of the row made for a reader without See PII (#844): its figures counted in visits are left out,
        /// zeroed here and never sent - its unique page views (the visits that saw it, which are the Visits tab's visits by
        /// page) and its entries, exits and bounces (the Journeys tab's). Those tabs floor their own lists of the same
        /// figures, so shown beside a page's other figures here they could give back a page one of them leaves out.
        /// </summary>
        [JsonIgnore]
        public bool VisitFiguresWithheld { get; private set; }

        public bool ShouldSerializeUniquePageViews() => !VisitFiguresWithheld;

        public bool ShouldSerializeEntries() => !VisitFiguresWithheld;

        public bool ShouldSerializeExits() => !VisitFiguresWithheld;

        public bool ShouldSerializeBounces() => !VisitFiguresWithheld;

        public bool ShouldSerializeBouncePct() => !VisitFiguresWithheld;

        /// <summary>
        /// Distinct people behind the row: who viewed the page, or whose visits started or ended on it. Only decides what a
        /// reader without See PII is shown (<see cref="WebActivitySmallGroups.WithoutSmallRows"/>), so it is never sent;
        /// <see cref="Visitors"/> is not it, and stays zero.
        /// </summary>
        [JsonIgnore]
        public int? People { get; set; }

        /// <summary>
        /// A copy of this row without its unique page views, entries, exits and bounces (see
        /// <see cref="VisitFiguresWithheld"/>).
        /// </summary>
        internal WebActivityPageRow WithoutVisitFigures()
        {
            var copy = (WebActivityPageRow)MemberwiseClone();
            copy.UniquePageViews = 0;
            copy.Entries = 0;
            copy.Exits = 0;
            copy.Bounces = 0;
            copy.BouncePct = null;
            copy.VisitFiguresWithheld = true;
            return copy;
        }
    }

    /// <summary>The Pages tab's headline figures.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityPageKpis
    {
        public long PageViews { get; set; }
        public long UniquePageViews { get; set; }

        /// <summary>Unique page views as a share of total - low means people re-read the same page a lot.</summary>
        public double UniqueSharePct { get; set; }

        public double PagesPerVisit { get; set; }

        /// <summary>Mean dwell seconds, or null when the tracker never reported one.</summary>
        public double? AverageSecondsOnPage { get; set; }

        /// <summary>Mean page load seconds, or null when the browser never reported one.</summary>
        public double? AverageLoadSeconds { get; set; }

        public int UniquePages { get; set; }

        /// <summary>Pages viewed no more than <c>QuietPageViewCeiling</c> times - the pruning backlog.</summary>
        public int QuietPages { get; set; }

        /// <summary>Share of all page views held by the busiest tenth of pages.</summary>
        public double TopDecilePagePct { get; set; }
    }

    /// <summary>The Pages tab.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityPages : WebActivitySection
    {
        public WebActivityPageKpis Kpis { get; set; } = new WebActivityPageKpis();

        public List<WebActivityPageRow> TopPages { get; set; } = new List<WebActivityPageRow>();

        /// <summary>Pages that took longest to load, among pages with enough views to be credible.</summary>
        public List<WebActivityPageRow> SlowestPages { get; set; } = new List<WebActivityPageRow>();

        /// <summary>Pages nobody reads - the cheapest content-cleanup backlog an owner will get.</summary>
        public List<WebActivityPageRow> QuietPages { get; set; } = new List<WebActivityPageRow>();

        /// <summary>Total and unique page views per site, the Power BI report's "by SITE" pair.</summary>
        public List<WebActivitySiteRow> BySite { get; set; } = new List<WebActivitySiteRow>();

        /// <summary>Weekly page views split by period of day - the "breakdown over time" chart.</summary>
        public List<WebActivityStackPoint> PeriodOverTime { get; set; } = new List<WebActivityStackPoint>();

        /// <summary>
        /// The fewest people a row of this section describes, for a reader without See PII; <c>null</c> when nothing is held
        /// back for the reader. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>Sites left out of <see cref="BySite"/> for a reader without See PII.</summary>
        public int WithheldSites { get; set; }

        /// <summary>Pages left out of <see cref="TopPages"/> for a reader without See PII.</summary>
        public int WithheldTopPages { get; set; }

        /// <summary>Pages left out of <see cref="SlowestPages"/> for a reader without See PII.</summary>
        public int WithheldSlowestPages { get; set; }

        /// <summary>
        /// Pages left out of <see cref="QuietPages"/> for a reader without See PII. A quiet page has at most
        /// <see cref="WebActivityScoring.QuietPageViewCeiling"/> page views, so fewer people than the minimum: every row
        /// is left out.
        /// </summary>
        public int WithheldQuietPages { get; set; }

        /// <summary>
        /// True for a reader without See PII (#844): no page row on this tab carries a figure counted in visits - its unique
        /// page views, entries, exits or bounces (<see cref="WebActivityPageRow.VisitFiguresWithheld"/>). Sent only when true,
        /// so a See PII reader's answer is as it was.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool VisitFiguresWithheld { get; set; }

        /// <summary>
        /// This section for a reader who does not hold the portal's See PII permission (#844): pages and sites of fewer
        /// than <paramref name="minimumPeople"/> visitors are left out, each list with its smallest row shown
        /// (<see cref="WebActivitySmallGroups.WithoutSmallRowsWithinCap"/>), so that no total on the tab - the page views,
        /// the unique page views - less the rows shown is ever one small page's or site's. The most viewed, slowest and quiet
        /// pages share each page's page views, so they leave out one combined set of pages - every page any of the three
        /// leaves out - and no page is left out of one and shown with its page views in another. No page carries a figure
        /// counted in visits (<see cref="VisitFiguresWithheld"/>): its unique page views are the Visits tab's visits by page,
        /// and its entries, exits and bounces the Journeys tab's, each floored on its own tab. The headline figures and the
        /// period-of-day trend are over the reader's whole scope and stay. A shallow copy: the section is cached and shared
        /// between readers.
        /// </summary>
        public WebActivityPages WithoutSmallGroups(int minimumPeople) => WithoutSmallGroups(minimumPeople, WebActivityQuery.MaximumTop);

        /// <summary>
        /// <see cref="WithoutSmallGroups(int)"/>, with each list floored over the first <see cref="WebActivityQuery.MaximumTop"/>
        /// rows the section holds - any row past them counting as one left out
        /// (<see cref="WebActivitySmallGroups.WithoutSmallRowsWithinCap"/>) - and then cut to its first <paramref name="top"/>
        /// rows (<see cref="WebActivitySmallGroups.FirstRows"/>).
        /// </summary>
        public WebActivityPages WithoutSmallGroups(int minimumPeople, int top)
        {
            var copy = (WebActivityPages)MemberwiseClone();
            copy.Window = WindowFor(top);
            copy.MinimumGroupSize = minimumPeople;
            copy.VisitFiguresWithheld = true;

            var topPages = FlooredPages(TopPages, minimumPeople);
            var slowest = FlooredPages(SlowestPages, minimumPeople);
            var quiet = FlooredPages(QuietPages, minimumPeople);
            var leftOut = new HashSet<string>(
                WebActivitySmallGroups.WithinCap(TopPages).Except(topPages)
                    .Concat(WebActivitySmallGroups.WithinCap(SlowestPages).Except(slowest))
                    .Concat(WebActivitySmallGroups.WithinCap(QuietPages).Except(quiet))
                    .Select(p => p.Url),
                StringComparer.Ordinal);
            copy.TopPages = FirstPages(TopPages, topPages, leftOut, top, out var withheldTop);
            copy.WithheldTopPages = withheldTop;
            copy.SlowestPages = FirstPages(SlowestPages, slowest, leftOut, top, out var withheldSlowest);
            copy.WithheldSlowestPages = withheldSlowest;
            copy.QuietPages = FirstPages(QuietPages, quiet, leftOut, top, out var withheldQuiet);
            copy.WithheldQuietPages = withheldQuiet;
            copy.BySite = WebActivitySmallGroups.FirstRows(
                BySite, WebActivitySmallGroups.WithoutSmallRowsWithinCap(BySite, s => s.Visitors, s => s.Name, minimumPeople), top, out var sites);
            copy.WithheldSites = sites;
            return copy;
        }

        private static List<WebActivityPageRow> FlooredPages(List<WebActivityPageRow> pages, int minimumPeople) =>
            WebActivitySmallGroups.WithoutSmallRowsWithinCap(pages, p => p.People, WebActivitySmallGroups.PageName, minimumPeople);

        private static List<WebActivityPageRow> FirstPages(
            List<WebActivityPageRow> pages, List<WebActivityPageRow> floored, ISet<string> leftOut, int top, out int withheld) =>
            WebActivitySmallGroups.FirstRows(pages, floored.Where(p => !leftOut.Contains(p.Url)).ToList(), top, out withheld)
                .Select(p => p.WithoutVisitFigures())
                .ToList();
    }

    /// <summary>A site (SharePoint web) with its total and unique page views.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivitySiteRow
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public long PageViews { get; set; }

        /// <summary>
        /// Page views on this site that belong to a visit. The only correct numerator for a
        /// per-visit ratio, since <see cref="PageViews"/> also counts hits with no session.
        /// </summary>
        public long VisitPageViews { get; set; }
        public long UniquePageViews { get; set; }
        public long Visits { get; set; }
        public int Visitors { get; set; }
    }

    #endregion

    #region Journeys

    /// <summary>A single step people take from one page to the next inside a visit.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityTransitionRow
    {
        public string FromTitle { get; set; }
        public string FromUrl { get; set; }
        public string ToTitle { get; set; }
        public string ToUrl { get; set; }
        public long Count { get; set; }

        /// <summary>Share of all steps taken out of the "from" page, 0-100.</summary>
        public double SharePct { get; set; }

        /// <summary>
        /// Distinct people who took the step. Only decides what a reader without See PII is shown
        /// (<see cref="WebActivitySmallGroups.WithoutSmallRowsInEachGroup"/>), so it is never sent.
        /// </summary>
        [JsonIgnore]
        public int? People { get; set; }
    }

    /// <summary>
    /// Where a visit started and where it ended, as one flow, for the Sankey diagram.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityFlowRow
    {
        public string StartTitle { get; set; }
        public string StartUrl { get; set; }
        public string EndTitle { get; set; }
        public string EndUrl { get; set; }

        /// <summary>Visits that started on the start page and ended on the end page.</summary>
        public long Visits { get; set; }

        /// <summary>
        /// How many of those visits saw only ONE page. Non-zero only when start and end are the
        /// same page, and then usually all of them - the UI labels these rather than hiding them.
        /// </summary>
        public long SinglePageVisits { get; set; }

        /// <summary>Average pages seen by the visits on this flow, or null when nothing reported.</summary>
        public double? AveragePages { get; set; }

        /// <summary>Share of ALL paired visits, 0-100 - not of the truncated top N.</summary>
        public double SharePct { get; set; }

        /// <summary>True when the visit ended on the page it began on.</summary>
        public bool EndedWhereItStarted { get; set; }

        /// <summary>
        /// Distinct people whose visits made the flow. Only decides what a reader without See PII is shown
        /// (<see cref="WebActivitySmallGroups.WithoutSmallRows"/>), so it is never sent.
        /// </summary>
        [JsonIgnore]
        public int? People { get; set; }
    }

    /// <summary>The Journeys tab's headline figures.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityJourneyKpis
    {
        public long Visits { get; set; }
        public long Bounces { get; set; }
        public double BouncePct { get; set; }
        public double PagesPerVisit { get; set; }

        /// <summary>Median pages per visit, which a long tail of deep visits cannot inflate.</summary>
        public int MedianPagesPerVisit { get; set; }

        /// <summary>
        /// Mean visit length in seconds over the visits that reported a dwell time, or null when none did.
        /// </summary>
        /// <remarks>
        /// Summed from the per-page dwell times, which exclude each visit's last page - so this
        /// under-states real visit length, consistently, and is still usable for comparing periods.
        /// Divided by <see cref="MeasuredVisits"/>, not by every visit: a visit with no dwell time is
        /// unmeasured, not a visit that took no time.
        /// Null rather than zero when nothing was measured: "0s" reads as a finding.
        /// </remarks>
        public double? AverageVisitSeconds { get; set; }

        /// <summary>
        /// The visits <see cref="AverageVisitSeconds"/> averages over: those in which at least one page reported a dwell time.
        /// </summary>
        public long MeasuredVisits { get; set; }

        /// <summary>Recorded element clicks - only populated when the tracker's click capture is on.</summary>
        public long Clicks { get; set; }
    }

    /// <summary>The Journeys tab - where people come in, where they leave, and what they click.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityJourneys : WebActivitySection
    {
        public WebActivityJourneyKpis Kpis { get; set; } = new WebActivityJourneyKpis();

        public List<WebActivityPageRow> EntryPages { get; set; } = new List<WebActivityPageRow>();

        public List<WebActivityPageRow> ExitPages { get; set; } = new List<WebActivityPageRow>();

        /// <summary>Entry pages ranked by how often the visit ended right there.</summary>
        public List<WebActivityPageRow> BouncePages { get; set; } = new List<WebActivityPageRow>();

        public List<WebActivityTransitionRow> Transitions { get; set; } = new List<WebActivityTransitionRow>();

        /// <summary>
        /// Start-to-end flows for the Sankey: which landing page led to which last page.
        /// </summary>
        public List<WebActivityFlowRow> Flows { get; set; } = new List<WebActivityFlowRow>();

        /// <summary>
        /// Share of all paired visits covered by <see cref="Flows"/> after truncation, 0-100, so the
        /// diagram can say how much of the traffic it is actually showing.
        /// </summary>
        public double FlowsCoveragePct { get; set; }

        public List<WebActivityBucket> Depth { get; set; } = new List<WebActivityBucket>();

        public List<WebActivityNamedCount> ClickedElements { get; set; } = new List<WebActivityNamedCount>();

        /// <summary>
        /// The fewest people a row of this section describes, for a reader without See PII; <c>null</c> when nothing is held
        /// back for the reader. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>Pages left out of <see cref="EntryPages"/> for a reader without See PII.</summary>
        public int WithheldEntryPages { get; set; }

        /// <summary>Pages left out of <see cref="ExitPages"/> for a reader without See PII.</summary>
        public int WithheldExitPages { get; set; }

        /// <summary>Pages left out of <see cref="BouncePages"/> for a reader without See PII.</summary>
        public int WithheldBouncePages { get; set; }

        /// <summary>Steps left out of <see cref="Transitions"/> for a reader without See PII.</summary>
        public int WithheldTransitions { get; set; }

        /// <summary>Flows left out of <see cref="Flows"/> for a reader without See PII.</summary>
        public int WithheldFlows { get; set; }

        /// <summary>Elements left out of <see cref="ClickedElements"/> for a reader without See PII.</summary>
        public int WithheldClickedElements { get; set; }

        /// <summary>
        /// This section for a reader who does not hold the portal's See PII permission (#844): an entry, exit or bounce
        /// page, a flow or a clicked element of fewer than <paramref name="minimumPeople"/> people is left out, each list
        /// with its smallest row shown (<see cref="WebActivitySmallGroups.WithoutSmallRowsWithinCap"/>, which also counts
        /// the rows past the cap as one left out), so that the visits, bounces and clicks less the rows shown are never one
        /// small row's. The entry, exit and bounce pages share each page's entries, exits and bounces, so they leave out one
        /// combined set of pages - every page any of the three leaves out - and no page is left out of one and shown with
        /// those figures in another (the Pages tab sends this reader none of them:
        /// <see cref="WebActivityPages.VisitFiguresWithheld"/>). Nor is a flow that starts or ends on such a page shown,
        /// because a page's flows add up to its entries and exits. A step is left out the same way among the steps out of
        /// its own page (<see cref="WebActivitySmallGroups.WithoutSmallRowsInEachGroup"/>), because its share is of that
        /// page's steps - and so are the steps of a page whose steps go on past the list
        /// (<see cref="StepsGoOnPastTheList"/>). The flows' coverage is of the flows shown. The headline figures and the
        /// visit-depth buckets are over the reader's whole scope and stay. A shallow copy: the section is cached and shared
        /// between readers.
        /// </summary>
        public WebActivityJourneys WithoutSmallGroups(int minimumPeople) => WithoutSmallGroups(minimumPeople, WebActivityQuery.MaximumTop);

        /// <summary>
        /// <see cref="WithoutSmallGroups(int)"/>, with each list floored over the first <see cref="WebActivityQuery.MaximumTop"/>
        /// rows the section holds - any row past them counting as one left out - and then cut to its first
        /// <paramref name="top"/> rows (<see cref="WebActivitySmallGroups.FirstRows"/>).
        /// </summary>
        public WebActivityJourneys WithoutSmallGroups(int minimumPeople, int top)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var copy = (WebActivityJourneys)MemberwiseClone();
            copy.Window = WindowFor(top);
            copy.MinimumGroupSize = minimumPeople;

            var entry = WebActivitySmallGroups.WithoutSmallRowsWithinCap(EntryPages, p => p.People, WebActivitySmallGroups.PageName, minimumPeople);
            var exit = WebActivitySmallGroups.WithoutSmallRowsWithinCap(ExitPages, p => p.People, WebActivitySmallGroups.PageName, minimumPeople);
            var bounce = WebActivitySmallGroups.WithoutSmallRowsWithinCap(BouncePages, p => p.People, WebActivitySmallGroups.PageName, minimumPeople);
            var leftOut = new HashSet<string>(
                WebActivitySmallGroups.WithinCap(EntryPages).Except(entry)
                    .Concat(WebActivitySmallGroups.WithinCap(ExitPages).Except(exit))
                    .Concat(WebActivitySmallGroups.WithinCap(BouncePages).Except(bounce))
                    .Select(p => p.Url),
                StringComparer.Ordinal);
            copy.EntryPages = WebActivitySmallGroups.FirstRows(EntryPages, entry.Where(p => !leftOut.Contains(p.Url)).ToList(), top, out var entries);
            copy.WithheldEntryPages = entries;
            copy.ExitPages = WebActivitySmallGroups.FirstRows(ExitPages, exit.Where(p => !leftOut.Contains(p.Url)).ToList(), top, out var exits);
            copy.WithheldExitPages = exits;
            copy.BouncePages = WebActivitySmallGroups.FirstRows(BouncePages, bounce.Where(p => !leftOut.Contains(p.Url)).ToList(), top, out var bounces);
            copy.WithheldBouncePages = bounces;

            var steps = WebActivitySmallGroups.WithinCap(Transitions).ToList();
            var stepsCut = Transitions.Count > steps.Count;
            copy.Transitions = WebActivitySmallGroups.FirstRows(
                steps,
                WebActivitySmallGroups.WithoutSmallRowsInEachGroup(
                    steps,
                    t => t.FromUrl,
                    t => t.People,
                    t => t.ToTitle + "\u0000" + t.ToUrl,
                    minimumPeople,
                    stepsOutOfAPage => StepsGoOnPastTheList(stepsOutOfAPage, stepsCut),
                    out _),
                top,
                out var transitions);
            copy.WithheldTransitions = transitions;
            copy.Flows = WebActivitySmallGroups.FirstRows(
                Flows,
                WebActivitySmallGroups.WithoutSmallRowsWithinCap(Flows, f => f.People, f => f.StartUrl + "\u0000" + f.EndUrl, minimumPeople)
                    .Where(f => !leftOut.Contains(f.StartUrl) && !leftOut.Contains(f.EndUrl))
                    .ToList(),
                top,
                out var flows);
            copy.WithheldFlows = flows;
            copy.FlowsCoveragePct = Math.Min(100, copy.Flows.Sum(f => f.SharePct));
            copy.ClickedElements = WebActivitySmallGroups.FirstRows(
                ClickedElements,
                WebActivitySmallGroups.WithoutSmallRowsWithinCap(ClickedElements, c => c.People, c => c.Name, minimumPeople),
                top,
                out var clicks);
            copy.WithheldClickedElements = clicks;
            return copy;
        }

        /// <summary>
        /// True when the steps out of one page go on past the list (#844). A step's share is of every step out of its page,
        /// so any step's count and share give that page's total away: steps that add up to less leave some out beyond the
        /// cap, which count as a step left out. Without a share to tell by, a list cut at the cap is taken to go on.
        /// </summary>
        internal static bool StepsGoOnPastTheList(IReadOnlyList<WebActivityTransitionRow> stepsOutOfAPage, bool listCut)
        {
            var widest = stepsOutOfAPage.Where(s => s.SharePct > 0).OrderByDescending(s => s.Count).FirstOrDefault();
            if (widest == null) return listCut;

            var stepsOfThePage = Math.Round(widest.Count * 100.0 / widest.SharePct);
            return stepsOutOfAPage.Sum(s => s.Count) < stepsOfThePage;
        }
    }

    #endregion

    #region Geography

    /// <summary>The Geography tab's headline figures.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityGeographyKpis
    {
        public long Visits { get; set; }
        public int Visitors { get; set; }
        public int Countries { get; set; }

        /// <summary>Distinct (city, country) pairs - the city lookup is keyed on name alone.</summary>
        public long Cities { get; set; }

        /// <summary>Distinct (region, country) pairs.</summary>
        public long Provinces { get; set; }

        /// <summary>Page views whose location the tracker could not resolve.</summary>
        public long UnknownLocationPageViews { get; set; }

        public double UnknownLocationPct { get; set; }

        /// <summary>
        /// Page views that DID resolve to a place - the denominator every place share is against.
        /// </summary>
        /// <remarks>
        /// Published so the UI can show an explicit remainder. The place lists are truncated to the
        /// top N, and a chart that normalised over only those rows would always total 100% however
        /// much traffic came from the countries it did not list.
        /// </remarks>
        public long LocatedPageViews { get; set; }

        /// <summary>
        /// Page views that resolved to a COUNTRY - the denominator the country chart and its
        /// remainder must use.
        /// </summary>
        /// <remarks>
        /// Not the same as <see cref="LocatedPageViews"/>, which also counts a page view that
        /// resolved to a city but not a country. Using the looser total would quietly fold those
        /// into an "other countries" slice they do not belong to.
        /// </remarks>
        public long CountryPageViews { get; set; }

        /// <summary>Page views that resolved to a city.</summary>
        public long CityPageViews { get; set; }

        /// <summary>Page views that resolved to a state, province or region.</summary>
        public long ProvincePageViews { get; set; }
    }

    /// <summary>A place, with the traffic that came from it.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityPlaceRow
    {
        public string Name { get; set; }

        /// <summary>The country a city or province sits in, so two same-named cities can be told apart.</summary>
        public string Country { get; set; }

        public long PageViews { get; set; }
        public long Visits { get; set; }
        public int Visitors { get; set; }
        public double SharePct { get; set; }
    }

    /// <summary>The Geography tab.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityGeography : WebActivitySection
    {
        public WebActivityGeographyKpis Kpis { get; set; } = new WebActivityGeographyKpis();

        public List<WebActivityPlaceRow> Countries { get; set; } = new List<WebActivityPlaceRow>();

        public List<WebActivityPlaceRow> Cities { get; set; } = new List<WebActivityPlaceRow>();

        public List<WebActivityPlaceRow> Provinces { get; set; } = new List<WebActivityPlaceRow>();

        /// <summary>Weekly page views for each of the busiest countries.</summary>
        public List<WebActivityStackPoint> CountryOverTime { get; set; } = new List<WebActivityStackPoint>();

        /// <summary>
        /// The fewest people a row of this section describes, for a reader without See PII; <c>null</c> when nothing is held
        /// back for the reader. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>Countries left out of <see cref="Countries"/> for a reader without See PII.</summary>
        public int WithheldCountries { get; set; }

        /// <summary>Cities left out of <see cref="Cities"/> for a reader without See PII.</summary>
        public int WithheldCities { get; set; }

        /// <summary>Provinces left out of <see cref="Provinces"/> for a reader without See PII.</summary>
        public int WithheldProvinces { get; set; }

        /// <summary>
        /// This section for a reader who does not hold the portal's See PII permission (#844): a country, city or province
        /// fewer than <paramref name="minimumPeople"/> people visited from is left out, each list with its smallest place
        /// shown, so that the located page views less the places shown are never one small place's
        /// (<see cref="WebActivitySmallGroups.WithoutSmallRows"/>); a country the list does not show is folded into
        /// "(other countries)" over time. The headline figures are over the reader's whole scope and stay. A shallow copy:
        /// the section is cached and shared between readers.
        /// </summary>
        public WebActivityGeography WithoutSmallGroups(int minimumPeople) => WithoutSmallGroups(minimumPeople, WebActivityQuery.MaximumTop);

        /// <summary>
        /// <see cref="WithoutSmallGroups(int)"/>, with each list floored over the first <see cref="WebActivityQuery.MaximumTop"/>
        /// places the section holds - any place past them counting as one left out
        /// (<see cref="WebActivitySmallGroups.WithoutSmallRowsWithinCap"/>) - and then cut to its first <paramref name="top"/>
        /// rows (<see cref="WebActivitySmallGroups.FirstRows"/>); the countries over time are folded to the countries the cut
        /// list shows.
        /// </summary>
        public WebActivityGeography WithoutSmallGroups(int minimumPeople, int top)
        {
            var copy = (WebActivityGeography)MemberwiseClone();
            copy.Window = WindowFor(top);
            copy.MinimumGroupSize = minimumPeople;
            copy.Countries = WebActivitySmallGroups.FirstRows(
                Countries, WebActivitySmallGroups.WithoutSmallRowsWithinCap(Countries, p => p.Visitors, PlaceName, minimumPeople), top, out var countries);
            copy.WithheldCountries = countries;
            copy.Cities = WebActivitySmallGroups.FirstRows(
                Cities, WebActivitySmallGroups.WithoutSmallRowsWithinCap(Cities, p => p.Visitors, PlaceName, minimumPeople), top, out var cities);
            copy.WithheldCities = cities;
            copy.Provinces = WebActivitySmallGroups.FirstRows(
                Provinces, WebActivitySmallGroups.WithoutSmallRowsWithinCap(Provinces, p => p.Visitors, PlaceName, minimumPeople), top, out var provinces);
            copy.WithheldProvinces = provinces;
            copy.CountryOverTime = WebActivitySmallGroups.WithSeriesFolded(
                CountryOverTime, copy.Countries.Select(c => c.Name), OtherCountries, UnknownCountry);
            return copy;
        }

        private static string PlaceName(WebActivityPlaceRow place) => place.Name + "\u0000" + place.Country;

        /// <summary>
        /// The city names a reader without See PII is shown on this section (#844) - <see cref="WithoutSmallGroups(int)"/>'s
        /// cities, floored over the first <see cref="WebActivityQuery.MaximumTop"/> cities the section holds as the tab floors
        /// them - less any name a city it leaves out or holds past the cap also has. Technology's detail rows
        /// name a city and nothing else (the city lookup is keyed on its name alone), so a detail row may carry only a city
        /// this reader can already see, or it would give back a city Geography left out as the smallest shown, and the
        /// remaining small cities with it.
        /// </summary>
        public ISet<string> CityNamesShownWithoutSeePii(int minimumPeople)
        {
            var floored = WithoutSmallGroups(minimumPeople);
            var shown = new HashSet<string>(floored.Cities.Select(c => c.Name), StringComparer.Ordinal);
            shown.ExceptWith(Cities.Where(c => !floored.Cities.Contains(c)).Select(c => c.Name));
            return shown;
        }

        /// <summary>The band <see cref="WebActivitySql.CountryOverTime"/> rolls the countries beyond the busiest into.</summary>
        internal const string OtherCountries = "(other countries)";

        /// <summary>The band <see cref="WebActivitySql.CountryOverTime"/> puts page views with no country into.</summary>
        internal const string UnknownCountry = "(unknown country)";
    }

    #endregion

    #region Search

    /// <summary>The Search tab's headline figures.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivitySearchKpis
    {
        public long Searches { get; set; }
        public int Terms { get; set; }
        public int Searchers { get; set; }

        /// <summary>Visits in which at least one search was run.</summary>
        public long SessionsWithSearch { get; set; }

        /// <summary>Visits with a search, as a percentage of all visits in the window.</summary>
        public double SearchReliancePct { get; set; }

        public double SearchesPerSearchingVisit { get; set; }

        /// <summary>Visits that ran three or more searches - someone who could not find it first time.</summary>
        public long StrugglingVisits { get; set; }

        /// <summary>
        /// Searches after which the visit recorded no further page view.
        /// </summary>
        /// <remarks>
        /// Page views within <c>WebActivitySql.SearchDeadEndGraceSeconds</c> of the search do not
        /// count: the search results page is itself a page view fired in the same page load.
        /// </remarks>
        public long DeadEndSearches { get; set; }

        public double DeadEndPct { get; set; }

        /// <summary>
        /// The grace window the dead-end measure uses, in seconds.
        /// </summary>
        /// <remarks>
        /// Published so the UI can state the real threshold instead of repeating a number that drifts
        /// the moment the constant is tuned - which it already did once.
        /// </remarks>
        public int DeadEndGraceSeconds { get; set; }
    }

    /// <summary>A search term and how well it worked out.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivitySearchTermRow
    {
        public string Term { get; set; }
        public long Searches { get; set; }
        public int Searchers { get; set; }

        /// <summary>Searches for this term after which the visit recorded no further page view.</summary>
        public long DeadEnds { get; set; }

        /// <summary>Dead ends as a share of this term's searches, 0-100.</summary>
        public double DeadEndPct { get; set; }
    }

    /// <summary>The Search tab.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivitySearch : WebActivitySection
    {
        public WebActivitySearchKpis Kpis { get; set; } = new WebActivitySearchKpis();

        public List<WebActivitySearchTermRow> TopTerms { get; set; } = new List<WebActivitySearchTermRow>();

        /// <summary>Terms most often followed by nothing - the demand your content is not meeting.</summary>
        public List<WebActivitySearchTermRow> DeadEndTerms { get; set; } = new List<WebActivitySearchTermRow>();

        public List<WebActivityTrendPoint> Trend { get; set; } = new List<WebActivityTrendPoint>();

        /// <summary>Searches by day of week, 0 = Monday.</summary>
        public List<WebActivityBucket> ByDay { get; set; } = new List<WebActivityBucket>();

        public List<WebActivityBucket> ByPeriodOfDay { get; set; } = new List<WebActivityBucket>();

        /// <summary>Which sites people were on when they searched.</summary>
        public List<WebActivityNamedCount> BySite { get; set; } = new List<WebActivityNamedCount>();

        /// <summary>
        /// The fewest people a row of this section describes, for a reader without See PII; <c>null</c> when nothing is held
        /// back for the reader. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="TopTerms"/> and <see cref="DeadEndTerms"/> are empty: a search
        /// term is text a person typed, which can name them or what they were looking for.
        /// </summary>
        public bool TermsWithheld { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="BySite"/> is empty: nothing counts the people who searched on
        /// a site.
        /// </summary>
        public bool SitesWithheld { get; set; }

        /// <summary>
        /// This section for a reader who does not hold the portal's See PII permission (#844): the search terms and dead-end
        /// terms are withheld, as Teams key phrases are, and so are the searches by site, whose query counts no people. The
        /// headline figures, trend and time-of-day buckets are over the reader's whole scope and stay. A shallow copy: the
        /// section is cached and shared between readers.
        /// </summary>
        public WebActivitySearch WithoutSmallGroups(int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var copy = (WebActivitySearch)MemberwiseClone();
            copy.MinimumGroupSize = minimumPeople;
            copy.TopTerms = new List<WebActivitySearchTermRow>();
            copy.DeadEndTerms = new List<WebActivitySearchTermRow>();
            copy.TermsWithheld = true;
            copy.BySite = new List<WebActivityNamedCount>();
            copy.SitesWithheld = true;
            return copy;
        }
    }

    #endregion

    #region Technology

    /// <summary>The Technology tab's headline figures.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityTechnologyKpis
    {
        public int Browsers { get; set; }
        public int OperatingSystems { get; set; }
        public int Devices { get; set; }

        /// <summary>
        /// Share of page views from a device classed as mobile, over the page views whose device is
        /// KNOWN. Null when no page view carried a device.
        /// </summary>
        public double? MobilePct { get; set; }

        /// <summary>Mean page load seconds, or null when the browser never reported one.</summary>
        public double? AverageLoadSeconds { get; set; }

        /// <summary>
        /// The load time one page view in twenty is worse than, or null when none was reported.
        /// </summary>
        public double? P95LoadSeconds { get; set; }

        /// <summary>
        /// True when the 95th percentile fell in the histogram's overflow bucket.
        /// </summary>
        /// <remarks>
        /// Loads slower than the ceiling are all folded into one bucket, so beyond it the estimate
        /// stops being an upper bound on the real value - the true p95 could be far worse. The UI
        /// renders it as "at least" rather than as a number when this is set.
        /// </remarks>
        public bool P95AtCeiling { get; set; }

        /// <summary>The ceiling the histogram folds slower loads into, in seconds.</summary>
        public double LoadCeilingSeconds { get; set; }

        /// <summary>All page views in the window - the denominator of every platform share.</summary>
        public long PageViews { get; set; }

        /// <summary>Page views whose device is known - the denominator of the mobile share.</summary>
        public long KnownDevicePageViews { get; set; }

        /// <summary>Page views whose browser the tracker could not identify.</summary>
        public long UnknownBrowserPageViews { get; set; }
    }

    /// <summary>A client platform and how it performed.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityPlatformRow
    {
        public string Name { get; set; }
        public long PageViews { get; set; }
        public long Visits { get; set; }
        public int Visitors { get; set; }
        public double SharePct { get; set; }
        public double? AverageLoadSeconds { get; set; }
        public double? AverageSecondsOnPage { get; set; }
    }

    /// <summary>One row of the Power BI report's "by DETAIL" table.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityTechnologyDetailRow
    {
        public string Browser { get; set; }
        public string Device { get; set; }
        public string OperatingSystem { get; set; }
        public string City { get; set; }
        public long Visits { get; set; }
        public int Visitors { get; set; }
        public long PageViews { get; set; }
        /// <summary>
        /// Visit-attributed page views divided by visits, or NULL when this combination produced no
        /// visits at all - hits carry a nullable session id, so a row can have page views and no
        /// visit to divide by, and a confident 0.0 there is a measurement that was never made.
        /// </summary>
        public double? PageViewsPerVisit { get; set; }
        public double? AverageSecondsOnPage { get; set; }
        public double? AverageLoadSeconds { get; set; }
    }

    /// <summary>The Technology tab.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityTechnology : WebActivitySection
    {
        public WebActivityTechnologyKpis Kpis { get; set; } = new WebActivityTechnologyKpis();

        public List<WebActivityPlatformRow> Browsers { get; set; } = new List<WebActivityPlatformRow>();

        public List<WebActivityPlatformRow> OperatingSystems { get; set; } = new List<WebActivityPlatformRow>();

        public List<WebActivityPlatformRow> Devices { get; set; } = new List<WebActivityPlatformRow>();

        /// <summary>Weekly mobile share of page views - the trend that decides a responsive-design budget.</summary>
        public List<WebActivityStackPoint> DeviceOverTime { get; set; } = new List<WebActivityStackPoint>();

        public List<WebActivityTechnologyDetailRow> Detail { get; set; } = new List<WebActivityTechnologyDetailRow>();

        /// <summary>
        /// The fewest people a row of this section describes, for a reader without See PII; <c>null</c> when nothing is held
        /// back for the reader. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>Browsers left out of <see cref="Browsers"/> for a reader without See PII.</summary>
        public int WithheldBrowsers { get; set; }

        /// <summary>Operating systems left out of <see cref="OperatingSystems"/> for a reader without See PII.</summary>
        public int WithheldOperatingSystems { get; set; }

        /// <summary>Devices left out of <see cref="Devices"/> for a reader without See PII.</summary>
        public int WithheldDevices { get; set; }

        /// <summary>Rows left out of <see cref="Detail"/> for a reader without See PII.</summary>
        public int WithheldDetailRows { get; set; }

        /// <summary>
        /// This section for a reader who does not hold the portal's See PII permission (#844): browsers, operating systems,
        /// devices and browser x device x operating system x city rows of fewer than <paramref name="minimumPeople"/>
        /// visitors are left out, each list with its smallest row shown (<see cref="WebActivitySmallGroups.WithoutSmallRows"/>);
        /// a device the list does not show is folded into "(other devices)" over time. The headline figures and load-time
        /// measures are over the reader's whole scope and stay. A shallow copy: the section is cached and shared between
        /// readers.
        /// </summary>
        public WebActivityTechnology WithoutSmallGroups(int minimumPeople) => WithoutSmallGroups(minimumPeople, null, WebActivityQuery.MaximumTop);

        /// <summary>
        /// <see cref="WithoutSmallGroups(int)"/>, where a detail row may also name only a city in
        /// <paramref name="citiesShown"/> - those Geography shows this reader (<see cref="WebActivityGeography.CityNamesShownWithoutSeePii"/>)
        /// - or no city at all: a row of any other city counts as small, so it is left out, and the smallest row shown with it.
        /// Null allows every city.
        /// </summary>
        public WebActivityTechnology WithoutSmallGroups(int minimumPeople, ISet<string> citiesShown) =>
            WithoutSmallGroups(minimumPeople, citiesShown, WebActivityQuery.MaximumTop);

        /// <summary>
        /// <see cref="WithoutSmallGroups(int, ISet{string})"/>, with each list floored over the first
        /// <see cref="WebActivityQuery.MaximumTop"/> rows the section holds - any row past them counting as one left out
        /// (<see cref="WebActivitySmallGroups.WithoutSmallRowsWithinCap"/>) - and then cut to its first <paramref name="top"/>
        /// rows (<see cref="WebActivitySmallGroups.FirstRows"/>); the devices over time are folded to the devices the cut list
        /// shows.
        /// </summary>
        public WebActivityTechnology WithoutSmallGroups(int minimumPeople, ISet<string> citiesShown, int top)
        {
            var copy = (WebActivityTechnology)MemberwiseClone();
            copy.Window = WindowFor(top);
            copy.MinimumGroupSize = minimumPeople;
            copy.Browsers = WebActivitySmallGroups.FirstRows(
                Browsers, WebActivitySmallGroups.WithoutSmallRowsWithinCap(Browsers, p => p.Visitors, p => p.Name, minimumPeople), top, out var browsers);
            copy.WithheldBrowsers = browsers;
            copy.OperatingSystems = WebActivitySmallGroups.FirstRows(
                OperatingSystems, WebActivitySmallGroups.WithoutSmallRowsWithinCap(OperatingSystems, p => p.Visitors, p => p.Name, minimumPeople), top, out var systems);
            copy.WithheldOperatingSystems = systems;
            copy.Devices = WebActivitySmallGroups.FirstRows(
                Devices, WebActivitySmallGroups.WithoutSmallRowsWithinCap(Devices, p => p.Visitors, p => p.Name, minimumPeople), top, out var devices);
            copy.WithheldDevices = devices;
            copy.DeviceOverTime = WebActivitySmallGroups.WithSeriesFolded(
                DeviceOverTime, copy.Devices.Select(d => d.Name), OtherDevices, UnknownDevice);
            copy.Detail = WebActivitySmallGroups.FirstRows(
                Detail,
                WebActivitySmallGroups.WithoutSmallRowsWithinCap(
                    Detail,
                    r => citiesShown == null || r.City == UnknownCity || citiesShown.Contains(r.City) ? r.Visitors : 0,
                    r => string.Join("\u0000", r.Browser, r.Device, r.OperatingSystem, r.City),
                    minimumPeople),
                top,
                out var detail);
            copy.WithheldDetailRows = detail;
            return copy;
        }

        /// <summary>What <see cref="WebActivitySql.TechnologyDetail"/> calls a page view's city when it has none.</summary>
        internal const string UnknownCity = "(unknown)";

        /// <summary>The band <see cref="WebActivitySql.DeviceOverTime"/> rolls the devices beyond the busiest into.</summary>
        internal const string OtherDevices = "(other devices)";

        /// <summary>The band <see cref="WebActivitySql.DeviceOverTime"/> puts page views with no device into.</summary>
        internal const string UnknownDevice = "(unknown device)";
    }

    #endregion
}
