using Common.Entities.LeadershipCohort;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    [Route("api/LeadershipCohort")]
    public class LeadershipCohortAPIController : ControllerBase
    {
        private readonly Func<LeadershipCohortService> _createService;

        public LeadershipCohortAPIController() : this(LeadershipCohortService.ForThisDeployment) { }

        internal LeadershipCohortAPIController(Func<LeadershipCohortService> createService)
        {
            _createService = createService ?? throw new ArgumentNullException(nameof(createService));
        }

        [HttpGet]
        [Route("")]
        public Task<IActionResult> Get(CancellationToken cancellationToken = default) =>
            Answer(async service => Ok(await service.GetStatusAsync(cancellationToken)));

        [HttpPut]
        [Route("")]
        [RequireSameOriginXhr]
        public Task<IActionResult> Save([FromBody] LeadershipCohortSaveRequest request, CancellationToken cancellationToken = default)
            => Answer(async service => Ok(await service.SaveAsync(request, cancellationToken)));

        [HttpPost]
        [Route("refresh")]
        [RequireSameOriginXhr]
        public Task<IActionResult> Refresh(CancellationToken cancellationToken = default) =>
            Answer(async service => Ok(await service.RefreshAsync(cancellationToken)));

        private async Task<IActionResult> Answer(Func<LeadershipCohortService, Task<IActionResult>> action)
        {
            try { return await action(_createService()); }
            catch (LeadershipCohortRequestException ex) { return StatusCode((int)ex.Status, new LeadershipCohortError { Code = ex.Code }); }
            catch (LeadershipCohortStateUnavailableException)
            {
                return StatusCode(503, new LeadershipCohortError { Code = LeadershipCohortErrorCodes.StateUnavailable });
            }
        }
    }
}
