using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.TeamsExplorer;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
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
    /// <para>
    /// People are behind the portal's See PII permission (#661): without it the People tab keeps its
    /// power users per department but loses the champion and dormant lists, their exports are refused,
    /// and the meetings section is served without its named leaderboards. The cached section is the
    /// complete one; a reader without the permission gets a trimmed copy of it.
    /// </para>
    /// </remarks>
    [Authorize]
    [Route("api/TeamsExplorer")]
    [ApiReplyExceptionFilter]
    public class TeamsExplorerAPIController : ControllerBase
    {
        /// <summary>
        /// How long a section is cached. Short enough that a fresh import shows up quickly, long
        /// enough that tab-switching and a page reload are instant.
        /// </summary>
        private const int CacheSeconds = 60;

        private readonly ITeamsExplorerStore _store;
        private readonly Func<TeamsExplorerSources> _sourcesFactory;
        private readonly ReportScopeResolver _scopes;
        private readonly ILogger _logger;

        private static readonly Lazy<AnalyticsLogger> ProductionLogger = new Lazy<AnalyticsLogger>(
            () => new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, "TeamsExplorer"));

        public TeamsExplorerAPIController()
            : this(
                new SqlTeamsExplorerStore(DefaultAnalyticsDbContextFactory.Instance, ProductionLogger.Value),
                () => TeamsExplorerSources.FromConfig(new AppConfig()),
                ReportScopeResolver.Default,
                ProductionLogger.Value)
        {
        }

        /// <summary>
        /// Testable entry point. The store's queries are raw SQL, so a broken statement only shows up
        /// when it actually runs - which is why the integration test points a real store at a real,
        /// migrated database rather than trusting that this compiles. No global filter unless
        /// <paramref name="scopes"/> supplies one.
        /// </summary>
        internal TeamsExplorerAPIController(
            ITeamsExplorerStore store, Func<TeamsExplorerSources> sourcesFactory, ReportScopeResolver scopes = null, ILogger logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _sourcesFactory = sourcesFactory ?? throw new ArgumentNullException(nameof(sourcesFactory));
            _scopes = scopes ?? new ReportScopeResolver(GlobalFilterProviders.None, CachedUserDirectorySource.Default);
            _logger = logger;
        }

        // GET: api/TeamsExplorer/availability
        [HttpGet]
        [Route("availability")]
        public async Task<IActionResult> Availability()
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
        public async Task<IActionResult> Overview(int days = TeamsExplorerQuery.DefaultWindowDays)
        {
            var query = await BuildQueryAsync(days).ConfigureAwait(false);
            return await CachedAsync("overview", query, () => _store.GetOverviewAsync(query, ReadSources())).ConfigureAwait(false);
        }

        // GET: api/TeamsExplorer/adoption?days=28&groupBy=department
        [HttpGet]
        [Route("adoption")]
        public async Task<IActionResult> Adoption(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            string groupBy = TeamsExplorerQuery.DefaultGrouping,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, groupBy, top).ConfigureAwait(false);
            return await CachedAsync("adoption", query, () => _store.GetAdoptionAsync(query)).ConfigureAwait(false);
        }

        // GET: api/TeamsExplorer/meetings?days=28
        [HttpGet]
        [Route("meetings")]
        public async Task<IActionResult> Meetings(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, TeamsExplorerQuery.DefaultGrouping, top).ConfigureAwait(false);
            Func<TeamsMeetings, TeamsMeetings> forReader = null;
            if (!PortalAccess.Evaluate(Request, User).SeePii) forReader = m => m.WithoutIndividualData();
            return await CachedAsync("meetings", query, () => _store.GetMeetingsAsync(query), forReader).ConfigureAwait(false);
        }

        // GET: api/TeamsExplorer/collaboration?days=28
        [HttpGet]
        [Route("collaboration")]
        public async Task<IActionResult> Collaboration(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, TeamsExplorerQuery.DefaultGrouping, top).ConfigureAwait(false);
            return await CachedAsync("collaboration", query, () => _store.GetCollaborationAsync(query)).ConfigureAwait(false);
        }

        // GET: api/TeamsExplorer/conversations?days=28
        [HttpGet]
        [Route("conversations")]
        public async Task<IActionResult> Conversations(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, TeamsExplorerQuery.DefaultGrouping, top).ConfigureAwait(false);
            var cognitive = ReadSources().Cognitive;
            return await CachedAsync("conversations", query, () => _store.GetConversationsAsync(query, cognitive)).ConfigureAwait(false);
        }

        // GET: api/TeamsExplorer/people?days=28&top=20
        [HttpGet]
        [Route("people")]
        public async Task<IActionResult> People(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var query = await BuildQueryAsync(days, TeamsExplorerQuery.DefaultGrouping, top).ConfigureAwait(false);
            Func<TeamsPeople, TeamsPeople> forReader = null;
            if (!PortalAccess.Evaluate(Request, User).SeePii) forReader = p => p.WithoutIndividualData();
            return await CachedAsync("people", query, () => _store.GetPeopleAsync(query), forReader).ConfigureAwait(false);
        }

        /// <summary>
        /// A section as a CSV download.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Exports are NOT served from the section cache. A cached payload is capped at the page's
        /// display row count, and someone clicking Export wants the rows they asked for, not the
        /// twenty the chart happened to show.
        /// </para>
        /// <para>
        /// A failed query is a 503, NOT an empty file - as in <c>WebActivityAPIController.Export</c>. The store
        /// captures a failure as a per-section error and returns no rows, so serialising them anyway would hand an
        /// admin a header-only spreadsheet that reads as "nobody is dormant". Only the query that produces the
        /// file's rows decides: another section of the same tab failing does not spoil a good export. The reply
        /// carries the failed query's key and no SQL text; the page writes its own message for any failure.
        /// </para>
        /// </remarks>
        // GET: api/TeamsExplorer/export/people?days=28&top=500
        [HttpGet]
        [Route("export/{section}")]
        public async Task<IActionResult> Export(
            string section,
            int days = TeamsExplorerQuery.DefaultWindowDays,
            string groupBy = TeamsExplorerQuery.DefaultGrouping,
            int top = TeamsExplorerQuery.MaximumTop)
        {
            if (!TeamsExplorerExports.IsKnownSection(section))
            {
                return NotFound(
                    "Unknown export. Supported sections: "
                    + string.Join(", ", TeamsExplorerExports.Sections) + ".");
            }

            if (TeamsExplorerExports.NamesPeople(section) && !PortalAccess.Evaluate(Request, User).SeePii)
            {
                return PortalPermissionDenied.Result(Request, PortalPermission.SeePii);
            }

            var query = await BuildQueryAsync(days, groupBy, top).ConfigureAwait(false);
            var normalised = section.ToLowerInvariant();
            byte[] csv;
            string failedQuery;
            TeamsExplorerQueryInfo failedInfo;
            switch (normalised)
            {
                case "dormant":
                {
                    var people = await _store.GetPeopleAsync(query).ConfigureAwait(false);
                    failedQuery = FailedQuery(people, "people-dormant", out failedInfo);
                    csv = CsvSerialiser.ToBytes(people.Dormant, TeamsExplorerExports.PersonColumns());
                    break;
                }

                case "teams":
                {
                    var collaboration = await _store.GetCollaborationAsync(query).ConfigureAwait(false);
                    failedQuery = FailedQuery(collaboration, "collab-teams", out failedInfo);
                    csv = CsvSerialiser.ToBytes(collaboration.Teams, TeamsExplorerExports.TeamColumns());
                    break;
                }

                case "channels":
                {
                    var collaboration = await _store.GetCollaborationAsync(query).ConfigureAwait(false);
                    failedQuery = FailedQuery(collaboration, "collab-channels", out failedInfo);
                    csv = CsvSerialiser.ToBytes(collaboration.Channels, TeamsExplorerExports.ChannelColumns());
                    break;
                }

                case "adoption":
                {
                    var adoption = await _store.GetAdoptionAsync(query).ConfigureAwait(false);
                    failedQuery = FailedQuery(adoption, "adoption-breakdown", out failedInfo);
                    csv = CsvSerialiser.ToBytes(
                        adoption.Breakdown,
                        TeamsExplorerExports.DemographicColumns(query.GroupBy));
                    break;
                }

                default:
                {
                    var people = await _store.GetPeopleAsync(query).ConfigureAwait(false);
                    failedQuery = FailedQuery(people, "people-champions", out failedInfo);
                    csv = CsvSerialiser.ToBytes(people.Champions, TeamsExplorerExports.PersonColumns());
                    break;
                }
            }

            if (failedQuery != null)
            {
                LogExportFailure(normalised, failedQuery, failedInfo);
                return StatusCode((int)HttpStatusCode.ServiceUnavailable, new TeamsExplorerExportFailure
                {
                    Code = TeamsExplorerExportFailure.QueryFailedCode,
                    QueryKey = failedQuery,
                });
            }

            // A file narrowed by the administrator's global filter says so in its name: it outlives the page.
            return CsvResponse(csv, CsvSerialiser.FileName(
                TeamsExplorerExports.FileNamePrefix(normalised) + (query.UserScope.IsRestricted ? "-filtered" : string.Empty),
                DateTime.UtcNow));
        }

        /// <summary>The key of the query behind an export's rows when that query failed; null when it succeeded.</summary>
        private static string FailedQuery(TeamsExplorerSection section, string queryKey, out TeamsExplorerQueryInfo info)
        {
            info = section?.Queries?.FirstOrDefault(q => q != null && q.Key == queryKey && !string.IsNullOrEmpty(q.Error));
            return info != null ? queryKey : null;
        }

        /// <summary>
        /// The 503 carries no SQL detail, so this is where an operator learns an export failed. The store has already
        /// logged the exception type, SQL error number and timeout flag for the same query key.
        /// </summary>
        private void LogExportFailure(string section, string queryKey, TeamsExplorerQueryInfo info)
        {
            if (_logger == null) return;

            try
            {
                var message = $"TeamsExplorer export failed: section={section} key={queryKey} elapsedMs={info?.ElapsedMs ?? 0} status=503";
                _logger.Log(LogLevel.Error, 0, message, null, (state, _) => state);
            }
            catch (Exception)
            {
                // Logging must not change the 503 the caller gets.
            }
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

        /// <summary>
        /// The window, narrowed to the people the administrator's global filter leaves this reader seeing.
        /// Resolved on every request rather than trusted from the page; a filter that cannot be evaluated
        /// refuses the request (503) rather than answering for the whole tenant.
        /// </summary>
        private async Task<TeamsExplorerQuery> BuildQueryAsync(
            int days,
            string groupBy = TeamsExplorerQuery.DefaultGrouping,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var query = TeamsExplorerQuery.Create(days, DateTime.UtcNow, groupBy, top);
            var scope = await _scopes.ResolveAsync(Request, User, null, CancellationToken.None).ConfigureAwait(false);
            return scope.IsRestricted ? query.WithUserScope(scope.Sql) : query;
        }

        /// <summary>Serves a section from the short-lived cache, or builds and caches it.</summary>
        /// <param name="forReader">
        /// Applied to what this reader is sent, never to what is cached: the cache holds the complete
        /// section, and <paramref name="forReader"/> returns a trimmed copy for a reader who may not see
        /// all of it. Null sends the section as it is.
        /// </param>
        private async Task<IActionResult> CachedAsync<T>(
            string section,
            TeamsExplorerQuery query,
            Func<Task<T>> build,
            Func<T, T> forReader = null)
            where T : class
        {
            var key = query.CacheKey(section);

            if (MemoryCache.Default.Get(key) is T cached)
            {
                return Ok(forReader == null ? cached : forReader(cached));
            }

            var model = await build().ConfigureAwait(false);

            // Cached even when individual sections errored. The per-section error is itself the
            // diagnostic, and re-running a query that just timed out on every reload only makes a
            // struggling database worse.
            MemoryCache.Default.Set(key, model, DateTimeOffset.UtcNow.AddSeconds(CacheSeconds));
            return Ok(forReader == null ? model : forReader(model));
        }

        private static IActionResult CsvResponse(byte[] csv, string fileName)
        {
            // File() sets Content-Type and an attachment Content-Disposition with the given name.
            return new FileContentResult(csv, "text/csv; charset=utf-8") { FileDownloadName = fileName };
        }

        #endregion
    }

    /// <summary>
    /// The reply to an export whose query failed (503): a stable code and the failed query's key, never its SQL
    /// error. The page shows its own translated message for a failed export.
    /// </summary>
    public sealed class TeamsExplorerExportFailure
    {
        public const string QueryFailedCode = "exportQueryFailed";

        [Newtonsoft.Json.JsonProperty("code")]
        public string Code { get; set; }

        /// <summary>The <see cref="TeamsExplorerQueryInfo.Key"/> of the query that failed.</summary>
        [Newtonsoft.Json.JsonProperty("queryKey")]
        public string QueryKey { get; set; }
    }
}
