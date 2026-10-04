using System;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models.CopilotAuditBackfill;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [RoutePrefix("api/CopilotAuditBackfill")]
    public class CopilotAuditBackfillAPIController : ApiController
    {
        private readonly Func<CopilotAuditBackfillService> _createService;

        public CopilotAuditBackfillAPIController() : this(CopilotAuditBackfillService.ForThisDeployment) { }

        internal CopilotAuditBackfillAPIController(Func<CopilotAuditBackfillService> createService)
        {
            _createService = createService ?? throw new ArgumentNullException(nameof(createService));
        }

        [HttpGet]
        [Route("")]
        public Task<IHttpActionResult> Get() => Answer(async service => Ok(await service.GetStatusAsync()));

        [HttpPost]
        [Route("start")]
        [RequireSameOriginXhr]
        public Task<IHttpActionResult> Start([FromBody] CopilotAuditBackfillStartRequest request)
            => Answer(async service => Content(HttpStatusCode.Accepted, await service.StartAsync(request, User?.Identity?.Name)));

        [HttpPost]
        [Route("{id:int}/cancel")]
        [RequireSameOriginXhr]
        public Task<IHttpActionResult> Cancel(int id)
            => Answer(async service => Ok(await service.CancelAsync(id)));

        private async Task<IHttpActionResult> Answer(Func<CopilotAuditBackfillService, Task<IHttpActionResult>> action)
        {
            try { return await action(_createService()); }
            catch (CopilotAuditBackfillRequestException ex) { return Content(ex.Status, new CopilotAuditBackfillError { Code = ex.Code }); }
            catch (Common.Entities.CopilotAuditBackfill.CopilotAuditBackfillStateUnavailableException)
            {
                return Content(HttpStatusCode.ServiceUnavailable, new CopilotAuditBackfillError { Code = Common.Entities.CopilotAuditBackfill.CopilotAuditBackfillErrorCodes.StateUnavailable });
            }
        }
    }
}
