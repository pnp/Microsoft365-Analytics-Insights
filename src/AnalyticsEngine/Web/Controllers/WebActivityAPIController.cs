using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.SpoWebActivity;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Powers the portal's SharePoint web activity page - "is the intranet being used, can people find
    /// anything on it, and is it fast enough to bother with?".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces the <c>Visits</c>, <c>Page Views</c>, <c>Geographics</c>, <c>Web Searches</c> and
    /// <c>Technology</c> pages of the Power BI web-traffic report. Those pages were built on the
    /// <c>hits_view</c> / <c>vwUsage</c> SQL views, which materialise whole tables and join through a
    /// date dimension; nothing here reads a view.
    /// </para>
    /// <para>
    /// The controller is deliberately thin. All query work lives in <see cref="SqlWebActivityStore"/>
    /// and all judgement in <see cref="WebActivityScoring"/>, both in <c>Common.Entities</c>, so the
    /// page can be tested against a real database without an ASP.NET request pipeline - the same split
    /// Teams Explorer, Copilot Adoption and Licence activity use.
    /// </para>
    /// <para>
    /// Each section is cached briefly. These are multi-query tabs over <c>dbo.hits</c>, which is the
    /// largest table a busy intranet produces, and an admin flipping between tabs or nudging the
    /// period should not pay for a full re-run each time. The cache key carries the window, the row
    /// count and the UTC date, so it can never be served across a day boundary.
    /// </para>
    /// <para>
    /// The cache holds the complete section, and a reader without the portal's See PII permission is
    /// sent a trimmed copy of it (#844): search terms, and the searches by site, whose query counts no
    /// people, are withheld, and a row of any other list - a site, page, step, flow, clicked element,
    /// place, platform or technology row - of fewer than
    /// <see cref="ReportScopeResolver.MinimumPeopleWithoutSeePii"/> people is left out, with the
    /// smallest row shown. That reader's copy is cut from the section built for one row more than any
    /// list can hold (<see cref="WebActivityQuery.ForFloor"/>), floored over the first
    /// <see cref="WebActivityQuery.MaximumTop"/> rows - the row past them, when there is one, counting
    /// as a row left out - so the rows left out never depend on how many rows were asked for, and the
    /// total less the rows shown always includes a row of at least the minimum. The Pages tab sends that
    /// reader no figure counted in visits, which the Visits and Journeys tabs floor on their own. Each
    /// export is the same rows as its screen, or refused when its screen withholds them.
    /// </para>
    /// </remarks>
    [Authorize]
    [Route("api/WebActivity")]
    [ApiReplyExceptionFilter]
    public class WebActivityAPIController : ControllerBase
    {
        /// <summary>
        /// How long a section is cached. Short enough that a fresh import shows up quickly, long
        /// enough that tab-switching and a page reload are instant.
        /// </summary>
        private const int CacheSeconds = 60;

        /// <summary>
        /// Builds currently in progress, keyed by cache key, so concurrent cold callers share one.
        /// </summary>
        private static readonly ConcurrentDictionary<string, Lazy<Task<object>>> InFlight =
            new ConcurrentDictionary<string, Lazy<Task<object>>>(StringComparer.Ordinal);

        private readonly IWebActivityStore _store;
        private readonly Func<WebActivitySources> _sourcesFactory;
        private readonly ReportScopeResolver _scopes;

        /// <summary>Where a failed section's classification and error reference go (<see cref="ReportQueryFailure.Log"/>).</summary>
        private static readonly Lazy<DataUtils.AnalyticsLogger> ProductionLogger = new Lazy<DataUtils.AnalyticsLogger>(
            () => new DataUtils.AnalyticsLogger(new AppConfig().AppInsightsConnectionString, "WebActivity"));

        public WebActivityAPIController()
            : this(
                new SqlWebActivityStore(DefaultAnalyticsDbContextFactory.Instance, WebActivitySql.CommandTimeoutSeconds, ProductionLogger.Value),
                () => WebActivitySources.FromConfig(new AppConfig()),
                ReportScopeResolver.Default)
        {
        }

        /// <summary>
        /// Testable entry point. The store's queries are raw SQL, so a broken statement only shows up
        /// when it actually runs - which is why the integration test points a real store at a real,
        /// migrated database rather than trusting that this compiles. No global filter unless
        /// <paramref name="scopes"/> supplies one.
        /// </summary>
        internal WebActivityAPIController(
            IWebActivityStore store, Func<WebActivitySources> sourcesFactory, ReportScopeResolver scopes = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _sourcesFactory = sourcesFactory ?? throw new ArgumentNullException(nameof(sourcesFactory));
            _scopes = scopes ?? new ReportScopeResolver(GlobalFilterProviders.None, CachedUserDirectorySource.Default);
        }

        // GET: api/WebActivity/availability
        [HttpGet]
        [Route("availability")]
        public async Task<IActionResult> Availability()
        {
            // ReadSources marks an unreadable configuration rather than throwing, and Build reads that
            // flag - so an unloadable config is reported as "the toggles could not be read" instead of
            // as a tenant that has deliberately switched every import off.
            var sources = ReadSources();
            var collection = await _store.GetCollectionStatusAsync().ConfigureAwait(false);
            var optional = await _store.GetOptionalFeatureUseAsync().ConfigureAwait(false);

            return Ok(WebActivityAvailability.Build(
                sources,
                collection,
                optional?.Item1,
                optional?.Item2,
                DateTime.UtcNow));
        }

        // GET: api/WebActivity/overview?days=28
        [HttpGet]
        [Route("overview")]
        public async Task<IActionResult> Overview(int days = WebActivityQuery.DefaultWindowDays)
        {
            var query = await BuildQueryAsync(days).ConfigureAwait(false);
            return await ForReaderAsync("overview", query, q => _store.GetOverviewAsync(q, ReadSources()),
                (o, rows) => o.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii, rows)).ConfigureAwait(false);
        }

        // GET: api/WebActivity/visits?days=28
        [HttpGet]
        [Route("visits")]
        public async Task<IActionResult> Visits(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            return await ForReaderAsync("visits", query, q => _store.GetVisitsAsync(q),
                (v, rows) => v.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii, rows)).ConfigureAwait(false);
        }

        // GET: api/WebActivity/pages?days=28
        [HttpGet]
        [Route("pages")]
        public async Task<IActionResult> Pages(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            return await ForReaderAsync("pages", query, q => _store.GetPagesAsync(q),
                (p, rows) => p.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii, rows)).ConfigureAwait(false);
        }

        // GET: api/WebActivity/journeys?days=28
        [HttpGet]
        [Route("journeys")]
        public async Task<IActionResult> Journeys(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            return await ForReaderAsync("journeys", query, q => _store.GetJourneysAsync(q),
                (j, rows) => j.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii, rows)).ConfigureAwait(false);
        }

        // GET: api/WebActivity/geography?days=28
        [HttpGet]
        [Route("geography")]
        public async Task<IActionResult> Geography(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            return await ForReaderAsync("geography", query, q => _store.GetGeographyAsync(q),
                (g, rows) => g.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii, rows)).ConfigureAwait(false);
        }

        // GET: api/WebActivity/search?days=28
        [HttpGet]
        [Route("search")]
        public async Task<IActionResult> Search(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            return await CachedAsync("search", query, () => _store.GetSearchAsync(query),
                ForReader<WebActivitySearch>(s => s.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii))).ConfigureAwait(false);
        }

        // GET: api/WebActivity/technology?days=28
        [HttpGet]
        [Route("technology")]
        public async Task<IActionResult> Technology(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            if (PortalAccess.Evaluate(Request, User).SeePii)
            {
                return await CachedAsync("technology", query, () => _store.GetTechnologyAsync(query)).ConfigureAwait(false);
            }

            // A detail row names a city, so it may name only one Geography shows this reader (#844).
            var cities = await CitiesShownWithoutSeePiiAsync(query).ConfigureAwait(false);
            return await ForReaderAsync("technology", query, q => _store.GetTechnologyAsync(q),
                (t, rows) => t.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii, cities, rows)).ConfigureAwait(false);
        }

        /// <summary>
        /// A section as a CSV download.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Exports are capped at <see cref="WebActivityQuery.MaximumTop"/> rather than the page's
        /// display row count: someone clicking Export on a content-pruning list wants the rows they
        /// asked for, not the fifteen the chart happened to show.
        /// </para>
        /// <para>
        /// A failed query is a 503, NOT an empty file. The store captures a timeout as a per-section
        /// error and returns no rows, so serialising them anyway would hand an admin a spreadsheet
        /// containing only a header row - which reads as "your intranet has no quiet pages" and is
        /// exactly the wrong conclusion.
        /// </para>
        /// <para>
        /// A file is never less redacted than its screen (#844). For a reader without See PII, the search
        /// terms, which the screen withholds, are refused with the permission they need, for the same
        /// reason a failure is not an empty file; every other file carries the screen's floored rows, from
        /// the same section and floored the same way, cut to <paramref name="top"/> - so a file of <c>top</c>
        /// rows is the first rows of the file of every row - and the page files carry no figure counted in
        /// visits, as the Pages tab does not.
        /// </para>
        /// </remarks>
        // GET: api/WebActivity/export/quiet-pages?days=28&top=500
        [HttpGet]
        [Route("export/{section}")]
        public async Task<IActionResult> Export(
            string section,
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.MaximumTop)
        {
            if (!WebActivityExports.IsKnownSection(section))
            {
                return NotFound(
                    "Unknown export. Supported sections: "
                    + string.Join(", ", WebActivityExports.Sections) + ".");
            }

            var seePii = PortalAccess.Evaluate(Request, User).SeePii;
            if (!seePii && WebActivityExports.WithheldWithoutSeePii(section))
            {
                return PortalPermissionDenied.Result(Request, PortalPermission.SeePii);
            }

            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            var normalised = section.ToLowerInvariant();

            // A file narrowed by the administrator's global filter says so in its name, because a file
            // outlives the page it was downloaded from and is forwarded without it.
            var fileNamePrefix = WebActivityExports.FileNamePrefix(normalised) + (query.UserScope.IsRestricted ? "-filtered" : string.Empty);

            // Exports rebuild a whole tab, so a repeated click - or a script - would re-run every
            // query behind it. Cached on its own key (the display cache is capped at a different row
            // count, so the two can never be confused), and per kind of reader, because a reader
            // without See PII is sent fewer rows.
            var cacheKey = query.CacheKey("export::" + normalised + (seePii ? string.Empty : "::without-see-pii"));
            if (MemoryCache.Default.Get(cacheKey) is byte[] cachedCsv)
            {
                return CsvResponse(cachedCsv, CsvSerialiser.FileName(fileNamePrefix, DateTime.UtcNow));
            }

            byte[] csv;
            string failure;

            // Never less redacted than the page: for a reader without See PII, every file carries the rows the page shows
            // them - from the same section, floored the same way, then cut to the rows asked for - with the same rows of too
            // few people left out.
            var minimum = ReportScopeResolver.MinimumPeopleWithoutSeePii;
            var forFloor = query.ForFloor();
            Task<T> SectionAsync<T>(string name, Func<WebActivityQuery, Task<T>> build) where T : class =>
                seePii ? build(query) : CachedModelAsync(name, forFloor, () => build(forFloor));

            switch (normalised)
            {
                case "quiet-pages":
                {
                    var pages = await SectionAsync("pages", q => _store.GetPagesAsync(q)).ConfigureAwait(false);
                    failure = ErrorFor(pages, "pages-quiet");
                    if (!seePii) pages = pages.WithoutSmallGroups(minimum, query.Top);
                    csv = CsvSerialiser.ToBytes(pages.QuietPages, WebActivityExports.PageTrafficColumns(includeVisitFigures: seePii));
                    break;
                }

                case "slow-pages":
                {
                    var pages = await SectionAsync("pages", q => _store.GetPagesAsync(q)).ConfigureAwait(false);
                    failure = ErrorFor(pages, "pages-slowest");
                    if (!seePii) pages = pages.WithoutSmallGroups(minimum, query.Top);
                    csv = CsvSerialiser.ToBytes(pages.SlowestPages, WebActivityExports.PageTrafficColumns(includeVisitFigures: seePii));
                    break;
                }

                case "entry-pages":
                {
                    var journeys = await SectionAsync("journeys", q => _store.GetJourneysAsync(q)).ConfigureAwait(false);
                    failure = ErrorFor(journeys, "journeys-entry");
                    if (!seePii) journeys = journeys.WithoutSmallGroups(minimum, query.Top);
                    csv = CsvSerialiser.ToBytes(
                        journeys.EntryPages,
                        WebActivityExports.EndpointPageColumns("Visits entering here", includeBounce: true));
                    break;
                }

                case "exit-pages":
                {
                    var journeys = await SectionAsync("journeys", q => _store.GetJourneysAsync(q)).ConfigureAwait(false);
                    failure = ErrorFor(journeys, "journeys-exit");
                    if (!seePii) journeys = journeys.WithoutSmallGroups(minimum, query.Top);
                    csv = CsvSerialiser.ToBytes(
                        journeys.ExitPages,
                        WebActivityExports.EndpointPageColumns("Visits ending here", includeBounce: false));
                    break;
                }

                case "transitions":
                {
                    var journeys = await SectionAsync("journeys", q => _store.GetJourneysAsync(q)).ConfigureAwait(false);
                    failure = ErrorFor(journeys, "journeys-transitions");
                    if (!seePii) journeys = journeys.WithoutSmallGroups(minimum, query.Top);
                    csv = CsvSerialiser.ToBytes(journeys.Transitions, WebActivityExports.TransitionColumns());
                    break;
                }

                case "flows":
                {
                    var journeys = await SectionAsync("journeys", q => _store.GetJourneysAsync(q)).ConfigureAwait(false);
                    failure = ErrorFor(journeys, "journeys-flows");
                    if (!seePii) journeys = journeys.WithoutSmallGroups(minimum, query.Top);
                    csv = CsvSerialiser.ToBytes(journeys.Flows, WebActivityExports.FlowColumns());
                    break;
                }

                case "search-terms":
                {
                    var search = await _store.GetSearchAsync(query).ConfigureAwait(false);
                    failure = ErrorFor(search, "search-terms");
                    csv = CsvSerialiser.ToBytes(search.TopTerms, WebActivityExports.SearchTermColumns());
                    break;
                }

                case "technology":
                {
                    var technology = await SectionAsync("technology", q => _store.GetTechnologyAsync(q)).ConfigureAwait(false);
                    failure = ErrorFor(technology, "tech-detail");
                    if (!seePii)
                    {
                        var cities = await CitiesShownWithoutSeePiiAsync(query).ConfigureAwait(false);
                        technology = technology.WithoutSmallGroups(minimum, cities, query.Top);
                    }

                    csv = CsvSerialiser.ToBytes(technology.Detail, WebActivityExports.TechnologyColumns());
                    break;
                }

                default:
                {
                    var pages = await SectionAsync("pages", q => _store.GetPagesAsync(q)).ConfigureAwait(false);
                    failure = ErrorFor(pages, "pages-top");
                    if (!seePii) pages = pages.WithoutSmallGroups(minimum, query.Top);
                    csv = CsvSerialiser.ToBytes(pages.TopPages, WebActivityExports.PageColumns(includeVisitFigures: seePii));
                    break;
                }
            }

            if (failure != null)
            {
                return StatusCode(
                    (int)HttpStatusCode.ServiceUnavailable,
                    "The query behind this export could not be completed, so the file would have been "
                    + "empty rather than genuinely empty: " + failure);
            }

            MemoryCache.Default.Set(cacheKey, csv, DateTimeOffset.UtcNow.AddSeconds(CacheSeconds));

            return CsvResponse(csv, CsvSerialiser.FileName(fileNamePrefix, DateTime.UtcNow));
        }

        /// <summary>The error from the query that produced an export's rows, or null when it succeeded.</summary>
        private static string ErrorFor(WebActivitySection section, string queryKey)
        {
            return section?.Queries?.FirstOrDefault(q => q.Key == queryKey)?.Error;
        }

        #region Helpers

        /// <summary>
        /// Reads the import toggles, marking a configuration failure rather than throwing.
        /// </summary>
        /// <remarks>
        /// A throwing <see cref="AppConfig"/> must not take the page down with a 500: the availability
        /// model's whole job is to explain why data is missing, and it can still do that when the
        /// reason is that configuration could not be read. It returns <c>Readable = false</c> rather
        /// than a blank object, because a blank object is indistinguishable from a tenant that has
        /// deliberately switched every import off - and only one of those is a reason to open the
        /// installer.
        /// </remarks>
        private WebActivitySources ReadSources()
        {
            try
            {
                return _sourcesFactory() ?? new WebActivitySources { Readable = false };
            }
            catch (Exception)
            {
                return new WebActivitySources { Readable = false };
            }
        }

        /// <summary>
        /// The window, narrowed to the people the administrator's global filter leaves this reader seeing.
        /// </summary>
        /// <remarks>
        /// Resolved on every request rather than trusted from the page: the filter is enforced here,
        /// whatever the page sends. A filter that cannot be evaluated refuses the request (503) rather than
        /// answering for the whole tenant.
        /// </remarks>
        private async Task<WebActivityQuery> BuildQueryAsync(int days, int top = WebActivityQuery.DefaultTop)
        {
            var query = WebActivityQuery.Create(days, DateTime.UtcNow, top);
            var scope = await _scopes.ResolveAsync(Request, User, null, CancellationToken.None).ConfigureAwait(false);
            return scope.IsRestricted ? query.WithUserScope(scope.Sql) : query;
        }

        /// <summary>
        /// Serves a section from the short-lived cache, or builds and caches it.
        /// </summary>
        /// <param name="forReader">
        /// Applied to what this reader is sent, never to what is cached: the cache holds the complete
        /// section, and <paramref name="forReader"/> returns a trimmed copy for a reader who may not see
        /// all of it. Null sends the section as it is.
        /// </param>
        /// <remarks>
        /// Single-flight: concurrent callers that miss the same key share ONE build rather than each
        /// starting their own batch of section queries. Without this, five admins opening the same
        /// tab on a cold cache queue five times the work against a database that is, by assumption,
        /// already slow - which is exactly when they would do it.
        /// <para>
        /// The shared build is deliberately NOT tied to any one caller's cancellation. The whole
        /// point is that several requests await the same work, so the first one to disconnect must
        /// not cancel it out from under the others.
        /// </para>
        /// </remarks>
        private async Task<IActionResult> CachedAsync<T>(
            string section,
            WebActivityQuery query,
            Func<Task<T>> build,
            Func<T, T> forReader = null)
            where T : class
        {
            var model = await CachedModelAsync(section, query, build).ConfigureAwait(false);
            return Ok(forReader == null ? model : forReader(model));
        }

        /// <summary>
        /// The complete section from the short-lived cache, or built, cached and returned - the section every reader's answer
        /// is trimmed from, and so also what another section may be floored against.
        /// </summary>
        private static async Task<T> CachedModelAsync<T>(string section, WebActivityQuery query, Func<Task<T>> build)
            where T : class
        {
            var key = query.CacheKey(section);

            if (MemoryCache.Default.Get(key) is T cached)
            {
                return cached;
            }

            var lazy = InFlight.GetOrAdd(
                key,
                _ => new Lazy<Task<object>>(
                    () => Task.Run(async () => (object)await build().ConfigureAwait(false)),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            T model;
            try
            {
                model = (T)await lazy.Value.ConfigureAwait(false);
            }
            finally
            {
                Lazy<Task<object>> mine;
                InFlight.TryRemove(key, out mine);
            }

            // Cached even when individual sections errored. The per-section error is itself the
            // diagnostic, and re-running a query that just timed out on every reload only makes a
            // struggling database worse.
            MemoryCache.Default.Set(key, model, DateTimeOffset.UtcNow.AddSeconds(CacheSeconds));
            return model;
        }

        /// <summary>
        /// The cities a reader without See PII is shown on the Geography tab for <paramref name="query"/> (#844): its cached
        /// section for that reader (<see cref="WebActivityQuery.ForFloor"/>), floored as that tab floors it, so the two tabs
        /// cannot disagree whatever row count either is asked for. A Geography query that failed shows no city, so no detail
        /// row may name one.
        /// </summary>
        private async Task<ISet<string>> CitiesShownWithoutSeePiiAsync(WebActivityQuery query)
        {
            var forFloor = query.ForFloor();
            var geography = await CachedModelAsync("geography", forFloor, () => _store.GetGeographyAsync(forFloor)).ConfigureAwait(false);
            return geography.CityNamesShownWithoutSeePii(ReportScopeResolver.MinimumPeopleWithoutSeePii);
        }

        /// <summary>
        /// A section as this reader is sent it (#844). A See PII reader is sent the section built for the rows they asked for,
        /// as it was built. A reader without See PII is sent the section built for one row more than any list can hold
        /// (<see cref="WebActivityQuery.ForFloor"/>, cached under its own key), floored over the first
        /// <see cref="WebActivityQuery.MaximumTop"/> rows - the row past them telling whether the list was cut there - and
        /// then cut to the rows they asked for by <paramref name="withoutSmallGroups"/>: so a shorter answer is always the
        /// first rows of a longer one, never a list floored again over fewer rows - which would show a row the longer list
        /// leaves out as its smallest shown, and give back the small rows' figures with it.
        /// </summary>
        private async Task<IActionResult> ForReaderAsync<T>(
            string section,
            WebActivityQuery query,
            Func<WebActivityQuery, Task<T>> build,
            Func<T, int, T> withoutSmallGroups)
            where T : class
        {
            if (PortalAccess.Evaluate(Request, User).SeePii)
            {
                return Ok(await CachedModelAsync(section, query, () => build(query)).ConfigureAwait(false));
            }

            var forFloor = query.ForFloor();
            var model = await CachedModelAsync(section, forFloor, () => build(forFloor)).ConfigureAwait(false);
            return Ok(withoutSmallGroups(model, query.Top));
        }

        /// <summary>
        /// What a section is trimmed with for this reader: nothing when they hold See PII, otherwise
        /// <paramref name="trim"/>, which returns a copy (#844).
        /// </summary>
        private Func<T, T> ForReader<T>(Func<T, T> trim) where T : class
        {
            return PortalAccess.Evaluate(Request, User).SeePii ? null : trim;
        }

        private static IActionResult CsvResponse(byte[] csv, string fileName)
        {
            // File() sets Content-Type and an attachment Content-Disposition with the given name.
            return new FileContentResult(csv, "text/csv; charset=utf-8") { FileDownloadName = fileName };
        }

        #endregion
    }
}
