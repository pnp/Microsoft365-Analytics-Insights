using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.SpoWebActivity;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Caching;
using System.Threading.Tasks;
using System.Web.Http;

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
    [RoutePrefix("api/WebActivity")]
    public class WebActivityAPIController : ApiController
    {
        /// <summary>
        /// How long a section is cached. Short enough that a fresh import shows up quickly, long
        /// enough that tab-switching and a page reload are instant.
        /// </summary>
        private const int CacheSeconds = 60;

        private readonly IWebActivityStore _store;
        private readonly Func<WebActivitySources> _sourcesFactory;

        public WebActivityAPIController()
            : this(
                new SqlWebActivityStore(DefaultAnalyticsDbContextFactory.Instance),
                () => WebActivitySources.FromConfig(new AppConfig()))
        {
        }

        /// <summary>
        /// Testable entry point. The store's queries are raw SQL, so a broken statement only shows up
        /// when it actually runs - which is why the integration test points a real store at a real,
        /// migrated database rather than trusting that this compiles.
        /// </summary>
        internal WebActivityAPIController(IWebActivityStore store, Func<WebActivitySources> sourcesFactory)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _sourcesFactory = sourcesFactory ?? throw new ArgumentNullException(nameof(sourcesFactory));
        }

        // GET: api/WebActivity/availability
        [HttpGet]
        [Route("availability")]
        public async Task<IHttpActionResult> Availability()
        {
            var sources = ReadSources();
            var lastHit = await _store.GetLastHitAsync().ConfigureAwait(false);
            var optional = await _store.GetOptionalFeatureUseAsync().ConfigureAwait(false);

            return Ok(WebActivityAvailability.Build(
                sources,
                lastHit,
                optional?.Item1,
                optional?.Item2,
                DateTime.UtcNow));
        }

        // GET: api/WebActivity/overview?days=28
        [HttpGet]
        [Route("overview")]
        public Task<IHttpActionResult> Overview(int days = WebActivityQuery.DefaultWindowDays)
        {
            var query = BuildQuery(days);
            return CachedAsync("overview", query, () => _store.GetOverviewAsync(query, ReadSources()));
        }

        // GET: api/WebActivity/visits?days=28
        [HttpGet]
        [Route("visits")]
        public Task<IHttpActionResult> Visits(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = BuildQuery(days, top);
            return CachedAsync("visits", query, () => _store.GetVisitsAsync(query));
        }

        // GET: api/WebActivity/pages?days=28
        [HttpGet]
        [Route("pages")]
        public Task<IHttpActionResult> Pages(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = BuildQuery(days, top);
            return CachedAsync("pages", query, () => _store.GetPagesAsync(query));
        }

        // GET: api/WebActivity/journeys?days=28
        [HttpGet]
        [Route("journeys")]
        public Task<IHttpActionResult> Journeys(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = BuildQuery(days, top);
            return CachedAsync("journeys", query, () => _store.GetJourneysAsync(query));
        }

        // GET: api/WebActivity/geography?days=28
        [HttpGet]
        [Route("geography")]
        public Task<IHttpActionResult> Geography(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = BuildQuery(days, top);
            return CachedAsync("geography", query, () => _store.GetGeographyAsync(query));
        }

        // GET: api/WebActivity/search?days=28
        [HttpGet]
        [Route("search")]
        public Task<IHttpActionResult> Search(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = BuildQuery(days, top);
            return CachedAsync("search", query, () => _store.GetSearchAsync(query));
        }

        // GET: api/WebActivity/technology?days=28
        [HttpGet]
        [Route("technology")]
        public Task<IHttpActionResult> Technology(
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.DefaultTop)
        {
            var query = BuildQuery(days, top);
            return CachedAsync("technology", query, () => _store.GetTechnologyAsync(query));
        }

        /// <summary>
        /// A section as a CSV download.
        /// </summary>
        /// <remarks>
        /// Exports are NOT served from the section cache. A cached payload is capped at the page's
        /// display row count, and someone clicking Export on a content-pruning list wants the rows they
        /// asked for, not the fifteen the chart happened to show.
        /// </remarks>
        // GET: api/WebActivity/export/quiet-pages?days=28&top=500
        [HttpGet]
        [Route("export/{section}")]
        public async Task<HttpResponseMessage> Export(
            string section,
            int days = WebActivityQuery.DefaultWindowDays,
            int top = WebActivityQuery.MaximumTop)
        {
            if (!WebActivityExports.IsKnownSection(section))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        "Unknown export. Supported sections: "
                        + string.Join(", ", WebActivityExports.Sections) + "."),
                };
            }

            var query = BuildQuery(days, top);
            var normalised = section.ToLowerInvariant();
            byte[] csv;

            switch (normalised)
            {
                case "quiet-pages":
                {
                    var pages = await _store.GetPagesAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(pages.QuietPages, WebActivityExports.PageColumns());
                    break;
                }

                case "slow-pages":
                {
                    var pages = await _store.GetPagesAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(pages.SlowestPages, WebActivityExports.PageColumns());
                    break;
                }

                case "entry-pages":
                {
                    var journeys = await _store.GetJourneysAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(journeys.EntryPages, WebActivityExports.PageColumns());
                    break;
                }

                case "exit-pages":
                {
                    var journeys = await _store.GetJourneysAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(journeys.ExitPages, WebActivityExports.PageColumns());
                    break;
                }

                case "transitions":
                {
                    var journeys = await _store.GetJourneysAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(journeys.Transitions, WebActivityExports.TransitionColumns());
                    break;
                }

                case "search-terms":
                {
                    var search = await _store.GetSearchAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(search.TopTerms, WebActivityExports.SearchTermColumns());
                    break;
                }

                case "technology":
                {
                    var technology = await _store.GetTechnologyAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(technology.Detail, WebActivityExports.TechnologyColumns());
                    break;
                }

                default:
                {
                    var pages = await _store.GetPagesAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(pages.TopPages, WebActivityExports.PageColumns());
                    break;
                }
            }

            return CsvResponse(csv, CsvSerialiser.FileName(
                WebActivityExports.FileNamePrefix(normalised),
                DateTime.UtcNow));
        }

        #region Helpers

        /// <summary>
        /// Reads the import toggles, treating a configuration failure as "nothing is available".
        /// </summary>
        /// <remarks>
        /// A throwing <see cref="AppConfig"/> must not take the page down with a 500: the availability
        /// model's whole job is to explain why data is missing, and it can still do that when the
        /// reason is that configuration could not be read.
        /// </remarks>
        private WebActivitySources ReadSources()
        {
            try
            {
                return _sourcesFactory() ?? new WebActivitySources();
            }
            catch (Exception)
            {
                return new WebActivitySources();
            }
        }

        private static WebActivityQuery BuildQuery(int days, int top = WebActivityQuery.DefaultTop)
        {
            return WebActivityQuery.Create(days, DateTime.UtcNow, top);
        }

        /// <summary>Serves a section from the short-lived cache, or builds and caches it.</summary>
        private async Task<IHttpActionResult> CachedAsync<T>(
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

            var model = await build().ConfigureAwait(false);

            // Cached even when individual sections errored. The per-section error is itself the
            // diagnostic, and re-running a query that just timed out on every reload only makes a
            // struggling database worse.
            MemoryCache.Default.Set(key, model, DateTimeOffset.UtcNow.AddSeconds(CacheSeconds));
            return Ok(model);
        }

        private static HttpResponseMessage CsvResponse(byte[] csv, string fileName)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(csv),
            };

            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv") { CharSet = "utf-8" };
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = fileName,
            };

            return response;
        }

        #endregion
    }
}
