using Common.Entities.Config;
using Common.Entities.UserDiagnostics;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.UserDataLookup;
using Web.AnalyticsWeb.Models.UserDiagnostics;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Admin lookup of everything held in SQL for a single user, keyed by UPN.
    /// Returns a profile + per-category record counts, and (per category) the most recent rows.
    /// </summary>
    /// <remarks>
    /// Model binding and the HTTP result only - the validation, category mapping and shaping live in
    /// <see cref="IUserDataLookupService"/>, and the EF queries in <see cref="SqlUserDataLookupQuery"/>,
    /// so both are testable without a database or an ASP.NET pipeline (issue #379).
    ///
    /// Needs both permissions: it is an Administration page, and everything it returns is about one
    /// named person (#660, #661).
    /// </remarks>
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [RequirePortalPermission(PortalPermission.SeePii)]
    [Route("api/UserDataLookup")]
    public class UserDataLookupAPIController  : ControllerBase
    {
        private readonly IUserDataLookupService _service;
        private readonly Func<UserDiagnosticsService> _diagnostics;

        public UserDataLookupAPIController() : this(new UserDataLookupService(new SqlUserDataLookupQuery()), UserDiagnosticsService.ForThisDeployment)
        {
        }

        internal UserDataLookupAPIController(IUserDataLookupService service) : this(service, UserDiagnosticsService.ForThisDeployment)
        {
        }

        internal UserDataLookupAPIController(IUserDataLookupService service, Func<UserDiagnosticsService> diagnostics)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        /// <summary>
        /// GET api/UserDataLookup/summary?upn=user@contoso.com
        /// Profile + per-category record counts for the user.
        /// </summary>
        [HttpGet]
        [Route("summary")]
        public async Task<IActionResult> Summary(string upn = "")
        {
            // AppConfig is read lazily so a bad request costs nothing, exactly as before the extraction.
            return ToActionResult(await _service.GetSummaryAsync(upn, () => new AppConfig().ImportJobSettings), upn);
        }

        /// <summary>
        /// GET api/UserDataLookup/detail?upn=user@contoso.com&amp;category=audit-events&amp;take=50
        /// The most recent rows for one category for the user.
        /// </summary>
        [HttpGet]
        [Route("detail")]
        public async Task<IActionResult> Detail(string upn = "", string category = "", int take = UserDataLookupRules.DefaultTake)
        {
            return ToActionResult(await _service.GetDetailAsync(upn, category, take), upn);
        }

        /// <summary>
        /// GET api/UserDataLookup/diagnostics?upn=user@contoso.com
        /// Stored licences with their Copilot classification, licence history, import freshness, scope and findings.
        /// Answers for a person the import has never stored too: that is often the question.
        /// </summary>
        [HttpGet]
        [Route("diagnostics")]
        public async Task<IActionResult> Diagnostics(string upn = "")
        {
            return ToActionResult(await _diagnostics().GetDiagnosticsAsync(upn), upn);
        }

        /// <summary>
        /// GET api/UserDataLookup/live-licences?upn=user@contoso.com
        /// Reads the person's licences from Microsoft Graph now and compares them with SQL. Read-only; refused without a
        /// Graph call for anyone not confirmed in the import scope.
        /// </summary>
        [HttpGet]
        [Route("live-licences")]
        public async Task<IActionResult> LiveLicences(string upn = "", CancellationToken cancellationToken = default(CancellationToken))
        {
            return ToActionResult(await _diagnostics().CompareLiveAsync(upn, cancellationToken), upn);
        }

        /// <summary>
        /// GET api/UserDataLookup/report-verdict?upn=...&amp;report=copilotAdoption&amp;windowDays=28[&amp;from=&amp;to=][&amp;licenceTypeIds=1,2]
        /// Why a report counts the person as licensed or unlicensed, recomputed with the report's own rule.
        /// </summary>
        [HttpGet]
        [Route("report-verdict")]
        public async Task<IActionResult> ReportVerdict(string upn = "", string report = "copilotAdoption", int windowDays = 28,
            string from = null, string to = null, string licenceTypeIds = null)
        {
            var ids = CopilotAdoptionAPIController.ParseIds(licenceTypeIds);
            return ToActionResult(await _diagnostics().GetReportVerdictAsync(upn, report, windowDays, from, to, ids), upn);
        }

        /// <summary>
        /// GET api/UserDataLookup/coverage?upn=user@contoso.com
        /// The newest stored record of each kind for the person, and what is known about each feeding import.
        /// </summary>
        [HttpGet]
        [Route("coverage")]
        public async Task<IActionResult> Coverage(string upn = "")
        {
            return ToActionResult(await _diagnostics().GetCoverageAsync(upn), upn);
        }

        /// <summary>
        /// POST api/UserDataLookup/refresh {"upn": "user@contoso.com"}
        /// Queues a one-person refresh of the profile and licences for the importer. Answers at once; follow the request
        /// with <c>refresh-status</c>. A refusal (out of scope, not stored, no Storage) is a 200 with a stable reason.
        /// </summary>
        [HttpPost]
        [Route("refresh")]
        [RequireSameOriginXhr]
        public async Task<IActionResult> Refresh([FromBody] UserRefreshSubmitRequest request)
        {
            var upn = request?.Upn ?? string.Empty;
            var requestedBy = PortalViewer.UserPrincipalNameOf(User) ?? User?.Identity?.Name;
            return ToActionResult(await _diagnostics().SubmitRefreshAsync(upn, requestedBy), upn);
        }

        /// <summary>
        /// GET api/UserDataLookup/refresh-status?id=... One refresh request and, once finished, its result. An unknown id is
        /// a bare 404: the portal words that itself, so the server sends no sentence.
        /// </summary>
        [HttpGet]
        [Route("refresh-status")]
        public async Task<IActionResult> RefreshStatus(string id = "")
        {
            var refresh = await _diagnostics().GetRefreshAsync(id);
            if (refresh == null)
            {
                return NotFound();
            }
            return Ok(refresh);
        }

        private IActionResult ToActionResult<T>(UserDataLookupResult<T> result, string upn) where T : class
        {
            switch (result.Status)
            {
                case UserDataLookupStatus.BadRequest:
                    return StatusCode((int)HttpStatusCode.BadRequest, BadRequestError(result.ErrorMessage));
                case UserDataLookupStatus.UserNotFound:
                    return StatusCode((int)HttpStatusCode.NotFound, new ApiErrorModel(result.ErrorMessage, "userNotFound")
                    {
                        Upn = UserDataLookupRules.Normalise(upn),
                    });
                default:
                    return Ok(result.Value);
            }
        }

        private static ApiErrorModel BadRequestError(string message)
        {
            if (message == "A 'upn' query parameter is required.")
                return new ApiErrorModel(message, "missingUpn");

            // The report-verdict range errors are already stable catalog keys (see CopilotAdoptionDateRange).
            if (message.StartsWith("copilotAdoption.error.", StringComparison.Ordinal))
                return new ApiErrorModel(message, message);

            if (message.StartsWith("Unknown report '", StringComparison.Ordinal))
                return new ApiErrorModel(message, "unknownReport");

            const string unknownPrefix = "Unknown category '";
            if (message.StartsWith(unknownPrefix) && message.EndsWith("'."))
                return new ApiErrorModel(message, "unknownCategory", message.Substring(
                    unknownPrefix.Length,
                    message.Length - unknownPrefix.Length - 2));

            const string noDrilldownPrefix = "Category '";
            const string noDrilldownSuffix = "' does not support drill-down.";
            if (message.StartsWith(noDrilldownPrefix) && message.EndsWith(noDrilldownSuffix))
                return new ApiErrorModel(message, "categoryNoDrilldown", message.Substring(
                    noDrilldownPrefix.Length,
                    message.Length - noDrilldownPrefix.Length - noDrilldownSuffix.Length));

            return new ApiErrorModel(message);
        }
    }
}