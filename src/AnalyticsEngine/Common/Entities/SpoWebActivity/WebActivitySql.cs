using System;
using System.Collections.Generic;
using System.Globalization;

namespace Common.Entities.SpoWebActivity
{
    /// <summary>
    /// Every SQL statement the SharePoint web-activity page runs, as constants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Base tables only.</b> Nothing here reads <c>hits_view</c>, <c>vwUsage</c> or any of the other
    /// legacy reporting views the Power BI reports were built on. Those views materialise whole tables
    /// and join through a date dimension; they do not survive a busy intranet. Every aggregate is
    /// computed in SQL against <c>dbo.hits</c> and its lookups, and nothing materialises a per-hit row
    /// set into memory.
    /// </para>
    /// <para>
    /// <b>One parameter set for every query.</b> All statements are executed with the same list of
    /// parameters even when they use only a few of them. SQL Server accepts declared-but-unused
    /// parameters, and a single shared list is the only way to guarantee the SQL shown in the UI's
    /// popover declares exactly what the executed command supplied.
    /// </para>
    /// <para>
    /// <b>Thresholds live in C#, not here.</b> Queries return raw distributions - page views per
    /// visit, visits per hour, load times in 250 ms buckets - and <see cref="WebActivityScoring"/>
    /// turns them into bounce rates, periods of the day and bands. Device classification is the
    /// clearest example: whether <c>Surface Duo</c> counts as mobile is a judgement, and a judgement
    /// pasted into six <c>CASE</c> expressions is a judgement nobody can change safely.
    /// </para>
    /// <para>
    /// <b>Day arithmetic, never DATEPART(WEEKDAY).</b> 1900-01-01 was a Monday, so
    /// <c>DATEDIFF(DAY, 0, col) % 7</c> is 0 on Mondays regardless of the connection's
    /// <c>DATEFIRST</c> or language. <c>DATEPART(WEEKDAY, ...)</c> is session-dependent and would
    /// silently give different callers different answers.
    /// </para>
    /// <para>
    /// <b>Times are UTC and are not converted.</b> <c>hits.hit_timestamp</c> is stored in UTC, and an
    /// intranet with offices in several time zones has no single local clock to convert to. Every
    /// hour-of-day and day-of-week figure on this page is therefore UTC, and the UI says so rather
    /// than quietly implying otherwise.
    /// </para>
    /// <para>
    /// <b>Load and dwell times are SECONDS.</b> <c>hits.page_load_time</c> is written from the App
    /// Insights import's <c>PageLoadInSeconds</c> and <c>hits.seconds_on_page</c> from the tracker's
    /// dwell measurement; both are floats in seconds, not milliseconds. Treating either as
    /// milliseconds would make the performance tab wrong by three orders of magnitude while still
    /// looking plausible.
    /// </para>
    /// <para>
    /// <b>A page_load_time of ZERO means "not measured", not "instant".</b> The App Insights import's
    /// <c>PageLoadInSeconds</c> returns <c>0</c> - not <c>NULL</c> - whenever the page-load custom
    /// property is absent or itself "0", so the column cannot distinguish the two by nullability.
    /// Every average, floor and histogram here therefore qualifies on <c>page_load_time &gt; 0</c>
    /// rather than <c>IS NOT NULL</c>. Counting the zeros drags every average toward zero, piles
    /// unmeasured views into the fastest histogram bucket, and produces a confident "comfortably
    /// fast" verdict from views that were never timed.
    /// </para>
    /// <para>
    /// Note a separate, PRE-EXISTING caveat this report inherits and does not fix: that same importer
    /// property derives the value as <c>DurationMS / (1000 * 10)</c>, which is a tenth of the
    /// milliseconds-to-seconds conversion, and it carries a standing TODO asking for a domain owner's
    /// decision before it changes. Load times here are therefore internally consistent and good for
    /// RANKING pages against each other, but their absolute scale is only as trustworthy as that
    /// import. Do not quote them as service-level figures until it is resolved.
    /// </para>
    /// <para>
    /// <b>OPTION (RECOMPILE) everywhere.</b> The window is the main cost lever, and a plan cached for
    /// 7 days is a bad plan for 365. Recompiling costs a millisecond or two on statements that are
    /// already reading tens of thousands of rows.
    /// </para>
    /// </remarks>
    public static class WebActivitySql
    {
        /// <summary>
        /// Per-query command timeout. A single slow aggregate would otherwise run until Azure App
        /// Service kills the request (~230s) and return a 500; capping it degrades one section to an
        /// error message instead.
        /// </summary>
        public const int CommandTimeoutSeconds = 25;

        /// <summary>
        /// Width, in seconds, of the page-load histogram buckets.
        /// </summary>
        /// <remarks>
        /// A histogram rather than <c>PERCENTILE_CONT</c> deliberately. The percentile functions have
        /// no aggregate form, so they sort every qualifying row - on the largest table in the product
        /// that is the one query on this page that would reliably time out. A hash aggregate into
        /// fixed buckets is cheap, gives an interpolated p95 accurate to a quarter of a second, and
        /// produces a distribution chart as a side effect.
        /// </remarks>
        public const double LoadBucketSeconds = 0.25;

        /// <summary>Page loads slower than this are folded into a single overflow bucket.</summary>
        public const double LoadBucketCeilingSeconds = 30.0;

        /// <summary>The overflow bucket index, i.e. <see cref="LoadBucketCeilingSeconds"/> / width.</summary>
        public const int LoadOverflowBucket = 120;

        /// <summary>Visits are "out of hours" before this UTC hour.</summary>
        public const int WorkingDayStartHour = 7;

        /// <summary>Visits are "out of hours" from this UTC hour onwards.</summary>
        public const int WorkingDayEndHour = 19;

        /// <summary>Searches in one visit at or above which the visitor was plainly struggling.</summary>
        public const int StrugglingSearchCount = 3;

        /// <summary>
        /// Seconds after a search within which a page view does NOT count as the search having worked.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This exists because the search results page is itself a page view. The tracker fires the
        /// search event and the results page view within the same page load, so a naive "was there a
        /// later hit?" test answers yes for EVERY search and the dead-end measure silently reports
        /// zero forever. The grace window excludes the results page and counts only a genuine next
        /// page.
        /// </para>
        /// <para>
        /// Five seconds is a compromise with a stated cost, and the UI states it: a visitor who clicks
        /// a result within five seconds is counted as a dead end, as is anyone whose last action of
        /// the day was a search. The measure is therefore an upper bound on searches that did not
        /// help, not a count of them - which is the direction that sends someone to LOOK at a page
        /// rather than hiding a content gap entirely.
        /// </para>
        /// </remarks>
        public const int SearchDeadEndGraceSeconds = 5;

        #region Fragments

        /// <summary>
        /// Directory users the reach percentage is measured against. Users with an unknown
        /// <c>account_enabled</c> are counted: treating unknown as disabled would silently shrink the
        /// denominator and inflate reach.
        /// </summary>
        public const string EnabledUsersPredicate = "(u.account_enabled IS NULL OR u.account_enabled = 1)";

        /// <summary>
        /// The window of hits every statement starts from.
        /// </summary>
        /// <remarks>
        /// Always the first CTE so the date predicate is the first thing the optimiser sees and
        /// <c>IX_hits_hit_timestamp</c> can drive the plan. Columns beyond <c>session_id</c> are key
        /// lookups, which is why the default window is 28 days rather than a year.
        /// </remarks>
        public const string HitWindowCte = @"
H AS (
    SELECT h.id, h.session_id, h.url_id, h.hit_timestamp, h.seconds_on_page, h.page_load_time,
           h.web_id, h.agent_id, h.device_id, h.os_id, h.city_id, h.country_id,
           h.location_province_id, h.page_title_id
    FROM dbo.hits AS h
    WHERE h.hit_timestamp >= @from AND h.hit_timestamp < @to
)";

        /// <summary>
        /// One row per visit: its first and last page view in the window, and how many pages it saw.
        /// </summary>
        /// <remarks>
        /// A visit that straddles the start of the window reports its first hit INSIDE the window, not
        /// its true start. That is a deliberate, stated limitation - the alternative is to read hits
        /// outside the window the caller asked for, which would make two adjacent periods overlap and
        /// their figures fail to add up.
        /// </remarks>
        public const string VisitCte = @"
V AS (
    SELECT h.session_id,
           MIN(h.hit_timestamp)     AS FirstHit,
           MAX(h.hit_timestamp)     AS LastHit,
           COUNT_BIG(*)             AS PageViews,
           SUM(h.seconds_on_page)   AS Seconds
    FROM H AS h
    WHERE h.session_id IS NOT NULL
    GROUP BY h.session_id
)";

        /// <summary>Buckets a datetime column to the Monday on or before it.</summary>
        public static string WeekBucket(string column)
        {
            return $"DATEADD(DAY, -(DATEDIFF(DAY, 0, {column}) % 7), CAST({column} AS date))";
        }

        /// <summary>Day of week as 0 = Monday ... 6 = Sunday, independent of DATEFIRST.</summary>
        public static string DayOfWeek(string column)
        {
            return $"(DATEDIFF(DAY, 0, {column}) % 7)";
        }

        /// <summary>True when a visit started outside the assumed working window, or at the weekend.</summary>
        public static string OutOfHours(string column)
        {
            return $"(CASE WHEN DATEPART(HOUR, {column}) < @workStart OR DATEPART(HOUR, {column}) >= @workEnd"
                + $" OR {DayOfWeek(column)} >= 5 THEN 1 ELSE 0 END)";
        }

        /// <summary>
        /// The title to show for a page: the one carried by that page's most recent TITLED hit in
        /// the window.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is a "max by" aggregate. The sort key (<c>hit_timestamp</c>) and the value
        /// (<c>page_title_id</c>) are concatenated into one binary, <c>MAX</c> picks the pair with the
        /// latest timestamp, and the value is sliced back out. It costs one aggregate over rows that
        /// are already being grouped, rather than the per-row <c>OUTER APPLY</c> into <c>dbo.hits</c>
        /// that the obvious "latest title" formulation needs.
        /// </para>
        /// <para>
        /// <c>MAX(page_title_id)</c> is NOT equivalent and was the previous, wrong, implementation.
        /// Lookup ids order by when a title string was first seen anywhere in the tenant, not by when
        /// this URL used it: renaming a page to a title another page already introduced earlier gives
        /// the new title a LOWER id, so <c>MAX</c> keeps returning the old name indefinitely.
        /// </para>
        /// <para>
        /// Untitled hits are excluded from the aggregate rather than mapped to a sentinel. A hit with
        /// no <c>page_title_id</c> is a MISSING OBSERVATION, not a rename to "no name": the tracker
        /// can fire before <c>document.title</c> resolves. Letting such a hit win - which a sentinel
        /// does whenever it is the newest, and also on any timestamp tie, since the unsigned byte
        /// comparison ranks <c>0xFFFFFFFF</c> above every real id - would drop a page that has a
        /// perfectly good known title back to displaying its raw URL. The result is NULL only when NO
        /// hit in the window carried a title, which is the one case where there is genuinely nothing
        /// to show.
        /// </para>
        /// <para>
        /// The binary ordering is only chronological because <c>datetime</c>'s first four bytes are a
        /// day count that is positive for any date after 1900, which every page hit is.
        /// </para>
        /// </remarks>
        public const string LatestTitleId =
            "CAST(SUBSTRING(MAX(CASE WHEN h.page_title_id IS NOT NULL THEN "
            + "CAST(h.hit_timestamp AS binary(8)) + CAST(h.page_title_id AS binary(4)) END"
            + "), 9, 4) AS int)";

        /// <summary>
        /// <see cref="LatestTitleId"/> for the <c>Ordered</c> CTEs, which alias the hit rows as
        /// <c>o</c>. Those CTEs must project <c>hit_timestamp</c> for this to resolve.
        /// </summary>
        public const string LatestTitleIdOrdered =
            "CAST(SUBSTRING(MAX(CASE WHEN o.page_title_id IS NOT NULL THEN "
            + "CAST(o.hit_timestamp AS binary(8)) + CAST(o.page_title_id AS binary(4)) END"
            + "), 9, 4) AS int)";

        #endregion

        #region Overview

        /// <summary>Tenant-wide totals for the Overview tab's headline figures.</summary>
        public const string OverviewKpis = @"
WITH" + HitWindowCte + @",
" + VisitCte + @"
SELECT
    (SELECT COUNT_BIG(*) FROM H)                                              AS PageViews,
    (SELECT CAST(ISNULL(SUM(v.PageViews), 0) AS bigint) FROM V AS v)          AS VisitPageViews,
    (SELECT COUNT_BIG(*) FROM (SELECT DISTINCT h.session_id, h.url_id FROM H AS h
                               WHERE h.session_id IS NOT NULL) AS upv)        AS UniquePageViews,
    (SELECT COUNT_BIG(*) FROM V)                                              AS Visits,
    (SELECT COUNT(DISTINCT s.user_id)
       FROM V AS v INNER JOIN dbo.sessions AS s ON s.id = v.session_id)       AS Visitors,
    (SELECT COUNT(DISTINCT s.user_id)
       FROM V AS v
       INNER JOIN dbo.sessions AS s ON s.id = v.session_id
       INNER JOIN dbo.users AS u ON u.id = s.user_id
      WHERE " + EnabledUsersPredicate + @")                                   AS EnabledVisitors,
    (SELECT COUNT(*) FROM dbo.users AS u WHERE " + EnabledUsersPredicate + @") AS KnownUsers,
    (SELECT COUNT(DISTINCT h.url_id) FROM H AS h)                             AS UniquePages,
    (SELECT COUNT(DISTINCT h.web_id) FROM H AS h WHERE h.web_id IS NOT NULL)  AS Sites,
    (SELECT CAST(ISNULL(SUM(CASE WHEN v.PageViews = 1 THEN 1 ELSE 0 END), 0) AS bigint) FROM V AS v) AS Bounces,
    (SELECT AVG(h.seconds_on_page) FROM H AS h WHERE h.seconds_on_page IS NOT NULL)  AS AverageSecondsOnPage,
    (SELECT AVG(h.page_load_time) FROM H AS h WHERE h.page_load_time > 0)           AS AverageLoadSeconds
OPTION (RECOMPILE);";

        /// <summary>
        /// Visitors split by whether they were seen before or after the midpoint of the window.
        /// </summary>
        /// <remarks>
        /// This is a proxy for new versus returning, not a fact. Anyone whose first-ever visit predates
        /// the window is indistinguishable here from someone who simply did not visit in the first half
        /// - so a "new" visitor means "new to this window". Establishing genuine first-ever visits
        /// would mean scanning the whole of <c>dbo.hits</c> with no date predicate, which is exactly the
        /// query this page must never run.
        /// </remarks>
        public const string OverviewVisitorHalves = @"
WITH" + HitWindowCte + @",
PerUser AS (
    SELECT s.user_id,
           MAX(CASE WHEN h.hit_timestamp <  @midpoint THEN 1 ELSE 0 END) AS InFirstHalf,
           MAX(CASE WHEN h.hit_timestamp >= @midpoint THEN 1 ELSE 0 END) AS InSecondHalf
    FROM H AS h
    INNER JOIN dbo.sessions AS s ON s.id = h.session_id
    GROUP BY s.user_id
)
SELECT
    ISNULL(SUM(CASE WHEN p.InFirstHalf = 0 AND p.InSecondHalf = 1 THEN 1 ELSE 0 END), 0) AS NewVisitors,
    ISNULL(SUM(CASE WHEN p.InFirstHalf = 1 AND p.InSecondHalf = 1 THEN 1 ELSE 0 END), 0) AS ReturningVisitors,
    ISNULL(SUM(CASE WHEN p.InFirstHalf = 1 AND p.InSecondHalf = 0 THEN 1 ELSE 0 END), 0) AS LapsedVisitors
FROM PerUser AS p
OPTION (RECOMPILE);";

        /// <summary>Weekly page views, and the visits/visitors/bounces of the visits that started that week.</summary>
        /// <remarks>
        /// Visits are attributed to the week they STARTED, so every visit is counted exactly once and
        /// the weekly visit totals add up to the window's total. Page views are attributed to the week
        /// they happened in, which is what a traffic chart is asking. The two can therefore disagree
        /// slightly at a week boundary for a visit that spans midnight on a Sunday - correctly.
        /// </remarks>
        public const string OverviewTrend = @"
WITH" + HitWindowCte + @",
" + VisitCte + @",
ViewWeeks AS (
    SELECT " + "DATEADD(DAY, -(DATEDIFF(DAY, 0, h.hit_timestamp) % 7), CAST(h.hit_timestamp AS date))" + @" AS WeekStart,
           COUNT_BIG(*) AS PageViews
    FROM H AS h
    GROUP BY " + "DATEADD(DAY, -(DATEDIFF(DAY, 0, h.hit_timestamp) % 7), CAST(h.hit_timestamp AS date))" + @"
),
VisitWeeks AS (
    SELECT " + "DATEADD(DAY, -(DATEDIFF(DAY, 0, v.FirstHit) % 7), CAST(v.FirstHit AS date))" + @" AS WeekStart,
           COUNT_BIG(*)                 AS Visits,
           COUNT(DISTINCT s.user_id)    AS Visitors,
           CAST(ISNULL(SUM(CASE WHEN v.PageViews = 1 THEN 1 ELSE 0 END), 0) AS bigint) AS Bounces
    FROM V AS v
    INNER JOIN dbo.sessions AS s ON s.id = v.session_id
    GROUP BY " + "DATEADD(DAY, -(DATEDIFF(DAY, 0, v.FirstHit) % 7), CAST(v.FirstHit AS date))" + @"
),
SearchWeeks AS (
    SELECT " + "DATEADD(DAY, -(DATEDIFF(DAY, 0, se.date_time) % 7), CAST(se.date_time AS date))" + @" AS WeekStart,
           COUNT_BIG(*) AS Searches
    FROM dbo.searches AS se
    WHERE se.date_time >= @from AND se.date_time < @to
    GROUP BY " + "DATEADD(DAY, -(DATEDIFF(DAY, 0, se.date_time) % 7), CAST(se.date_time AS date))" + @"
)
SELECT weeks.WeekStart,
       ISNULL(vw.PageViews, 0) AS PageViews,
       ISNULL(vs.Visits, 0)    AS Visits,
       ISNULL(vs.Visitors, 0)  AS Visitors,
       CAST(ISNULL(vs.Bounces, 0) AS bigint)   AS Bounces,
       ISNULL(sw.Searches, 0)  AS Searches
FROM (
    SELECT WeekStart FROM ViewWeeks
    UNION
    SELECT WeekStart FROM VisitWeeks
    UNION
    SELECT WeekStart FROM SearchWeeks
) AS weeks
LEFT JOIN ViewWeeks   AS vw ON vw.WeekStart = weeks.WeekStart
LEFT JOIN VisitWeeks  AS vs ON vs.WeekStart = weeks.WeekStart
LEFT JOIN SearchWeeks AS sw ON sw.WeekStart = weeks.WeekStart
ORDER BY weeks.WeekStart
OPTION (RECOMPILE);";

        /// <summary>Visitors by the number of distinct days on which they visited.</summary>
        public const string VisitorActiveDays = @"
WITH" + HitWindowCte + @",
PerUser AS (
    SELECT s.user_id, COUNT(DISTINCT CAST(h.hit_timestamp AS date)) AS ActiveDays
    FROM H AS h
    INNER JOIN dbo.sessions AS s ON s.id = h.session_id
    GROUP BY s.user_id
)
SELECT p.ActiveDays, COUNT_BIG(*) AS Visitors
FROM PerUser AS p
GROUP BY p.ActiveDays
ORDER BY p.ActiveDays
OPTION (RECOMPILE);";

        /// <summary>Visits by the number of pages they saw - the raw input to bounce rate and depth.</summary>
        public const string VisitDepth = @"
WITH" + HitWindowCte + @",
" + VisitCte + @"
SELECT v.PageViews AS Pages, COUNT_BIG(*) AS Visits, SUM(v.Seconds) AS Seconds
FROM V AS v
GROUP BY v.PageViews
ORDER BY v.PageViews
OPTION (RECOMPILE);";

        /// <summary>
        /// Page views and visit starts per (day of week, hour of day) cell, in UTC.
        /// </summary>
        /// <remarks>
        /// Visits are counted where they STARTED rather than where they were active, so the cells sum
        /// to the window's total visits. Counting "visits active in this hour" would double-count a
        /// visit that spans an hour boundary, and the day-of-week and popular-hours views derived from
        /// this grid would then total more than the number of visits that happened.
        /// </remarks>
        public const string Heatmap = @"
WITH" + HitWindowCte + @",
" + VisitCte + @",
ViewCells AS (
    SELECT " + "(DATEDIFF(DAY, 0, h.hit_timestamp) % 7)" + @" AS [Day],
           DATEPART(HOUR, h.hit_timestamp)                    AS [Hour],
           COUNT_BIG(*)                                       AS PageViews
    FROM H AS h
    GROUP BY " + "(DATEDIFF(DAY, 0, h.hit_timestamp) % 7)" + @", DATEPART(HOUR, h.hit_timestamp)
),
VisitCells AS (
    SELECT " + "(DATEDIFF(DAY, 0, v.FirstHit) % 7)" + @" AS [Day],
           DATEPART(HOUR, v.FirstHit)                    AS [Hour],
           COUNT_BIG(*)                                  AS Visits
    FROM V AS v
    GROUP BY " + "(DATEDIFF(DAY, 0, v.FirstHit) % 7)" + @", DATEPART(HOUR, v.FirstHit)
)
SELECT cells.[Day], cells.[Hour],
       ISNULL(vc.PageViews, 0) AS PageViews,
       ISNULL(sc.Visits, 0)    AS Visits
FROM (
    SELECT [Day], [Hour] FROM ViewCells
    UNION
    SELECT [Day], [Hour] FROM VisitCells
) AS cells
LEFT JOIN ViewCells  AS vc ON vc.[Day] = cells.[Day] AND vc.[Hour] = cells.[Hour]
LEFT JOIN VisitCells AS sc ON sc.[Day] = cells.[Day] AND sc.[Hour] = cells.[Hour]
ORDER BY cells.[Day], cells.[Hour]
OPTION (RECOMPILE);";

        #endregion

        #region Sites

        /// <summary>
        /// Traffic per SharePoint web, for the site leaderboards on Overview, Visits and Pages.
        /// </summary>
        /// <remarks>
        /// Ranked by the measure the caller actually displays. A leaderboard that selects its top N
        /// by page views and then prints visit counts can omit the site with the MOST visits - a busy
        /// site with shallow sessions loses to a quiet one with deep ones - and is not even sorted by
        /// the number on screen.
        /// </remarks>
        public static string BySite(bool rankByVisits)
        {
            var order = rankByVisits
                ? "g.Visits DESC, g.PageViews DESC, g.web_id ASC"
                : "g.PageViews DESC, g.Visits DESC, g.web_id ASC";

            return @"
WITH" + HitWindowCte + @",
Grouped AS (
    SELECT h.web_id,
           COUNT_BIG(*)                 AS PageViews,
           COUNT(DISTINCT h.session_id) AS Visits
    FROM H AS h
    WHERE h.web_id IS NOT NULL
    GROUP BY h.web_id
),
Upv AS (
    SELECT d.web_id, COUNT_BIG(*) AS UniquePageViews
    FROM (SELECT DISTINCT h.web_id, h.session_id, h.url_id
          FROM H AS h
          WHERE h.web_id IS NOT NULL AND h.session_id IS NOT NULL) AS d
    GROUP BY d.web_id
),
Visitors AS (
    SELECT h.web_id, COUNT(DISTINCT s.user_id) AS Visitors
    FROM H AS h
    INNER JOIN dbo.sessions AS s ON s.id = h.session_id
    WHERE h.web_id IS NOT NULL
    GROUP BY h.web_id
)
SELECT TOP (@top)
       CAST(CASE
            WHEN COUNT(*) OVER (PARTITION BY ISNULL(w.title, w.url_base)) > 1
            THEN ISNULL(w.title, w.url_base) + ' (' + w.url_base + ')'
            ELSE ISNULL(w.title, w.url_base)
       END AS nvarchar(300))                              AS Name,
       CAST(w.url_base AS nvarchar(500))                  AS Url,
       g.PageViews,
       ISNULL(up.UniquePageViews, 0)                      AS UniquePageViews,
       CAST(g.Visits AS bigint)                           AS Visits,
       ISNULL(vi.Visitors, 0)                             AS Visitors
FROM Grouped AS g
INNER JOIN dbo.webs AS w ON w.id = g.web_id
LEFT JOIN Upv AS up ON up.web_id = g.web_id
LEFT JOIN Visitors AS vi ON vi.web_id = g.web_id
ORDER BY " + order + @"
OPTION (RECOMPILE);";
        }

        /// <summary>
        /// Weekly PAGE VIEWS for each of the busiest sites, for the stacked site-over-time chart.
        /// </summary>
        /// <remarks>
        /// Sites outside the top N are rolled into "(other sites)" and hits with no web into
        /// "(unknown site)", so the stack height is total page views for the week. An INNER JOIN to the
        /// top-N list would silently drop the tail, and a stack that omits the tail reads as traffic
        /// falling when only its distribution changed.
        /// <para>
        /// Banding is keyed on web_id, not on the title: SharePoint web titles are not unique, and
        /// grouping by title merges two unrelated "Team Site" webs into a single series. The title is
        /// resolved once per band for display, and disambiguated with the URL when it repeats.
        /// </para>
        /// </remarks>
        public const string SiteOverTime = @"
WITH" + HitWindowCte + @",
TopSites AS (
    SELECT TOP (@top) h.web_id
    FROM H AS h
    WHERE h.web_id IS NOT NULL
    GROUP BY h.web_id
    ORDER BY COUNT_BIG(*) DESC, h.web_id ASC
),
TopNames AS (
    SELECT t.web_id,
           CAST(CASE
                WHEN COUNT(*) OVER (PARTITION BY ISNULL(w.title, w.url_base)) > 1
                THEN ISNULL(w.title, w.url_base) + ' (' + w.url_base + ')'
                ELSE ISNULL(w.title, w.url_base)
           END AS nvarchar(300)) AS Name
    FROM TopSites AS t
    INNER JOIN dbo.webs AS w ON w.id = t.web_id
),
Banded AS (
    SELECT " + "DATEADD(DAY, -(DATEDIFF(DAY, 0, h.hit_timestamp) % 7), CAST(h.hit_timestamp AS date))" + @" AS WeekStart,
           CAST(CASE
                WHEN h.web_id IS NULL THEN '(unknown site)'
                WHEN n.web_id IS NULL THEN '(other sites)'
                ELSE n.Name
           END AS nvarchar(300)) AS Name
    FROM H AS h
    LEFT JOIN TopNames AS n ON n.web_id = h.web_id
)
SELECT b.WeekStart, b.Name, COUNT_BIG(*) AS Count
FROM Banded AS b
GROUP BY b.WeekStart, b.Name
ORDER BY b.WeekStart, b.Name
OPTION (RECOMPILE);";

        #endregion

        #region Visits tab

        /// <summary>Visit counts, the first and last visit start hour, and the out-of-hours share.</summary>
        public const string VisitKpis = @"
WITH" + HitWindowCte + @",
" + VisitCte + @"
SELECT
    COUNT_BIG(*)                                                    AS Visits,
    (SELECT COUNT(DISTINCT s.user_id)
       FROM V AS v2 INNER JOIN dbo.sessions AS s ON s.id = v2.session_id) AS Visitors,
    (SELECT COUNT(DISTINCT h.url_id) FROM H AS h)                   AS UniquePages,
    MIN(DATEPART(HOUR, v.FirstHit))                                 AS EarliestVisitHour,
    MAX(DATEPART(HOUR, v.FirstHit))                                 AS LatestVisitHour,
    ISNULL(SUM(CAST(" + "(CASE WHEN DATEPART(HOUR, v.FirstHit) < @workStart OR DATEPART(HOUR, v.FirstHit) >= @workEnd OR (DATEDIFF(DAY, 0, v.FirstHit) % 7) >= 5 THEN 1 ELSE 0 END)" + @" AS bigint)), 0) AS OutOfHoursVisits
FROM V AS v
OPTION (RECOMPILE);";

        /// <summary>Visits, visitors and page views per page, for the Visits tab's page leaderboard.</summary>
        public const string VisitsByPage = @"
WITH" + HitWindowCte + @",
Grouped AS (
    SELECT h.url_id,
           " + LatestTitleId + @" AS TitleId,
           COUNT(DISTINCT h.session_id) AS Visits,
           COUNT_BIG(*)                 AS PageViews
    FROM H AS h
    GROUP BY h.url_id
)
SELECT TOP (@top)
       CAST(ISNULL(pt.title, u.full_url) AS nvarchar(300)) AS Name,
       CAST(g.Visits AS bigint)                            AS Count
FROM Grouped AS g
INNER JOIN dbo.urls AS u ON u.id = g.url_id
LEFT JOIN dbo.page_titles AS pt ON pt.id = g.TitleId
ORDER BY g.Visits DESC, g.PageViews DESC, g.url_id ASC
OPTION (RECOMPILE);";

        /// <summary>Visits and page views per client attribute (browser, OS or device).</summary>
        /// <remarks>
        /// One statement shaped by its lookup table rather than three near-identical constants, so the
        /// three cannot drift. The lookup name and its key column are supplied by the call site from a
        /// fixed set - they are never taken from a request - so no user input reaches the SQL text.
        ///
        /// <para>
        /// Ranked by the measure the caller displays. Selecting the top N by page views and then
        /// printing visit counts can omit the browser or device with the MOST visits, and leaves the
        /// list not even sorted by the number on screen.
        /// </para>
        /// </remarks>
        public static string ByClientAttribute(
            string lookupTable,
            string keyColumn,
            string nameColumn,
            bool rankByVisits = false)
        {
            var order = rankByVisits
                ? "g.Visits DESC, g.PageViews DESC, g.LookupId ASC"
                : "g.PageViews DESC, g.Visits DESC, g.LookupId ASC";

            return @"
WITH" + HitWindowCte + @",
Grouped AS (
    SELECT h." + keyColumn + @" AS LookupId,
           COUNT_BIG(*)                  AS PageViews,
           COUNT(DISTINCT h.session_id)  AS Visits,
           AVG(CASE WHEN h.page_load_time > 0 THEN h.page_load_time END) AS AverageLoadSeconds,
           AVG(h.seconds_on_page)        AS AverageSecondsOnPage
    FROM H AS h
    WHERE h." + keyColumn + @" IS NOT NULL
    GROUP BY h." + keyColumn + @"
),
Visitors AS (
    SELECT h." + keyColumn + @" AS LookupId, COUNT(DISTINCT s.user_id) AS Visitors
    FROM H AS h
    INNER JOIN dbo.sessions AS s ON s.id = h.session_id
    WHERE h." + keyColumn + @" IS NOT NULL
    GROUP BY h." + keyColumn + @"
)
SELECT TOP (@top)
       CAST(lk." + nameColumn + @" AS nvarchar(200)) AS Name,
       g.PageViews,
       CAST(g.Visits AS bigint)                      AS Visits,
       ISNULL(vi.Visitors, 0)                        AS Visitors,
       g.AverageLoadSeconds,
       g.AverageSecondsOnPage
FROM Grouped AS g
INNER JOIN dbo." + lookupTable + @" AS lk ON lk.id = g.LookupId
LEFT JOIN Visitors AS vi ON vi.LookupId = g.LookupId
ORDER BY " + order + @"
OPTION (RECOMPILE);";
        }

        #endregion

        #region Pages tab

        /// <summary>Page-level totals, entries, exits and bounces - the backbone of the Pages tab.</summary>
        /// <remarks>
        /// Entry, exit and bounce are computed from the same ordered pass as the totals rather than in
        /// three separate statements. A page's bounce rate has to be its own bounces over its own
        /// entries, and computing those in different queries invites them to be joined on different
        /// row sets and quietly produce rates over 100%.
        ///
        /// <para>
        /// The ordered pass needs a visit to order within, so page views with no session are excluded
        /// here. The importer sets one on every hit it writes, so this only affects rows that arrived
        /// without a session id - and those genuinely cannot be placed in a journey.
        /// </para>
        /// </remarks>
        public const string PageStats = @"
WITH" + HitWindowCte + @",
Ordered AS (
    SELECT h.session_id, h.url_id, h.page_title_id, h.web_id, h.seconds_on_page, h.page_load_time, h.hit_timestamp,
           ROW_NUMBER() OVER (PARTITION BY h.session_id ORDER BY h.hit_timestamp ASC,  h.id ASC)  AS FirstSeq,
           ROW_NUMBER() OVER (PARTITION BY h.session_id ORDER BY h.hit_timestamp DESC, h.id DESC) AS LastSeq,
           COUNT(*) OVER (PARTITION BY h.session_id)                                              AS VisitPages
    FROM H AS h
    WHERE h.session_id IS NOT NULL
)
SELECT TOP (@top)
       CAST(ISNULL(pt.title, u.full_url) AS nvarchar(300)) AS Title,
       CAST(u.full_url AS nvarchar(850))                   AS Url,
       CAST(ISNULL(w.title, w.url_base) AS nvarchar(300))  AS Site,
       g.PageViews,
       g.UniquePageViews,
       0                                                   AS Visitors,
       g.AverageSecondsOnPage,
       g.AverageLoadSeconds,
       g.Entries,
       g.Exits,
       g.Bounces
FROM (
    SELECT o.url_id,
           " + LatestTitleIdOrdered + @"                                        AS TitleId,
           MAX(o.web_id)                                                         AS WebId,
           COUNT_BIG(*)                                                          AS PageViews,
           CAST(COUNT(DISTINCT o.session_id) AS bigint)                           AS UniquePageViews,
           AVG(o.seconds_on_page)                                                AS AverageSecondsOnPage,
           AVG(CASE WHEN o.page_load_time > 0 THEN o.page_load_time END)        AS AverageLoadSeconds,
           CAST(SUM(CASE WHEN o.FirstSeq = 1 THEN 1 ELSE 0 END) AS bigint)       AS Entries,
           CAST(SUM(CASE WHEN o.LastSeq  = 1 THEN 1 ELSE 0 END) AS bigint)       AS Exits,
           CAST(SUM(CASE WHEN o.FirstSeq = 1 AND o.VisitPages = 1 THEN 1 ELSE 0 END) AS bigint) AS Bounces
    FROM Ordered AS o
    GROUP BY o.url_id
) AS g
INNER JOIN dbo.urls AS u ON u.id = g.url_id
LEFT JOIN dbo.page_titles AS pt ON pt.id = g.TitleId
LEFT JOIN dbo.webs AS w ON w.id = g.WebId
ORDER BY g.PageViews DESC, g.url_id ASC
OPTION (RECOMPILE);";

        /// <summary>The same page statistics, ranked so the slowest qualifying pages come first.</summary>
        /// <remarks>
        /// The view floor counts views that actually CARRY a load time, not all views. Page load time
        /// is optional telemetry, so a page opened fifty times with one timed 20-second load would
        /// otherwise clear a floor of five on its untimed views and top the table on a single
        /// measurement - exactly the "noise presented as a finding" the floor exists to prevent.
        /// </remarks>
        public const string SlowestPages = @"
WITH" + HitWindowCte + @",
Grouped AS (
    SELECT h.url_id,
           " + LatestTitleId + @" AS TitleId,
           MAX(h.web_id)          AS WebId,
           COUNT_BIG(*)           AS PageViews,
           SUM(CASE WHEN h.page_load_time > 0 THEN 1 ELSE 0 END) AS MeasuredPageViews,
           COUNT(DISTINCT h.session_id) AS UniquePageViews,
           AVG(h.seconds_on_page) AS AverageSecondsOnPage,
           AVG(CASE WHEN h.page_load_time > 0 THEN h.page_load_time END) AS AverageLoadSeconds
    FROM H AS h
    GROUP BY h.url_id
)
SELECT TOP (@top)
       CAST(ISNULL(pt.title, u.full_url) AS nvarchar(300)) AS Title,
       CAST(u.full_url AS nvarchar(850))                   AS Url,
       CAST(ISNULL(w.title, w.url_base) AS nvarchar(300))  AS Site,
       g.PageViews,
       CAST(g.UniquePageViews AS bigint)                   AS UniquePageViews,
       0                                                   AS Visitors,
       g.AverageSecondsOnPage,
       g.AverageLoadSeconds,
       CAST(0 AS bigint)                                   AS Entries,
       CAST(0 AS bigint)                                   AS Exits,
       CAST(0 AS bigint)                                   AS Bounces
FROM Grouped AS g
INNER JOIN dbo.urls AS u ON u.id = g.url_id
LEFT JOIN dbo.page_titles AS pt ON pt.id = g.TitleId
LEFT JOIN dbo.webs AS w ON w.id = g.WebId
WHERE g.MeasuredPageViews >= @minViews AND g.AverageLoadSeconds IS NOT NULL
ORDER BY g.AverageLoadSeconds DESC, g.url_id ASC
OPTION (RECOMPILE);";

        /// <summary>The least-viewed pages, and how many pages fall under the quiet-page ceiling.</summary>
        /// <remarks>
        /// Ordered by page views ascending and then by URL so the list is stable between refreshes -
        /// a pruning backlog that reshuffles every time it is opened is not a backlog.
        /// </remarks>
        public const string QuietPages = @"
WITH" + HitWindowCte + @",
Grouped AS (
    SELECT h.url_id,
           " + LatestTitleId + @" AS TitleId,
           MAX(h.web_id)          AS WebId,
           COUNT_BIG(*)           AS PageViews,
           COUNT(DISTINCT h.session_id) AS UniquePageViews,
           AVG(h.seconds_on_page) AS AverageSecondsOnPage,
           AVG(CASE WHEN h.page_load_time > 0 THEN h.page_load_time END) AS AverageLoadSeconds
    FROM H AS h
    GROUP BY h.url_id
)
SELECT TOP (@top)
       CAST(ISNULL(pt.title, u.full_url) AS nvarchar(300)) AS Title,
       CAST(u.full_url AS nvarchar(850))                   AS Url,
       CAST(ISNULL(w.title, w.url_base) AS nvarchar(300))  AS Site,
       g.PageViews,
       CAST(g.UniquePageViews AS bigint)                   AS UniquePageViews,
       0                                                   AS Visitors,
       g.AverageSecondsOnPage,
       g.AverageLoadSeconds,
       CAST(0 AS bigint)                                   AS Entries,
       CAST(0 AS bigint)                                   AS Exits,
       CAST(0 AS bigint)                                   AS Bounces
FROM Grouped AS g
INNER JOIN dbo.urls AS u ON u.id = g.url_id
LEFT JOIN dbo.page_titles AS pt ON pt.id = g.TitleId
LEFT JOIN dbo.webs AS w ON w.id = g.WebId
WHERE g.PageViews <= @quietMax
ORDER BY g.PageViews ASC, u.full_url ASC
OPTION (RECOMPILE);";

        /// <summary>
        /// The page-view distribution across pages, so concentration and the quiet-page count can be
        /// computed without returning a row per page.
        /// </summary>
        public const string PageViewDistribution = @"
WITH" + HitWindowCte + @",
Grouped AS (
    SELECT h.url_id, COUNT_BIG(*) AS PageViews
    FROM H AS h
    GROUP BY h.url_id
)
SELECT g.PageViews AS Views, COUNT_BIG(*) AS Pages
FROM Grouped AS g
GROUP BY g.PageViews
ORDER BY g.PageViews
OPTION (RECOMPILE);";

        /// <summary>Weekly page views by hour of day, so the UI can stack them by period of the day.</summary>
        public const string PeriodOverTime = @"
WITH" + HitWindowCte + @"
SELECT " + "DATEADD(DAY, -(DATEDIFF(DAY, 0, h.hit_timestamp) % 7), CAST(h.hit_timestamp AS date))" + @" AS WeekStart,
       DATEPART(HOUR, h.hit_timestamp) AS [Hour],
       COUNT_BIG(*) AS Count
FROM H AS h
GROUP BY " + "DATEADD(DAY, -(DATEDIFF(DAY, 0, h.hit_timestamp) % 7), CAST(h.hit_timestamp AS date))" + @",
         DATEPART(HOUR, h.hit_timestamp)
ORDER BY WeekStart, [Hour]
OPTION (RECOMPILE);";

        #endregion

        #region Journeys tab

        /// <summary>Entry pages, with the bounces that started there.</summary>
        public static readonly string EntryPages = EndpointPages("FirstSeq");

        /// <summary>Exit pages.</summary>
        public static readonly string ExitPages = EndpointPages("LastSeq");

        /// <summary>
        /// Entry pages ranked by bounce RATE rather than by volume.
        /// </summary>
        /// <remarks>
        /// This has to be its own query rather than a re-sort of <see cref="EntryPages"/>. That list
        /// is already truncated to the busiest N entry pages, so re-ranking it by rate can only ever
        /// reorder pages that were popular anyway - and the pages worth surfacing here are precisely
        /// the low-volume ones where nearly everyone who lands bounces. Ranking before the TOP is the
        /// whole point.
        /// <para>
        /// <c>@minViews</c> keeps a page that was entered twice and bounced twice off the top of the
        /// list; a 100% rate over a handful of visits is noise, not a finding. Pages with NO bounces
        /// are excluded outright - without that, a tenant with fewer than N qualifying pages fills
        /// the remaining slots with its busiest landing pages at a 0% rate, and the volume tie-break
        /// sorts them to the TOP of a table captioned "the pages costing you the most traffic".
        /// </para>
        /// </remarks>
        public static readonly string BouncePages = EndpointPages(
            "FirstSeq",
            havingSql: "HAVING SUM(CASE WHEN o.FirstSeq = 1 THEN 1 ELSE 0 END) >= @minViews"
                     + " AND SUM(CASE WHEN o.VisitPages = 1 THEN 1 ELSE 0 END) > 0",
            orderSql: "ORDER BY CAST(g.Bounces AS float) / NULLIF(g.Entries, 0) DESC, g.Entries DESC, g.url_id ASC");

        /// <summary>
        /// Shared shape for the entry-page and exit-page leaderboards.
        /// </summary>
        /// <remarks>
        /// <paramref name="sequenceColumn"/>, <paramref name="havingSql"/> and
        /// <paramref name="orderSql"/> are compile-time constants from this file and never come from
        /// a request.
        ///
        /// <para>
        /// Note what <c>PageViews</c> means here: the rows are already filtered to the visit's first
        /// (or last) page view, so the count is the number of ENTRIES (or exits), not the page's total
        /// traffic. The UI labels the column accordingly - it is the same model type the
        /// most-viewed-pages table uses, and showing it under a "Page views" heading would invite a
        /// reader to compare two numbers that count different things.
        /// </para>
        /// </remarks>
        private static string EndpointPages(
            string sequenceColumn,
            string havingSql = "",
            string orderSql = "ORDER BY g.PageViews DESC, g.url_id ASC")
        {
            return @"
WITH" + HitWindowCte + @",
Ordered AS (
    SELECT h.session_id, h.url_id, h.page_title_id, h.web_id, h.seconds_on_page, h.hit_timestamp,
           ROW_NUMBER() OVER (PARTITION BY h.session_id ORDER BY h.hit_timestamp ASC,  h.id ASC)  AS FirstSeq,
           ROW_NUMBER() OVER (PARTITION BY h.session_id ORDER BY h.hit_timestamp DESC, h.id DESC) AS LastSeq,
           COUNT(*) OVER (PARTITION BY h.session_id)                                              AS VisitPages
    FROM H AS h
    WHERE h.session_id IS NOT NULL
)
SELECT TOP (@top)
       CAST(ISNULL(pt.title, u.full_url) AS nvarchar(300)) AS Title,
       CAST(u.full_url AS nvarchar(850))                   AS Url,
       CAST(ISNULL(w.title, w.url_base) AS nvarchar(300))  AS Site,
       g.PageViews,
       CAST(0 AS bigint)                                   AS UniquePageViews,
       0                                                   AS Visitors,
       g.AverageSecondsOnPage,
       CAST(NULL AS float)                                 AS AverageLoadSeconds,
       g.Entries,
       g.Exits,
       g.Bounces
FROM (
    SELECT o.url_id,
           " + LatestTitleIdOrdered + @" AS TitleId,
           MAX(o.web_id)        AS WebId,
           COUNT_BIG(*)         AS PageViews,
           AVG(o.seconds_on_page) AS AverageSecondsOnPage,
           CAST(SUM(CASE WHEN o.FirstSeq = 1 THEN 1 ELSE 0 END) AS bigint) AS Entries,
           CAST(SUM(CASE WHEN o.LastSeq  = 1 THEN 1 ELSE 0 END) AS bigint) AS Exits,
           CAST(SUM(CASE WHEN o.VisitPages = 1 THEN 1 ELSE 0 END) AS bigint) AS Bounces
    FROM Ordered AS o
    WHERE o." + sequenceColumn + @" = 1
    GROUP BY o.url_id
    " + havingSql + @"
) AS g
INNER JOIN dbo.urls AS u ON u.id = g.url_id
LEFT JOIN dbo.page_titles AS pt ON pt.id = g.TitleId
LEFT JOIN dbo.webs AS w ON w.id = g.WebId
" + orderSql + @"
OPTION (RECOMPILE);";
        }

        /// <summary>The most-walked steps from one page to the next inside a visit.</summary>
        /// <remarks>
        /// A step to the SAME page is excluded. A refresh, or the tracker firing twice on a page that
        /// re-renders, would otherwise dominate this list with "Home to Home" and bury the real paths.
        /// </remarks>
        public const string Transitions = @"
WITH" + HitWindowCte + @",
Ordered AS (
    SELECT h.session_id, h.url_id,
           LEAD(h.url_id) OVER (PARTITION BY h.session_id ORDER BY h.hit_timestamp ASC, h.id ASC) AS NextUrlId
    FROM H AS h
    WHERE h.session_id IS NOT NULL
),
Steps AS (
    SELECT o.url_id AS FromUrlId, o.NextUrlId AS ToUrlId, COUNT_BIG(*) AS Count
    FROM Ordered AS o
    WHERE o.NextUrlId IS NOT NULL AND o.NextUrlId <> o.url_id
    GROUP BY o.url_id, o.NextUrlId
),
Titles AS (
    SELECT h.url_id, " + LatestTitleId + @" AS TitleId
    FROM H AS h
    GROUP BY h.url_id
)
SELECT TOP (@top)
       CAST(ISNULL(fpt.title, fu.full_url) AS nvarchar(300)) AS FromTitle,
       CAST(fu.full_url AS nvarchar(850))                    AS FromUrl,
       CAST(ISNULL(tpt.title, tu.full_url) AS nvarchar(300)) AS ToTitle,
       CAST(tu.full_url AS nvarchar(850))                    AS ToUrl,
       s.Count,
       CAST(s.Count AS float) * 100.0
           / NULLIF(SUM(s.Count) OVER (PARTITION BY s.FromUrlId), 0) AS SharePct
FROM Steps AS s
INNER JOIN dbo.urls AS fu ON fu.id = s.FromUrlId
INNER JOIN dbo.urls AS tu ON tu.id = s.ToUrlId
LEFT JOIN Titles AS ft ON ft.url_id = s.FromUrlId
LEFT JOIN Titles AS tt ON tt.url_id = s.ToUrlId
LEFT JOIN dbo.page_titles AS fpt ON fpt.id = ft.TitleId
LEFT JOIN dbo.page_titles AS tpt ON tpt.id = tt.TitleId
ORDER BY s.Count DESC, s.FromUrlId ASC, s.ToUrlId ASC
OPTION (RECOMPILE);";

        /// <summary>What visitors clicked, when the tracker's element-click capture is switched on.</summary>
        public const string ClickedElements = @"
SELECT TOP (@top)
       CAST(ISNULL(t.name, N'(untitled element)') AS nvarchar(200)) AS Name,
       COUNT_BIG(*) AS Count
FROM dbo.hits_clicked_elements AS c
LEFT JOIN dbo.hits_clicked_element_titles AS t ON t.id = c.element_title_id
WHERE c.[timestamp] >= @from AND c.[timestamp] < @to
GROUP BY CAST(ISNULL(t.name, N'(untitled element)') AS nvarchar(200))
ORDER BY COUNT_BIG(*) DESC, CAST(ISNULL(t.name, N'(untitled element)') AS nvarchar(200)) ASC
OPTION (RECOMPILE);";

        /// <summary>How many element clicks were recorded at all, so an empty list can be explained.</summary>
        public const string ClickCount = @"
SELECT COUNT_BIG(*) AS Clicks
FROM dbo.hits_clicked_elements AS c
WHERE c.[timestamp] >= @from AND c.[timestamp] < @to
OPTION (RECOMPILE);";

        #endregion

        #region Geography

        /// <summary>Traffic per place, for the country, city and province leaderboards.</summary>
        /// <remarks>
        /// <paramref name="keyColumn"/>, <paramref name="lookupTable"/> and
        /// <paramref name="nameColumn"/> are supplied from a fixed set at the call site and never come
        /// from a request.
        /// </remarks>
        public static string ByPlace(string keyColumn, string lookupTable, string nameColumn, bool withCountry)
        {
            // A city or province lookup is keyed on its NAME alone, so London, UK and London, Ontario
            // share one row. Grouping by the place id only would merge their traffic and then label
            // the result with whichever country id happened to be larger. Grouping by the pair keeps
            // them apart - which is the whole reason the UI shows a country column beside the city.
            var groupKey = withCountry ? "h." + keyColumn + ", h.country_id" : "h." + keyColumn;
            var countrySelect = withCountry
                ? "CAST(c.country_name AS nvarchar(250))"
                : "CAST(NULL AS nvarchar(250))";

            var countryJoin = withCountry
                ? "LEFT JOIN dbo.countries AS c ON c.id = g.CountryId"
                : string.Empty;

            var countryProjection = withCountry ? "h.country_id" : "CAST(NULL AS int)";
            var visitorJoin = withCountry
                ? "ON vi.PlaceId = g.PlaceId AND ISNULL(vi.CountryId, -1) = ISNULL(g.CountryId, -1)"
                : "ON vi.PlaceId = g.PlaceId";

            return @"
WITH" + HitWindowCte + @",
Grouped AS (
    SELECT h." + keyColumn + @" AS PlaceId,
           " + countryProjection + @"          AS CountryId,
           COUNT_BIG(*)                   AS PageViews,
           COUNT(DISTINCT h.session_id)   AS Visits
    FROM H AS h
    WHERE h." + keyColumn + @" IS NOT NULL
    GROUP BY " + groupKey + @"
),
Visitors AS (
    SELECT h." + keyColumn + @" AS PlaceId,
           " + countryProjection + @"          AS CountryId,
           COUNT(DISTINCT s.user_id)      AS Visitors
    FROM H AS h
    INNER JOIN dbo.sessions AS s ON s.id = h.session_id
    WHERE h." + keyColumn + @" IS NOT NULL
    GROUP BY " + groupKey + @"
)
SELECT TOP (@top)
       CAST(lk." + nameColumn + @" AS nvarchar(250)) AS Name,
       " + countrySelect + @"                        AS Country,
       g.PageViews,
       CAST(g.Visits AS bigint)                      AS Visits,
       ISNULL(vi.Visitors, 0)                        AS Visitors
FROM Grouped AS g
INNER JOIN dbo." + lookupTable + @" AS lk ON lk.id = g.PlaceId
" + countryJoin + @"
LEFT JOIN Visitors AS vi " + visitorJoin + @"
ORDER BY g.PageViews DESC, g.PlaceId ASC, ISNULL(g.CountryId, -1) ASC
OPTION (RECOMPILE);";
        }

        /// <summary>Distinct place counts and how much traffic had no location at all.</summary>
        public const string GeographyKpis = @"
WITH" + HitWindowCte + @"
SELECT
    (SELECT COUNT(DISTINCT h.country_id) FROM H AS h WHERE h.country_id IS NOT NULL)                   AS Countries,
    (SELECT COUNT_BIG(*) FROM (SELECT DISTINCT h.city_id, h.country_id FROM H AS h
                               WHERE h.city_id IS NOT NULL) AS c)                                       AS Cities,
    (SELECT COUNT_BIG(*) FROM (SELECT DISTINCT h.location_province_id, h.country_id FROM H AS h
                               WHERE h.location_province_id IS NOT NULL) AS p)                         AS Provinces,
    (SELECT COUNT_BIG(*) FROM H AS h WHERE h.country_id IS NOT NULL)                                   AS CountryPageViews,
    (SELECT COUNT_BIG(*) FROM H AS h WHERE h.city_id IS NOT NULL)                                      AS CityPageViews,
    (SELECT COUNT_BIG(*) FROM H AS h WHERE h.location_province_id IS NOT NULL)                         AS ProvincePageViews,
    (SELECT COUNT_BIG(*) FROM H AS h WHERE h.country_id IS NULL AND h.city_id IS NULL)                 AS UnknownLocationPageViews,
    (SELECT COUNT_BIG(*) FROM H AS h)                                                                  AS PageViews,
    (SELECT CAST(COUNT(DISTINCT h.session_id) AS bigint) FROM H AS h WHERE h.session_id IS NOT NULL) AS Visits,
    (SELECT COUNT(DISTINCT s.user_id) FROM H AS h INNER JOIN dbo.sessions AS s ON s.id = h.session_id) AS Visitors
OPTION (RECOMPILE);";

        /// <summary>
        /// Weekly PAGE VIEWS for each of the busiest countries.
        /// </summary>
        /// <remarks>
        /// Countries outside the top N are rolled into "(other countries)" and hits that could not be
        /// located into "(unknown country)", so the stack sums to total page views for the week rather
        /// than quietly dropping the tail.
        /// </remarks>
        public const string CountryOverTime = @"
WITH" + HitWindowCte + @",
TopCountries AS (
    SELECT TOP (@top) h.country_id
    FROM H AS h
    WHERE h.country_id IS NOT NULL
    GROUP BY h.country_id
    ORDER BY COUNT_BIG(*) DESC, h.country_id ASC
),
Banded AS (
    SELECT " + "DATEADD(DAY, -(DATEDIFF(DAY, 0, h.hit_timestamp) % 7), CAST(h.hit_timestamp AS date))" + @" AS WeekStart,
           CAST(CASE
                WHEN h.country_id IS NULL THEN '(unknown country)'
                WHEN t.country_id IS NULL THEN '(other countries)'
                ELSE ISNULL(c.country_name, '(unknown country)')
           END AS nvarchar(250)) AS Name
    FROM H AS h
    LEFT JOIN TopCountries AS t ON t.country_id = h.country_id
    LEFT JOIN dbo.countries AS c ON c.id = h.country_id
)
SELECT b.WeekStart, b.Name, COUNT_BIG(*) AS Count
FROM Banded AS b
GROUP BY b.WeekStart, b.Name
ORDER BY b.WeekStart, b.Name
OPTION (RECOMPILE);";

        #endregion

        #region Search

        /// <summary>
        /// Search totals, including the two measures that make this tab worth reading: how many visits
        /// relied on search at all, and how many searches were followed by nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// "Dead end" means no further page view in the same visit after the search, ignoring page
        /// views within <see cref="SearchDeadEndGraceSeconds"/> seconds of it. The grace window is
        /// not optional: the search results page is itself a page view fired in the same page load,
        /// so without it every search looks successful and the measure reports zero forever.
        /// </para>
        /// <para>
        /// It is a proxy for a search that did not help, not a fact. The import does not record
        /// result counts or clicks on results, so a genuine zero-result search and a search whose
        /// results were ignored look the same here. The UI says so rather than presenting it as a
        /// failure count.
        /// </para>
        /// <para>
        /// <c>dbo.searches</c> has no index on <c>date_time</c>, so this scans. It is a far smaller
        /// table than <c>dbo.hits</c> and the section has its own timeout, which is why no index is
        /// being added speculatively: an unmeasured index costs every customer an offline build on
        /// upgrade for a benefit nobody has demonstrated.
        /// </para>
        /// <para>
        /// Rows imported before migration <c>SearchImportFix</c> have a NULL <c>date_time</c> and are
        /// therefore invisible to every window. That is correct - an undated search cannot honestly be
        /// attributed to a period.
        /// </para>
        /// </remarks>
        public const string SearchKpis = @"
WITH S AS (
    SELECT se.id, se.session_id, se.search_term_id, se.date_time
    FROM dbo.searches AS se
    WHERE se.date_time >= @from AND se.date_time < @to
),
DeadEnds AS (
    SELECT s.id
    FROM S AS s
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.hits AS h
        WHERE h.session_id = s.session_id
              AND h.hit_timestamp > DATEADD(SECOND, @searchGrace, s.date_time))
),
PerVisit AS (
    SELECT s.session_id, COUNT_BIG(*) AS Searches
    FROM S AS s
    WHERE s.session_id IS NOT NULL
    GROUP BY s.session_id
)
SELECT
    (SELECT COUNT_BIG(*) FROM S)                                  AS Searches,
    (SELECT COUNT(DISTINCT s.search_term_id) FROM S AS s)         AS Terms,
    (SELECT COUNT(DISTINCT se.user_id)
       FROM S AS s INNER JOIN dbo.sessions AS se ON se.id = s.session_id) AS Searchers,
    (SELECT COUNT_BIG(*) FROM PerVisit)                           AS SessionsWithSearch,
    (SELECT CAST(ISNULL(SUM(CASE WHEN p.Searches >= @strugglingSearches THEN 1 ELSE 0 END), 0) AS bigint)
       FROM PerVisit AS p)                                        AS StrugglingVisits,
    (SELECT COUNT_BIG(*) FROM DeadEnds)                           AS DeadEndSearches
OPTION (RECOMPILE);";

        /// <summary>Search terms with their searcher counts and how often they led nowhere.</summary>
        public const string SearchTerms = @"
WITH S AS (
    SELECT se.id, se.session_id, se.search_term_id, se.date_time
    FROM dbo.searches AS se
    WHERE se.date_time >= @from AND se.date_time < @to
),
Scored AS (
    SELECT s.search_term_id,
           s.session_id,
           CASE WHEN NOT EXISTS (
                SELECT 1 FROM dbo.hits AS h
                WHERE h.session_id = s.session_id
              AND h.hit_timestamp > DATEADD(SECOND, @searchGrace, s.date_time))
                THEN 1 ELSE 0 END AS DeadEnd
    FROM S AS s
),
Grouped AS (
    SELECT sc.search_term_id,
           COUNT_BIG(*)                                          AS Searches,
           CAST(SUM(sc.DeadEnd) AS bigint)                       AS DeadEnds
    FROM Scored AS sc
    GROUP BY sc.search_term_id
),
Searchers AS (
    -- Reads S, not Scored: counting distinct users does not need the dead-end check, and
    -- selecting from Scored makes the optimiser evaluate that correlated NOT EXISTS against
    -- dbo.hits a second time for every search in the window.
    SELECT s.search_term_id, COUNT(DISTINCT se.user_id) AS Searchers
    FROM S AS s
    INNER JOIN dbo.sessions AS se ON se.id = s.session_id
    GROUP BY s.search_term_id
)
SELECT TOP (@top)
       CAST(t.search_term AS nvarchar(250)) AS Term,
       g.Searches,
       ISNULL(sr.Searchers, 0)              AS Searchers,
       g.DeadEnds
FROM Grouped AS g
INNER JOIN dbo.search_terms AS t ON t.id = g.search_term_id
LEFT JOIN Searchers AS sr ON sr.search_term_id = g.search_term_id
ORDER BY g.Searches DESC, g.search_term_id ASC
OPTION (RECOMPILE);";

        /// <summary>
        /// Terms most often followed by nothing, among terms searched often enough to be credible.
        /// </summary>
        public const string DeadEndTerms = @"
WITH S AS (
    SELECT se.id, se.session_id, se.search_term_id, se.date_time
    FROM dbo.searches AS se
    WHERE se.date_time >= @from AND se.date_time < @to
),
Scored AS (
    SELECT s.search_term_id,
           s.session_id,
           CASE WHEN NOT EXISTS (
                SELECT 1 FROM dbo.hits AS h
                WHERE h.session_id = s.session_id
              AND h.hit_timestamp > DATEADD(SECOND, @searchGrace, s.date_time))
                THEN 1 ELSE 0 END AS DeadEnd
    FROM S AS s
),
Grouped AS (
    SELECT sc.search_term_id,
           COUNT_BIG(*)                    AS Searches,
           CAST(SUM(sc.DeadEnd) AS bigint) AS DeadEnds
    FROM Scored AS sc
    GROUP BY sc.search_term_id
)
SELECT TOP (@top)
       CAST(t.search_term AS nvarchar(250)) AS Term,
       g.Searches,
       0                                    AS Searchers,
       g.DeadEnds
FROM Grouped AS g
INNER JOIN dbo.search_terms AS t ON t.id = g.search_term_id
WHERE g.Searches >= @minSearches AND g.DeadEnds > 0
ORDER BY CAST(g.DeadEnds AS float) / g.Searches DESC, g.Searches DESC, g.search_term_id ASC
OPTION (RECOMPILE);";

        /// <summary>Searches by day of week and hour of day, in UTC.</summary>
        public const string SearchesByDayHour = @"
SELECT " + "(DATEDIFF(DAY, 0, se.date_time) % 7)" + @" AS [Day],
       DATEPART(HOUR, se.date_time)                    AS [Hour],
       COUNT_BIG(*)                                    AS Count
FROM dbo.searches AS se
WHERE se.date_time >= @from AND se.date_time < @to
GROUP BY " + "(DATEDIFF(DAY, 0, se.date_time) % 7)" + @", DATEPART(HOUR, se.date_time)
ORDER BY [Day], [Hour]
OPTION (RECOMPILE);";

        /// <summary>
        /// Which site a visitor was on when they searched, taken from their last page view before it.
        /// </summary>
        /// <remarks>
        /// The import does not record where a search was launched from, so this is reconstructed. The
        /// seek is on <c>IX_FK_hits_sessions</c> once per search, which is affordable because searches
        /// are a small fraction of hits - but it IS per-row, so it has its own section and timeout.
        /// </remarks>
        public const string SearchesBySite = @"
WITH S AS (
    SELECT se.id, se.session_id, se.date_time
    FROM dbo.searches AS se
    WHERE se.date_time >= @from AND se.date_time < @to
),
Context AS (
    SELECT s.id, ctx.web_id
    FROM S AS s
    OUTER APPLY (
        SELECT TOP (1) h.web_id
        FROM dbo.hits AS h
        WHERE h.session_id = s.session_id AND h.hit_timestamp <= s.date_time
        ORDER BY h.hit_timestamp DESC, h.id DESC
    ) AS ctx
)
SELECT TOP (@top)
       CAST(CASE
            WHEN COUNT(*) OVER (PARTITION BY ISNULL(w.title, w.url_base)) > 1
            THEN ISNULL(w.title, w.url_base) + ' (' + w.url_base + ')'
            ELSE ISNULL(w.title, w.url_base)
       END AS nvarchar(300)) AS Name,
       Cnt AS Count
FROM (
    SELECT c.web_id, COUNT_BIG(*) AS Cnt
    FROM Context AS c
    WHERE c.web_id IS NOT NULL
    GROUP BY c.web_id
) AS g
INNER JOIN dbo.webs AS w ON w.id = g.web_id
ORDER BY Cnt DESC, g.web_id ASC
OPTION (RECOMPILE);";

        #endregion

        #region Technology

        /// <summary>
        /// Page views per device across EVERY device, with no <c>TOP</c>.
        /// </summary>
        /// <remarks>
        /// Separate from the device leaderboard on purpose. Mobile share has to be computed over the
        /// whole known-device population: <c>devices.device_name</c> is Application Insights'
        /// <c>client_Model</c>, which is model-specific for phones and generic for desktops, so the
        /// long tail a <c>TOP (@top)</c> leaderboard drops is disproportionately mobile. Computing
        /// the share from the leaderboard would systematically UNDER-report it - and under-report it
        /// worse on exactly the tenants with the most varied phone estate.
        /// </remarks>
        public const string DeviceTotals = @"
WITH" + HitWindowCte + @"
SELECT CAST(d.device_name AS nvarchar(200)) AS Name,
       COUNT_BIG(*)                         AS Count
FROM H AS h
INNER JOIN dbo.devices AS d ON d.id = h.device_id
WHERE h.device_id IS NOT NULL
GROUP BY CAST(d.device_name AS nvarchar(200))
OPTION (RECOMPILE);";

        /// <summary>Distinct platform counts and the unattributed page views.</summary>
        public const string TechnologyKpis = @"
WITH" + HitWindowCte + @"
SELECT
    (SELECT COUNT(DISTINCT h.agent_id) FROM H AS h WHERE h.agent_id IS NOT NULL)   AS Browsers,
    (SELECT COUNT(DISTINCT h.os_id) FROM H AS h WHERE h.os_id IS NOT NULL)         AS OperatingSystems,
    (SELECT COUNT(DISTINCT h.device_id) FROM H AS h WHERE h.device_id IS NOT NULL) AS Devices,
    (SELECT COUNT_BIG(*) FROM H AS h WHERE h.agent_id IS NULL)                     AS UnknownBrowserPageViews,
    (SELECT COUNT_BIG(*) FROM H AS h)                                              AS PageViews,
    (SELECT AVG(h.page_load_time) FROM H AS h WHERE h.page_load_time > 0)         AS AverageLoadSeconds
OPTION (RECOMPILE);";

        /// <summary>
        /// The page-load histogram, in fixed-width buckets.
        /// </summary>
        /// <remarks>
        /// See <see cref="LoadBucketSeconds"/> for why this is a histogram rather than a percentile.
        /// Loads slower than <see cref="LoadBucketCeilingSeconds"/> collapse into one overflow bucket,
        /// so a single absurd outlier cannot blow up the group cardinality.
        /// </remarks>
        public const string LoadHistogram = @"
WITH" + HitWindowCte + @"
SELECT CASE WHEN h.page_load_time >= @loadCeiling THEN @loadOverflowBucket
            ELSE CAST(FLOOR(h.page_load_time / @loadBucket) AS int) END AS Bucket,
       COUNT_BIG(*) AS Count
FROM H AS h
WHERE h.page_load_time > 0
GROUP BY CASE WHEN h.page_load_time >= @loadCeiling THEN @loadOverflowBucket
              ELSE CAST(FLOOR(h.page_load_time / @loadBucket) AS int) END
ORDER BY Bucket
OPTION (RECOMPILE);";

        /// <summary>Weekly page views by device, for the device-mix trend.</summary>
        /// <remarks>
        /// <para>
        /// Every page view is represented. The busiest devices get their own band and everything else
        /// is rolled into an explicit remainder rather than dropped, so the stack's height is the
        /// week's total page views - which is what a stacked area chart's height claims to be.
        /// </para>
        /// <para>
        /// Rolling up rather than truncating matters most for exactly the question this chart is
        /// captioned with. <c>client_Model</c> is model-specific for phones and generic for desktops,
        /// so a plain top-N would cut the fragmented mobile tail and show a responsive-design case as
        /// far weaker than the untruncated mobile-share figure above it.
        /// </para>
        /// </remarks>
        public const string DeviceOverTime = @"
WITH" + HitWindowCte + @",
TopDevices AS (
    SELECT TOP (@top) h.device_id
    FROM H AS h
    WHERE h.device_id IS NOT NULL
    GROUP BY h.device_id
    ORDER BY COUNT_BIG(*) DESC, h.device_id ASC
),
Banded AS (
    SELECT " + "DATEADD(DAY, -(DATEDIFF(DAY, 0, h.hit_timestamp) % 7), CAST(h.hit_timestamp AS date))" + @" AS WeekStart,
           CAST(CASE
                WHEN h.device_id IS NULL THEN '(unknown device)'
                WHEN t.device_id IS NULL THEN '(other devices)'
                ELSE ISNULL(d.device_name, '(unknown device)')
           END AS nvarchar(200)) AS Name
    FROM H AS h
    LEFT JOIN TopDevices AS t ON t.device_id = h.device_id
    LEFT JOIN dbo.devices AS d ON d.id = h.device_id
)
SELECT b.WeekStart, b.Name, COUNT_BIG(*) AS Count
FROM Banded AS b
GROUP BY b.WeekStart, b.Name
ORDER BY b.WeekStart, b.Name
OPTION (RECOMPILE);";

        /// <summary>The Power BI report's "by DETAIL" table: browser x device x OS x city.</summary>
        public const string TechnologyDetail = @"
WITH" + HitWindowCte + @",
Grouped AS (
    SELECT h.agent_id, h.device_id, h.os_id, h.city_id,
           COUNT_BIG(*)                 AS PageViews,
           COUNT(DISTINCT h.session_id) AS Visits,
           AVG(h.seconds_on_page)       AS AverageSecondsOnPage,
           AVG(CASE WHEN h.page_load_time > 0 THEN h.page_load_time END) AS AverageLoadSeconds
    FROM H AS h
    GROUP BY h.agent_id, h.device_id, h.os_id, h.city_id
),
Visitors AS (
    SELECT h.agent_id, h.device_id, h.os_id, h.city_id, COUNT(DISTINCT s.user_id) AS Visitors
    FROM H AS h
    INNER JOIN dbo.sessions AS s ON s.id = h.session_id
    GROUP BY h.agent_id, h.device_id, h.os_id, h.city_id
)
SELECT TOP (@top)
       CAST(ISNULL(b.browser_name, '(unknown)') AS nvarchar(200)) AS Browser,
       CAST(ISNULL(d.device_name, '(unknown)') AS nvarchar(200))  AS Device,
       CAST(ISNULL(o.os_name, '(unknown)') AS nvarchar(200))      AS OperatingSystem,
       CAST(ISNULL(c.city_name, N'(unknown)') AS nvarchar(250))   AS City,
       CAST(g.Visits AS bigint)                                   AS Visits,
       ISNULL(vi.Visitors, 0)                                     AS Visitors,
       g.PageViews,
       g.AverageSecondsOnPage,
       g.AverageLoadSeconds
FROM Grouped AS g
LEFT JOIN dbo.browsers AS b ON b.id = g.agent_id
LEFT JOIN dbo.devices AS d ON d.id = g.device_id
LEFT JOIN dbo.operating_systems AS o ON o.id = g.os_id
LEFT JOIN dbo.cities AS c ON c.id = g.city_id
LEFT JOIN Visitors AS vi
       ON ISNULL(vi.agent_id, -1)  = ISNULL(g.agent_id, -1)
      AND ISNULL(vi.device_id, -1) = ISNULL(g.device_id, -1)
      AND ISNULL(vi.os_id, -1)     = ISNULL(g.os_id, -1)
      AND ISNULL(vi.city_id, -1)   = ISNULL(g.city_id, -1)
ORDER BY g.Visits DESC, g.PageViews DESC,
         ISNULL(g.agent_id, -1) ASC, ISNULL(g.device_id, -1) ASC,
         ISNULL(g.os_id, -1) ASC, ISNULL(g.city_id, -1) ASC
OPTION (RECOMPILE);";

        #endregion

        #region Availability

        /// <summary>
        /// Whether any web-traffic data exists at all, and when it was last collected.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT windowed. The availability bar's job is to tell an admin whether the import
        /// has ever worked; answering "no hits" because the chosen window happens to be quiet would
        /// send them to debug a tracker that is running perfectly.
        /// </remarks>
        public const string LatestHit = @"
SELECT MAX(h.hit_timestamp) AS LastHitUtc
FROM dbo.hits AS h
OPTION (RECOMPILE);";

        /// <summary>Whether searches and element clicks have ever been recorded.</summary>
        public const string OptionalFeatureCounts = @"
SELECT
    (SELECT COUNT_BIG(*) FROM (SELECT TOP (1) 1 AS x FROM dbo.searches) AS s)              AS AnySearches,
    (SELECT COUNT_BIG(*) FROM (SELECT TOP (1) 1 AS x FROM dbo.hits_clicked_elements) AS c) AS AnyClicks
OPTION (RECOMPILE);";

        #endregion

        #region Describe

        /// <summary>
        /// The SQL with its parameters declared, ready to paste into SSMS.
        /// </summary>
        /// <remarks>
        /// Built from the same window the command was executed with, so the text an admin copies is
        /// the query that actually produced the figure beside it - not a re-typed approximation of it.
        /// </remarks>
        public static string Describe(string sql, WebActivityQuery query)
        {
            if (query == null) return sql;

            var lines = new List<string>
            {
                Declare("from", query.FromUtc),
                Declare("to", query.ToExclusiveUtc),
                Declare("midpoint", query.MidpointUtc),
                $"DECLARE @top int = {query.Top};",
                $"DECLARE @minViews int = {query.MinimumViews};",
                $"DECLARE @quietMax int = {WebActivityScoring.QuietPageViewCeiling};",
                $"DECLARE @minSearches int = {MinimumSearchesForDeadEndRanking};",
                $"DECLARE @strugglingSearches int = {StrugglingSearchCount};",
                $"DECLARE @searchGrace int = {SearchDeadEndGraceSeconds};",
                $"DECLARE @workStart int = {WorkingDayStartHour};",
                $"DECLARE @workEnd int = {WorkingDayEndHour};",
                Declare("loadBucket", LoadBucketSeconds),
                Declare("loadCeiling", LoadBucketCeilingSeconds),
                $"DECLARE @loadOverflowBucket int = {LoadOverflowBucket};",
            };

            return string.Join("\r\n", lines) + "\r\n" + sql;
        }

        /// <summary>Searches a term needs before it can be ranked by its dead-end rate.</summary>
        public const int MinimumSearchesForDeadEndRanking = 3;

        private static string Declare(string name, DateTime value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "DECLARE @{0} datetime = '{1:yyyy-MM-ddTHH:mm:ss}';",
                name,
                value);
        }

        private static string Declare(string name, double value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "DECLARE @{0} float = {1};",
                name,
                value);
        }

        #endregion
    }
}
