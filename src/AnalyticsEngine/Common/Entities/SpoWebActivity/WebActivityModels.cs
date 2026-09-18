using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;

namespace Common.Entities.SpoWebActivity
{
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

        /// <summary>The innermost error message when the section failed; null when it succeeded.</summary>
        public string Error { get; set; }

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
            };
        }
    }

    /// <summary>Base class carrying the window and the per-section query diagnostics.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public abstract class WebActivitySection
    {
        public WebActivityWindow Window { get; set; }

        public List<WebActivityQueryInfo> Queries { get; set; } = new List<WebActivityQueryInfo>();
    }

    /// <summary>A verdict on one headline figure, in words an intranet owner can act on.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityJudgement
    {
        public string Key { get; set; }

        /// <summary>One of good / neutral / warning / critical - drives the message-bar intent.</summary>
        public string Tone { get; set; }

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

    /// <summary>The Overview tab.</summary>    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
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

        /// <summary>Weekly visits for each of the busiest sites, for the stacked site-over-time chart.</summary>
        public List<WebActivityStackPoint> SiteOverTime { get; set; } = new List<WebActivityStackPoint>();
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
    }

    /// <summary>A site (SharePoint web) with its total and unique page views.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivitySiteRow
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public long PageViews { get; set; }
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
        /// Mean visit length in seconds, or null when no visit reported a dwell time.
        /// </summary>
        /// <remarks>
        /// Summed from the per-page dwell times, which exclude each visit's last page - so this
        /// under-states real visit length, consistently, and is still usable for comparing periods.
        /// Null rather than zero when nothing was measured: "0s" reads as a finding.
        /// </remarks>
        public double? AverageVisitSeconds { get; set; }

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

        public List<WebActivityBucket> Depth { get; set; } = new List<WebActivityBucket>();

        public List<WebActivityNamedCount> ClickedElements { get; set; } = new List<WebActivityNamedCount>();
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

        /// <summary>Weekly visits for each of the busiest countries.</summary>
        public List<WebActivityStackPoint> CountryOverTime { get; set; } = new List<WebActivityStackPoint>();
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
        public double PageViewsPerVisit { get; set; }
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
    }

    #endregion
}
