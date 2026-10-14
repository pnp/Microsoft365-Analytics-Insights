using Common.Entities.CopilotAdoption;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models.CopilotAdoption;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Administration &gt; Copilot Adoption settings: the tenant-wide engagement-score weights and band thresholds
    /// (issues #683, #684). Administration only - the history names who changed what - and every write requires a
    /// same-origin script request.
    /// </summary>
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [Route("api/CopilotAdoptionSettings")]
    public class CopilotAdoptionSettingsAPIController : ControllerBase
    {
        private readonly Func<CopilotAdoptionSettingsService> _createService;

        public CopilotAdoptionSettingsAPIController() : this(CopilotAdoptionSettingsService.ForThisDeployment) { }

        internal CopilotAdoptionSettingsAPIController(Func<CopilotAdoptionSettingsService> createService)
        {
            _createService = createService ?? throw new ArgumentNullException(nameof(createService));
        }

        // GET: api/CopilotAdoptionSettings
        [HttpGet]
        [Route("")]
        public Task<IActionResult> Get(CancellationToken cancellationToken = default) =>
            Answer(async service => Ok(await service.GetAsync(cancellationToken)));

        // POST: api/CopilotAdoptionSettings
        [HttpPost]
        [Route("")]
        [RequireSameOriginXhr]
        public Task<IActionResult> Save([FromBody] CopilotAdoptionSettingsSaveRequest request, CancellationToken cancellationToken = default)
            => Answer(async service => Ok(await service.SaveAsync(request, ChangedBy, cancellationToken)));

        // POST: api/CopilotAdoptionSettings/reset
        [HttpPost]
        [Route("reset")]
        [RequireSameOriginXhr]
        public Task<IActionResult> Reset([FromBody] CopilotAdoptionSettingsResetRequest request, CancellationToken cancellationToken = default)
            => Answer(async service => Ok(await service.ResetAsync(request, ChangedBy, cancellationToken)));

        /// <summary>The administrator's UPN where the token carries one, as the history shows it.</summary>
        private string ChangedBy => PortalViewer.UserPrincipalNameOf(User) ?? User?.Identity?.Name;

        private async Task<IActionResult> Answer(Func<CopilotAdoptionSettingsService, Task<IActionResult>> action)
        {
            try
            {
                return await action(_createService());
            }
            catch (CopilotAdoptionScoreSettingsRejectedException ex)
            {
                return StatusCode((int)StatusFor(ex.Code), new CopilotAdoptionSettingsError { Code = ex.Code, ValidationErrors = ex.ValidationErrors.ToList() });
            }
            catch (CopilotAdoptionScoreSettingsUnavailableException ex)
            {
                WebExceptionTelemetry.Report(ex, "CopilotAdoptionScoreSettings");
                return StatusCode(503, new CopilotAdoptionSettingsError { Code = CopilotAdoptionScoreSettingsErrorCodes.StateUnavailable });
            }
        }

        internal static HttpStatusCode StatusFor(string code)
        {
            switch (code)
            {
                case CopilotAdoptionScoreSettingsErrorCodes.VersionConflict: return HttpStatusCode.Conflict;
                case CopilotAdoptionScoreSettingsErrorCodes.StorageNotConfigured: return HttpStatusCode.ServiceUnavailable;
                default: return HttpStatusCode.BadRequest;
            }
        }
    }
}
