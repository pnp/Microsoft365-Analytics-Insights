using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Net;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.UserScope;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Backs the portal's Administration &gt; User scope page: the <c>UserGroupsFilter</c> scope, how it resolved,
    /// and purging everything stored about people outside it.
    /// </summary>
    /// <remarks>
    /// Every state-changing call is a POST carrying <see cref="RequireSameOriginXhrAttribute"/>: a signed-in admin's
    /// cookie alone is not enough to start a purge. Failures are answered with a stable error code and no text; the
    /// page writes the sentence.
    /// </remarks>
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [Route("api/UserScope")]
    public class UserScopeAPIController : ControllerBase
    {
        private readonly Func<UserScopeService> _createService;

        public UserScopeAPIController()
            : this(UserScopeService.ForThisDeployment)
        {
        }

        internal UserScopeAPIController(Func<UserScopeService> createService)
        {
            _createService = createService ?? throw new ArgumentNullException(nameof(createService));
        }

        // GET: api/UserScope
        [HttpGet]
        [Route("")]
        public Task<IActionResult> Get()
            => Answer(async service => Ok(await service.GetStatusAsync()));

        // POST: api/UserScope/refresh
        [HttpPost]
        [Route("refresh")]
        [RequireSameOriginXhr]
        public Task<IActionResult> Refresh()
            => Answer(async service => Ok(await service.RefreshAsync()));

        // POST: api/UserScope/purge   { "acknowledged": true }
        [HttpPost]
        [Route("purge")]
        [RequireSameOriginXhr]
        public Task<IActionResult> StartPurge([FromBody] UserScopePurgeRequest request)
            => Answer(async service => StatusCode((int)HttpStatusCode.Accepted,
                await service.StartPurgeAsync(request?.Acknowledged ?? false, User?.Identity?.Name)));

        // GET: api/UserScope/purge/{id}
        [HttpGet]
        [Route("purge/{id:int}")]
        public Task<IActionResult> GetPurge(int id)
            => Answer(async service => Ok(await service.GetPurgeAsync(id)));

        // POST: api/UserScope/purge/{id}/cancel
        [HttpPost]
        [Route("purge/{id:int}/cancel")]
        [RequireSameOriginXhr]
        public Task<IActionResult> CancelPurge(int id)
            => Answer(async service => Ok(await service.CancelPurgeAsync(id)));

        private async Task<IActionResult> Answer(Func<UserScopeService, Task<IActionResult>> action)
        {
            try
            {
                return await action(_createService());
            }
            catch (UserScopeRequestException ex)
            {
                return StatusCode((int)ex.Status, new UserScopeError { Code = ex.Code });
            }
        }
    }
}
