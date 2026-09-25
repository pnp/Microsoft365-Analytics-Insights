using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models.CopilotAdoption;

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
    /// </list>
    /// </summary>
    [Authorize]
    [Route("api/CopilotAdoption")]
    public class CopilotAdoptionAPIController  : ControllerBase
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
            : this(CopilotAdoptionAnalysisCoordinator.Default)
        {
        }

        internal CopilotAdoptionAPIController(CopilotAdoptionAnalysisCoordinator coordinator)
        {
            Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        internal CopilotAdoptionAnalysisCoordinator Coordinator { get; }

        #region Availability

        /// <summary>
        /// Whether this deployment can show the tool at all, and what is missing if it cannot. Called
        /// before anything heavy so the SPA can hide the tab (or explain itself) rather than showing an
        /// empty dashboard that looks like zero adoption.
        /// </summary>
        // GET: api/CopilotAdoption/availability
        [HttpGet]
        [Route("availability")]
        public IActionResult Availability()
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
            int windowDays, string seatLicenceTypeIds, CancellationToken cancellationToken)
        {
            return await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, FirstResponseBudget, cancellationToken);
        }

        /// <summary>
        /// The analysis with its row collections narrowed to one email domain, for the list, paging and
        /// export endpoints.
        /// </summary>
        /// <remarks>
        /// Deliberately the cheap half of the scope filter: these endpoints read rows and data-source
        /// warnings, never the aggregate figures, so there is no reason to re-score the population on
        /// every page of a table. The summary endpoint uses <see cref="TryGetScopedSummaryAsync"/>,
        /// which does.
        /// </remarks>
        private async Task<CopilotAdoptionAnalysis> TryGetScopedRowsAsync(
            int windowDays, string seatLicenceTypeIds, string emailDomain, TimeSpan budget, CancellationToken cancellationToken)
        {
            var analysis = await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, budget, cancellationToken);
            if (analysis == null) return null;

            return CopilotAdoptionScopeFilter.FilterRows(
                analysis, CopilotAdoptionScope.ForEmailDomain(emailDomain));
        }

        private async Task<CopilotAdoptionAnalysis> TryGetScopedRowsAsync(
            int windowDays, string seatLicenceTypeIds, string emailDomain, CancellationToken cancellationToken)
        {
            return await TryGetScopedRowsAsync(
                windowDays, seatLicenceTypeIds, emailDomain, FirstResponseBudget, cancellationToken);
        }

        /// <summary>
        /// The analysis with every aggregate recomputed for one email domain.
        /// </summary>
        /// <remarks>
        /// The heavy SQL stays cached tenant-wide and shared - narrowing re-scores the loaded rows in
        /// memory instead of re-querying per domain, which would multiply the load on a database the
        /// importer is already sharing. Sections that cannot be narrowed are carried across and named
        /// in <see cref="CopilotAdoptionSummary.UnscopedSections"/> so the page can label them.
        /// </remarks>
        private async Task<CopilotAdoptionAnalysis> TryGetScopedSummaryAsync(
            int windowDays, string seatLicenceTypeIds, string emailDomain,
            TimeSpan budget, CancellationToken cancellationToken)
        {
            var analysis = await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, budget, cancellationToken);
            if (analysis == null) return null;

            var scope = CopilotAdoptionScope.ForEmailDomain(emailDomain);
            if (!scope.IsNarrowed) return analysis;

            var service = new CopilotAdoptionService(analysis.Summary.Options);
            return CopilotAdoptionScopeFilter.Apply(analysis, scope, service.FinaliseSummary);
        }

        /// <summary>
        /// As above, but with an explicit wait budget. Exports use a much longer one - see
        /// <see cref="ExportWaitBudget"/>.
        /// </summary>
        private async Task<CopilotAdoptionAnalysis> TryGetAnalysisAsync(
            int windowDays, string seatLicenceTypeIds, TimeSpan budget, CancellationToken cancellationToken)
        {
            return await Coordinator.TryGetAsync(
                NormaliseWindowDays(windowDays),
                ParseIds(seatLicenceTypeIds),
                budget,
                cancellationToken);
        }

        /// <summary>
        /// The 202 body. Deliberately the same shape for every endpoint so the SPA has one thing to detect.
        /// </summary>
        private IActionResult StillBuilding(int windowDays, string seatLicenceTypeIds)
            => StillBuildingResponse(InFlightRunId(windowDays, seatLicenceTypeIds));

        /// <summary>
        /// The telemetry id of the run a 202 is waiting on, so a browser trace can be matched to the run's
        /// <c>CopilotAdoptionLifecycle</c> events in Application Insights.
        /// </summary>
        private string InFlightRunId(int windowDays, string seatLicenceTypeIds)
        {
            return Coordinator.InFlightRunId(NormaliseWindowDays(windowDays), ParseIds(seatLicenceTypeIds));
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
        /// The 202 response itself, with its <c>Retry-After</c> and run id headers. One place builds it
        /// so every endpoint that can still be waiting answers identically.
        /// </summary>
        private IActionResult StillBuildingResponse(string runId)
        {
            Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            AddRunIdHeader(Response, runId);

            // A dictionary rather than a model, as on the Web API 2 build: its keys are written exactly
            // as given whichever serializer the host runs, so the body the SPA detects cannot be
            // re-cased by a naming policy.
            return StatusCode((int)HttpStatusCode.Accepted, StillBuildingBody(runId));
        }

        /// <summary>
        /// Adds the run id header to the response being built. Set on <see cref="HttpResponse.Headers"/>
        /// before the result executes; the file and content results add their own headers on top and
        /// leave this one in place.
        /// </summary>
        private static void AddRunIdHeader(HttpResponse response, string runId)
        {
            if (response != null && !string.IsNullOrEmpty(runId)) response.Headers[RunIdHeader] = runId;
        }

        /// <summary>
        /// What an EXPORT returns when the analysis did not finish inside <see cref="ExportWaitBudget"/>.
        /// </summary>
        /// <remarks>
        /// Plain text, not JSON: this lands in a browser tab as the result of a download navigation, so
        /// the person who clicked has to be able to read it. 503 + <c>Retry-After</c> is the honest
        /// status - the report is temporarily unavailable and retrying later will work.
        /// </remarks>
        private IActionResult ExportNotReadyResponse(int windowDays, string seatLicenceTypeIds)
        {
            Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            AddRunIdHeader(Response, InFlightRunId(windowDays, seatLicenceTypeIds));

            return new ContentResult
            {
                StatusCode = (int)HttpStatusCode.ServiceUnavailable,
                ContentType = "text/plain; charset=utf-8",
                Content =
                    "The Copilot adoption analysis is still being prepared, so this export is not ready yet.\r\n\r\n"
                    + "This happens when the export is opened directly, or more than a few minutes after the "
                    + "page was last loaded. The analysis is still running in the background.\r\n\r\n"
                    + "Open the Copilot Adoption page, wait for it to finish loading, then use the export "
                    + "button there.",
            };
        }

        #endregion

        #region Summary and licence types

        /// <summary>The executive view: headline figures, the adoption funnel and the breakdown charts.</summary>
        // GET: api/CopilotAdoption/summary?windowDays=28&emailDomain=contoso.com
        [HttpGet]
        [Route("summary")]
        public async Task<IActionResult> Summary(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            string emailDomain = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetScopedSummaryAsync(
                windowDays, seatLicenceTypeIds, emailDomain, FirstResponseBudget, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, seatLicenceTypeIds);
            return Ok(analysis.Summary);
        }

        /// <summary>
        /// Every licence type in the tenant and whether it was counted as a Copilot seat.
        ///
        /// Exposed deliberately: Microsoft ships new Copilot SKUs faster than any shipped classification
        /// list can track, so an admin has to be able to see what the tool decided - and override it -
        /// rather than discover from a wrong headline number that a SKU was missed.
        /// </summary>
        // GET: api/CopilotAdoption/licence-types
        [HttpGet]
        [Route("licence-types")]
        public async Task<IActionResult> LicenceTypes(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, seatLicenceTypeIds);
            return Ok(analysis.Summary.SeatLicenceTypes);
        }

        /// <summary>The queries behind the numbers, for the SQL popover the rest of the admin site uses.</summary>
        // GET: api/CopilotAdoption/sql
        [HttpGet]
        [Route("sql")]
        public async Task<IActionResult> Sql(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, seatLicenceTypeIds);
            return Ok(analysis.Sql);
        }

        /// <summary>
        /// The distinct email domains, departments and countries present in the analysis, for the
        /// filter drop-downs. Derived from the already-loaded result rather than from another query.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT narrowed by the current scope: this is the list the domain filter itself is
        /// chosen from, so narrowing it would leave the selected domain as the only option and make the
        /// filter impossible to change.
        /// </remarks>
        // GET: api/CopilotAdoption/filters
        [HttpGet]
        [Route("filters")]
        public async Task<IActionResult> Filters(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, seatLicenceTypeIds);

            return Ok(new
            {
                // Every population, so a domain that holds no seats at all - an acquired business
                // using Copilot Chat without ever having been licensed - is still selectable.
                emailDomains = Distinct(
                    analysis.LicensedUsers.Select(u => CopilotAdoptionEmailDomain.Label(u.EmailDomain))
                        .Concat(analysis.Opportunities.Select(o => CopilotAdoptionEmailDomain.Label(o.EmailDomain)))
                        .Concat(analysis.CoworkReadiness.Select(c => CopilotAdoptionEmailDomain.Label(c.EmailDomain)))
                        .Concat(analysis.UnlicensedUsers.Select(u => CopilotAdoptionEmailDomain.Label(u.EmailDomain)))),
                departments = Distinct(
                    analysis.LicensedUsers.Select(u => u.Department)
                        .Concat(analysis.Opportunities.Select(o => o.Department))
                        .Concat(analysis.CoworkReadiness.Select(c => c.Department))),
                countries = Distinct(
                    analysis.LicensedUsers.Select(u => u.Country)
                        .Concat(analysis.Opportunities.Select(o => o.Country))
                        .Concat(analysis.CoworkReadiness.Select(c => c.Country))),
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
        public async Task<IActionResult> LicensedUsers(
            int windowDays = 28,
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
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetScopedRowsAsync(windowDays, seatLicenceTypeIds, emailDomain, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, seatLicenceTypeIds);

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
            });
        }

        /// <summary>
        /// The same list as a CSV download. Takes the identical filter parameters, so what is exported
        /// is exactly what is on screen - just without the paging.
        /// </summary>
        // GET: api/CopilotAdoption/licensed-users/export
        [HttpGet]
        [Route("licensed-users/export")]
        public async Task<IActionResult> ExportLicensedUsers(
            int windowDays = 28,
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
            CancellationToken cancellationToken = default(CancellationToken))
        {
            // Exports are <a href> downloads, not fetch() calls: a browser will not retry a 202, it
            // would just render the JSON body as the "file". So an export WAITS - but only up to
            // ExportWaitBudget, because waiting past the platform limit produced a 500 and a corrupt
            // download instead of an answer.
            var analysis = await TryGetScopedRowsAsync(
                windowDays, seatLicenceTypeIds, emailDomain, ExportWaitBudget, cancellationToken);
            if (analysis == null) return ExportNotReadyResponse(windowDays, seatLicenceTypeIds);

            var query = BuildLicensedUserQuery(
                search, bands, department, country, reclaimEligibility, coworkOnly, disabledOnly, minScore, maxScore, sortBy, sortDesc, actions);

            var rows = CopilotAdoptionExports.Apply(analysis.LicensedUsers, query).Take(MaxCsvRows).ToList();

            return CsvResponse(
                CsvSerialiser.ToBytes(
                    rows,
                    CopilotAdoptionExports.LicensedUserColumns(
                        analysis.Summary.FiguresIncomplete,
                        WarningSummary(analysis.Summary))),
                CsvSerialiser.FileName(ScopedFileNamePrefix("copilot-licensed-users", emailDomain), analysis.Summary.GeneratedUtc),
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
        public async Task<IActionResult> Opportunities(
            int windowDays = 28,
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
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetScopedRowsAsync(windowDays, seatLicenceTypeIds, emailDomain, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, seatLicenceTypeIds);

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
            });
        }

        // GET: api/CopilotAdoption/opportunities/export
        [HttpGet]
        [Route("opportunities/export")]
        public async Task<IActionResult> ExportOpportunities(
            int windowDays = 28,
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
            CancellationToken cancellationToken = default(CancellationToken))
        {
            // Exports are <a href> downloads, not fetch() calls: a browser will not retry a 202, it
            // would just render the JSON body as the "file". So an export WAITS - but only up to
            // ExportWaitBudget, because waiting past the platform limit produced a 500 and a corrupt
            // download instead of an answer.
            var analysis = await TryGetScopedRowsAsync(
                windowDays, seatLicenceTypeIds, emailDomain, ExportWaitBudget, cancellationToken);
            if (analysis == null) return ExportNotReadyResponse(windowDays, seatLicenceTypeIds);

            var query = BuildOpportunityQuery(
                search, department, country, recommendedOnly, existingCopilotUsersOnly, minScore, sortBy, sortDesc);

            var rows = CopilotAdoptionExports.Apply(analysis.Opportunities, query).Take(MaxCsvRows).ToList();

            return CsvResponse(
                CsvSerialiser.ToBytes(
                    rows,
                    CopilotAdoptionExports.LicenceOpportunityColumns(
                        analysis.Summary.FiguresIncomplete,
                        WarningSummary(analysis.Summary))),
                CsvSerialiser.FileName(ScopedFileNamePrefix("copilot-licence-opportunities", emailDomain), analysis.Summary.GeneratedUtc),
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
        public async Task<IActionResult> Cowork(
            int windowDays = 28,
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
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetScopedRowsAsync(windowDays, seatLicenceTypeIds, emailDomain, cancellationToken);
            if (analysis == null) return StillBuilding(windowDays, seatLicenceTypeIds);

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
        public async Task<IActionResult> ExportCowork(
            int windowDays = 28,
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
            CancellationToken cancellationToken = default(CancellationToken))
        {
            // Exports are <a href> downloads, not fetch() calls - see ExportOpportunities.
            var analysis = await TryGetScopedRowsAsync(
                windowDays, seatLicenceTypeIds, emailDomain, ExportWaitBudget, cancellationToken);
            if (analysis == null) return ExportNotReadyResponse(windowDays, seatLicenceTypeIds);

            var query = BuildCoworkQuery(
                search, tiers, department, country, recommendedOnly, coworkUsersOnly,
                minLoad, minFluency, sortBy, sortDesc);

            var rows = CopilotAdoptionExports.Apply(analysis.CoworkReadiness, query).Take(MaxCsvRows).ToList();

            return CsvResponse(
                CsvSerialiser.ToBytes(rows, CopilotAdoptionExports.CoworkReadinessColumns()),
                CsvSerialiser.FileName(ScopedFileNamePrefix("copilot-cowork-readiness", emailDomain), analysis.Summary.GeneratedUtc),
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
        /// <c>copilotMinutesSavedPerDocument</c> restate the licence estimate;
        /// <c>coworkMinutesSavedPerTask</c> and, for each kind of work Cowork could take on, its share and
        /// minutes under the option's own name (<c>coworkOrganiseMeetingsShare</c>,
        /// <c>coworkOrganiseMeetingsMinutes</c> and so on - see <see cref="CoworkActivities"/>) restate the
        /// Cowork estimate; <c>coworkEstimateLowerBoundRatio</c> applies to both. The per-activity figures
        /// are read from the query string by those names rather than bound one parameter each, so a kind
        /// of work added to the catalogue needs no change here. Those figures live in the browser only, so
        /// the export has to be told them or a customised page would download a workbook modelling
        /// different hours. They change the modelled estimates and the matching Settings rows, never a
        /// measured figure, and never the cached analysis itself.</para>
        /// </summary>
        // GET: api/CopilotAdoption/export/workbook?windowDays=28
        [HttpGet]
        [Route("export/workbook")]
        public async Task<IActionResult> ExportWorkbook(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            string emailDomain = null,
            string copilotMinutesSavedPerMeeting = null,
            string copilotMinutesSavedPerMailThread = null,
            string copilotMinutesSavedPerDocument = null,
            string coworkEstimateLowerBoundRatio = null,
            string coworkMinutesSavedPerTask = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            // Exports are <a href> downloads, not fetch() calls: a browser will not retry a 202, it
            // would just render the JSON body as the "file". So an export WAITS - but only up to
            // ExportWaitBudget, because waiting past the platform limit produced a 500 and a corrupt
            // download instead of an answer.
            var analysis = await TryGetScopedSummaryAsync(
                windowDays, seatLicenceTypeIds, emailDomain, ExportWaitBudget, cancellationToken);
            if (analysis == null) return ExportNotReadyResponse(windowDays, seatLicenceTypeIds);

            var timeSaved = ParseTimeSavedOverrides(
                copilotMinutesSavedPerMeeting,
                copilotMinutesSavedPerMailThread,
                copilotMinutesSavedPerDocument,
                coworkEstimateLowerBoundRatio,
                coworkMinutesSavedPerTask,
                QueryNameValuePairs(Request));

            byte[] bytes;
            try
            {
                bytes = CopilotAdoptionWorkbook.Build(analysis, timeSaved.Any ? timeSaved : null);
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

                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    ContentType = "text/plain; charset=utf-8",
                    Content =
                        "The Copilot adoption workbook could not be generated. The failure has been logged; "
                        + "the CSV exports on the Licensed users and Licence opportunities tabs are unaffected.",
                };
            }

            AddRunIdHeader(Response, analysis.Summary.Diagnostics?.RunId);

            return new FileContentResult(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
            {
                FileDownloadName = CopilotAdoptionWorkbook.FileName(analysis.Summary),
            };
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
            string minutesPerTask = null,
            IEnumerable<KeyValuePair<string, string>> query = null)
        {
            var overrides = new TimeSavedOverrides
            {
                MinutesSavedPerMeeting = ParseInvariantDouble(minutesPerMeeting),
                MinutesSavedPerMailThread = ParseInvariantDouble(minutesPerMailThread),
                MinutesSavedPerDocument = ParseInvariantDouble(minutesPerDocument),
                LowerBoundRatio = ParseInvariantDouble(lowerBoundRatio),
                MinutesSavedPerTask = ParseInvariantDouble(minutesPerTask),
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
            if (AllowedWindowDays.Contains(windowDays))
            {
                return windowDays;
            }

            return AllowedWindowDays
                .OrderBy(allowed => Math.Abs(allowed - windowDays))
                .ThenBy(allowed => allowed)
                .First();
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
        /// Folds the email-domain scope into a CSV file name.
        /// </summary>
        /// <remarks>
        /// A spreadsheet outlives the screen it was exported from and gets forwarded without that
        /// context, so a file describing one of several organisations in a tenant has to say which one
        /// somewhere durable. The rows carry an "Email domain" column, but columns get reordered and
        /// trimmed downstream; the file name survives. Same reasoning as the workbook's scope banner.
        /// </remarks>
        private static string ScopedFileNamePrefix(string prefix, string emailDomain)
        {
            var scope = CopilotAdoptionEmailDomain.Normalise(emailDomain);

            return string.IsNullOrWhiteSpace(scope) ? prefix : prefix + "-" + scope;
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
        private IActionResult CsvResponse(byte[] csv, string fileName, string runId)
        {
            AddRunIdHeader(Response, runId);

            // FileContentResult sets Content-Type and an attachment Content-Disposition with the given name.
            return new FileContentResult(csv, "text/csv; charset=utf-8") { FileDownloadName = fileName };
        }

        /// <summary>
        /// The request's query string as name/value pairs, for <see cref="ParseTimeSavedOverrides"/>.
        /// </summary>
        /// <remarks>
        /// The ASP.NET Core counterpart of Web API 2's <c>Request.GetQueryNameValuePairs()</c>. That
        /// yielded a repeated key once per value and the parser keeps the first; ASP.NET Core groups a
        /// repeated key's values into one <see cref="Microsoft.Extensions.Primitives.StringValues"/>,
        /// so the first of them is handed over - never the comma-joined <c>ToString()</c>, which would
        /// turn <c>?coworkSendEmailShare=0.1&amp;coworkSendEmailShare=0.9</c> into an unparseable
        /// "0.1,0.9" and silently drop the reader's figure.
        /// </remarks>
        internal static IEnumerable<KeyValuePair<string, string>> QueryNameValuePairs(HttpRequest request)
        {
            if (request?.Query == null) return null;

            return request.Query
                .Select(pair => new KeyValuePair<string, string>(
                    pair.Key, pair.Value.Count > 0 ? pair.Value[0] : null))
                .ToList();
        }

        #endregion
    }
}