using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Net;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.CopilotAuditBackfill;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [Route("api/CopilotAuditBackfill")]
    public class CopilotAuditBackfillAPIController : ControllerBase
    {
        private readonly Func<CopilotAuditBackfillService> _createService;

        public CopilotAuditBackfillAPIController() : this(CopilotAuditBackfillService.ForThisDeployment) { }

        internal CopilotAuditBackfillAPIController(Func<CopilotAuditBackfillService> createService)
        {
            _createService = createService ?? throw new ArgumentNullException(nameof(createService));
        }

        [HttpGet]
        [Route("")]
        public Task<IActionResult> Get() => Answer(async service => Ok(await service.GetStatusAsync()));

        [HttpPost]
        [Route("start")]
        [RequireSameOriginXhr]
        public Task<IActionResult> Start([FromBody] CopilotAuditBackfillStartRequest request)
            => Answer(async service => StatusCode((int)HttpStatusCode.Accepted, await service.StartAsync(request, User?.Identity?.Name)));

        [HttpPost]
        [Route("{id:int}/cancel")]
        [RequireSameOriginXhr]
        public Task<IActionResult> Cancel(int id)
            => Answer(async service => Ok(await service.CancelAsync(id)));

        // net10: Web API 2's Content(status, value) is ported as StatusCode(status, value), an ObjectResult
        // written by the Web API compatible JSON formatter, so CopilotAuditBackfillError keeps its "code" name.
        private async Task<IActionResult> Answer(Func<CopilotAuditBackfillService, Task<IActionResult>> action)
        {
            try { return await action(_createService()); }
            catch (CopilotAuditBackfillRequestException ex) { return StatusCode((int)ex.Status, new CopilotAuditBackfillError { Code = ex.Code }); }
            catch (Common.Entities.CopilotAuditBackfill.CopilotAuditBackfillStateUnavailableException)
            {
                return StatusCode((int)HttpStatusCode.ServiceUnavailable, new CopilotAuditBackfillError { Code = Common.Entities.CopilotAuditBackfill.CopilotAuditBackfillErrorCodes.StateUnavailable });
            }
        }
    }
}
