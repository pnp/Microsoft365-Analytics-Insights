using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static Common.Entities.SpoWebActivity.WebActivityRows;

namespace Common.Entities.SpoWebActivity
{
    /// <summary>
    /// Whether the page-hit table could be read, and the newest hit in it.
    /// </summary>
    /// <remarks>
    /// A tri-state, because the availability model gives DIFFERENT advice for "the tracker has never
    /// collected anything" (deploy it) and "we could not tell" (do nothing). Collapsing both into a
    /// null <see cref="LastHitUtc"/> is how an admin ends up redeploying a tracker that works because
    /// one query timed out.
    /// </remarks>
    public sealed class WebActivityCollectionStatus
    {
        /// <summary>False when the query failed, so nothing below can be trusted.</summary>
        public bool Readable { get; set; }

        /// <summary>The newest page hit of any age, or null when there are none.</summary>
        public DateTime? LastHitUtc { get; set; }
    }

    /// <summary>Loads the SharePoint web-activity page's sections.</summary>
    public interface IWebActivityStore
    {
        /// <summary>
        /// Whether page hits are readable at all, and the newest one - ignoring the reporting window.
        /// </summary>
        Task<WebActivityCollectionStatus> GetCollectionStatusAsync();

        /// <summary>
        /// Whether any search and any element click has ever been recorded, or null when unknown.
        /// </summary>
        Task<Tuple<bool, bool>> GetOptionalFeatureUseAsync();

        Task<WebActivityOverview> GetOverviewAsync(WebActivityQuery query, WebActivitySources sources);

        Task<WebActivityVisits> GetVisitsAsync(WebActivityQuery query);

        Task<WebActivityPages> GetPagesAsync(WebActivityQuery query);

        Task<WebActivityJourneys> GetJourneysAsync(WebActivityQuery query);

        Task<WebActivityGeography> GetGeographyAsync(WebActivityQuery query);

        Task<WebActivitySearch> GetSearchAsync(WebActivityQuery query);

        Task<WebActivityTechnology> GetTechnologyAsync(WebActivityQuery query);
    }

    /// <summary>
    /// Runs the SharePoint web-activity SQL and turns the raw result sets into the page's models.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every section of a tab runs as its own command, on its own connection, in parallel, with its
    /// own short timeout. A failure is captured as a <see cref="WebActivityQueryInfo.Error"/> rather
    /// than thrown, so one slow leaderboard degrades to a message beside eight working charts instead
    /// of taking the whole tab down. That matters more here than anywhere else in the product:
    /// <c>dbo.hits</c> is the largest table a busy intranet produces and only carries a date index, so
    /// a wide window genuinely can run out of time.
    /// </para>
    /// <para>
    /// Parameters are rebuilt for every command. A <see cref="SqlParameter"/> instance cannot be
    /// attached to two commands at once, so sharing one array across parallel queries would fail
    /// intermittently and only under load - exactly the bug that is hardest to find later.
    /// </para>
    /// <para>
    /// All judgement (segments, bands, bounce definitions, mobile classification) is applied here from
    /// <see cref="WebActivityScoring"/>, never in SQL, so the thresholds exist once and are unit
    /// tested.
    /// </para>
    /// </remarks>
    public sealed class SqlWebActivityStore : IWebActivityStore
    {
        private readonly IAnalyticsDbContextFactory _contextFactory;
        private readonly int _commandTimeoutSeconds;

        /// <summary>
        /// Database queries this store will run at once, across all callers in the process.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every section is its own command on its own connection, which is what lets one slow
        /// leaderboard fail without taking its tab down - but a tab has up to eleven of them, and the
        /// page issues two requests on load. Unbounded, a handful of concurrent admins would exhaust
        /// the ADO.NET pool (100 connections by default) and start timing out on connection
        /// acquisition, which surfaces as an unexplained failure rather than as a slow query.
        /// </para>
        /// <para>
        /// Six is chosen against the per-query timeout: eleven queries in two waves is at most
        /// ~50 seconds even if every one of them times out, well inside the ~230s Azure App Service
        /// request limit, while capping a single page load at six connections rather than thirteen.
        /// </para>
        /// </remarks>
        internal const int MaxConcurrentQueries = 6;

        private static readonly SemaphoreSlim QuerySlots = new SemaphoreSlim(MaxConcurrentQueries);

        public SqlWebActivityStore(IAnalyticsDbContextFactory contextFactory)
            : this(contextFactory, WebActivitySql.CommandTimeoutSeconds)
        {
        }

        public SqlWebActivityStore(IAnalyticsDbContextFactory contextFactory, int commandTimeoutSeconds)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            _commandTimeoutSeconds = commandTimeoutSeconds;
        }

        #region Availability

        /// <summary>
        /// The newest page hit, regardless of the reporting window, and whether it could be read.
        /// </summary>
        /// <remarks>
        /// Reports the failure rather than folding it into "no hits". A failed query presented to an
        /// admin as "the tracker has never collected anything" sends them to redeploy a tracker that
        /// is working perfectly, which is the opposite of useful.
        /// </remarks>
        public async Task<WebActivityCollectionStatus> GetCollectionStatusAsync()
        {
            try
            {
                using (var db = _contextFactory.Create())
                {
                    db.Database.CommandTimeout = _commandTimeoutSeconds;
                    var rows = await db.Database
                        .SqlQuery<LatestHitRow>(WebActivitySql.LatestHit)
                        .ToListAsync()
                        .ConfigureAwait(false);

                    return new WebActivityCollectionStatus
                    {
                        Readable = true,
                        LastHitUtc = AsUtc(rows.FirstOrDefault()?.LastHitUtc),
                    };
                }
            }
            catch (Exception)
            {
                return new WebActivityCollectionStatus { Readable = false };
            }
        }

        /// <inheritdoc />
        public async Task<Tuple<bool, bool>> GetOptionalFeatureUseAsync()
        {
            try
            {
                using (var db = _contextFactory.Create())
                {
                    db.Database.CommandTimeout = _commandTimeoutSeconds;
                    var rows = await db.Database
                        .SqlQuery<OptionalFeatureRow>(WebActivitySql.OptionalFeatureCounts)
                        .ToListAsync()
                        .ConfigureAwait(false);

                    var row = rows.FirstOrDefault();
                    if (row == null) return null;

                    return Tuple.Create(row.AnySearches > 0, row.AnyClicks > 0);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region Overview

        public async Task<WebActivityOverview> GetOverviewAsync(WebActivityQuery query, WebActivitySources sources)
        {
            sources = sources ?? new WebActivitySources();
            var window = WebActivityWindow.From(query);

            var kpiTask = RunAsync<OverviewKpiRow>("overview-kpis", WebActivitySql.OverviewKpis, query);
            var halvesTask = RunAsync<VisitorHalvesRow>("overview-visitors", WebActivitySql.OverviewVisitorHalves, query);
            var trendTask = RunAsync<TrendRow>("overview-trend", WebActivitySql.OverviewTrend, query);
            var segmentTask = RunAsync<ActiveDaysRow>("overview-segments", WebActivitySql.VisitorActiveDays, query);
            var depthTask = RunAsync<VisitDepthRow>("overview-depth", WebActivitySql.VisitDepth, query);
            var heatTask = RunAsync<HeatCellRow>("overview-heatmap", WebActivitySql.Heatmap, query);
            var siteTask = RunAsync<SiteRow>("overview-sites", SiteByPageViewsSql, query);
            var deviceTask = RunAsync<NamedCountRow>("overview-devices", WebActivitySql.DeviceTotals, query);
            var distributionTask = RunAsync<PageViewDistributionRow>(
                "overview-distribution", WebActivitySql.PageViewDistribution, query);
            var searchTask = RunAsync<SearchKpiRow>("overview-search", WebActivitySql.SearchKpis, query);
            var histogramTask = RunAsync<LoadBucketRow>("overview-load", WebActivitySql.LoadHistogram, query);

            await Task.WhenAll(
                kpiTask, halvesTask, trendTask, segmentTask, depthTask, heatTask, siteTask,
                deviceTask, distributionTask, searchTask, histogramTask).ConfigureAwait(false);

            var model = new WebActivityOverview { Window = window };
            model.Queries.AddRange(new[]
            {
                kpiTask.Result.Info, halvesTask.Result.Info, trendTask.Result.Info, segmentTask.Result.Info,
                depthTask.Result.Info, heatTask.Result.Info, siteTask.Result.Info, deviceTask.Result.Info,
                distributionTask.Result.Info, searchTask.Result.Info, histogramTask.Result.Info,
            });

            var kpi = kpiTask.Result.Rows.FirstOrDefault() ?? new OverviewKpiRow();
            var halves = halvesTask.Result.Rows.FirstOrDefault() ?? new VisitorHalvesRow();
            var mobilePct = MobileSharePct(deviceTask.Result.Rows);

            model.Kpis = new WebActivityOverviewKpis
            {
                PageViews = kpi.PageViews,
                UniquePageViews = kpi.UniquePageViews,
                Visits = kpi.Visits,
                Visitors = kpi.Visitors,
                KnownUsers = kpi.KnownUsers,
                DirectoryImported = sources.UserMetadata,

                // Reach is the enabled-directory visitors over the enabled directory. Using the raw
                // visitor count as the numerator lets a user who visited and was later disabled push
                // the figure past 100%, and without the directory import the denominator is just
                // "people an importer has already seen", which makes the whole ratio circular.
                ReachPct = sources.UserMetadata && kpi.KnownUsers > 0
                    ? (double?)WebActivityScoring.Percentage(kpi.EnabledVisitors, kpi.KnownUsers)
                    : null,

                UniquePages = kpi.UniquePages,
                Sites = kpi.Sites,
                PagesPerVisit = kpi.Visits > 0 ? (double)kpi.PageViews / kpi.Visits : 0,
                BouncePct = WebActivityScoring.Percentage(kpi.Bounces, kpi.Visits),
                AverageSecondsOnPage = kpi.AverageSecondsOnPage,
                AverageLoadSeconds = kpi.AverageLoadSeconds,
                NewVisitors = halves.NewVisitors,
                ReturningVisitors = halves.ReturningVisitors,
                MobilePageViewPct = mobilePct,
            };

            model.Trend = BuildTrend(trendTask.Result.Rows);
            model.VisitorSegments = BuildVisitorSegments(segmentTask.Result.Rows, query.Days);
            model.VisitDepth = BuildDepthBuckets(depthTask.Result.Rows);
            model.Heatmap = heatTask.Result.Rows
                .Select(r => new WebActivityHeatCell
                {
                    Day = r.Day,
                    Hour = r.Hour,
                    PageViews = r.PageViews,
                    Visits = r.Visits,
                })
                .ToList();

            model.TopSites = WithShare(
                siteTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.PageViews }),
                kpi.PageViews);

            var search = searchTask.Result.Rows.FirstOrDefault() ?? new SearchKpiRow();

            model.Judgements = WebActivityScoring.Judgements(new WebActivityJudgementInputs
            {
                WebTrafficAvailable = sources.WebTraffic,
                SearchAvailable = search.Searches > 0,
                ConfigurationReadable = sources.Readable,
                DirectoryImported = sources.UserMetadata,
                PageViews = kpi.PageViews,
                Visits = kpi.Visits,
                Visitors = kpi.Visitors,
                EnabledVisitors = kpi.EnabledVisitors,
                KnownUsers = kpi.KnownUsers,
                UniquePages = kpi.UniquePages,
                BouncePct = model.Kpis.BouncePct,
                PagesPerVisit = model.Kpis.PagesPerVisit,
                AverageLoadSeconds = kpi.AverageLoadSeconds,

                // The slow tail, not just the mean. A 1.2s average next to a 12s p95 is a fast site
                // for most people and an unusable one for a minority, and only the second number
                // matches what that minority reports.
                P95LoadSeconds = PercentileSeconds(histogramTask.Result.Rows),

                SessionsWithSearch = search.SessionsWithSearch,
                TopDecilePagePct = TopDecilePageShare(distributionTask.Result.Rows),
                MobilePageViewPct = mobilePct,
            });

            return model;
        }

        #endregion

        #region Visits

        public async Task<WebActivityVisits> GetVisitsAsync(WebActivityQuery query)
        {
            var window = WebActivityWindow.From(query);

            var kpiTask = RunAsync<VisitKpiRow>("visits-kpis", WebActivitySql.VisitKpis, query);
            var trendTask = RunAsync<TrendRow>("visits-trend", WebActivitySql.OverviewTrend, query);
            var siteTask = RunAsync<SiteRow>("visits-sites", SiteByVisitsSql, query);
            var pageTask = RunAsync<NamedCountRow>("visits-pages", WebActivitySql.VisitsByPage, query);
            var deviceTask = RunAsync<PlatformRow>("visits-devices", DeviceByVisitsSql, query);
            var browserTask = RunAsync<PlatformRow>("visits-browsers", BrowserByVisitsSql, query);
            var heatTask = RunAsync<HeatCellRow>("visits-heatmap", WebActivitySql.Heatmap, query);
            var siteTimeTask = RunAsync<StackRow>("visits-site-over-time", WebActivitySql.SiteOverTime, query);

            await Task.WhenAll(kpiTask, trendTask, siteTask, pageTask, deviceTask, browserTask, heatTask, siteTimeTask)
                .ConfigureAwait(false);

            var model = new WebActivityVisits { Window = window };
            model.Queries.AddRange(new[]
            {
                kpiTask.Result.Info, trendTask.Result.Info, siteTask.Result.Info, pageTask.Result.Info,
                deviceTask.Result.Info, browserTask.Result.Info, heatTask.Result.Info, siteTimeTask.Result.Info,
            });

            var kpi = kpiTask.Result.Rows.FirstOrDefault() ?? new VisitKpiRow();

            model.Kpis = new WebActivityVisitKpis
            {
                Visits = kpi.Visits,
                Visitors = kpi.Visitors,
                UniquePages = kpi.UniquePages,
                VisitsPerVisitor = kpi.Visitors > 0 ? (double)kpi.Visits / kpi.Visitors : 0,
                EarliestVisitHour = kpi.EarliestVisitHour,
                LatestVisitHour = kpi.LatestVisitHour,
                OutOfHoursVisits = kpi.OutOfHoursVisits,
                OutOfHoursPct = WebActivityScoring.Percentage(kpi.OutOfHoursVisits, kpi.Visits),
            };

            model.Trend = BuildTrend(trendTask.Result.Rows);

            // Shares are of the window's TOTAL visits, not of the rows that happened to make the
            // top N. A share recomputed over a truncated leaderboard always sums to 100% and quietly
            // hides the tail, which is the same mistake in every ranked chart on the page.
            model.BySite = WithShare(
                siteTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Visits }),
                kpi.Visits);
            model.ByPage = WithShare(
                pageTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Count }),
                kpi.Visits);
            model.ByDevice = WithShare(
                deviceTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Visits }),
                kpi.Visits);
            model.ByBrowser = WithShare(
                browserTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Visits }),
                kpi.Visits);

            var cells = heatTask.Result.Rows;
            model.ByDay = BuildDayBuckets(cells);
            model.ByHour = BuildHourBuckets(cells);
            model.ByPeriodOfDay = BuildPeriodBuckets(cells);

            model.SiteOverTime = siteTimeTask.Result.Rows
                .Select(r => new WebActivityStackPoint { WeekStart = AsUtc(r.WeekStart), Name = r.Name, Count = r.Count })
                .ToList();

            return model;
        }

        #endregion

        #region Pages

        public async Task<WebActivityPages> GetPagesAsync(WebActivityQuery query)
        {
            var window = WebActivityWindow.From(query);

            var kpiTask = RunAsync<OverviewKpiRow>("pages-kpis", WebActivitySql.OverviewKpis, query);
            var topTask = RunAsync<PageRow>("pages-top", WebActivitySql.PageStats, query);
            var slowTask = RunAsync<PageRow>("pages-slowest", WebActivitySql.SlowestPages, query);
            var quietTask = RunAsync<PageRow>("pages-quiet", WebActivitySql.QuietPages, query);
            var siteTask = RunAsync<SiteRow>("pages-sites", SiteByPageViewsSql, query);
            var periodTask = RunAsync<WeekHourRow>("pages-period-over-time", WebActivitySql.PeriodOverTime, query);
            var distributionTask = RunAsync<PageViewDistributionRow>(
                "pages-distribution", WebActivitySql.PageViewDistribution, query);

            await Task.WhenAll(kpiTask, topTask, slowTask, quietTask, siteTask, periodTask, distributionTask)
                .ConfigureAwait(false);

            var model = new WebActivityPages { Window = window };
            model.Queries.AddRange(new[]
            {
                kpiTask.Result.Info, topTask.Result.Info, slowTask.Result.Info, quietTask.Result.Info,
                siteTask.Result.Info, periodTask.Result.Info, distributionTask.Result.Info,
            });

            var kpi = kpiTask.Result.Rows.FirstOrDefault() ?? new OverviewKpiRow();
            var distribution = distributionTask.Result.Rows;

            model.Kpis = new WebActivityPageKpis
            {
                PageViews = kpi.PageViews,
                UniquePageViews = kpi.UniquePageViews,
                UniqueSharePct = WebActivityScoring.Percentage(kpi.UniquePageViews, kpi.PageViews),
                PagesPerVisit = kpi.Visits > 0 ? (double)kpi.PageViews / kpi.Visits : 0,
                AverageSecondsOnPage = kpi.AverageSecondsOnPage,
                AverageLoadSeconds = kpi.AverageLoadSeconds,
                UniquePages = kpi.UniquePages,
                QuietPages = (int)distribution
                    .Where(r => r.Views <= WebActivityScoring.QuietPageViewCeiling)
                    .Sum(r => r.Pages),
                TopDecilePagePct = TopDecilePageShare(distribution),
            };

            model.TopPages = topTask.Result.Rows.Select(ToPageModel).ToList();
            model.SlowestPages = slowTask.Result.Rows.Select(ToPageModel).ToList();
            model.QuietPages = quietTask.Result.Rows.Select(ToPageModel).ToList();

            model.BySite = siteTask.Result.Rows
                .Select(r => new WebActivitySiteRow
                {
                    Name = r.Name,
                    Url = r.Url,
                    PageViews = r.PageViews,
                    UniquePageViews = r.UniquePageViews,
                    Visits = r.Visits,
                    Visitors = r.Visitors,
                })
                .ToList();

            model.PeriodOverTime = BuildPeriodOverTime(periodTask.Result.Rows);

            return model;
        }

        #endregion

        #region Journeys

        public async Task<WebActivityJourneys> GetJourneysAsync(WebActivityQuery query)
        {
            var window = WebActivityWindow.From(query);

            var depthTask = RunAsync<VisitDepthRow>("journeys-depth", WebActivitySql.VisitDepth, query);
            var entryTask = RunAsync<PageRow>("journeys-entry", WebActivitySql.EntryPages, query);
            var exitTask = RunAsync<PageRow>("journeys-exit", WebActivitySql.ExitPages, query);
            var transitionTask = RunAsync<TransitionRow>("journeys-transitions", WebActivitySql.Transitions, query);
            var clickTask = RunAsync<NamedCountRow>("journeys-clicks", WebActivitySql.ClickedElements, query);
            var clickCountTask = RunAsync<ClickCountRow>("journeys-click-count", WebActivitySql.ClickCount, query);

            await Task.WhenAll(depthTask, entryTask, exitTask, transitionTask, clickTask, clickCountTask)
                .ConfigureAwait(false);

            var model = new WebActivityJourneys { Window = window };
            model.Queries.AddRange(new[]
            {
                depthTask.Result.Info, entryTask.Result.Info, exitTask.Result.Info,
                transitionTask.Result.Info, clickTask.Result.Info, clickCountTask.Result.Info,
            });

            var depth = depthTask.Result.Rows;
            var visits = depth.Sum(r => r.Visits);
            var pageViews = depth.Sum(r => r.Pages * r.Visits);
            var bounces = depth.Where(r => r.Pages == 1).Sum(r => r.Visits);
            var measuredDwell = depth.Where(r => r.Seconds.HasValue).ToList();

            model.Kpis = new WebActivityJourneyKpis
            {
                Visits = visits,
                Bounces = bounces,
                BouncePct = WebActivityScoring.Percentage(bounces, visits),
                PagesPerVisit = visits > 0 ? (double)pageViews / visits : 0,
                MedianPagesPerVisit = MedianPages(depth),
                // Null rather than zero when nothing reported a dwell time: a confident "0s average
                // visit" is the same missing-data failure the timing KPIs were just fixed for.
                AverageVisitSeconds = measuredDwell.Count > 0 && visits > 0
                    ? (double?)(measuredDwell.Sum(r => r.Seconds.Value) / visits)
                    : null,
                Clicks = clickCountTask.Result.Rows.FirstOrDefault()?.Clicks ?? 0,
            };

            model.EntryPages = entryTask.Result.Rows.Select(ToPageModel).ToList();
            model.ExitPages = exitTask.Result.Rows.Select(ToPageModel).ToList();

            // Bounce pages are the entry pages re-ranked, not a separate query: an entry page's bounce
            // rate must be its own bounces over its own entries, and computing the two from different
            // row sets is how a rate over 100% gets shipped.
            model.BouncePages = entryTask.Result.Rows
                .Where(r => r.Entries >= query.MinimumViews && r.Bounces > 0)
                .Select(ToPageModel)
                .OrderByDescending(r => r.BouncePct ?? 0)
                .ThenByDescending(r => r.Bounces)
                .Take(query.Top)
                .ToList();

            model.Transitions = transitionTask.Result.Rows
                .Select(r => new WebActivityTransitionRow
                {
                    FromTitle = r.FromTitle,
                    FromUrl = r.FromUrl,
                    ToTitle = r.ToTitle,
                    ToUrl = r.ToUrl,
                    Count = r.Count,
                    SharePct = r.SharePct ?? 0,
                })
                .ToList();

            model.Depth = BuildDepthBuckets(depth);

            model.ClickedElements = WithShare(
                clickTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Count }),
                model.Kpis.Clicks);

            return model;
        }

        #endregion

        #region Geography

        public async Task<WebActivityGeography> GetGeographyAsync(WebActivityQuery query)
        {
            var window = WebActivityWindow.From(query);

            var kpiTask = RunAsync<GeographyKpiRow>("geo-kpis", WebActivitySql.GeographyKpis, query);
            var countryTask = RunAsync<PlaceRow>("geo-countries", CountrySql, query);
            var cityTask = RunAsync<PlaceRow>("geo-cities", CitySql, query);
            var provinceTask = RunAsync<PlaceRow>("geo-provinces", ProvinceSql, query);
            var trendTask = RunAsync<StackRow>("geo-country-over-time", WebActivitySql.CountryOverTime, query);

            await Task.WhenAll(kpiTask, countryTask, cityTask, provinceTask, trendTask).ConfigureAwait(false);

            var model = new WebActivityGeography { Window = window };
            model.Queries.AddRange(new[]
            {
                kpiTask.Result.Info, countryTask.Result.Info, cityTask.Result.Info,
                provinceTask.Result.Info, trendTask.Result.Info,
            });

            var kpi = kpiTask.Result.Rows.FirstOrDefault() ?? new GeographyKpiRow();

            model.Kpis = new WebActivityGeographyKpis
            {
                Visits = kpi.Visits,
                Visitors = kpi.Visitors,
                Countries = kpi.Countries,
                Cities = kpi.Cities,
                Provinces = kpi.Provinces,
                UnknownLocationPageViews = kpi.UnknownLocationPageViews,
                UnknownLocationPct = WebActivityScoring.Percentage(kpi.UnknownLocationPageViews, kpi.PageViews),
                LocatedPageViews = kpi.PageViews - kpi.UnknownLocationPageViews,
                CountryPageViews = kpi.CountryPageViews,
                CityPageViews = kpi.CityPageViews,
                ProvincePageViews = kpi.ProvincePageViews,
            };

            // Each list's share is against the page views that resolved to THAT attribute. A page
            // view with a city but no country belongs to neither a country row nor the country
            // denominator, so a single "located" total would quietly inflate every country's share.
            model.Countries = ToPlaces(countryTask.Result.Rows, kpi.CountryPageViews);
            model.Cities = ToPlaces(cityTask.Result.Rows, kpi.CityPageViews);
            model.Provinces = ToPlaces(provinceTask.Result.Rows, kpi.ProvincePageViews);

            model.CountryOverTime = trendTask.Result.Rows
                .Select(r => new WebActivityStackPoint { WeekStart = AsUtc(r.WeekStart), Name = r.Name, Count = r.Count })
                .ToList();

            return model;
        }

        #endregion

        #region Search

        public async Task<WebActivitySearch> GetSearchAsync(WebActivityQuery query)
        {
            var window = WebActivityWindow.From(query);

            var kpiTask = RunAsync<SearchKpiRow>("search-kpis", WebActivitySql.SearchKpis, query);
            var visitTask = RunAsync<VisitKpiRow>("search-visits", WebActivitySql.VisitKpis, query);
            var termTask = RunAsync<SearchTermRow>("search-terms", WebActivitySql.SearchTerms, query);
            var deadEndTask = RunAsync<SearchTermRow>("search-dead-ends", WebActivitySql.DeadEndTerms, query);
            var trendTask = RunAsync<TrendRow>("search-trend", WebActivitySql.OverviewTrend, query);
            var dayHourTask = RunAsync<DayHourRow>("search-day-hour", WebActivitySql.SearchesByDayHour, query);
            var siteTask = RunAsync<NamedCountRow>("search-sites", WebActivitySql.SearchesBySite, query);

            await Task.WhenAll(kpiTask, visitTask, termTask, deadEndTask, trendTask, dayHourTask, siteTask)
                .ConfigureAwait(false);

            var model = new WebActivitySearch { Window = window };
            model.Queries.AddRange(new[]
            {
                kpiTask.Result.Info, visitTask.Result.Info, termTask.Result.Info, deadEndTask.Result.Info,
                trendTask.Result.Info, dayHourTask.Result.Info, siteTask.Result.Info,
            });

            var kpi = kpiTask.Result.Rows.FirstOrDefault() ?? new SearchKpiRow();
            var visits = visitTask.Result.Rows.FirstOrDefault()?.Visits ?? 0;

            model.Kpis = new WebActivitySearchKpis
            {
                Searches = kpi.Searches,
                Terms = kpi.Terms,
                Searchers = kpi.Searchers,
                SessionsWithSearch = kpi.SessionsWithSearch,
                SearchReliancePct = WebActivityScoring.Percentage(kpi.SessionsWithSearch, visits),
                SearchesPerSearchingVisit = kpi.SessionsWithSearch > 0
                    ? (double)kpi.Searches / kpi.SessionsWithSearch
                    : 0,
                StrugglingVisits = kpi.StrugglingVisits,
                DeadEndSearches = kpi.DeadEndSearches,
                DeadEndPct = WebActivityScoring.Percentage(kpi.DeadEndSearches, kpi.Searches),
                DeadEndGraceSeconds = WebActivitySql.SearchDeadEndGraceSeconds,
            };

            model.TopTerms = termTask.Result.Rows.Select(ToTermModel).ToList();
            model.DeadEndTerms = deadEndTask.Result.Rows.Select(ToTermModel).ToList();
            model.Trend = BuildTrend(trendTask.Result.Rows);

            var cells = dayHourTask.Result.Rows
                .Select(r => new HeatCellRow { Day = r.Day, Hour = r.Hour, PageViews = r.Count, Visits = r.Count })
                .ToList();
            model.ByDay = BuildDayBuckets(cells);
            model.ByPeriodOfDay = BuildPeriodBuckets(cells);

            model.BySite = WithShare(
                siteTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Count }),
                kpi.Searches);

            return model;
        }

        #endregion

        #region Technology

        public async Task<WebActivityTechnology> GetTechnologyAsync(WebActivityQuery query)
        {
            var window = WebActivityWindow.From(query);

            var kpiTask = RunAsync<TechnologyKpiRow>("tech-kpis", WebActivitySql.TechnologyKpis, query);
            var browserTask = RunAsync<PlatformRow>("tech-browsers", BrowserSql, query);
            var osTask = RunAsync<PlatformRow>("tech-os", OperatingSystemSql, query);
            var deviceTask = RunAsync<PlatformRow>("tech-devices", DeviceSql, query);
            var deviceTotalsTask = RunAsync<NamedCountRow>("tech-device-totals", WebActivitySql.DeviceTotals, query);
            var histogramTask = RunAsync<LoadBucketRow>("tech-load-histogram", WebActivitySql.LoadHistogram, query);
            var deviceTimeTask = RunAsync<StackRow>("tech-device-over-time", WebActivitySql.DeviceOverTime, query);
            var detailTask = RunAsync<TechnologyDetailRow>("tech-detail", WebActivitySql.TechnologyDetail, query);

            await Task.WhenAll(kpiTask, browserTask, osTask, deviceTask, deviceTotalsTask, histogramTask,
                deviceTimeTask, detailTask).ConfigureAwait(false);

            var model = new WebActivityTechnology { Window = window };
            model.Queries.AddRange(new[]
            {
                kpiTask.Result.Info, browserTask.Result.Info, osTask.Result.Info, deviceTask.Result.Info,
                deviceTotalsTask.Result.Info, histogramTask.Result.Info, deviceTimeTask.Result.Info,
                detailTask.Result.Info,
            });

            var kpi = kpiTask.Result.Rows.FirstOrDefault() ?? new TechnologyKpiRow();
            var buckets = histogramTask.Result.Rows
                .Select(r => new KeyValuePair<int, long>(r.Bucket, r.Count))
                .ToList();
            var p95Bucket = WebActivityScoring.PercentileBucket(buckets, 0.95);

            model.Kpis = new WebActivityTechnologyKpis
            {
                Browsers = kpi.Browsers,
                OperatingSystems = kpi.OperatingSystems,
                Devices = kpi.Devices,
                MobilePct = MobileSharePct(deviceTotalsTask.Result.Rows),
                AverageLoadSeconds = kpi.AverageLoadSeconds,
                P95LoadSeconds = p95Bucket.HasValue
                    ? (double?)(p95Bucket.Value >= WebActivitySql.LoadOverflowBucket
                        ? WebActivitySql.LoadBucketCeilingSeconds
                        : (p95Bucket.Value + 1) * WebActivitySql.LoadBucketSeconds)
                    : null,
                P95AtCeiling = p95Bucket.HasValue && p95Bucket.Value >= WebActivitySql.LoadOverflowBucket,
                LoadCeilingSeconds = WebActivitySql.LoadBucketCeilingSeconds,
                UnknownBrowserPageViews = kpi.UnknownBrowserPageViews,
                PageViews = kpi.PageViews,
                KnownDevicePageViews = deviceTotalsTask.Result.Rows.Sum(r => r.Count),
            };

            model.Browsers = ToPlatforms(browserTask.Result.Rows, kpi.PageViews);
            model.OperatingSystems = ToPlatforms(osTask.Result.Rows, kpi.PageViews);
            model.Devices = ToPlatforms(deviceTask.Result.Rows, kpi.PageViews);

            model.DeviceOverTime = deviceTimeTask.Result.Rows
                .Select(r => new WebActivityStackPoint { WeekStart = AsUtc(r.WeekStart), Name = r.Name, Count = r.Count })
                .ToList();

            model.Detail = detailTask.Result.Rows
                .Select(r => new WebActivityTechnologyDetailRow
                {
                    Browser = r.Browser,
                    Device = r.Device,
                    OperatingSystem = r.OperatingSystem,
                    City = r.City,
                    Visits = r.Visits,
                    Visitors = r.Visitors,
                    PageViews = r.PageViews,
                    PageViewsPerVisit = r.Visits > 0 ? (double)r.PageViews / r.Visits : 0,
                    AverageSecondsOnPage = r.AverageSecondsOnPage,
                    AverageLoadSeconds = r.AverageLoadSeconds,
                })
                .ToList();

            return model;
        }

        #endregion

        #region Shaping

        /// <summary>
        /// The client-attribute statements, built once.
        /// </summary>
        /// <remarks>
        /// The lookup table and column names are compile-time constants from this file, never request
        /// input. Building them once also means the SQL shown in the UI's popover is byte-identical to
        /// the text that was executed.
        /// </remarks>
        internal static readonly string BrowserSql =
            WebActivitySql.ByClientAttribute("browsers", "agent_id", "browser_name");

        internal static readonly string OperatingSystemSql =
            WebActivitySql.ByClientAttribute("operating_systems", "os_id", "os_name");

        internal static readonly string DeviceSql =
            WebActivitySql.ByClientAttribute("devices", "device_id", "device_name");

        // The Visits tab prints visit counts, so its leaderboards must be RANKED by visits. Ranking
        // by page views and printing visits can omit the browser or device with the most visits
        // entirely, and leaves the rest sorted by a column that is not on screen.
        internal static readonly string BrowserByVisitsSql =
            WebActivitySql.ByClientAttribute("browsers", "agent_id", "browser_name", rankByVisits: true);

        internal static readonly string DeviceByVisitsSql =
            WebActivitySql.ByClientAttribute("devices", "device_id", "device_name", rankByVisits: true);

        internal static readonly string SiteByPageViewsSql = WebActivitySql.BySite(rankByVisits: false);

        internal static readonly string SiteByVisitsSql = WebActivitySql.BySite(rankByVisits: true);

        internal static readonly string CountrySql =
            WebActivitySql.ByPlace("country_id", "countries", "country_name", false);

        internal static readonly string CitySql =
            WebActivitySql.ByPlace("city_id", "cities", "city_name", true);

        internal static readonly string ProvinceSql =
            WebActivitySql.ByPlace("location_province_id", "provinces", "province_name", true);

        /// <summary>
        /// The 95th-percentile load time from a histogram, or null when nothing was measured.
        /// </summary>
        private static double? PercentileSeconds(IEnumerable<LoadBucketRow> buckets)
        {
            var bucket = WebActivityScoring.PercentileBucket(
                buckets.Select(r => new KeyValuePair<int, long>(r.Bucket, r.Count)), 0.95);

            if (!bucket.HasValue) return null;

            // Everything slower than the ceiling shares the overflow bucket, so its upper edge is not
            // evidence of anything. Reporting the ceiling itself keeps the claim to what the
            // histogram actually proves - "at least this" - and keeps the judgement in step with the
            // Technology tab, which renders the same case as a lower bound.
            if (bucket.Value >= WebActivitySql.LoadOverflowBucket) return WebActivitySql.LoadBucketCeilingSeconds;

            return (bucket.Value + 1) * WebActivitySql.LoadBucketSeconds;
        }

        /// <summary>
        /// Stamps a SQL-derived timestamp as UTC.
        /// </summary>
        /// <remarks>
        /// EF materialises <c>datetime</c> and <c>date</c> columns with
        /// <see cref="DateTimeKind.Unspecified"/>. Serialised, that produces an ISO string with NO
        /// timezone suffix, which JavaScript's <c>Date</c> parses as LOCAL time - so a Monday week
        /// bucket renders as the previous Sunday for every reader east of UTC, and every weekly chart
        /// is silently off by a day. These values are UTC by construction (the columns store UTC), so
        /// saying so is both correct and the only way the client can read them back unchanged.
        /// </remarks>
        private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

        /// <summary>Stamps a nullable SQL-derived timestamp as UTC.</summary>
        private static DateTime? AsUtc(DateTime? value) =>
            value.HasValue ? (DateTime?)AsUtc(value.Value) : null;

        private static WebActivityPageRow ToPageModel(PageRow row)
        {
            return new WebActivityPageRow
            {
                Title = row.Title,
                Url = row.Url,
                Site = row.Site,
                PageViews = row.PageViews,
                UniquePageViews = row.UniquePageViews,
                Visitors = row.Visitors,
                AverageSecondsOnPage = row.AverageSecondsOnPage,
                AverageLoadSeconds = row.AverageLoadSeconds,
                Entries = row.Entries,
                Exits = row.Exits,
                Bounces = row.Bounces,
                BouncePct = row.Entries > 0
                    ? (double?)WebActivityScoring.Percentage(row.Bounces, row.Entries)
                    : null,
            };
        }

        private static WebActivitySearchTermRow ToTermModel(SearchTermRow row)
        {
            return new WebActivitySearchTermRow
            {
                Term = row.Term,
                Searches = row.Searches,
                Searchers = row.Searchers,
                DeadEnds = row.DeadEnds,
                DeadEndPct = WebActivityScoring.Percentage(row.DeadEnds, row.Searches),
            };
        }

        private static List<WebActivityPlaceRow> ToPlaces(List<PlaceRow> rows, long totalPageViews)
        {
            return rows
                .Select(r => new WebActivityPlaceRow
                {
                    Name = r.Name,
                    Country = r.Country,
                    PageViews = r.PageViews,
                    Visits = r.Visits,
                    Visitors = r.Visitors,
                    SharePct = WebActivityScoring.Percentage(r.PageViews, totalPageViews),
                })
                .ToList();
        }

        /// <summary>
        /// Platform rows with each one's share of ALL page views in the window.
        /// </summary>
        /// <remarks>
        /// Against the window total rather than the sum of the returned rows, so a truncated
        /// leaderboard cannot imply it accounts for every page view. The shares therefore do not sum
        /// to 100% when there is a tail, which is the honest outcome.
        /// </remarks>
        private static List<WebActivityPlatformRow> ToPlatforms(List<PlatformRow> rows, long total)
        {

            return rows
                .Select(r => new WebActivityPlatformRow
                {
                    Name = r.Name,
                    PageViews = r.PageViews,
                    Visits = r.Visits,
                    Visitors = r.Visitors,
                    SharePct = WebActivityScoring.Percentage(r.PageViews, total),
                    AverageLoadSeconds = r.AverageLoadSeconds,
                    AverageSecondsOnPage = r.AverageSecondsOnPage,
                })
                .ToList();
        }

        /// <summary>
        /// Mobile page views as a share of the page views whose device is known.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Fed from the UNTRUNCATED device totals, never from the device leaderboard. Device names
        /// are model-specific for phones and generic for desktops, so the long tail a top-N
        /// leaderboard drops is disproportionately mobile and the share would be systematically
        /// under-reported - worst on exactly the tenants with the most varied phone estate.
        /// </para>
        /// <para>
        /// The denominator is deliberately the KNOWN devices, not all page views. Hits with no device
        /// are unmeasured, and folding them into the denominator would report a falling mobile share
        /// whenever device detection got worse - the opposite of what the figure is for. Null when no
        /// page view carried a device at all, because 0% would read as "nobody uses a phone".
        /// </para>
        /// </remarks>
        private static double? MobileSharePct(IEnumerable<NamedCountRow> devices)
        {
            var rows = devices?.ToList() ?? new List<NamedCountRow>();
            var known = rows.Sum(r => r.Count);
            if (known <= 0) return null;

            var mobile = rows.Where(r => WebActivityScoring.IsMobileDevice(r.Name)).Sum(r => r.Count);
            return WebActivityScoring.Percentage(mobile, known);
        }

        /// <summary>Share of all page views held by the busiest tenth of pages.</summary>
        /// <remarks>
        /// The distribution is (views, how many pages had exactly that many views) and is passed
        /// through in that form. Expanding it to one element per page would allocate a multi-million
        /// element list on a large tenant - which is precisely why the SQL groups it in the first
        /// place - and then sort it.
        /// </remarks>
        private static double TopDecilePageShare(IEnumerable<PageViewDistributionRow> distribution)
        {
            return WebActivityScoring.TopDecileShareOfDistribution(
                (distribution ?? Enumerable.Empty<PageViewDistributionRow>())
                    .Select(r => new KeyValuePair<long, long>(r.Views, r.Pages)));
        }

        private static List<WebActivityTrendPoint> BuildTrend(IEnumerable<TrendRow> rows)
        {
            return (rows ?? Enumerable.Empty<TrendRow>())
                .OrderBy(r => r.WeekStart)
                .Select(r => new WebActivityTrendPoint
                {
                    WeekStart = AsUtc(r.WeekStart),
                    PageViews = r.PageViews,
                    Visits = r.Visits,
                    Visitors = r.Visitors,
                    Bounces = r.Bounces,
                    Searches = r.Searches,
                })
                .ToList();
        }

        private static List<WebActivityBucket> BuildVisitorSegments(
            IEnumerable<ActiveDaysRow> rows,
            int windowDays)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var segment in WebActivityScoring.VisitorSegments) counts[segment] = 0;

            foreach (var row in rows ?? Enumerable.Empty<ActiveDaysRow>())
            {
                var segment = WebActivityScoring.VisitorSegment(row.ActiveDays, windowDays);
                if (!counts.ContainsKey(segment)) continue;
                counts[segment] += row.Visitors;
            }

            var total = counts.Values.Sum();

            return WebActivityScoring.VisitorSegments
                .Select(segment => new WebActivityBucket
                {
                    Key = segment,
                    Label = segment,
                    Count = counts[segment],
                    SharePct = WebActivityScoring.Percentage(counts[segment], total),
                })
                .ToList();
        }

        private static List<WebActivityBucket> BuildDepthBuckets(IEnumerable<VisitDepthRow> rows)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var band in WebActivityScoring.DepthBands) counts[band.Item2] = 0;

            foreach (var row in rows ?? Enumerable.Empty<VisitDepthRow>())
            {
                var band = WebActivityScoring.DepthBand(row.Pages);
                counts[band] += row.Visits;
            }

            var total = counts.Values.Sum();

            return WebActivityScoring.DepthBands
                .Select(band => new WebActivityBucket
                {
                    Key = band.Item2,
                    Label = band.Item2,
                    Count = counts[band.Item2],
                    SharePct = WebActivityScoring.Percentage(counts[band.Item2], total),
                })
                .ToList();
        }

        private static List<WebActivityBucket> BuildDayBuckets(IEnumerable<HeatCellRow> cells)
        {
            var totals = new long[7];
            foreach (var cell in cells ?? Enumerable.Empty<HeatCellRow>())
            {
                if (cell.Day < 0 || cell.Day > 6) continue;
                totals[cell.Day] += cell.Visits;
            }

            var total = totals.Sum();

            return Enumerable.Range(0, 7)
                .Select(day => new WebActivityBucket
                {
                    Key = day.ToString(),
                    Label = WebActivityScoring.DayName(day),
                    Count = totals[day],
                    SharePct = WebActivityScoring.Percentage(totals[day], total),
                })
                .ToList();
        }

        private static List<WebActivityBucket> BuildHourBuckets(IEnumerable<HeatCellRow> cells)
        {
            var totals = new long[24];
            foreach (var cell in cells ?? Enumerable.Empty<HeatCellRow>())
            {
                if (cell.Hour < 0 || cell.Hour > 23) continue;
                totals[cell.Hour] += cell.Visits;
            }

            var total = totals.Sum();

            return Enumerable.Range(0, 24)
                .Select(hour => new WebActivityBucket
                {
                    Key = hour.ToString("00"),
                    Label = hour.ToString("00") + ":00",
                    Count = totals[hour],
                    SharePct = WebActivityScoring.Percentage(totals[hour], total),
                })
                .ToList();
        }

        private static List<WebActivityBucket> BuildPeriodBuckets(IEnumerable<HeatCellRow> cells)
        {
            var totals = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var period in WebActivityScoring.PeriodsOfDay) totals[period.Key] = 0;

            foreach (var cell in cells ?? Enumerable.Empty<HeatCellRow>())
            {
                if (cell.Hour < 0 || cell.Hour > 23) continue;
                totals[WebActivityScoring.PeriodFor(cell.Hour).Key] += cell.Visits;
            }

            var total = totals.Values.Sum();

            return WebActivityScoring.PeriodsOfDay
                .Select(period => new WebActivityBucket
                {
                    Key = period.Key,
                    Label = period.Label,
                    Count = totals[period.Key],
                    SharePct = WebActivityScoring.Percentage(totals[period.Key], total),
                })
                .ToList();
        }

        private static List<WebActivityStackPoint> BuildPeriodOverTime(IEnumerable<WeekHourRow> rows)
        {
            var totals = new Dictionary<Tuple<DateTime, string>, long>();

            foreach (var row in rows ?? Enumerable.Empty<WeekHourRow>())
            {
                if (row.Hour < 0 || row.Hour > 23) continue;
                var key = Tuple.Create(row.WeekStart, WebActivityScoring.PeriodFor(row.Hour).Label);
                totals.TryGetValue(key, out long current);
                totals[key] = current + row.Count;
            }

            return totals
                .OrderBy(kv => kv.Key.Item1)
                .ThenBy(kv => kv.Key.Item2, StringComparer.Ordinal)
                .Select(kv => new WebActivityStackPoint
                {
                    WeekStart = AsUtc(kv.Key.Item1),
                    Name = kv.Key.Item2,
                    Count = kv.Value,
                })
                .ToList();
        }

        /// <summary>The median number of pages per visit, read off the depth distribution.</summary>
        private static int MedianPages(IEnumerable<VisitDepthRow> rows)
        {
            var ordered = (rows ?? Enumerable.Empty<VisitDepthRow>())
                .Where(r => r.Visits > 0)
                .OrderBy(r => r.Pages)
                .ToList();

            var total = ordered.Sum(r => r.Visits);
            if (total == 0) return 0;

            var target = (total + 1) / 2;
            long running = 0;

            foreach (var row in ordered)
            {
                running += row.Visits;
                if (running >= target) return (int)Math.Min(row.Pages, int.MaxValue);
            }

            return 0;
        }

        /// <summary>
        /// Adds each row's share of <paramref name="total"/>.
        /// </summary>
        /// <remarks>
        /// The total is the window's real denominator, NOT the sum of the rows. These lists are
        /// truncated to the top N, so a share computed over the returned rows would always sum to
        /// exactly 100% however much traffic the tail held - which is how a ranked chart quietly
        /// asserts it shows everything.
        /// </remarks>
        private static List<WebActivityNamedCount> WithShare(
            IEnumerable<WebActivityNamedCount> rows,
            long total)
        {
            var list = rows?.ToList() ?? new List<WebActivityNamedCount>();

            foreach (var row in list)
            {
                row.SharePct = total > 0 ? (double?)WebActivityScoring.Percentage(row.Count, total) : null;
            }

            return list;
        }

        #endregion

        #region Execution

        private sealed class QueryResult<T>
        {
            public WebActivityQueryInfo Info { get; set; }
            public List<T> Rows { get; set; } = new List<T>();
        }

        /// <summary>
        /// Runs one statement on its own context, capturing a failure as a per-section error.
        /// </summary>
        private async Task<QueryResult<T>> RunAsync<T>(string key, string sql, WebActivityQuery query)
        {
            var info = new WebActivityQueryInfo
            {
                Key = key,
                Sql = WebActivitySql.Describe(sql, query),
            };

            var result = new QueryResult<T> { Info = info };
            var watch = Stopwatch.StartNew();

            await QuerySlots.WaitAsync().ConfigureAwait(false);

            try
            {
                using (var db = _contextFactory.Create())
                {
                    db.Database.CommandTimeout = _commandTimeoutSeconds;
                    result.Rows = await db.Database
                        .SqlQuery<T>(sql, Parameters(query))
                        .ToListAsync()
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                info.Error = InnermostMessage(ex);
            }
            finally
            {
                QuerySlots.Release();
                watch.Stop();
                info.ElapsedMs = watch.ElapsedMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// The one parameter list every statement is executed with.
        /// </summary>
        /// <remarks>
        /// SQL Server accepts parameters that a statement does not reference, so a single list keeps
        /// the executed command and the SQL shown in the UI's popover in step by construction. New
        /// instances every call: a <see cref="SqlParameter"/> belongs to one command at a time, and
        /// these statements run in parallel.
        /// </remarks>
        private static SqlParameter[] Parameters(WebActivityQuery query)
        {
            return new[]
            {
                DateParameter("from", query.FromUtc),
                DateParameter("to", query.ToExclusiveUtc),
                DateParameter("midpoint", query.MidpointUtc),
                IntParameter("top", query.Top),
                IntParameter("minViews", query.MinimumViews),
                IntParameter("quietMax", WebActivityScoring.QuietPageViewCeiling),
                IntParameter("minSearches", WebActivitySql.MinimumSearchesForDeadEndRanking),
                IntParameter("strugglingSearches", WebActivitySql.StrugglingSearchCount),
                IntParameter("searchGrace", WebActivitySql.SearchDeadEndGraceSeconds),
                IntParameter("workStart", WebActivitySql.WorkingDayStartHour),
                IntParameter("workEnd", WebActivitySql.WorkingDayEndHour),
                FloatParameter("loadBucket", WebActivitySql.LoadBucketSeconds),
                FloatParameter("loadCeiling", WebActivitySql.LoadBucketCeilingSeconds),
                IntParameter("loadOverflowBucket", WebActivitySql.LoadOverflowBucket),
            };
        }

        /// <summary>
        /// A window boundary.
        /// </summary>
        /// <remarks>
        /// Typed <c>datetime</c> rather than <c>date</c> because <c>hits.hit_timestamp</c> and
        /// <c>searches.date_time</c> are both <c>datetime</c>. Matching the column type keeps the
        /// comparison free of an implicit conversion, which is what lets the optimiser seek
        /// <c>IX_hits_hit_timestamp</c> rather than scan.
        /// </remarks>
        private static SqlParameter DateParameter(string name, DateTime value)
        {
            return new SqlParameter(name, SqlDbType.DateTime) { Value = value };
        }

        private static SqlParameter IntParameter(string name, int value)
        {
            return new SqlParameter(name, SqlDbType.Int) { Value = value };
        }

        private static SqlParameter FloatParameter(string name, double value)
        {
            return new SqlParameter(name, SqlDbType.Float) { Value = value };
        }

        /// <summary>EF wraps SQL errors; the innermost message (the SqlException) is the useful one.</summary>
        private static string InnermostMessage(Exception ex)
        {
            var current = ex;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current.Message;
        }

        #endregion
    }
}
