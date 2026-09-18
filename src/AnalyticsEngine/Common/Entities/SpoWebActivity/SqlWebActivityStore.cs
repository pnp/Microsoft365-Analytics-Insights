using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using static Common.Entities.SpoWebActivity.WebActivityRows;

namespace Common.Entities.SpoWebActivity
{
    /// <summary>Loads the SharePoint web-activity page's sections.</summary>
    public interface IWebActivityStore
    {
        /// <summary>
        /// The newest page hit of any age, or null when there are none or it could not be read.
        /// </summary>
        Task<DateTime?> GetLastHitAsync();

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
        /// The newest page hit, regardless of the reporting window.
        /// </summary>
        /// <remarks>
        /// Returns null rather than a default date when the read fails. A failed query reported to an
        /// admin as "the tracker has never collected anything" would send them to redeploy a tracker
        /// that is working perfectly.
        /// </remarks>
        public async Task<DateTime?> GetLastHitAsync()
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

                    return rows.FirstOrDefault()?.LastHitUtc;
                }
            }
            catch (Exception)
            {
                return null;
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
            var siteTask = RunAsync<SiteRow>("overview-sites", WebActivitySql.BySite, query);
            var deviceTask = RunAsync<PlatformRow>("overview-devices", DeviceSql, query);
            var distributionTask = RunAsync<PageViewDistributionRow>(
                "overview-distribution", WebActivitySql.PageViewDistribution, query);
            var searchTask = RunAsync<SearchKpiRow>("overview-search", WebActivitySql.SearchKpis, query);

            await Task.WhenAll(
                kpiTask, halvesTask, trendTask, segmentTask, depthTask,
                heatTask, siteTask, deviceTask, distributionTask, searchTask).ConfigureAwait(false);

            var model = new WebActivityOverview { Window = window };
            model.Queries.AddRange(new[]
            {
                kpiTask.Result.Info, halvesTask.Result.Info, trendTask.Result.Info, segmentTask.Result.Info,
                depthTask.Result.Info, heatTask.Result.Info, siteTask.Result.Info, deviceTask.Result.Info,
                distributionTask.Result.Info, searchTask.Result.Info,
            });

            var kpi = kpiTask.Result.Rows.FirstOrDefault() ?? new OverviewKpiRow();
            var halves = halvesTask.Result.Rows.FirstOrDefault() ?? new VisitorHalvesRow();
            var devices = deviceTask.Result.Rows;
            var mobilePct = MobileSharePct(devices);

            model.Kpis = new WebActivityOverviewKpis
            {
                PageViews = kpi.PageViews,
                UniquePageViews = kpi.UniquePageViews,
                Visits = kpi.Visits,
                Visitors = kpi.Visitors,
                KnownUsers = kpi.KnownUsers,
                ReachPct = WebActivityScoring.Percentage(kpi.Visitors, kpi.KnownUsers),
                UniquePages = kpi.UniquePages,
                Sites = kpi.Sites,
                PagesPerVisit = kpi.Visits > 0 ? (double)kpi.PageViews / kpi.Visits : 0,
                BouncePct = WebActivityScoring.Percentage(kpi.Bounces, kpi.Visits),
                AverageSecondsOnPage = kpi.AverageSecondsOnPage ?? 0,
                AverageLoadSeconds = kpi.AverageLoadSeconds ?? 0,
                NewVisitors = halves.NewVisitors,
                ReturningVisitors = halves.ReturningVisitors,
                MobileVisitPct = mobilePct,
            };

            model.Trend = BuildTrend(trendTask.Result.Rows);
            model.VisitorSegments = BuildVisitorSegments(segmentTask.Result.Rows, window.WorkingDays);
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
                siteTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.PageViews }));

            var search = searchTask.Result.Rows.FirstOrDefault() ?? new SearchKpiRow();

            model.Judgements = WebActivityScoring.Judgements(new WebActivityJudgementInputs
            {
                WebTrafficAvailable = sources.WebTraffic,
                SearchAvailable = search.Searches > 0,
                PageViews = kpi.PageViews,
                Visits = kpi.Visits,
                Visitors = kpi.Visitors,
                KnownUsers = kpi.KnownUsers,
                UniquePages = kpi.UniquePages,
                BouncePct = model.Kpis.BouncePct,
                PagesPerVisit = model.Kpis.PagesPerVisit,
                AverageLoadSeconds = model.Kpis.AverageLoadSeconds,
                SessionsWithSearch = search.SessionsWithSearch,
                TopDecilePagePct = TopDecilePageShare(distributionTask.Result.Rows),
                MobileVisitPct = mobilePct,
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
            var siteTask = RunAsync<SiteRow>("visits-sites", WebActivitySql.BySite, query);
            var pageTask = RunAsync<NamedCountRow>("visits-pages", WebActivitySql.VisitsByPage, query);
            var deviceTask = RunAsync<PlatformRow>("visits-devices", DeviceSql, query);
            var browserTask = RunAsync<PlatformRow>("visits-browsers", BrowserSql, query);
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

            model.BySite = WithShare(
                siteTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Visits }));
            model.ByPage = WithShare(
                pageTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Count }));
            model.ByDevice = WithShare(
                deviceTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Visits }));
            model.ByBrowser = WithShare(
                browserTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Visits }));

            var cells = heatTask.Result.Rows;
            model.ByDay = BuildDayBuckets(cells);
            model.ByHour = BuildHourBuckets(cells);
            model.ByPeriodOfDay = BuildPeriodBuckets(cells);

            model.SiteOverTime = siteTimeTask.Result.Rows
                .Select(r => new WebActivityStackPoint { WeekStart = r.WeekStart, Name = r.Name, Count = r.Count })
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
            var siteTask = RunAsync<SiteRow>("pages-sites", WebActivitySql.BySite, query);
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
                AverageSecondsOnPage = kpi.AverageSecondsOnPage ?? 0,
                AverageLoadSeconds = kpi.AverageLoadSeconds ?? 0,
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
            var seconds = depth.Sum(r => r.Seconds ?? 0);

            model.Kpis = new WebActivityJourneyKpis
            {
                Visits = visits,
                Bounces = bounces,
                BouncePct = WebActivityScoring.Percentage(bounces, visits),
                PagesPerVisit = visits > 0 ? (double)pageViews / visits : 0,
                MedianPagesPerVisit = MedianPages(depth),
                AverageVisitSeconds = visits > 0 ? seconds / visits : 0,
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
                clickTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Count }));

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
            };

            model.Countries = ToPlaces(countryTask.Result.Rows, kpi.PageViews);
            model.Cities = ToPlaces(cityTask.Result.Rows, kpi.PageViews);
            model.Provinces = ToPlaces(provinceTask.Result.Rows, kpi.PageViews);

            model.CountryOverTime = trendTask.Result.Rows
                .Select(r => new WebActivityStackPoint { WeekStart = r.WeekStart, Name = r.Name, Count = r.Count })
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
                siteTask.Result.Rows.Select(r => new WebActivityNamedCount { Name = r.Name, Count = r.Count }));

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
            var histogramTask = RunAsync<LoadBucketRow>("tech-load-histogram", WebActivitySql.LoadHistogram, query);
            var deviceTimeTask = RunAsync<StackRow>("tech-device-over-time", WebActivitySql.DeviceOverTime, query);
            var detailTask = RunAsync<TechnologyDetailRow>("tech-detail", WebActivitySql.TechnologyDetail, query);

            await Task.WhenAll(kpiTask, browserTask, osTask, deviceTask, histogramTask, deviceTimeTask, detailTask)
                .ConfigureAwait(false);

            var model = new WebActivityTechnology { Window = window };
            model.Queries.AddRange(new[]
            {
                kpiTask.Result.Info, browserTask.Result.Info, osTask.Result.Info, deviceTask.Result.Info,
                histogramTask.Result.Info, deviceTimeTask.Result.Info, detailTask.Result.Info,
            });

            var kpi = kpiTask.Result.Rows.FirstOrDefault() ?? new TechnologyKpiRow();
            var devices = deviceTask.Result.Rows;

            model.Kpis = new WebActivityTechnologyKpis
            {
                Browsers = kpi.Browsers,
                OperatingSystems = kpi.OperatingSystems,
                Devices = kpi.Devices,
                MobilePct = MobileSharePct(devices),
                AverageLoadSeconds = kpi.AverageLoadSeconds ?? 0,
                P95LoadSeconds = WebActivityScoring.PercentileFromBuckets(
                    histogramTask.Result.Rows.Select(r => new KeyValuePair<int, long>(r.Bucket, r.Count)),
                    0.95,
                    WebActivitySql.LoadBucketSeconds),
                UnknownBrowserPageViews = kpi.UnknownBrowserPageViews,
            };

            model.Browsers = ToPlatforms(browserTask.Result.Rows);
            model.OperatingSystems = ToPlatforms(osTask.Result.Rows);
            model.Devices = ToPlatforms(devices);

            model.DeviceOverTime = deviceTimeTask.Result.Rows
                .Select(r => new WebActivityStackPoint { WeekStart = r.WeekStart, Name = r.Name, Count = r.Count })
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

        internal static readonly string CountrySql =
            WebActivitySql.ByPlace("country_id", "countries", "country_name", false);

        internal static readonly string CitySql =
            WebActivitySql.ByPlace("city_id", "cities", "city_name", true);

        internal static readonly string ProvinceSql =
            WebActivitySql.ByPlace("location_province_id", "provinces", "province_name", true);

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

        private static List<WebActivityPlatformRow> ToPlatforms(List<PlatformRow> rows)
        {
            var total = rows.Sum(r => r.PageViews);

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
        /// The denominator is deliberately the KNOWN devices, not all page views. Hits with no device
        /// are unmeasured, and folding them into the denominator would report a falling mobile share
        /// whenever device detection got worse - the opposite of what the figure is for.
        /// </remarks>
        private static double MobileSharePct(IEnumerable<PlatformRow> devices)
        {
            var rows = devices?.ToList() ?? new List<PlatformRow>();
            var known = rows.Sum(r => r.PageViews);
            var mobile = rows.Where(r => WebActivityScoring.IsMobileDevice(r.Name)).Sum(r => r.PageViews);
            return WebActivityScoring.Percentage(mobile, known);
        }

        /// <summary>Share of all page views held by the busiest tenth of pages.</summary>
        private static double TopDecilePageShare(IEnumerable<PageViewDistributionRow> distribution)
        {
            // The distribution is (views, how many pages had exactly that many views), so it is
            // expanded back into one value per page. It is bounded by the number of DISTINCT view
            // counts multiplied out - which is the page count - so this is the one place the page
            // list is materialised, and only as a list of longs.
            var values = new List<long>();
            foreach (var row in distribution ?? Enumerable.Empty<PageViewDistributionRow>())
            {
                for (long i = 0; i < row.Pages; i++) values.Add(row.Views);
            }

            return WebActivityScoring.TopDecileShare(values);
        }

        private static List<WebActivityTrendPoint> BuildTrend(IEnumerable<TrendRow> rows)
        {
            return (rows ?? Enumerable.Empty<TrendRow>())
                .OrderBy(r => r.WeekStart)
                .Select(r => new WebActivityTrendPoint
                {
                    WeekStart = r.WeekStart,
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
            int workingDays)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var segment in WebActivityScoring.VisitorSegments) counts[segment] = 0;

            foreach (var row in rows ?? Enumerable.Empty<ActiveDaysRow>())
            {
                var segment = WebActivityScoring.VisitorSegment(row.ActiveDays, workingDays);
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
                    WeekStart = kv.Key.Item1,
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

        /// <summary>Adds each row's share of the set's total.</summary>
        private static List<WebActivityNamedCount> WithShare(IEnumerable<WebActivityNamedCount> rows)
        {
            var list = rows?.ToList() ?? new List<WebActivityNamedCount>();
            var total = list.Sum(r => r.Count);

            foreach (var row in list)
            {
                row.SharePct = WebActivityScoring.Percentage(row.Count, total);
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
