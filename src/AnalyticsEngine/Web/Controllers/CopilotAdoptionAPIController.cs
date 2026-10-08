using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.LeadershipCohort;
using Common.Entities.UserFilters;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.CopilotAdoption;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Powers the SPA's "Copilot Adoption" area - the licence-adoption tool.
    ///
    /// It answers two questions that decide real money:
    /// <list type="number">
    ///   <item><b>Who holds a Microsoft 365 Copilot licence and is not getting value from it?</b> Not as
    ///   a yes/no, but as a graded engagement score, because almost nobody is either a power user or a
    ///   complete non-user - and "50 people used it once" and "50 people use it daily" produce identical
    ///   "active user" counts while calling for opposite responses.</item>
    ///   <item><b>Who is a heavy Microsoft 365 user without a licence?</b> Ranked by a business case,
    ///   led by the strongest evidence there is: people already using Copilot Chat without a seat.</item>
    /// </list>
    ///
    /// Design notes:
    /// <list type="bullet">
    ///   <item>All the analysis lives in <see cref="CopilotAdoptionService"/> in Common.Entities, not
    ///   here. This controller caches, filters, pages and serialises. That keeps the whole thing
    ///   reusable by a scheduled report web-job later without lifting logic out of a controller.</item>
    ///   <item>The heavy work runs <b>once</b> per (window, licence override) and is cached, so paging,
    ///   sorting, filtering and CSV export are all served from the same in-memory result. That is a
    ///   performance decision, but mostly a correctness one: an exported spreadsheet is guaranteed to
    ///   match the summary it was exported from.</item>
    ///   <item>Concurrent first-hits share one execution (the cache holds the <see cref="Task{T}"/>),
    ///   so a page refresh during a slow analysis cannot start a second full scan of the audit history.</item>
    ///   <item>Per-person lists, their CSV exports and any population-narrowing filter need the portal's
    ///   See PII permission. The tenant-wide summary and workbook are served to everyone, without the
    ///   parts that name a person for a reader who lacks it (#661). The shared cached analysis is never
    ///   edited for one reader.</item>
    /// </list>
    /// </summary>
    [Authorize]
    [RoutePrefix("api/CopilotAdoption")]
    public class CopilotAdoptionAPIController : ApiController
    {
        /// <summary>Windows the UI offers. Anything else is snapped to the nearest, so a hand-edited URL cannot force a year-long scan.</summary>
        private static readonly int[] AllowedWindowDays = { 7, 28, 90, 180 };

        private const int DefaultTake = 50;
        private const int MaxTake = 500;

        /// <summary>
        /// Hard cap on a CSV export. Comfortably above any realistic Copilot seat count while stopping a
        /// single request from materialising an entire directory into one HTTP response.
        /// </summary>
        private const int MaxCsvRows = 100000;

        public CopilotAdoptionAPIController()
            : this(CopilotAdoptionAnalysisCoordinator.Default, CachedUserDirectorySource.Default, ReportScopeResolver.Default)
        {
        }

        internal CopilotAdoptionAPIController(CopilotAdoptionAnalysisCoordinator coordinator)
            : this(coordinator, CachedUserDirectorySource.Default)
        {
        }

        /// <summary>For tests: no global filter, unless one is supplied through <paramref name="scopes"/>.</summary>
        internal CopilotAdoptionAPIController(
            CopilotAdoptionAnalysisCoordinator coordinator, IUserDirectorySource directory, ReportScopeResolver scopes = null)
        {
            Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            Scopes = scopes ?? new ReportScopeResolver(GlobalFilterProviders.None, directory);
        }

        internal CopilotAdoptionAnalysisCoordinator Coordinator { get; }

        /// <summary>The directory snapshot a <c>userFilter</c> is evaluated against.</summary>
        internal IUserDirectorySource Directory { get; }

        /// <summary>Resolves the administrator's global filter and the reader's own filter into one scope.</summary>
        internal ReportScopeResolver Scopes { get; }

        #region Availability

        /// <summary>
        /// Whether this deployment can show the tool at all, and what is missing if it cannot. Called
        /// before anything heavy so the SPA can hide the tab (or explain itself) rather than showing an
        /// empty dashboard that looks like zero adoption.
        /// </summary>
        // GET: api/CopilotAdoption/availability
        [HttpGet]
        [Route("availability")]
        public IHttpActionResult Availability()
        {
            var settings = new AppConfig().ImportJobSettings ?? new ImportTaskSettings();

            var model = new CopilotAdoptionAvailability
            {
                CopilotAuditImportEnabled = settings.Copilot,
                CopilotUsageReportImportEnabled = settings.GraphCopilotUsageReports,
                UserMetadataImportEnabled = settings.GraphUsersMetadata,
                M365UsageReportImportEnabled = settings.GraphUsageReports,
            };

            // Licence data is what makes this a *licence* adoption tool - without the user metadata
            // import there is no way to know who holds a Copilot seat, so nothing here works.
            model.Available = model.UserMetadataImportEnabled
                              && (model.CopilotAuditImportEnabled || model.CopilotUsageReportImportEnabled);

            if (!model.UserMetadataImportEnabled)
            {
                model.Messages.Add(
                    "The user metadata import is disabled, so licence assignments are unknown. Enable it in the "
                    + "installer to identify who holds a Microsoft 365 Copilot licence.");
            }

            if (!model.CopilotAuditImportEnabled && !model.CopilotUsageReportImportEnabled)
            {
                model.Messages.Add(
                    "Neither the Copilot audit import nor the Copilot usage-report import is enabled, so there is "
                    + "no Copilot usage to measure. Enable at least one in the installer.");
            }

            if (!model.CopilotAuditImportEnabled && model.CopilotUsageReportImportEnabled)
            {
                model.Messages.Add(
                    "The Copilot audit import is disabled. Engagement will be based on Microsoft's own usage "
                    + "report, which covers licensed users only - unlicensed Copilot Chat use, per-app breakdowns "
                    + "and Cowork adoption will not be visible.");
            }

            if (!model.M365UsageReportImportEnabled)
            {
                model.Messages.Add(
                    "The Microsoft 365 usage-report import is disabled, so licence candidates can only be ranked "
                    + "on existing unlicensed Copilot use. Enable it to also find heavy Microsoft 365 users who "
                    + "have never tried Copilot.");
            }

            return Ok(model);
        }

        /// <summary>
        /// How long an HTTP request will wait for the analysis before answering "still building".
        /// </summary>
        /// <remarks>
        /// Azure App Service terminates a request at around 230 seconds. On a large tenant this analysis
        /// legitimately takes longer than that - measured well past it at the MEDIAN, not the tail - so a
        /// request that simply awaited the task was killed by the platform and the caller saw a 500 with no
        /// server-side exception to explain it (issue #360).
        ///
        /// The work itself is not tied to the request: it runs on the shared Task held in the cache. So the
        /// request waits a bounded time, and if the analysis has not finished it returns 202 and lets the
        /// caller poll. The analysis carries on and the next poll picks it up.
        ///
        /// Comfortably under the platform limit, and long enough that a small or warm tenant still answers
        /// on the first request rather than being sent round a polling loop for no reason.
        /// </remarks>
        private static readonly TimeSpan FirstResponseBudget = TimeSpan.FromSeconds(20);

        /// <summary>
        /// How long an EXPORT waits for the analysis before giving up.
        /// </summary>
        /// <remarks>
        /// Exports are <c>&lt;a href&gt;</c> navigations, not fetch() calls, so a browser cannot be sent
        /// round a polling loop - it would simply render the 202 body as the "download". They therefore
        /// wait, and the only question is what they do when the wait is hopeless.
        /// <para>
        /// Set below the ~230-second point at which Azure App Service abandons a request. Waiting
        /// indefinitely (the previous behaviour) meant an export started against a cold cache - a direct
        /// link, or a click after the 10-minute entry expired - ran past that limit and the platform
        /// killed it, producing a 500 and a corrupt download with nothing logged. This is strictly
        /// better: every export that used to succeed inside the limit still does, and the one that used
        /// to die now returns something the operator can read and act on.
        /// </para>
        /// </remarks>
        internal static readonly TimeSpan ExportWaitBudget = TimeSpan.FromSeconds(150);

        /// <summary>What the SPA is told to wait before polling again.</summary>
        private const int RetryAfterSeconds = 5;

        /// <summary>
        /// Waits a bounded time for the analysis, returning null when it is still running.
        /// </summary>
        /// <remarks>
        /// Task.WhenAny rather than a cancellable await: the analysis is shared between callers, so one
        /// caller giving up must not cancel it for everyone else. The task is left running in the shared
        /// <see cref="CopilotAdoptionAnalysisCoordinator"/>.
        /// </remarks>
        private async Task<CopilotAdoptionAnalysis> TryGetAnalysisAsync(
            int windowDays, string from, string to, string seatLicenceTypeIds, CancellationToken cancellationToken)
        {
            return await TryGetAnalysisAsync(windowDays, from, to, seatLicenceTypeIds, FirstResponseBudget, cancellationToken);
        }

        /// <summary>
        /// The analysis with its row collections narrowed to the scope - an email domain, a user filter,
        /// or both - for the list, paging and export endpoints.
        /// </summary>
        /// <remarks>
        /// Deliberately the cheap half of the scope filter: these endpoints read rows and data-source
        /// warnings, never the aggregate figures, so there is no reason to re-score the population on
        /// every page of a table. The summary endpoint uses <see cref="TryGetScopedSummaryAsync"/>,
        /// which does.
        /// </remarks>
        private async Task<CopilotAdoptionAnalysis> TryGetScopedRowsAsync(
            int windowDays, string from, string to, string seatLicenceTypeIds, string emailDomain, UserFilterExpression userFilter,
            TimeSpan budget, CancellationToken cancellationToken)
        {
            // Read the directory while the analysis is being waited for, not after it: on a cold
            // process both are slow, and neither depends on the other.
            if (!userFilter.IsEmpty) Directory.Prefetch();

            var analysis = await TryGetAnalysisAsync(windowDays, from, to, seatLicenceTypeIds, budget, cancellationToken);
            if (analysis == null) return null;

            var scope = await ResolveScopeAsync(emailDomain, userFilter, cancellationToken);
            return CopilotAdoptionScopeFilter.FilterRows(analysis, scope);
        }

        private async Task<CopilotAdoptionAnalysis> TryGetScopedRowsAsync(
            int windowDays, string from, string to, string seatLicenceTypeIds, string emailDomain, UserFilterExpression userFilter,
            CancellationToken cancellationToken)
        {
            return await TryGetScopedRowsAsync(
                windowDays, from, to, seatLicenceTypeIds, emailDomain, userFilter, FirstResponseBudget, cancellationToken);
        }

        /// <summary>
        /// The analysis with every aggregate recomputed for the scope.
        /// </summary>
        /// <remarks>
        /// The heavy SQL stays cached tenant-wide and shared - narrowing re-scores the loaded rows in
        /// memory instead of re-querying per domain or per filter, which would multiply the load on a
        /// database the importer is already sharing. Sections that cannot be narrowed are carried across
        /// and named in <see cref="CopilotAdoptionSummary.UnscopedSections"/> so the page can label them.
        /// </remarks>
        private async Task<CopilotAdoptionAnalysis> TryGetScopedSummaryAsync(
            int windowDays, string from, string to, string seatLicenceTypeIds, string emailDomain, UserFilterExpression userFilter,
            TimeSpan budget, CancellationToken cancellationToken)
        {
            var scoped = await TryGetScopedAnalysisAsync(
                windowDays, from, to, seatLicenceTypeIds, emailDomain, userFilter, budget, cancellationToken);
            return scoped.Analysis;
        }

        /// <summary>As <see cref="TryGetScopedSummaryAsync"/>, also saying whether the population was narrowed.</summary>
        private async Task<(CopilotAdoptionAnalysis Analysis, bool Narrowed)> TryGetScopedAnalysisAsync(
            int windowDays, string from, string to, string seatLicenceTypeIds, string emailDomain, UserFilterExpression userFilter,
            TimeSpan budget, CancellationToken cancellationToken)
        {
            if (!userFilter.IsEmpty) Directory.Prefetch();

            var analysis = await TryGetAnalysisAsync(windowDays, from, to, seatLicenceTypeIds, budget, cancellationToken);
            if (analysis == null) return (null, false);

            var scope = await ResolveScopeAsync(emailDomain, userFilter, cancellationToken);
            if (!scope.IsNarrowed) return (analysis, false);

            var service = new CopilotAdoptionService(analysis.Summary.Options);
            return (CopilotAdoptionScopeFilter.Apply(analysis, scope, service.FinaliseSummary), true);
        }

        /// <summary>
        /// The leadership comparison for this response (#654), or null when it cannot be produced. Compared with the
        /// whole tenant only: under a domain, a reader's filter or the administrator's global filter it reports
        /// <see cref="LeadershipComparisonStatuses.ScopedView"/> instead, because a narrowed population can leave a
        /// handful of leaders to single out. Never throws - a comparison problem must not cost the reader the report.
        /// </summary>
        private async Task<LeadershipAdoptionComparison> LeadershipComparisonForAsync(CopilotAdoptionAnalysis analysis, bool narrowed)
        {
            try
            {
                return await (LeadershipComparisons ?? LeadershipComparisonProvider.Default).GetAsync(analysis, narrowed);
            }
            catch (Exception)
            {
                return LeadershipAdoptionComparison.WithStatus(
                    LeadershipComparisonStatuses.Unavailable, LeadershipComparisonReasons.StateUnavailable);
            }
        }

        /// <summary>The provider, replaceable by tests. Null means the process-wide one.</summary>
        internal LeadershipComparisonProvider LeadershipComparisons { get; set; }

        /// <summary>
        /// Combines the email domain, the reader's user filter and the administrator's global filter into one
        /// scope, evaluating the filters against the shared directory snapshot.
        /// </summary>
        /// <remarks>
        /// A filter that cannot be evaluated - the directory or the global filter cannot be read - is
        /// answered with a plain 503 by <see cref="ReportScopeResolver"/> rather than left to the Web API
        /// pipeline: this site ships with customErrors off, so an unhandled SQL exception would reach the
        /// browser carrying object names. The fault still goes to Application Insights.
        /// </remarks>
        private async Task<CopilotAdoptionScope> ResolveScopeAsync(
            string emailDomain, UserFilterExpression userFilter, CancellationToken cancellationToken)
        {
            var scope = await Scopes.ResolveAsync(Request, User, userFilter, cancellationToken);
            if (!scope.IsRestricted) return CopilotAdoptionScope.ForEmailDomain(emailDomain);

            return CopilotAdoptionScope.Create(
                emailDomain,
                scope.UserFilter,
                scope.Restriction,
                scope.GlobalEcho,
                scope.Global?.DescribeInEnglish());
        }

        /// <summary>
        /// Reads the <c>userFilter</c> query parameter. A filter that cannot be read is refused with a 400
        /// rather than ignored: ignoring it would show the whole tenant under a page that believes it is
        /// filtered, which is exactly the silent-scope mistake the echo exists to prevent.
        /// </summary>
        internal static bool TryParseUserFilter(string userFilter, out UserFilterExpression expression, out string error)
        {
            try
            {
                expression = UserFilterCodec.Parse(userFilter);
                error = null;
                return true;
            }
            catch (UserFilterFormatException ex)
            {
                expression = UserFilterExpression.Empty;
                error = "The filter could not be applied: " + ex.Message;
                return false;
            }
        }

        private IHttpActionResult InvalidFilter(string error)
        {
            return Content(HttpStatusCode.BadRequest, new ApiErrorModel(error));
        }

        /// <summary>The same refusal for the exports, in plain text because it lands in a browser tab.</summary>
        private HttpResponseMessage InvalidFilterResponse(string error)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(error, Encoding.UTF8, "text/plain"),
            };
        }

        /// <summary>
        /// As above, but with an explicit wait budget. Exports use a much longer one - see
        /// <see cref="ExportWaitBudget"/>.
        /// </summary>
        private async Task<CopilotAdoptionAnalysis> TryGetAnalysisAsync(
            int windowDays, string from, string to, string seatLicenceTypeIds, TimeSpan budget, CancellationToken cancellationToken)
        {
            CopilotAdoptionDateRange range;
            try
            {
                range = CopilotAdoptionDateRange.Create(windowDays, from, to, DateTime.UtcNow);
            }
            catch (ArgumentException ex)
            {
                throw new HttpResponseException(Request.CreateResponse(
                    HttpStatusCode.BadRequest,
                    new ApiErrorModel(ex.Message, ex.Message)));
            }

            return await Coordinator.TryGetAsync(
                range,
                ParseIds(seatLicenceTypeIds),
                budget,
                cancellationToken);
        }

        /// <summary>
        /// The 202 body. Deliberately the same shape for every endpoint so the SPA has one thing to detect.
        /// </summary>
        private IHttpActionResult StillBuilding(int windowDays, string from, string to, string seatLicenceTypeIds)
        {
            return ResponseMessage(StillBuildingResponse(InFlightRunId(windowDays, from, to, seatLicenceTypeIds)));
        }

        /// <summary>
        /// The telemetry id of the run a 202 is waiting on, so a browser trace can be matched to the run's
        /// <c>CopilotAdoptionLifecycle</c> events in Application Insights.
        /// </summary>
        private string InFlightRunId(int windowDays, string from, string to, string seatLicenceTypeIds)
        {
            return Coordinator.InFlightRunId(
                CopilotAdoptionDateRange.Create(windowDays, from, to, DateTime.UtcNow),
                ParseIds(seatLicenceTypeIds));
        }

        /// <summary>The header carrying the analysis run id on 202s, "not ready" 503s and export downloads.</summary>
        internal const string RunIdHeader = "X-CopilotAdoption-RunId";

        /// <summary>
        /// The 202 body as a model: the status the SPA detects, the poll delay, a readable message and, when
        /// telemetry is running, the <c>runId</c> of the analysis being waited for.
        /// </summary>
        internal static IDictionary<string, object> StillBuildingBody(string runId)
        {
            var body = new Dictionary<string, object>
            {
                { "status", "building" },
                { "retryAfterSeconds", RetryAfterSeconds },
                {
                    "message",
                    "The Copilot adoption analysis is still running. This can take a few minutes the "
                    + "first time on a large tenant; the page will refresh automatically."
                },
            };

            if (!string.IsNullOrEmpty(runId)) body.Add("runId", runId);
            return body;
        }

        /// <summary>
        /// The same 202, for the export endpoints - they return <see cref="HttpResponseMessage"/> directly
        /// because they stream a file rather than a model.
        /// </summary>
        private HttpResponseMessage StillBuildingResponse(string runId)
        {
            var response = Request.CreateResponse(HttpStatusCode.Accepted, StillBuildingBody(runId));

            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfterSeconds));
            AddRunIdHeader(response, runId);
            return response;
        }

        private static void AddRunIdHeader(HttpResponseMessage response, string runId)
        {
            if (!string.IsNullOrEmpty(runId)) response.Headers.TryAddWithoutValidation(RunIdHeader, runId);
        }

        /// <summary>
        /// What an EXPORT returns when the analysis did not finish inside <see cref="ExportWaitBudget"/>.
        /// </summary>
        /// <remarks>
        /// Plain text, not JSON: this lands in a browser tab as the result of a download navigation, so
        /// the person who clicked has to be able to read it. 503 + <c>Retry-After</c> is the honest
        /// status - the report is temporarily unavailable and retrying later will work.
        /// </remarks>
        private HttpResponseMessage ExportNotReadyResponse(int windowDays, string from, string to, string seatLicenceTypeIds)
        {
            var response = Request.CreateResponse(HttpStatusCode.ServiceUnavailable);

            response.Content = new StringContent(
                "The Copilot adoption analysis is still being prepared, so this export is not ready yet.\r\n\r\n"
                + "This happens when the export is opened directly, or more than a few minutes after the "
                + "page was last loaded. The analysis is still running in the background.\r\n\r\n"
                + "Open the Copilot Adoption page, wait for it to finish loading, then use the export "
                + "button there.",
                Encoding.UTF8,
                "text/plain");

            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfterSeconds));
            AddRunIdHeader(response, InFlightRunId(windowDays, from, to, seatLicenceTypeIds));
            return response;
        }

        private static HttpResponseMessage PastRangeListExportHiddenResponse()
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new ApiErrorModel(
                        "copilotAdoption.error.pastRangeNamedListsHidden",
                        "copilotAdoption.error.pastRangeNamedListsHidden")),
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        private IHttpActionResult PastRangeListHidden()
        {
            return Content(
                HttpStatusCode.BadRequest,
                new ApiErrorModel(
                    "copilotAdoption.error.pastRangeNamedListsHidden",
                    "copilotAdoption.error.pastRangeNamedListsHidden"));
        }

        #endregion

        #region Summary and licence types

        /// <summary>The executive view: headline figures, the adoption funnel and the breakdown charts.</summary>
        // GET: api/CopilotAdoption/summary?windowDays=28&emailDomain=contoso.com&userFilter=[...]
        [HttpGet]
        [Route("summary")]
        public async Task<IHttpActionResult> Summary(
            int windowDays = 28,
            string from = null,
            string to = null,
            string seatLicenceTypeIds = null,
            string emailDomain = null,
            string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!TryParseUserFilter(userFilter, out var filter, out var filterError)) return InvalidFilter(filterError);
            var permissionDenied = ScopePermissionDenied(emailDomain, filter);
            if (permissionDenied != null) return ResponseMessage(permissionDenied);

            var (analysis, narrowed) = await TryGetScopedAnalysisAsync(
                windowDays, from, to, seatLicenceTypeIds, emailDomain, filter, FirstResponseBudget, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, from, to, seatLicenceTypeIds);
            var leadership = await LeadershipComparisonForAsync(analysis, narrowed);
            return Ok(SummaryForCaller(analysis.Summary.WithLeadershipComparison(leadership)));
        }

        /// <summary>Whether the caller holds the portal's See PII permission (#661).</summary>
        private bool CanSeeIndividuals() => PortalAccess.Evaluate(Request, User).SeePii;

        private CopilotAdoptionSummary SummaryForCaller(CopilotAdoptionSummary summary)
        {
            var visible = CanSeeIndividuals() ? summary : summary.WithoutIndividualData();
            return visible?.WithoutPastRangeNamedLists();
        }

        /// <summary>
        /// Refuses a population-narrowing scope for a reader who cannot see individual data. Even when the
        /// response names nobody, a filter that selects one known person turns every aggregate into that
        /// person's licence and activity record.
        /// </summary>
        private HttpResponseMessage ScopePermissionDenied(string emailDomain, UserFilterExpression userFilter)
        {
            var narrowed = CopilotAdoptionEmailDomain.Normalise(emailDomain) != null
                || (userFilter != null && !userFilter.IsEmpty);
            return narrowed && !CanSeeIndividuals()
                ? PortalPermissionDenied.Response(Request, PortalPermission.SeePii)
                : null;
        }

        /// <summary>
        /// Every licence type in the tenant and whether it was counted as a Copilot seat.
        ///
        /// Exposed deliberately: Microsoft ships new Copilot SKUs faster than any shipped classification
        /// list can track, so an admin has to be able to see what the tool decided - and override it -
        /// rather than discover from a wrong headline number that a SKU was missed.
        ///
        /// Under the administrator's global filter only the Copilot seat types are listed, with the narrowed
        /// assigned and idle counts the summary shows: a reader limited to one department must not be able to
        /// read the whole tenant's per-licence counts here instead.
        /// </summary>
        // GET: api/CopilotAdoption/licence-types
        [HttpGet]
        [Route("licence-types")]
        public async Task<IHttpActionResult> LicenceTypes(
            int windowDays = 28,
            string from = null,
            string to = null,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetScopedSummaryAsync(
                windowDays, from, to, seatLicenceTypeIds, null, UserFilterExpression.Empty, FirstResponseBudget, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, from, to, seatLicenceTypeIds);
            return Ok(analysis.Summary.SeatLicenceTypes);
        }

        /// <summary>The queries behind the numbers, for the SQL popover the rest of the admin site uses.</summary>
        // GET: api/CopilotAdoption/sql
        [HttpGet]
        [Route("sql")]
        public async Task<IHttpActionResult> Sql(
            int windowDays = 28,
            string from = null,
            string to = null,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetAnalysisAsync(windowDays, from, to, seatLicenceTypeIds, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, from, to, seatLicenceTypeIds);
            return Ok(analysis.Sql);
        }

        /// <summary>
        /// The distinct email domains, departments and countries present in the analysis, for the
        /// filter drop-downs. Derived from the already-loaded result rather than from another query.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT narrowed by the reader's own scope: this is the list the domain filter itself is
        /// chosen from, so narrowing it would leave the selected domain as the only option and make the
        /// filter impossible to change. It IS narrowed by the administrator's global filter, which the
        /// reader cannot change: offering values held only by people outside it would list organisations
        /// the reader may not see, every one of which would select nobody.
        /// </remarks>
        // GET: api/CopilotAdoption/filters
        [HttpGet]
        [Route("filters")]
        public async Task<IHttpActionResult> Filters(
            int windowDays = 28,
            string from = null,
            string to = null,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetAnalysisAsync(windowDays, from, to, seatLicenceTypeIds, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, from, to, seatLicenceTypeIds);

            var global = await Scopes.ResolveAsync(Request, User, null, cancellationToken);
            Func<int, bool> visible = global.IsRestricted ? (Func<int, bool>)global.Includes : _ => true;

            var licensed = analysis.LicensedUsers.Where(u => visible(u.UserId)).ToList();
            var opportunities = analysis.Opportunities.Where(o => visible(o.UserId)).ToList();
            var cowork = analysis.CoworkReadiness.Where(c => visible(c.UserId)).ToList();
            var unlicensed = analysis.UnlicensedUsers.Where(u => visible(u.UserId)).ToList();

            return Ok(new
            {
                // Every population, so a domain that holds no seats at all - an acquired business
                // using Copilot Chat without ever having been licensed - is still selectable.
                emailDomains = Distinct(
                    licensed.Select(u => CopilotAdoptionEmailDomain.Label(u.EmailDomain))
                        .Concat(opportunities.Select(o => CopilotAdoptionEmailDomain.Label(o.EmailDomain)))
                        .Concat(cowork.Select(c => CopilotAdoptionEmailDomain.Label(c.EmailDomain)))
                        .Concat(unlicensed.Select(u => CopilotAdoptionEmailDomain.Label(u.EmailDomain)))),
                departments = Distinct(
                    licensed.Select(u => u.Department)
                        .Concat(opportunities.Select(o => o.Department))
                        .Concat(cowork.Select(c => c.Department))),
                countries = Distinct(
                    licensed.Select(u => u.Country)
                        .Concat(opportunities.Select(o => o.Country))
                        .Concat(cowork.Select(c => c.Country))),
                bands = CopilotAdoptionScoring.AllBands
                    .Select(b => new { value = (int)b, name = CopilotAdoptionScoring.BandDisplayName(b) })
                    .ToList(),
                // Carries the basis alongside the label so the filter UI can show which tiers rest on
                // observed Cowork use and which are predictions, rather than presenting all six as
                // equally-evidenced categories.
                coworkTiers = CopilotAdoptionScoring.AllCoworkTiers
                    .Select(t => new
                    {
                        value = t,
                        name = CopilotAdoptionScoring.CoworkTierLabel(t),
                        basis = CopilotAdoptionScoring.CoworkTierBasis(t),
                    })
                    .ToList(),
            });
        }

        #endregion

        #region Licensed users

        /// <summary>
        /// The licensed-user list: who holds a seat, how much they use it, and what to do about it.
        /// Defaults to the lowest scores first, because the people who need attention are the point.
        /// </summary>
        // GET: api/CopilotAdoption/licensed-users?windowDays=28&skip=0&take=50
        [HttpGet]
        [Route("licensed-users")]
        [RequirePortalPermission(PortalPermission.SeePii)]
        public async Task<IHttpActionResult> LicensedUsers(
            int windowDays = 28,
            string from = null,
            string to = null,
            string seatLicenceTypeIds = null,
            string search = null,
            string bands = null,
            string actions = null,
            string department = null,
            string country = null,
            string emailDomain = null,
            string reclaimEligibility = null,
            bool coworkOnly = false,
            bool disabledOnly = false,
            double? minScore = null,
            double? maxScore = null,
            string sortBy = LicensedUserSortFields.Score,
            bool sortDesc = false,
            int skip = 0,
            int take = DefaultTake,
            string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!TryParseUserFilter(userFilter, out var filter, out var filterError)) return InvalidFilter(filterError);

            var analysis = await TryGetScopedRowsAsync(windowDays, from, to, seatLicenceTypeIds, emailDomain, filter, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, from, to, seatLicenceTypeIds);
            if (analysis.Summary.Options.UsesExplicitDates)
            {
                return PastRangeListHidden();
            }

            var query = BuildLicensedUserQuery(
                search, bands, department, country, reclaimEligibility, coworkOnly, disabledOnly, minScore, maxScore, sortBy, sortDesc, actions);

            var matched = CopilotAdoptionExports.Apply(analysis.LicensedUsers, query);

            return Ok(new LicensedUserPage
            {
                Total = matched.Count,
                Skip = Math.Max(0, skip),
                Take = Math.Min(Math.Max(1, take), MaxTake),
                Rows = CopilotAdoptionExports.Page(matched, skip, take, MaxTake),
                Warnings = analysis.Summary.Warnings,
                WarningDetails = analysis.Summary.WarningDetails,
            });
        }

        /// <summary>
        /// The same list as a CSV download. Takes the identical filter parameters, so what is exported
        /// is exactly what is on screen - just without the paging.
        /// </summary>
        // GET: api/CopilotAdoption/licensed-users/export
        [HttpGet]
        [Route("licensed-users/export")]
        [RequirePortalPermission(PortalPermission.SeePii)]
        public async Task<HttpResponseMessage> ExportLicensedUsers(
            int windowDays = 28,
            string from = null,
            string to = null,
            string seatLicenceTypeIds = null,
            string search = null,
            string bands = null,
            string actions = null,
            string department = null,
            string country = null,
            string emailDomain = null,
            string reclaimEligibility = null,
            bool coworkOnly = false,
            bool disabledOnly = false,
            double? minScore = null,
            double? maxScore = null,
            string sortBy = LicensedUserSortFields.Score,
            bool sortDesc = false,
            string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!TryParseUserFilter(userFilter, out var filter, out var filterError)) return InvalidFilterResponse(filterError);

            // Exports are <a href> downloads, not fetch() calls: a browser will not retry a 202, it
            // would just render the JSON body as the "file". So an export WAITS - but only up to
            // ExportWaitBudget, because waiting past the platform limit produced a 500 and a corrupt
            // download instead of an answer.
            var analysis = await TryGetScopedRowsAsync(
                windowDays, from, to, seatLicenceTypeIds, emailDomain, filter, ExportWaitBudget, cancellationToken);
            if (analysis == null) return ExportNotReadyResponse(windowDays, from, to, seatLicenceTypeIds);
            if (analysis.Summary.Options.UsesExplicitDates) return PastRangeListExportHiddenResponse();

            var query = BuildLicensedUserQuery(
                search, bands, department, country, reclaimEligibility, coworkOnly, disabledOnly, minScore, maxScore, sortBy, sortDesc, actions);

            var rows = CopilotAdoptionExports.Apply(analysis.LicensedUsers, query).Take(MaxCsvRows).ToList();

            return CsvResponse(
                CsvSerialiser.ToBytes(
                    rows,
                    CopilotAdoptionExports.LicensedUserColumns(
                        analysis.Summary.FiguresIncomplete,
                        WarningSummary(analysis.Summary))),
                CsvSerialiser.FileName(ScopedFileNamePrefix("copilot-licensed-users", emailDomain, filter, analysis.Summary), analysis.Summary.GeneratedUtc),
                analysis.Summary.Diagnostics?.RunId);
        }

        /// <summary>
        /// The on-screen warnings, flattened onto every exported row.
        /// </summary>
        /// <remarks>
        /// A spreadsheet outlives the banner that was above it when it was generated, and it gets
        /// forwarded without that context. If a source query failed or a figure was computed over a
        /// biased subset, the file has to say so itself or it will be read as complete.
        /// </remarks>
        private static string WarningSummary(CopilotAdoptionSummary summary)
        {
            if (summary == null) return null;

            var warnings = new List<string>();
            if (summary.FiguresIncomplete)
            {
                warnings.Add("Figures incomplete: " + string.Join(", ", summary.IncompleteReasons));
            }

            warnings.AddRange(summary.Warnings ?? new List<string>());
            return warnings.Count == 0 ? null : string.Join(" | ", warnings);
        }

        #endregion

        #region Licence opportunities

        /// <summary>
        /// Unlicensed users ranked as candidates for a Copilot seat, strongest business case first.
        /// </summary>
        // GET: api/CopilotAdoption/opportunities?windowDays=28&skip=0&take=50
        [HttpGet]
        [Route("opportunities")]
        [RequirePortalPermission(PortalPermission.SeePii)]
        public async Task<IHttpActionResult> Opportunities(
            int windowDays = 28,
            string from = null,
            string to = null,
            string seatLicenceTypeIds = null,
            string search = null,
            string department = null,
            string country = null,
            string emailDomain = null,
            bool recommendedOnly = false,
            bool existingCopilotUsersOnly = false,
            double? minScore = null,
            string sortBy = LicenceOpportunitySortFields.Score,
            bool sortDesc = true,
            int skip = 0,
            int take = DefaultTake,
            string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!TryParseUserFilter(userFilter, out var filter, out var filterError)) return InvalidFilter(filterError);

            var analysis = await TryGetScopedRowsAsync(windowDays, from, to, seatLicenceTypeIds, emailDomain, filter, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, from, to, seatLicenceTypeIds);
            if (analysis.Summary.Options.UsesExplicitDates)
            {
                return PastRangeListHidden();
            }

            var query = BuildOpportunityQuery(
                search, department, country, recommendedOnly, existingCopilotUsersOnly, minScore, sortBy, sortDesc);

            var matched = CopilotAdoptionExports.Apply(analysis.Opportunities, query);

            return Ok(new LicenceOpportunityPage
            {
                Total = matched.Count,
                Skip = Math.Max(0, skip),
                Take = Math.Min(Math.Max(1, take), MaxTake),
                Rows = CopilotAdoptionExports.Page(matched, skip, take, MaxTake),
                Warnings = analysis.Summary.Warnings,
                WarningDetails = analysis.Summary.WarningDetails,
            });
        }

        // GET: api/CopilotAdoption/opportunities/export
        [HttpGet]
        [Route("opportunities/export")]
        [RequirePortalPermission(PortalPermission.SeePii)]
        public async Task<HttpResponseMessage> ExportOpportunities(
            int windowDays = 28,
            string from = null,
            string to = null,
            string seatLicenceTypeIds = null,
            string search = null,
            string department = null,
            string country = null,
            string emailDomain = null,
            bool recommendedOnly = false,
            bool existingCopilotUsersOnly = false,
            double? minScore = null,
            string sortBy = LicenceOpportunitySortFields.Score,
            bool sortDesc = true,
            string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!TryParseUserFilter(userFilter, out var filter, out var filterError)) return InvalidFilterResponse(filterError);

            // Exports are <a href> downloads, not fetch() calls: a browser will not retry a 202, it
            // would just render the JSON body as the "file". So an export WAITS - but only up to
            // ExportWaitBudget, because waiting past the platform limit produced a 500 and a corrupt
            // download instead of an answer.
            var analysis = await TryGetScopedRowsAsync(
                windowDays, from, to, seatLicenceTypeIds, emailDomain, filter, ExportWaitBudget, cancellationToken);
            if (analysis == null) return ExportNotReadyResponse(windowDays, from, to, seatLicenceTypeIds);
            if (analysis.Summary.Options.UsesExplicitDates) return PastRangeListExportHiddenResponse();

            var query = BuildOpportunityQuery(
                search, department, country, recommendedOnly, existingCopilotUsersOnly, minScore, sortBy, sortDesc);

            var rows = CopilotAdoptionExports.Apply(analysis.Opportunities, query).Take(MaxCsvRows).ToList();

            return CsvResponse(
                CsvSerialiser.ToBytes(
                    rows,
                    CopilotAdoptionExports.LicenceOpportunityColumns(
                        analysis.Summary.FiguresIncomplete,
                        WarningSummary(analysis.Summary))),
                CsvSerialiser.FileName(ScopedFileNamePrefix("copilot-licence-opportunities", emailDomain, filter, analysis.Summary), analysis.Summary.GeneratedUtc),
                analysis.Summary.Diagnostics?.RunId);
        }

        #endregion

        #region Cowork readiness

        /// <summary>
        /// Copilot seat holders assessed for Microsoft 365 Copilot Cowork readiness: who already uses it,
        /// and who carries the coordination load it is built to absorb.
        /// </summary>
        // GET: api/CopilotAdoption/cowork?windowDays=28&skip=0&take=50
        [HttpGet]
        [Route("cowork")]
        [RequirePortalPermission(PortalPermission.SeePii)]
        public async Task<IHttpActionResult> Cowork(
            int windowDays = 28,
            string from = null,
            string to = null,
            string seatLicenceTypeIds = null,
            string search = null,
            string tiers = null,
            string department = null,
            string country = null,
            string emailDomain = null,
            bool recommendedOnly = false,
            bool coworkUsersOnly = false,
            double? minLoad = null,
            double? minFluency = null,
            string sortBy = CoworkSortFields.CoordinationLoad,
            bool sortDesc = true,
            int skip = 0,
            int take = DefaultTake,
            string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!TryParseUserFilter(userFilter, out var filter, out var filterError)) return InvalidFilter(filterError);

            var analysis = await TryGetScopedRowsAsync(windowDays, from, to, seatLicenceTypeIds, emailDomain, filter, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, from, to, seatLicenceTypeIds);
            if (analysis.Summary.Options.UsesExplicitDates)
            {
                return PastRangeListHidden();
            }

            var query = BuildCoworkQuery(
                search, tiers, department, country, recommendedOnly, coworkUsersOnly,
                minLoad, minFluency, sortBy, sortDesc);

            var matched = CopilotAdoptionExports.Apply(analysis.CoworkReadiness, query);

            return Ok(new CoworkReadinessPage
            {
                Total = matched.Count,
                Skip = Math.Max(0, skip),
                Take = Math.Min(Math.Max(1, take), MaxTake),
                Rows = CopilotAdoptionExports.Page(matched, skip, take, MaxTake),
                Warnings = analysis.Summary.Warnings,
                WarningDetails = analysis.Summary.WarningDetails,
            });
        }

        /// <summary>
        /// The Cowork candidate list as a CSV, built to be a <b>spending-policy scoping list</b>: Cowork
        /// access is granted by a spending policy scoped to users or groups, so the useful artefact is a
        /// list of UPNs with the justification next to each one.
        /// </summary>
        // GET: api/CopilotAdoption/cowork/export
        [HttpGet]
        [Route("cowork/export")]
        [RequirePortalPermission(PortalPermission.SeePii)]
        public async Task<HttpResponseMessage> ExportCowork(
            int windowDays = 28,
            string from = null,
            string to = null,
            string seatLicenceTypeIds = null,
            string search = null,
            string tiers = null,
            string department = null,
            string country = null,
            string emailDomain = null,
            bool recommendedOnly = false,
            bool coworkUsersOnly = false,
            double? minLoad = null,
            double? minFluency = null,
            string sortBy = CoworkSortFields.CoordinationLoad,
            bool sortDesc = true,
            string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!TryParseUserFilter(userFilter, out var filter, out var filterError)) return InvalidFilterResponse(filterError);

            // Exports are <a href> downloads, not fetch() calls - see ExportOpportunities.
            var analysis = await TryGetScopedRowsAsync(
                windowDays, from, to, seatLicenceTypeIds, emailDomain, filter, ExportWaitBudget, cancellationToken);
            if (analysis == null) return ExportNotReadyResponse(windowDays, from, to, seatLicenceTypeIds);
            if (analysis.Summary.Options.UsesExplicitDates) return PastRangeListExportHiddenResponse();

            var query = BuildCoworkQuery(
                search, tiers, department, country, recommendedOnly, coworkUsersOnly,
                minLoad, minFluency, sortBy, sortDesc);

            var rows = CopilotAdoptionExports.Apply(analysis.CoworkReadiness, query).Take(MaxCsvRows).ToList();

            return CsvResponse(
                CsvSerialiser.ToBytes(rows, CopilotAdoptionExports.CoworkReadinessColumns()),
                CsvSerialiser.FileName(ScopedFileNamePrefix("copilot-cowork-readiness", emailDomain, filter, analysis.Summary), analysis.Summary.GeneratedUtc),
                analysis.Summary.Diagnostics?.RunId);
        }

        #endregion

        #region Workbook export

        /// <summary>
        /// The whole report as an Excel workbook - every figure, table and chart, with the charts live
        /// and bound to the cells rather than pasted in as pictures.
        ///
        /// Exists for the point-in-time snapshot. A screenshot of this page cannot be compared with
        /// another screenshot six months later: the numbers cannot be subtracted and nobody can tell
        /// what period, thresholds or product build either was run with. The workbook records all
        /// three, so two files taken before and after an enablement programme are comparable and the
        /// comparison is checkable.
        ///
        /// <para>Two sheets make that comparison mechanical rather than manual: "Snapshot facts"
        /// carries every scalar figure as a stable key/value row and "Settings" carries every tuning
        /// option the same way, both sorted by key so a lookup against the other file always resolves.
        /// The per-user sheets carry the same columns as the CSV exports, from the same definitions.</para>
        ///
        /// Built from the same cached analysis that renders the page, so the two can never disagree.
        ///
        /// <para>The optional time-saved parameters carry the assumptions the reader entered in the
        /// portal. <c>copilotMinutesSavedPerMeeting</c>, <c>copilotMinutesSavedPerMailThread</c> and
        /// <c>copilotMinutesSavedPerDocument</c> restate the licence estimate; for each kind of work Cowork
        /// could take on, its share and minutes under the option's own name
        /// (<c>coworkOrganiseMeetingsShare</c>, <c>coworkOrganiseMeetingsMinutes</c> and so on - see
        /// <see cref="CoworkActivities"/>) restate the Cowork estimate; <c>coworkEstimateLowerBoundRatio</c>
        /// applies to both. The per-activity figures
        /// are read from the query string by those names rather than bound one parameter each, so a kind
        /// of work added to the catalogue needs no change here. Those figures live in the browser only, so
        /// the export has to be told them or a customised page would download a workbook modelling
        /// different hours. They change the modelled estimates and the matching Settings rows, never a
        /// measured figure, and never the cached analysis itself.</para>
        /// </summary>
        // GET: api/CopilotAdoption/export/workbook?windowDays=28
        [HttpGet]
        [Route("export/workbook")]
        public async Task<HttpResponseMessage> ExportWorkbook(
            int windowDays = 28,
            string from = null,
            string to = null,
            string seatLicenceTypeIds = null,
            string emailDomain = null,
            string copilotMinutesSavedPerMeeting = null,
            string copilotMinutesSavedPerMailThread = null,
            string copilotMinutesSavedPerDocument = null,
            string coworkEstimateLowerBoundRatio = null,
            string copilotSeatOutlookMinutesPerAction = null,
            string copilotSeatOfficeMinutesPerAction = null,
            string copilotSeatMeetingMinutesPerAction = null,
            string copilotSeatUncreditedMinutesPerAction = null,
            string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!TryParseUserFilter(userFilter, out var filter, out var filterError)) return InvalidFilterResponse(filterError);
            var permissionDenied = ScopePermissionDenied(emailDomain, filter);
            if (permissionDenied != null) return permissionDenied;

            // Exports are <a href> downloads, not fetch() calls: a browser will not retry a 202, it
            // would just render the JSON body as the "file". So an export WAITS - but only up to
            // ExportWaitBudget, because waiting past the platform limit produced a 500 and a corrupt
            // download instead of an answer.
            var (analysis, narrowed) = await TryGetScopedAnalysisAsync(
                windowDays, from, to, seatLicenceTypeIds, emailDomain, filter, ExportWaitBudget, cancellationToken);
            if (analysis == null) return ExportNotReadyResponse(windowDays, from, to, seatLicenceTypeIds);
            var leadership = await LeadershipComparisonForAsync(analysis, narrowed);

            var timeSaved = ParseTimeSavedOverrides(
                copilotMinutesSavedPerMeeting,
                copilotMinutesSavedPerMailThread,
                copilotMinutesSavedPerDocument,
                coworkEstimateLowerBoundRatio,
                copilotSeatOutlookMinutesPerAction,
                copilotSeatOfficeMinutesPerAction,
                copilotSeatMeetingMinutesPerAction,
                copilotSeatUncreditedMinutesPerAction,
                Request?.GetQueryNameValuePairs());

            byte[] bytes;
            try
            {
                bytes = CopilotAdoptionWorkbook.Build(
                    analysis,
                    timeSaved.Any ? timeSaved : null,
                    includeIndividualData: CanSeeIndividuals() && !analysis.Summary.Options.UsesExplicitDates,
                    leadership: leadership);
            }
            catch (Exception ex)
            {
                // The workbook is assembled as OpenXML by hand, so a failure here is a defect in this
                // application rather than anything the caller did. Log it with detail and return a
                // deliberately plain message: the default Web API behaviour would put the exception
                // and its stack trace in the response body, and this endpoint is reachable by every
                // admin user.
                try
                {
                    var config = new AppConfig();
                    var logger = new AnalyticsLogger(
                        config.AppInsightsConnectionString, nameof(CopilotAdoptionAPIController));
                    logger.TrackException(ex);
                    logger.LogError($"Failed to build the Copilot adoption workbook: {ex.Message}");
                }
                catch (Exception)
                {
                    // Telemetry must never be the reason a request fails differently. Swallowing here
                    // is deliberate - the caller is already getting an error, and a logging failure on
                    // top of it would replace a useful message with a confusing one.
                }

                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(
                        "The Copilot adoption workbook could not be generated. The failure has been logged; "
                        + "the CSV exports on the Licensed users and Licence opportunities tabs are unaffected."),
                };
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };

            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = CopilotAdoptionWorkbook.FileName(analysis.Summary),
            };
            AddRunIdHeader(response, analysis.Summary.Diagnostics?.RunId);

            return response;
        }

        #endregion

        #region Parameter handling

        /// <summary>
        /// The reader's time-saved figures from an export URL, parsed with the INVARIANT culture.
        /// </summary>
        /// <remarks>
        /// Taken as strings and parsed here rather than bound as <c>double?</c>, so the rule is stated
        /// rather than inherited from the model binder. The portal writes JavaScript numbers ("0.5"),
        /// and on a server running a European culture a culture-sensitive parse reads the full stop as a
        /// thousands separator - "0.5" minutes per email would become 5, and the workbook would model
        /// ten times the saving the reader entered. Anything unparseable is ignored and keeps the
        /// product default; the bounds are applied by <see cref="TimeSavedOverrides.ApplyTo"/>.
        /// </remarks>
        /// <param name="query">
        /// The request's query string, from which each kind of work's share and minutes are read under
        /// their option names (<see cref="CoworkActivity.ShareOption"/>, <see cref="CoworkActivity.MinutesOption"/>),
        /// matched case-insensitively as Web API binds the named parameters.
        /// </param>
        internal static TimeSavedOverrides ParseTimeSavedOverrides(
            string minutesPerMeeting,
            string minutesPerMailThread,
            string minutesPerDocument,
            string lowerBoundRatio,
            IEnumerable<KeyValuePair<string, string>> query)
        {
            return ParseTimeSavedOverrides(
                minutesPerMeeting,
                minutesPerMailThread,
                minutesPerDocument,
                lowerBoundRatio,
                null,
                null,
                null,
                null,
                query);
        }

        internal static TimeSavedOverrides ParseTimeSavedOverrides(
            string minutesPerMeeting,
            string minutesPerMailThread,
            string minutesPerDocument,
            string lowerBoundRatio,
            string seatOutlookMinutesPerAction = null,
            string seatOfficeMinutesPerAction = null,
            string seatMeetingMinutesPerAction = null,
            string seatUncreditedMinutesPerAction = null,
            IEnumerable<KeyValuePair<string, string>> query = null)
        {
            var overrides = new TimeSavedOverrides
            {
                MinutesSavedPerMeeting = ParseInvariantDouble(minutesPerMeeting),
                MinutesSavedPerMailThread = ParseInvariantDouble(minutesPerMailThread),
                MinutesSavedPerDocument = ParseInvariantDouble(minutesPerDocument),
                LowerBoundRatio = ParseInvariantDouble(lowerBoundRatio),
                SeatOutlookMinutesPerAction = ParseInvariantDouble(seatOutlookMinutesPerAction),
                SeatOfficeMinutesPerAction = ParseInvariantDouble(seatOfficeMinutesPerAction),
                SeatMeetingMinutesPerAction = ParseInvariantDouble(seatMeetingMinutesPerAction),
                SeatUncreditedMinutesPerAction = ParseInvariantDouble(seatUncreditedMinutesPerAction),
            };

            if (query == null) return overrides;

            // First value wins for a repeated key, as it does for a bound parameter.
            var figures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in query)
            {
                if (pair.Key != null && !figures.ContainsKey(pair.Key)) figures[pair.Key] = pair.Value;
            }

            foreach (var activity in CoworkActivities.All)
            {
                if (figures.TryGetValue(activity.ShareOption, out var share))
                    overrides.CoworkShares[activity.Key] = ParseInvariantDouble(share);
                if (figures.TryGetValue(activity.MinutesOption, out var minutes))
                    overrides.CoworkMinutes[activity.Key] = ParseInvariantDouble(minutes);
            }

            return overrides;
        }

        private static double? ParseInvariantDouble(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            return double.TryParse(
                value.Trim(),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : (double?)null;
        }

        /// <summary>
        /// Snaps a requested window to one of the supported values. A free-form window would let a
        /// hand-edited URL ask for an arbitrarily long scan of the audit history.
        /// </summary>
        internal static int NormaliseWindowDays(int windowDays)
        {
            return CopilotAdoptionDateRange.NormaliseWindowDays(windowDays);
        }

        /// <summary>
        /// Parses a comma-separated id list, ignoring anything that is not an integer. These ids are
        /// interpolated into SQL after being checked against the licence types that actually exist, so
        /// discarding non-numeric input here is the first of two gates rather than the only one.
        /// </summary>
        internal static List<int> ParseIds(string commaSeparated)
        {
            if (string.IsNullOrWhiteSpace(commaSeparated))
            {
                return new List<int>();
            }

            return commaSeparated
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Select(part => int.TryParse(part, out var id) ? (int?)id : null)
                .Where(id => id.HasValue)
                .Select(id => id.Value)
                .Distinct()
                .ToList();
        }

        /// <summary>Parses the band filter, accepting either the numeric value or the enum name.</summary>
        internal static List<AdoptionBand> ParseBands(string commaSeparated)
        {
            var result = new List<AdoptionBand>();
            if (string.IsNullOrWhiteSpace(commaSeparated))
            {
                return result;
            }

            foreach (var part in commaSeparated.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim();

                if (int.TryParse(token, out var numeric) && Enum.IsDefined(typeof(AdoptionBand), numeric))
                {
                    result.Add((AdoptionBand)numeric);
                }
                else if (Enum.TryParse(token, ignoreCase: true, result: out AdoptionBand band)
                         && Enum.IsDefined(typeof(AdoptionBand), band))
                {
                    // Enum.TryParse happily accepts any numeric string - "99" would parse to
                    // (AdoptionBand)99 - so the IsDefined check is what stops an out-of-range value
                    // reaching the filter and silently matching nothing.
                    result.Add(band);
                }
            }

            return result.Distinct().ToList();
        }

        /// <summary>
        /// Parses a comma-separated list of recommended-action codes down to the known catalogue.
        ///
        /// Unknown tokens are dropped rather than passed through: an unrecognised code would filter
        /// the list to nothing and look like "there is no one in this group", which is the most
        /// misleading possible failure for a page whose job is to size an enablement programme.
        /// </summary>
        internal static List<string> ParseActions(string commaSeparated)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(commaSeparated))
            {
                return result;
            }

            foreach (var part in commaSeparated.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim();
                var known = CopilotAdoptionScoring.AllActionCodes
                    .FirstOrDefault(c => string.Equals(c, token, StringComparison.OrdinalIgnoreCase));

                if (known != null && !result.Contains(known))
                {
                    result.Add(known);
                }
            }

            return result;
        }

        private static LicensedUserQuery BuildLicensedUserQuery(
            string search, string bands, string department, string country, string reclaimEligibility,
            bool coworkOnly, bool disabledOnly, double? minScore, double? maxScore,
            string sortBy, bool sortDesc, string actions = null)
        {
            return new LicensedUserQuery
            {
                Search = search,
                Bands = ParseBands(bands),
                Actions = ParseActions(actions),
                Department = department,
                Country = country,
                ReclaimEligibility = reclaimEligibility,
                CoworkOnly = coworkOnly,
                DisabledAccountsOnly = disabledOnly,
                MinScore = minScore,
                MaxScore = maxScore,
                SortBy = sortBy,
                SortDescending = sortDesc,
            };
        }

        private static LicenceOpportunityQuery BuildOpportunityQuery(
            string search, string department, string country,
            bool recommendedOnly, bool existingCopilotUsersOnly, double? minScore,
            string sortBy, bool sortDesc)
        {
            return new LicenceOpportunityQuery
            {
                Search = search,
                Department = department,
                Country = country,
                RecommendedOnly = recommendedOnly,
                ExistingCopilotUsersOnly = existingCopilotUsersOnly,
                MinScore = minScore,
                SortBy = sortBy,
                SortDescending = sortDesc,
            };
        }

        /// <summary>
        /// Parses a comma-separated list of Cowork tier codes down to the known catalogue.
        ///
        /// Unknown tokens are dropped rather than passed through, for the same reason as
        /// <see cref="ParseActions"/>: an unrecognised code would filter the list to nothing and read as
        /// "nobody is a candidate for Cowork", which is the most misleading possible failure on a page
        /// whose job is to size a rollout.
        /// </summary>
        internal static List<string> ParseCoworkTiers(string commaSeparated)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(commaSeparated))
            {
                return result;
            }

            foreach (var part in commaSeparated.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim();
                var known = CopilotAdoptionScoring.AllCoworkTiers
                    .FirstOrDefault(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase));

                if (known != null && !result.Contains(known))
                {
                    result.Add(known);
                }
            }

            return result;
        }

        private static CoworkReadinessQuery BuildCoworkQuery(
            string search, string tiers, string department, string country,
            bool recommendedOnly, bool coworkUsersOnly, double? minLoad, double? minFluency,
            string sortBy, bool sortDesc)
        {
            return new CoworkReadinessQuery
            {
                Search = search,
                Tiers = ParseCoworkTiers(tiers),
                Department = department,
                Country = country,
                RecommendedOnly = recommendedOnly,
                CoworkUsersOnly = coworkUsersOnly,
                MinCoordinationLoad = minLoad,
                MinFluency = minFluency,
                SortBy = sortBy,
                SortDescending = sortDesc,
            };
        }

        /// <summary>
        /// Folds the scope into a CSV file name: the email domain, and "filtered" when a user filter
        /// narrowed the rows.
        /// </summary>
        /// <remarks>
        /// A spreadsheet outlives the screen it was exported from and gets forwarded without that
        /// context, so a file describing one of several organisations in a tenant has to say which one
        /// somewhere durable. The rows carry an "Email domain" column, but columns get reordered and
        /// trimmed downstream; the file name survives. Same reasoning as the workbook's scope banner. A
        /// user filter is too long to spell out in a file name, but saying that there was one keeps the
        /// file from being mistaken for the whole list.
        /// </remarks>
        private static string ScopedFileNamePrefix(
            string prefix, string emailDomain, UserFilterExpression userFilter = null, CopilotAdoptionSummary summary = null)
        {
            var scope = CopilotAdoptionEmailDomain.Normalise(emailDomain);
            var name = string.IsNullOrWhiteSpace(scope) ? prefix : prefix + "-" + scope;
            if (summary?.Options?.UsesExplicitDates == true)
            {
                name += "-" + summary.FromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + "-to-" + summary.ToUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            // The administrator's global filter narrows the file exactly as the reader's own filter does.
            var filtered = (userFilter != null && !userFilter.IsEmpty) || summary?.GlobalFilter != null;
            return filtered ? name + "-filtered" : name;
        }

        private static List<string> Distinct(IEnumerable<string> values)        {
            return values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// A CSV file download. The bytes already carry a UTF-8 BOM (see <see cref="CsvSerialiser"/>)
        /// so Excel renders non-ASCII names correctly, and the charset is declared explicitly for
        /// everything that is not Excel.
        /// </summary>
        private static HttpResponseMessage CsvResponse(byte[] csv, string fileName, string runId)
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
            AddRunIdHeader(response, runId);

            return response;
        }

        #endregion
    }
}
