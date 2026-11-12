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
    /// and the meetings section is served without its named leaderboards. The adoption breakdown - and its
    /// export - and the power users per department fold every group of fewer than
    /// <see cref="ReportScopeResolver.MinimumPeopleWithoutSeePii"/> people into one row for such a reader (#680), so a
    /// one-person job title, office or department is not that person's activity. The cached section is the complete
    /// one; a reader without the permission gets a trimmed copy of it.
    /// </para>
    /// <para>
    /// The same reader is held to that floor in the other sections too (#844): meetings withholds a week of fewer than
    /// five attendees - with the week of fewest attendees still shown, so that the calls and call hours less the weeks shown
    /// are never one week's - the distributions when fewer than five people attended at all, and folds modalities used by fewer;
    /// collaboration leaves out teams - and the channels of teams - with fewer than five members, withholds reactions one
    /// to four people made, folds rarely-used reactions and withholds the tab list; conversations withholds the key phrases
    /// and languages and leaves out the sentiment of teams with fewer than five members. Each such list leaves out the smallest
    /// row shown too, so that the totals beside it less its rows are never one small team's, and is decided over the
    /// export-sized list - its rows beyond counted as left out - before it is cut to the top asked for, so that two tops never
    /// leave out different rows; the power users per department are folded over every department (P4-1). The team and
    /// channel exports match the page.
    /// </para>
    /// <para>
    /// The overview and the rest of the adoption tab are read, for that reader, over the people the adoption breakdown is
    /// made of - the enabled directory users reach is measured against - so that no total less the breakdown's rows leaves
    /// the activity of an account disabled since (#844, S4-1); that section is cached apart from a See PII reader's, whose
    /// figures are unchanged. Then a week, an engagement segment, a lifecycle count or a platform of one to four people is
    /// withheld, with whatever would give it back, and the KPIs when one to four people were active at all.
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

        /// <summary>
        /// How much longer than its ttl a build in progress stays shared (see <see cref="SharedBuild"/>): more than App
        /// Service's 230-second request limit, so no request still waiting can outlive it.
        /// </summary>
        internal static readonly TimeSpan InFlightBuildAllowance = TimeSpan.FromMinutes(5);

        private readonly ITeamsExplorerStore _store;
        private readonly Func<TeamsExplorerSources> _sourcesFactory;
        private readonly ReportScopeResolver _scopes;
        private readonly ILogger _logger;
        private readonly ObjectCache _cache;

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
        /// <param name="cache">Where sections are cached and shared; the process-wide default cache unless a test supplies one.</param>
        internal TeamsExplorerAPIController(
            ITeamsExplorerStore store, Func<TeamsExplorerSources> sourcesFactory, ReportScopeResolver scopes = null, ILogger logger = null,
            ObjectCache cache = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _sourcesFactory = sourcesFactory ?? throw new ArgumentNullException(nameof(sourcesFactory));
            _scopes = scopes ?? new ReportScopeResolver(GlobalFilterProviders.None, CachedUserDirectorySource.Default);
            _logger = logger;
            _cache = cache ?? MemoryCache.Default;
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
            Func<TeamsOverview, TeamsOverview> forReader = null;
            if (!PortalAccess.Evaluate(Request, User).SeePii)
            {
                // Read over the people the Adoption breakdown is made of, and cached apart from a See PII reader's section.
                query = query.WithDirectoryPopulation();
                forReader = o => o.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii);
            }
            return await CachedAsync("overview", query, () => _store.GetOverviewAsync(query, ReadSources()), forReader).ConfigureAwait(false);
        }

        // GET: api/TeamsExplorer/adoption?days=28&groupBy=department
        [HttpGet]
        [Route("adoption")]
        public async Task<IActionResult> Adoption(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            string groupBy = TeamsExplorerQuery.DefaultGrouping,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var seePii = PortalAccess.Evaluate(Request, User).SeePii;
            // Without See PII, small groups are folded over every group, whatever top was asked for, and only then cut to
            // it: see TeamsAdoption.WithFirstGroups and TeamsExplorerQuery.WithEveryRow. The rest of the section is read over
            // the breakdown's own population (TeamsExplorerQuery.WithDirectoryPopulation), and cached apart.
            var query = await BuildQueryAsync(days, groupBy, top).ConfigureAwait(false);
            if (!seePii) query = query.WithEveryRow().WithDirectoryPopulation();
            Func<TeamsAdoption, TeamsAdoption> forReader = null;
            if (!seePii)
            {
                var shown = TeamsExplorerQuery.NormaliseTop(top);
                forReader = a => a.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii).WithFirstGroups(shown);
            }
            return await CachedAsync("adoption", query, () => _store.GetAdoptionAsync(query), forReader).ConfigureAwait(false);
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
            if (!PortalAccess.Evaluate(Request, User).SeePii)
                forReader = m => m.WithoutIndividualData().WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii);
            return await CachedAsync("meetings", query, () => _store.GetMeetingsAsync(query), forReader).ConfigureAwait(false);
        }

        // GET: api/TeamsExplorer/collaboration?days=28
        [HttpGet]
        [Route("collaboration")]
        public async Task<IActionResult> Collaboration(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var seePii = PortalAccess.Evaluate(Request, User).SeePii;
            // For a reader without See PII the leaderboards are floored over the export-sized list, read with one row more to
            // tell whether more exist, and only then cut to top (TeamsCollaboration.WithFirstRows): what is left out never
            // depends on the top asked for (#844, P4-1). That read is cached apart, under its own top.
            var query = await BuildQueryAsync(days, TeamsExplorerQuery.DefaultGrouping, seePii ? top : TeamsExplorerQuery.MaximumTop).ConfigureAwait(false);
            Func<TeamsCollaboration, TeamsCollaboration> forReader = null;
            if (!seePii)
            {
                query = query.WithListProbe();
                var shown = TeamsExplorerQuery.NormaliseTop(top);
                forReader = c => c.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii).WithFirstRows(shown);
            }
            return await CachedAsync("collaboration", query, () => _store.GetCollaborationAsync(query), forReader).ConfigureAwait(false);
        }

        // GET: api/TeamsExplorer/conversations?days=28
        [HttpGet]
        [Route("conversations")]
        public async Task<IActionResult> Conversations(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var seePii = PortalAccess.Evaluate(Request, User).SeePii;
            // As for collaboration: the sentiment lists are floored over the export-sized list before the cut to top.
            var query = await BuildQueryAsync(days, TeamsExplorerQuery.DefaultGrouping, seePii ? top : TeamsExplorerQuery.MaximumTop).ConfigureAwait(false);
            var cognitive = ReadSources().Cognitive;
            Func<TeamsConversations, TeamsConversations> forReader = null;
            if (!seePii)
            {
                query = query.WithListProbe();
                var shown = TeamsExplorerQuery.NormaliseTop(top);
                forReader = c => c.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii).WithFirstRows(shown);
            }
            return await CachedAsync("conversations", query, () => _store.GetConversationsAsync(query, cognitive), forReader).ConfigureAwait(false);
        }

        // GET: api/TeamsExplorer/people?days=28&top=20
        [HttpGet]
        [Route("people")]
        public async Task<IActionResult> People(
            int days = TeamsExplorerQuery.DefaultWindowDays,
            int top = TeamsExplorerQuery.DefaultTop)
        {
            var seePii = PortalAccess.Evaluate(Request, User).SeePii;
            // As for adoption, departments are folded before the cut to top (TeamsPeople.WithFirstDepartments), over every
            // department (TeamsExplorerQuery.WithEveryGroup) - they add up to the Power segment the Overview publishes, over the
            // same people (WithDirectoryPopulation), so none beyond a cut can be worked out from it (#844, P4-1). The lists of
            // people keep MaximumTop: this reader is never sent them.
            var query = await BuildQueryAsync(days, TeamsExplorerQuery.DefaultGrouping, seePii ? top : TeamsExplorerQuery.MaximumTop).ConfigureAwait(false);
            Func<TeamsPeople, TeamsPeople> forReader = null;
            if (!seePii)
            {
                query = query.WithEveryGroup().WithDirectoryPopulation();
                var shown = TeamsExplorerQuery.NormaliseTop(top);
                forReader = p => p.WithoutIndividualData(ReportScopeResolver.MinimumPeopleWithoutSeePii).WithFirstDepartments(shown);
            }
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

            var normalised = section.ToLowerInvariant();
            var seePii = PortalAccess.Evaluate(Request, User).SeePii;
            // The adoption breakdown is folded over every group for a reader without See PII, as on the page; and the team and
            // channel leaderboards floored over the export-sized list read with one row more, then cut to top (#844, P4-1).
            var foldsGroups = normalised == "adoption" && !seePii;
            var floorsList = (normalised == "teams" || normalised == "channels") && !seePii;
            var query = await BuildQueryAsync(days, groupBy, top).ConfigureAwait(false);
            if (foldsGroups) query = query.WithEveryRow();
            if (floorsList) query = query.WithListProbe();
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
                    // Never less redacted than the page: the same teams are left out, and the same reactions withheld.
                    if (floorsList)
                        collaboration = collaboration.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii)
                            .WithFirstRows(TeamsExplorerQuery.NormaliseTop(top));
                    csv = CsvSerialiser.ToBytes(collaboration.Teams, TeamsExplorerExports.TeamColumns());
                    break;
                }

                case "channels":
                {
                    var collaboration = await _store.GetCollaborationAsync(query).ConfigureAwait(false);
                    failedQuery = FailedQuery(collaboration, "collab-channels", out failedInfo);
                    if (floorsList)
                        collaboration = collaboration.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii)
                            .WithFirstRows(TeamsExplorerQuery.NormaliseTop(top));
                    csv = CsvSerialiser.ToBytes(collaboration.Channels, TeamsExplorerExports.ChannelColumns());
                    break;
                }

                case "adoption":
                {
                    var adoption = await _store.GetAdoptionAsync(query).ConfigureAwait(false);
                    failedQuery = FailedQuery(adoption, "adoption-breakdown", out failedInfo);
                    // Never less redacted than the page: the same groups are folded for a reader without See PII.
                    if (foldsGroups)
                        adoption = adoption.WithoutSmallGroups(ReportScopeResolver.MinimumPeopleWithoutSeePii)
                            .WithFirstGroups(TeamsExplorerQuery.NormaliseTop(top));
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
            var query = TeamsExplorerQuery.Create(days, DateTime.UtcNow, groupBy, top)
                .WithUsageReportsGroupFiltered(ReadSources().UsageReportsGroupFiltered);
            var scope = await _scopes.ResolveAsync(Request, User, null, CancellationToken.None).ConfigureAwait(false);
            return scope.IsRestricted ? query.WithUserScope(scope.Sql) : query;
        }

        /// <summary>Serves a section from the short-lived cache, or builds and caches it.</summary>
        /// <param name="forReader">
        /// Applied to what this reader is sent, never to what is cached: the cache holds the complete
        /// section, and <paramref name="forReader"/> returns a trimmed copy for a reader who may not see
        /// all of it. Null sends the section as it is.
        /// </param>
        /// <remarks>
        /// Single-flight: requests that miss the same key while it is being built - a burst of page loads on a cold cache -
        /// share that one build instead of each running every query of the tab. The key carries the reader's scope, so
        /// readers only share a build that answers the same question.
        /// </remarks>
        private async Task<IActionResult> CachedAsync<T>(
            string section,
            TeamsExplorerQuery query,
            Func<Task<T>> build,
            Func<T, T> forReader = null)
            where T : class
        {
            var model = await SharedBuild(_cache, query.CacheKey(section), build, TimeSpan.FromSeconds(CacheSeconds)).ConfigureAwait(false);
            return Ok(forReader == null ? model : forReader(model));
        }

        /// <summary>
        /// The section under <paramref name="key"/>: the cached one, the one being built, or a new build that every request
        /// arriving meanwhile shares - the pattern <c>CopilotInvestmentReportService.BuildAsync</c> uses.
        /// </summary>
        /// <remarks>
        /// The entry is the build's task. Once built it is cached for <paramref name="ttl"/> from when it was ready, even when
        /// individual sections errored: the per-section error is itself the diagnostic, and re-running a query that just timed
        /// out on every reload only makes a struggling database worse. A build that throws is not kept: the entry goes, so the
        /// next request builds again, and the requests already waiting are given the failure.
        /// </remarks>
        internal static async Task<T> SharedBuild<T>(ObjectCache cache, string key, Func<Task<T>> build, TimeSpan ttl)
            where T : class
        {
            var building = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            // The build in progress is kept for as long as a build can take, not for the result's ttl: a build may queue for
            // a query slot and then run every query of the tab, and an entry that expired while it ran would let the next
            // request start a second build of the same section - when the database is already slow. The ttl starts once the
            // build is done (cache.Set below), and a build that throws removes its entry at once.
            if (cache.AddOrGetExisting(key, building.Task, DateTimeOffset.UtcNow.Add(ttl).Add(InFlightBuildAllowance)) is Task<T> shared)
            {
                return await shared.ConfigureAwait(false);
            }

            try
            {
                var model = await build().ConfigureAwait(false);
                building.SetResult(model);
                cache.Set(key, building.Task, DateTimeOffset.UtcNow.Add(ttl));
                return model;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(cache.Get(key), building.Task))
                {
                    cache.Remove(key);
                }
                building.TrySetException(ex);
                // Observed here, so a failure nobody was waiting for is never an unobserved task exception.
                var ignored = building.Task.Exception;
                throw;
            }
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
