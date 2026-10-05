using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.SpoWebActivity;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models.UserFilters;

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
    /// </remarks>
    [Authorize]
    [Route("api/WebActivity")]
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

        public WebActivityAPIController()
            : this(
                new SqlWebActivityStore(DefaultAnalyticsDbContextFactory.Instance),
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
            return await CachedAsync("overview", query, () => _store.GetOverviewAsync(query, ReadSources())).ConfigureAwait(false);
        }

        // GET: api/WebActivity/visits?days=28
        [HttpGet]
        [Route("visits")]
        public async Task<IActionResult> Visits(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            return await CachedAsync("visits", query, () => _store.GetVisitsAsync(query)).ConfigureAwait(false);
        }

        // GET: api/WebActivity/pages?days=28
        [HttpGet]
        [Route("pages")]
        public async Task<IActionResult> Pages(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            return await CachedAsync("pages", query, () => _store.GetPagesAsync(query)).ConfigureAwait(false);
        }

        // GET: api/WebActivity/journeys?days=28
        [HttpGet]
        [Route("journeys")]
        public async Task<IActionResult> Journeys(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            return await CachedAsync("journeys", query, () => _store.GetJourneysAsync(query)).ConfigureAwait(false);
        }

        // GET: api/WebActivity/geography?days=28
        [HttpGet]
        [Route("geography")]
        public async Task<IActionResult> Geography(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            return await CachedAsync("geography", query, () => _store.GetGeographyAsync(query)).ConfigureAwait(false);
        }

        // GET: api/WebActivity/search?days=28
        [HttpGet]
        [Route("search")]
        public async Task<IActionResult> Search(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            return await CachedAsync("search", query, () => _store.GetSearchAsync(query)).ConfigureAwait(false);
        }

        // GET: api/WebActivity/technology?days=28
        [HttpGet]
        [Route("technology")]
        public async Task<IActionResult> Technology(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            return await CachedAsync("technology", query, () => _store.GetTechnologyAsync(query)).ConfigureAwait(false);
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

            var query = await BuildQueryAsync(days, top).ConfigureAwait(false);
            var normalised = section.ToLowerInvariant();

            // A file narrowed by the administrator's global filter says so in its name, because a file
            // outlives the page it was downloaded from and is forwarded without it.
            var fileNamePrefix = WebActivityExports.FileNamePrefix(normalised) + (query.UserScope.IsRestricted ? "-filtered" : string.Empty);

            // Exports rebuild a whole tab, so a repeated click - or a script - would re-run every
            // query behind it. Cached on its own key (the display cache is capped at a different row
            // count, so the two can never be confused).
            var cacheKey = query.CacheKey("export::" + normalised);
            if (MemoryCache.Default.Get(cacheKey) is byte[] cachedCsv)
            {
                return CsvResponse(cachedCsv, CsvSerialiser.FileName(fileNamePrefix, DateTime.UtcNow));
            }

            byte[] csv;
            string failure;

            switch (normalised)
            {
                case "quiet-pages":
                {
                    var pages = await _store.GetPagesAsync(query).ConfigureAwait(false);
                    failure = ErrorFor(pages, "pages-quiet");
                    csv = CsvSerialiser.ToBytes(pages.QuietPages, WebActivityExports.PageTrafficColumns());
                    break;
                }

                case "slow-pages":
                {
                    var pages = await _store.GetPagesAsync(query).ConfigureAwait(false);
                    failure = ErrorFor(pages, "pages-slowest");
                    csv = CsvSerialiser.ToBytes(pages.SlowestPages, WebActivityExports.PageTrafficColumns());
                    break;
                }

                case "entry-pages":
                {
                    var journeys = await _store.GetJourneysAsync(query).ConfigureAwait(false);
                    failure = ErrorFor(journeys, "journeys-entry");
                    csv = CsvSerialiser.ToBytes(
                        journeys.EntryPages,
                        WebActivityExports.EndpointPageColumns("Visits entering here", includeBounce: true));
                    break;
                }

                case "exit-pages":
                {
                    var journeys = await _store.GetJourneysAsync(query).ConfigureAwait(false);
                    failure = ErrorFor(journeys, "journeys-exit");
                    csv = CsvSerialiser.ToBytes(
                        journeys.ExitPages,
                        WebActivityExports.EndpointPageColumns("Visits ending here", includeBounce: false));
                    break;
                }

                case "transitions":
                {
                    var journeys = await _store.GetJourneysAsync(query).ConfigureAwait(false);
                    failure = ErrorFor(journeys, "journeys-transitions");
                    csv = CsvSerialiser.ToBytes(journeys.Transitions, WebActivityExports.TransitionColumns());
                    break;
                }

                case "flows":
                {
                    var journeys = await _store.GetJourneysAsync(query).ConfigureAwait(false);
                    failure = ErrorFor(journeys, "journeys-flows");
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
                    var technology = await _store.GetTechnologyAsync(query).ConfigureAwait(false);
                    failure = ErrorFor(technology, "tech-detail");
                    csv = CsvSerialiser.ToBytes(technology.Detail, WebActivityExports.TechnologyColumns());
                    break;
                }

                default:
                {
                    var pages = await _store.GetPagesAsync(query).ConfigureAwait(false);
                    failure = ErrorFor(pages, "pages-top");
                    csv = CsvSerialiser.ToBytes(pages.TopPages, WebActivityExports.PageColumns());
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
            Func<Task<T>> build)
            where T : class
        {
            var key = query.CacheKey(section);

            if (MemoryCache.Default.Get(key) is T cached)
            {
                return Ok(cached);
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
            return Ok(model);
        }

        private static IActionResult CsvResponse(byte[] csv, string fileName)
        {
            // File() sets Content-Type and an attachment Content-Disposition with the given name.
            return new FileContentResult(csv, "text/csv; charset=utf-8") { FileDownloadName = fileName };
        }

        #endregion
    }
}
