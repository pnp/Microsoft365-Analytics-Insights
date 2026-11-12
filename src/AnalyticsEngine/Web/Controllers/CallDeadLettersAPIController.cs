using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System;
using System.Threading;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.Calls;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Backs the dead-lettered Teams calls card on the portal's Administration &gt; Service configuration page: the
    /// dead-letter count and the latest request to reprocess or clear the dead-letter queue (GET), and a new request (POST).
    /// </summary>
    /// <remarks>
    /// The web app only records a request; the importer carries it out and records the result (see
    /// <see cref="CallDeadLetterService"/>). Both actions need Administration: reprocessing re-imports calls, and clearing
    /// deletes them for good. The POST is a state-changing call, so the site-wide <see cref="SameOriginStateChangeFilter"/>
    /// refuses it unless it comes from the portal's own scripts. Refusals are a stable code and no text; the page writes the
    /// sentence.
    ///
    /// <para>net10: an ASP.NET Core controller with the Web API 2 build's routes, statuses and bodies. The request body is
    /// <c>[FromBody]</c> with an empty body allowed, so a missing body is the service's <c>unknownAction</c> 400, as it is
    /// there, rather than a model-binding error.</para>
    /// </remarks>
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [Route("api/CallDeadLetters")]
    public class CallDeadLettersAPIController : ControllerBase
    {
        private readonly Func<CallDeadLetterService> _createService;

        public CallDeadLettersAPIController()
            : this(CallDeadLetterService.ForThisDeployment)
        {
        }

        internal CallDeadLettersAPIController(Func<CallDeadLetterService> createService)
        {
            _createService = createService ?? throw new ArgumentNullException(nameof(createService));
        }

        // GET: api/CallDeadLetters
        [HttpGet]
        [Route("")]
        public async Task<IActionResult> Status(CancellationToken cancellationToken)
        {
            return Ok(await _createService().GetStatusAsync(cancellationToken));
        }

        // POST: api/CallDeadLetters/requests   { "action": "reprocess" | "clear" }
        [HttpPost]
        [Route("requests")]
        public async Task<IActionResult> Submit(
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CallDeadLetterSubmitRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                return StatusCode(StatusCodes.Status202Accepted, await _createService().SubmitAsync(request?.Action, cancellationToken));
            }
            catch (CallDeadLetterRequestException ex)
            {
                return StatusCode((int)ex.Status, new CallDeadLetterError { Code = ex.Code });
            }
        }
    }
}
