using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Newtonsoft.Json;
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
            : this(CopilotAdoptionAnalysisCoordinator.Default)
        {
        }

        internal CopilotAdoptionAPIController(CopilotAdoptionAnalysisCoordinator coordinator)
            : this(
                coordinator,
                () => new AppConfig().CopilotAdoptionGovernance,
                new SqlCopilotAdoptionExportAuditSink())
        {
        }

        internal CopilotAdoptionAPIController(
            CopilotAdoptionAnalysisCoordinator coordinator,
            Func<CopilotAdoptionGovernanceSettings> governanceSettingsFactory,
            ICopilotAdoptionExportAuditSink exportAuditSink)
        {
            Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _governanceSettingsFactory = governanceSettingsFactory ?? (() => new CopilotAdoptionGovernanceSettings());
            _exportAuditSink = exportAuditSink ?? new SqlCopilotAdoptionExportAuditSink();
        }

        internal CopilotAdoptionAnalysisCoordinator Coordinator { get; }

        private readonly Func<CopilotAdoptionGovernanceSettings> _governanceSettingsFactory;
        private readonly ICopilotAdoptionExportAuditSink _exportAuditSink;

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
            var config = new AppConfig();
            var settings = config.ImportJobSettings ?? new ImportTaskSettings();
            // Read through the same factory every other endpoint uses. AppConfig always constructs a
            // governance object, so a null-coalescing fallback here would never fire and this endpoint
            // would be the one place the policy could not be substituted - including in the tests that
            // prove the off-switch.
            var governance = GetGovernanceSettings();

            var model = new CopilotAdoptionAvailability
            {
                CopilotAuditImportEnabled = settings.Copilot,
                CopilotUsageReportImportEnabled = settings.GraphCopilotUsageReports,
                UserMetadataImportEnabled = settings.GraphUsersMetadata,
                M365UsageReportImportEnabled = settings.GraphUsageReports,
                CanViewIndividualData = governance.HasIndividualDataAccess(UserPrincipal),
                IndividualDataDisabled = governance.DisableIndividualData,
                IndividualDataPseudonymised = governance.PseudonymiseIndividualData,
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

            if (!model.CanViewIndividualData)
            {
                model.Messages.Add(governance.IndividualDataDeniedMessage(UserPrincipal));
            }
            else if (model.IndividualDataPseudonymised)
            {
                model.Messages.Add(
                    "Per-user Copilot Adoption rows are pseudonymised on this deployment. Department, country, "
                    + "band and activity measures remain visible, but direct identifiers are replaced by stable "
                    + "surrogates in the page and in every export.");
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
        private static readonly TimeSpan ExportWaitBudget = TimeSpan.FromSeconds(150);

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
        private IHttpActionResult StillBuilding()
        {
            return ResponseMessage(StillBuildingResponse());
        }

        /// <summary>
        /// The same 202, for the export endpoints - they return <see cref="HttpResponseMessage"/> directly
        /// because they stream a file rather than a model.
        /// </summary>
        private HttpResponseMessage StillBuildingResponse()
        {
            var response = Request.CreateResponse(
                HttpStatusCode.Accepted,
                new
                {
                    status = "building",
                    retryAfterSeconds = RetryAfterSeconds,
                    message = "The Copilot adoption analysis is still running. This can take a few minutes the "
                              + "first time on a large tenant; the page will refresh automatically.",
                });

            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfterSeconds));
            return response;
        }

        /// <summary>
        /// What an EXPORT returns when the analysis did not finish inside <see cref="ExportWaitBudget"/>.
        /// </summary>
        /// <remarks>
        /// Plain text, not JSON: this lands in a browser tab as the result of a download navigation, so
        /// the person who clicked has to be able to read it. 503 + <c>Retry-After</c> is the honest
        /// status - the report is temporarily unavailable and retrying later will work.
        /// </remarks>
        private HttpResponseMessage ExportNotReadyResponse()
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
            return response;
        }

        #endregion

        #region Summary and licence types

        /// <summary>The executive view: headline figures, the adoption funnel and the breakdown charts.</summary>
        // GET: api/CopilotAdoption/summary?windowDays=28
        [HttpGet]
        [Route("summary")]
        public async Task<IHttpActionResult> Summary(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            if (analysis == null) return StillBuilding();
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
        public async Task<IHttpActionResult> LicenceTypes(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            if (analysis == null) return StillBuilding();
            return Ok(analysis.Summary.SeatLicenceTypes);
        }

        /// <summary>The queries behind the numbers, for the SQL popover the rest of the admin site uses.</summary>
        // GET: api/CopilotAdoption/sql
        [HttpGet]
        [Route("sql")]
        public async Task<IHttpActionResult> Sql(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            if (analysis == null) return StillBuilding();
            return Ok(analysis.Sql);
        }

        /// <summary>
        /// The distinct departments and countries present in the analysis, for the filter drop-downs.
        /// Derived from the already-loaded result rather than from another query.
        /// </summary>
        // GET: api/CopilotAdoption/filters
        [HttpGet]
        [Route("filters")]
        public async Task<IHttpActionResult> Filters(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var governance = GetGovernanceSettings();
            if (!governance.HasIndividualDataAccess(UserPrincipal))
            {
                return IndividualDataForbidden(governance);
            }

            var analysis = await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            if (analysis == null) return StillBuilding();

            // Deliberately NOT pseudonymised. This endpoint returns only department and country, which
            // are cohort columns the redaction carries through unchanged - so pseudonymising first
            // would clone the whole scored population and hash every row to produce a byte-identical
            // answer. At the 200,000-user design point that is a large per-request cost for nothing.
            // The [Authorize] attribute on the controller is what protects this endpoint, the same as
            // every other one; the redaction has nothing to remove here.
            return Ok(new
            {
                departments = Distinct(
                    analysis.LicensedUsers.Select(u => u.Department)
                        .Concat(analysis.Opportunities.Select(o => o.Department))),
                countries = Distinct(
                    analysis.LicensedUsers.Select(u => u.Country)
                        .Concat(analysis.Opportunities.Select(o => o.Country))),
                bands = CopilotAdoptionScoring.AllBands
                    .Select(b => new { value = (int)b, name = CopilotAdoptionScoring.BandDisplayName(b) })
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
        public async Task<IHttpActionResult> LicensedUsers(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            string search = null,
            string bands = null,
            string actions = null,
            string department = null,
            string country = null,
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
            var governance = GetGovernanceSettings();
            if (!governance.HasIndividualDataAccess(UserPrincipal))
            {
                return IndividualDataForbidden(governance);
            }

            var analysis = await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            if (analysis == null) return StillBuilding();

            var query = BuildLicensedUserQuery(
                search, bands, department, country, reclaimEligibility, coworkOnly, disabledOnly, minScore, maxScore, sortBy, sortDesc, actions);

            var matched = CopilotAdoptionExports.Apply(GovernedRows(analysis.LicensedUsers, governance), query);

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
        public async Task<HttpResponseMessage> ExportLicensedUsers(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            string search = null,
            string bands = null,
            string actions = null,
            string department = null,
            string country = null,
            string reclaimEligibility = null,
            bool coworkOnly = false,
            bool disabledOnly = false,
            double? minScore = null,
            double? maxScore = null,
            string sortBy = LicensedUserSortFields.Score,
            bool sortDesc = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var governance = GetGovernanceSettings();
            var audit = NewExportAudit("licensed-users/export", windowDays, governance);
            HttpResponseMessage response;

            try
            {
                if (!governance.HasIndividualDataAccess(UserPrincipal))
                {
                    audit.StatusCode = (int)HttpStatusCode.Forbidden;
                    audit.FailureReason = governance.IndividualDataDeniedMessage(UserPrincipal);
                    response = IndividualDataForbiddenResponse(governance);
                }
                else
                {
                    // Exports are <a href> downloads, not fetch() calls: a browser will not retry a 202, it
                    // would just render the JSON body as the "file". So an export WAITS - but only up to
                    // ExportWaitBudget, because waiting past the platform limit produced a 500 and a corrupt
                    // download instead of an answer.
                    var analysis = await TryGetAnalysisAsync(
                        windowDays, seatLicenceTypeIds, ExportWaitBudget, cancellationToken);
                    if (analysis == null)
                    {
                        audit.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        audit.FailureReason = "analysis-not-ready";
                        response = ExportNotReadyResponse();
                    }
                    else
                    {
                        var query = BuildLicensedUserQuery(
                            search, bands, department, country, reclaimEligibility, coworkOnly, disabledOnly, minScore, maxScore, sortBy, sortDesc, actions);

                        var matched = CopilotAdoptionExports.Apply(GovernedRows(analysis.LicensedUsers, governance), query);
                        var rows = matched.Take(MaxCsvRows).ToList();
                        audit.OptionsJson = JsonConvert.SerializeObject(analysis.Summary.Options);
                        audit.RowCount = rows.Count;
                        audit.Truncated = matched.Count > rows.Count;
                        audit.Succeeded = true;
                        audit.StatusCode = (int)HttpStatusCode.OK;

                        response = CsvResponse(
                            CsvSerialiser.ToBytes(
                                rows,
                                CopilotAdoptionExports.LicensedUserColumns(
                                    analysis.Summary.FiguresIncomplete,
                                    WarningSummary(analysis.Summary))),
                            CsvSerialiser.FileName("copilot-licensed-users", analysis.Summary.GeneratedUtc));
                    }
                }
            }
            catch (Exception ex)
            {
                audit.StatusCode = (int)HttpStatusCode.InternalServerError;
                audit.FailureReason = ex.GetType().Name;
                response = Request.CreateResponse(HttpStatusCode.InternalServerError,
                    "The Copilot Adoption licensed-user export failed. The failure has been recorded.");
            }

            return AuditAndMaybeBlockExport(audit, response);
        }

        #endregion

        #region Licence opportunities

        /// <summary>
        /// Unlicensed users ranked as candidates for a Copilot seat, strongest business case first.
        /// </summary>
        // GET: api/CopilotAdoption/opportunities?windowDays=28&skip=0&take=50
        [HttpGet]
        [Route("opportunities")]
        public async Task<IHttpActionResult> Opportunities(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            string search = null,
            string department = null,
            string country = null,
            bool recommendedOnly = false,
            bool existingCopilotUsersOnly = false,
            double? minScore = null,
            string sortBy = LicenceOpportunitySortFields.Score,
            bool sortDesc = true,
            int skip = 0,
            int take = DefaultTake,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var governance = GetGovernanceSettings();
            if (!governance.HasIndividualDataAccess(UserPrincipal))
            {
                return IndividualDataForbidden(governance);
            }

            var analysis = await TryGetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            if (analysis == null) return StillBuilding();

            var query = BuildOpportunityQuery(
                search, department, country, recommendedOnly, existingCopilotUsersOnly, minScore, sortBy, sortDesc);

            var matched = CopilotAdoptionExports.Apply(GovernedRows(analysis.Opportunities, governance), query);

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
        public async Task<HttpResponseMessage> ExportOpportunities(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            string search = null,
            string department = null,
            string country = null,
            bool recommendedOnly = false,
            bool existingCopilotUsersOnly = false,
            double? minScore = null,
            string sortBy = LicenceOpportunitySortFields.Score,
            bool sortDesc = true,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var governance = GetGovernanceSettings();
            var audit = NewExportAudit("opportunities/export", windowDays, governance);
            HttpResponseMessage response;

            try
            {
                if (!governance.HasIndividualDataAccess(UserPrincipal))
                {
                    audit.StatusCode = (int)HttpStatusCode.Forbidden;
                    audit.FailureReason = governance.IndividualDataDeniedMessage(UserPrincipal);
                    response = IndividualDataForbiddenResponse(governance);
                }
                else
                {
                    var analysis = await TryGetAnalysisAsync(
                        windowDays, seatLicenceTypeIds, ExportWaitBudget, cancellationToken);
                    if (analysis == null)
                    {
                        audit.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        audit.FailureReason = "analysis-not-ready";
                        response = ExportNotReadyResponse();
                    }
                    else
                    {
                        var query = BuildOpportunityQuery(
                            search, department, country, recommendedOnly, existingCopilotUsersOnly, minScore, sortBy, sortDesc);

                        var matched = CopilotAdoptionExports.Apply(GovernedRows(analysis.Opportunities, governance), query);
                        var rows = matched.Take(MaxCsvRows).ToList();
                        audit.OptionsJson = JsonConvert.SerializeObject(analysis.Summary.Options);
                        audit.RowCount = rows.Count;
                        audit.Truncated = matched.Count > rows.Count;
                        audit.Succeeded = true;
                        audit.StatusCode = (int)HttpStatusCode.OK;

                        response = CsvResponse(
                            CsvSerialiser.ToBytes(
                                rows,
                                CopilotAdoptionExports.LicenceOpportunityColumns(
                                    analysis.Summary.FiguresIncomplete,
                                    WarningSummary(analysis.Summary))),
                            CsvSerialiser.FileName("copilot-licence-opportunities", analysis.Summary.GeneratedUtc));
                    }
                }
            }
            catch (Exception ex)
            {
                audit.StatusCode = (int)HttpStatusCode.InternalServerError;
                audit.FailureReason = ex.GetType().Name;
                response = Request.CreateResponse(HttpStatusCode.InternalServerError,
                    "The Copilot Adoption licence-opportunity export failed. The failure has been recorded.");
            }

            return AuditAndMaybeBlockExport(audit, response);
        }

        #endregion

        #region Workbook export

        /// <summary>
        /// The whole report as an Excel workbook - every figure, table and chart, with the charts live
        /// and bound to the cells rather than pasted in as pictures.
        ///
        /// Exists for the point-in-time snapshot. A screenshot of this page cannot be compared with
        /// another screenshot six months later: the numbers cannot be subtracted and nobody can tell
        /// what period or thresholds either was run with. The workbook records both, so two files taken
        /// before and after an enablement programme are comparable and the comparison is checkable.
        ///
        /// Built from the same cached analysis that renders the page, so the two can never disagree.
        /// </summary>
        // GET: api/CopilotAdoption/export/workbook?windowDays=28
        [HttpGet]
        [Route("export/workbook")]
        public async Task<HttpResponseMessage> ExportWorkbook(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var governance = GetGovernanceSettings();
            var audit = NewExportAudit("export/workbook", windowDays, governance);
            HttpResponseMessage response;

            try
            {
                if (!governance.HasIndividualDataAccess(UserPrincipal))
                {
                    audit.StatusCode = (int)HttpStatusCode.Forbidden;
                    audit.FailureReason = governance.IndividualDataDeniedMessage(UserPrincipal);
                    response = IndividualDataForbiddenResponse(governance);
                }
                else
                {
                    // Exports are <a href> downloads, not fetch() calls: a browser will not retry a 202, it
                    // would just render the JSON body as the "file". So an export WAITS - but only up to
                    // ExportWaitBudget, because waiting past the platform limit produced a 500 and a corrupt
                    // download instead of an answer.
                    var analysis = await TryGetAnalysisAsync(
                        windowDays, seatLicenceTypeIds, ExportWaitBudget, cancellationToken);
                    if (analysis == null)
                    {
                        audit.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        audit.FailureReason = "analysis-not-ready";
                        response = ExportNotReadyResponse();
                    }
                    else
                    {
                        byte[] bytes;
                        try
                        {
                            bytes = CopilotAdoptionWorkbook.Build(GovernedAnalysis(analysis, governance));
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

                            audit.StatusCode = (int)HttpStatusCode.InternalServerError;
                            audit.FailureReason = "workbook-build-failed";
                            response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                            {
                                Content = new StringContent(
                                    "The Copilot adoption workbook could not be generated. The failure has been logged; "
                                    + "the CSV exports on the Licensed users and Licence opportunities tabs are unaffected."),
                            };
                            return AuditAndMaybeBlockExport(audit, response);
                        }

                        audit.OptionsJson = JsonConvert.SerializeObject(analysis.Summary.Options);
                        audit.RowCount = (analysis.LicensedUsers?.Count ?? 0) + (analysis.Opportunities?.Count ?? 0);
                        audit.Truncated = (analysis.LicensedUsers?.Count ?? 0) > CopilotAdoptionWorkbook.MaxUserRows
                            || (analysis.Opportunities?.Count ?? 0) > CopilotAdoptionWorkbook.MaxUserRows;
                        audit.Succeeded = true;
                        audit.StatusCode = (int)HttpStatusCode.OK;

                        response = new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(bytes),
                        };

                        response.Content.Headers.ContentType =
                            new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                        {
                            FileName = CopilotAdoptionWorkbook.FileName(analysis.Summary),
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                audit.StatusCode = (int)HttpStatusCode.InternalServerError;
                audit.FailureReason = ex.GetType().Name;
                response = Request.CreateResponse(HttpStatusCode.InternalServerError,
                    "The Copilot Adoption workbook export failed. The failure has been recorded.");
            }

            return AuditAndMaybeBlockExport(audit, response);
        }

        #endregion

        #region Individual data governance

        /// <summary>
        /// Reads the current per-user governance policy. Kept behind a factory so tests can prove the
        /// off-switch's 403 path without touching application configuration or starting a SQL analysis.
        /// </summary>
        private CopilotAdoptionGovernanceSettings GetGovernanceSettings()
        {
            return _governanceSettingsFactory() ?? new CopilotAdoptionGovernanceSettings();
        }

        private IPrincipal UserPrincipal => User ?? RequestContext?.Principal;

        private IHttpActionResult IndividualDataForbidden(CopilotAdoptionGovernanceSettings governance)
        {
            return ResponseMessage(IndividualDataForbiddenResponse(governance));
        }

        private HttpResponseMessage IndividualDataForbiddenResponse(CopilotAdoptionGovernanceSettings governance)
        {
            var response = Request.CreateResponse(HttpStatusCode.Forbidden);
            response.Content = new StringContent(
                governance.IndividualDataDeniedMessage(UserPrincipal),
                Encoding.UTF8,
                "text/plain");
            return response;
        }

        private List<LicensedUserAdoptionRow> GovernedRows(
            IEnumerable<LicensedUserAdoptionRow> rows,
            CopilotAdoptionGovernanceSettings governance)
        {
            return governance.PseudonymiseIndividualData
                ? CopilotAdoptionPseudonymiser.Pseudonymise(rows, governance)
                : (rows ?? Enumerable.Empty<LicensedUserAdoptionRow>()).ToList();
        }

        private List<LicenceOpportunityRow> GovernedRows(
            IEnumerable<LicenceOpportunityRow> rows,
            CopilotAdoptionGovernanceSettings governance)
        {
            return governance.PseudonymiseIndividualData
                ? CopilotAdoptionPseudonymiser.Pseudonymise(rows, governance)
                : (rows ?? Enumerable.Empty<LicenceOpportunityRow>()).ToList();
        }

        private CopilotAdoptionAnalysis GovernedAnalysis(
            CopilotAdoptionAnalysis analysis,
            CopilotAdoptionGovernanceSettings governance)
        {
            if (!governance.PseudonymiseIndividualData) return analysis;

            return new CopilotAdoptionAnalysis
            {
                Summary = analysis.Summary,
                Sql = analysis.Sql,
                LicensedUsers = GovernedRows(analysis.LicensedUsers, governance),
                Opportunities = GovernedRows(analysis.Opportunities, governance),
                UnlicensedUsers = analysis.UnlicensedUsers,
                Agents = analysis.Agents,
            };
        }

        private CopilotAdoptionExportAuditRecord NewExportAudit(
            string endpoint,
            int windowDays,
            CopilotAdoptionGovernanceSettings governance)
        {
            return new CopilotAdoptionExportAuditRecord
            {
                Actor = ActorName(UserPrincipal),
                Endpoint = endpoint,
                Parameters = Request?.RequestUri?.Query,
                WindowDays = NormaliseWindowDays(windowDays),
                Pseudonymised = governance.PseudonymiseIndividualData,
                IndividualDataDisabled = governance.DisableIndividualData,
            };
        }

        private HttpResponseMessage AuditAndMaybeBlockExport(
            CopilotAdoptionExportAuditRecord audit,
            HttpResponseMessage response)
        {
            bool written;
            try
            {
                written = _exportAuditSink.Write(audit);
            }
            catch
            {
                written = false;
            }

            if (!written && response != null && response.IsSuccessStatusCode)
            {
                return Request.CreateResponse(
                    HttpStatusCode.InternalServerError,
                    "The Copilot Adoption export was blocked because the export audit row could not be written.");
            }

            return response;
        }

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

        private static string ActorName(IPrincipal principal)
        {
            var claims = principal as ClaimsPrincipal;
            var claim = claims?.Claims.FirstOrDefault(c =>
                c.Type == ClaimTypes.Upn
                || c.Type == ClaimTypes.Email
                || c.Type == ClaimTypes.Name
                || c.Type == "preferred_username");

            if (!string.IsNullOrWhiteSpace(claim?.Value)) return claim.Value;
            return principal?.Identity?.Name;
        }

        #endregion

        #region Parameter handling

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

        private static List<string> Distinct(IEnumerable<string> values)
        {
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
