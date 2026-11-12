using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
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
    /// </remarks>
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [RoutePrefix("api/CallDeadLetters")]
    public class CallDeadLettersAPIController : ApiController
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
        public async Task<IHttpActionResult> Status(CancellationToken cancellationToken)
        {
            return Ok(await _createService().GetStatusAsync(cancellationToken));
        }

        // POST: api/CallDeadLetters/requests   { "action": "reprocess" | "clear" }
        [HttpPost]
        [Route("requests")]
        public async Task<IHttpActionResult> Submit([FromBody] CallDeadLetterSubmitRequest request, CancellationToken cancellationToken)
        {
            try
            {
                return Content(HttpStatusCode.Accepted, await _createService().SubmitAsync(request?.Action, cancellationToken));
            }
            catch (CallDeadLetterRequestException ex)
            {
                return Content(ex.Status, new CallDeadLetterError { Code = ex.Code });
            }
        }
    }
}
