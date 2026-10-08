using Common.Entities.CopilotAdoption;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
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
    [RoutePrefix("api/CopilotAdoptionSettings")]
    public class CopilotAdoptionSettingsAPIController : ApiController
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
        public Task<IHttpActionResult> Get() => Answer(async service => Ok(await service.GetAsync()));

        // POST: api/CopilotAdoptionSettings
        [HttpPost]
        [Route("")]
        [RequireSameOriginXhr]
        public Task<IHttpActionResult> Save([FromBody] CopilotAdoptionSettingsSaveRequest request)
            => Answer(async service => Ok(await service.SaveAsync(request, ChangedBy)));

        // POST: api/CopilotAdoptionSettings/reset
        [HttpPost]
        [Route("reset")]
        [RequireSameOriginXhr]
        public Task<IHttpActionResult> Reset([FromBody] CopilotAdoptionSettingsResetRequest request)
            => Answer(async service => Ok(await service.ResetAsync(request, ChangedBy)));

        /// <summary>The administrator's UPN where the token carries one, as the history shows it.</summary>
        private string ChangedBy => PortalViewer.UserPrincipalNameOf(User) ?? User?.Identity?.Name;

        private async Task<IHttpActionResult> Answer(Func<CopilotAdoptionSettingsService, Task<IHttpActionResult>> action)
        {
            try
            {
                return await action(_createService());
            }
            catch (CopilotAdoptionScoreSettingsRejectedException ex)
            {
                return Content(StatusFor(ex.Code), new CopilotAdoptionSettingsError { Code = ex.Code, ValidationErrors = ex.ValidationErrors.ToList() }, Configuration.Formatters.JsonFormatter);
            }
            catch (CopilotAdoptionScoreSettingsUnavailableException ex)
            {
                WebExceptionTelemetry.Report(ex, "CopilotAdoptionScoreSettings");
                return Content(HttpStatusCode.ServiceUnavailable, new CopilotAdoptionSettingsError { Code = CopilotAdoptionScoreSettingsErrorCodes.StateUnavailable }, Configuration.Formatters.JsonFormatter);
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
