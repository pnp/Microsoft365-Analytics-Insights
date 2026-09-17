using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.TeamsExplorer;
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
    /// Powers the portal's Teams Explorer - "is Teams being used, is it delivering value, and where do
    /// we need to intervene?".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces the archived <c>reports\Misc\Archive\Teams.pbit</c> Power BI report. That report was
    /// built on the <c>vwTeams*</c> SQL views, which is a large part of why it never worked well: the
    /// views join through a date dimension and materialise whole tables, and several of them have
    /// already been dropped or hollowed out (<c>DeprecateTeamsAddons</c>). Nothing here reads a view.
    /// </para>
    /// <para>
    /// The controller is deliberately thin. All query work lives in
    /// <see cref="SqlTeamsExplorerStore"/> and all judgement in <see cref="TeamsExplorerScoring"/>,
    /// both in <c>Common.Entities</c>, so the page can be tested against a real database without an
    /// ASP.NET request pipeline - the same split the Copilot Adoption and Licence activity pages use.
    /// </para>
    /// <para>
    /// Each section is cached briefly. These are multi-query tabs over tables that are still missing
    /// some date indexes, and an admin flipping between tabs or nudging the period should not pay for
    /// a full re-run each time. The cache key carries the window, the grouping and the UTC date, so it
    /// can never be served across a day boundary.
    /// </para>
    /// </remarks>
    [Authorize]
    [RoutePrefix("api/TeamsExplorer")]
    public class TeamsExplorerAPIController : ApiController
    {
        /// <summary>
        /// How long a section is cached. Short enough that a fresh import shows up quickly, long
        /// enough that tab-switching and a page reload are instant.
        /// </summary>
        private const int CacheSeconds = 60;

        private readonly ITeamsExplorerStore _store;
        private readonly Func<TeamsExplorerSources> _sourcesFactory;

        public TeamsExplorerAPIController()
            : this(
                new SqlTeamsExplorerStore(DefaultAnalyticsDbContextFactory.Instance),
                () => TeamsExplorerSources.FromConfig(new AppConfig()))
        {
        }

        /// <summary>
        /// Testable entry point. The store's queries are raw SQL, so a broken statement only shows up
        /// when it actually runs - which is why the integration test points a real store at a real,
        /// migrated database rather than trusting that this compiles.
        /// </summary>
        internal TeamsExplorerAPIController(ITeamsExplorerStore store, Func<TeamsExplorerSources> sourcesFactory)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _sourcesFactory = sourcesFactory ?? throw new ArgumentNullException(nameof(sourcesFactory));
        }

        // GET: api/TeamsExplorer/availability
        [HttpGet]
        [Route("availability")]
        public async Task<IHttpActionResult> Availability()
        {
            var sources = ReadSources();
            var counts = await _store.GetTeamCountsAsync().ConfigureAwait(false);

            return Ok(TeamsExplorerAvailability.Build(
                sources,
                counts?.Item2,
                counts?.Item1));
        }

        // GET: api/TeamsExplorer/overview?days=28
        [HttpGet]
        [Route("overview")]
        public Task<IHttpActionResult> Overview(int days = TeamsExplorerQuery.DefaultWindowDays)
        {
            var query = BuildQuery(days);
            return CachedAsync("overview", query, () => _store.GetOverviewAsync(query, ReadSources()));
        }

        // GET: api/TeamsExplorer/adoption?days=28&groupBy=department
        [HttpGet]
        [Route("adoption")]
        public Task<IHttpActionResult> Adoption(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            string groupBy = TeamsExplorerQuery.DefaultGrouping,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var query = BuildQuery(days, groupBy, top);
            return CachedAsync("adoption", query, () => _store.GetAdoptionAsync(query));
        }

        // GET: api/TeamsExplorer/meetings?days=28
        [HttpGet]
        [Route("meetings")]
        public Task<IHttpActionResult> Meetings(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var query = BuildQuery(days, TeamsExplorerQuery.DefaultGrouping, top);
            return CachedAsync("meetings", query, () => _store.GetMeetingsAsync(query));
        }

        // GET: api/TeamsExplorer/collaboration?days=28
        [HttpGet]
        [Route("collaboration")]
        public Task<IHttpActionResult> Collaboration(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var query = BuildQuery(days, TeamsExplorerQuery.DefaultGrouping, top);
            return CachedAsync("collaboration", query, () => _store.GetCollaborationAsync(query));
        }

        // GET: api/TeamsExplorer/conversations?days=28
        [HttpGet]
        [Route("conversations")]
        public Task<IHttpActionResult> Conversations(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var query = BuildQuery(days, TeamsExplorerQuery.DefaultGrouping, top);
            var cognitive = ReadSources().Cognitive;
            return CachedAsync("conversations", query, () => _store.GetConversationsAsync(query, cognitive));
        }

        // GET: api/TeamsExplorer/people?days=28&top=20
        [HttpGet]
        [Route("people")]
        public Task<IHttpActionResult> People(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var query = BuildQuery(days, TeamsExplorerQuery.DefaultGrouping, top);
            return CachedAsync("people", query, () => _store.GetPeopleAsync(query));
        }

        /// <summary>
        /// A section as a CSV download.
        /// </summary>
        /// <remarks>
        /// Exports are NOT served from the section cache. A cached payload is capped at the page's
        /// display row count, and someone clicking Export wants the rows they asked for, not the
        /// twenty the chart happened to show.
        /// </remarks>
        // GET: api/TeamsExplorer/export/people?days=28&top=500
        [HttpGet]
        [Route("export/{section}")]
        public async Task<HttpResponseMessage> Export(
            string section,
            int days = TeamsExplorerQuery.DefaultWindowDays,
            string groupBy = TeamsExplorerQuery.DefaultGrouping,
            int top = TeamsExplorerQuery.MaximumTop)
        {
            if (!TeamsExplorerExports.IsKnownSection(section))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        "Unknown export. Supported sections: "
                        + string.Join(", ", TeamsExplorerExports.Sections) + "."),
                };
            }

            var query = BuildQuery(days, groupBy, top);
            var normalised = section.ToLowerInvariant();
            byte[] csv;

            switch (normalised)
            {
                case "dormant":
                {
                    var people = await _store.GetPeopleAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(people.Dormant, TeamsExplorerExports.PersonColumns());
                    break;
                }

                case "teams":
                {
                    var collaboration = await _store.GetCollaborationAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(collaboration.Teams, TeamsExplorerExports.TeamColumns());
                    break;
                }

                case "channels":
                {
                    var collaboration = await _store.GetCollaborationAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(collaboration.Channels, TeamsExplorerExports.ChannelColumns());
                    break;
                }

                case "adoption":
                {
                    var adoption = await _store.GetAdoptionAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(
                        adoption.Breakdown,
                        TeamsExplorerExports.DemographicColumns(query.GroupBy));
                    break;
                }

                default:
                {
                    var people = await _store.GetPeopleAsync(query).ConfigureAwait(false);
                    csv = CsvSerialiser.ToBytes(people.Champions, TeamsExplorerExports.PersonColumns());
                    break;
                }
            }

            return CsvResponse(csv, CsvSerialiser.FileName(
                TeamsExplorerExports.FileNamePrefix(normalised),
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
        private TeamsExplorerSources ReadSources()
        {
            try
            {
                return _sourcesFactory() ?? new TeamsExplorerSources();
            }
            catch (Exception)
            {
                return new TeamsExplorerSources();
            }
        }

        private static TeamsExplorerQuery BuildQuery(
            int days,
            string groupBy = TeamsExplorerQuery.DefaultGrouping,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            return TeamsExplorerQuery.Create(days, DateTime.UtcNow, groupBy, top);
        }

        /// <summary>Serves a section from the short-lived cache, or builds and caches it.</summary>
        private async Task<IHttpActionResult> CachedAsync<T>(
            string section,
            TeamsExplorerQuery query,
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
