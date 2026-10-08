using Common.Entities.LeadershipCohort;
using System;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models.LeadershipCohort;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Configures the Entra ID group whose adoption the Copilot Adoption report compares with the tenant's (#654).
    /// Administration only. Independent of See PII: nothing here returns a person, only the group's name and counts.
    /// </summary>
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [RoutePrefix("api/LeadershipCohort")]
    public class LeadershipCohortAPIController : ApiController
    {
        private readonly Func<LeadershipCohortService> _createService;

        public LeadershipCohortAPIController() : this(LeadershipCohortService.ForThisDeployment) { }

        internal LeadershipCohortAPIController(Func<LeadershipCohortService> createService)
        {
            _createService = createService ?? throw new ArgumentNullException(nameof(createService));
        }

        [HttpGet]
        [Route("")]
        public Task<IHttpActionResult> Get() => Answer(async service => Ok(await service.GetStatusAsync()));

        [HttpPut]
        [Route("")]
        [RequireSameOriginXhr]
        public Task<IHttpActionResult> Save([FromBody] LeadershipCohortSaveRequest request)
            => Answer(async service => Ok(await service.SaveAsync(request)));

        [HttpPost]
        [Route("refresh")]
        [RequireSameOriginXhr]
        public Task<IHttpActionResult> Refresh() => Answer(async service => Ok(await service.RefreshAsync()));

        private async Task<IHttpActionResult> Answer(Func<LeadershipCohortService, Task<IHttpActionResult>> action)
        {
            try { return await action(_createService()); }
            catch (LeadershipCohortRequestException ex) { return Content(ex.Status, new LeadershipCohortError { Code = ex.Code }); }
            catch (LeadershipCohortStateUnavailableException)
            {
                return Content(HttpStatusCode.ServiceUnavailable, new LeadershipCohortError { Code = LeadershipCohortErrorCodes.StateUnavailable });
            }
        }
    }
}
