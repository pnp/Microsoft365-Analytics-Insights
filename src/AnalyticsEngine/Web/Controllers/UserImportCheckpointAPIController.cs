using System;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models.UserImport;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Backs the portal's Administration &gt; User import page: whether the Graph user import has a stored
    /// checkpoint (its <c>/users/delta</c> token), and clearing it so the next run reads every user again (issue #664).
    /// </summary>
    /// <remarks>
    /// Clearing is the portal's first action that changes state, so it is a POST and carries
    /// <see cref="RequireSameOriginXhrAttribute"/>: a signed-in admin's cookie alone is not enough to trigger it.
    /// Failures are answered with a stable error code and no text; the page writes the sentence.
    /// </remarks>
    [Authorize]
    [RoutePrefix("api/UserImportCheckpoint")]
    public class UserImportCheckpointAPIController : ApiController
    {
        /// <summary>Storage is configured but could not be reached.</summary>
        internal const string StorageUnavailableCode = "storageUnavailable";

        /// <summary>There is no Storage connection string, so there is no checkpoint to clear.</summary>
        internal const string StorageNotConfiguredCode = "storageNotConfigured";

        private readonly Func<UserImportCheckpointService> _createService;

        public UserImportCheckpointAPIController()
            : this(UserImportCheckpointService.ForThisDeployment)
        {
        }

        internal UserImportCheckpointAPIController(Func<UserImportCheckpointService> createService)
        {
            _createService = createService ?? throw new ArgumentNullException(nameof(createService));
        }

        // GET: api/UserImportCheckpoint
        [HttpGet]
        [Route("")]
        public async Task<IHttpActionResult> Get()
        {
            try
            {
                return Ok(await _createService().GetStatusAsync());
            }
            catch (UserImportCheckpointUnavailableException)
            {
                return Failure(HttpStatusCode.ServiceUnavailable, StorageUnavailableCode);
            }
        }

        // POST: api/UserImportCheckpoint/clear   { "runOnNextCycle": true }
        [HttpPost]
        [Route("clear")]
        [RequireSameOriginXhr]
        public async Task<IHttpActionResult> Clear([FromBody] UserImportCheckpointClearRequest request)
        {
            var service = _createService();
            if (!service.StorageConfigured)
            {
                return Failure(HttpStatusCode.Conflict, StorageNotConfiguredCode);
            }

            try
            {
                return Ok(await service.ClearAsync(request?.RunOnNextCycle ?? false));
            }
            catch (UserImportCheckpointUnavailableException)
            {
                return Failure(HttpStatusCode.ServiceUnavailable, StorageUnavailableCode);
            }
        }

        private IHttpActionResult Failure(HttpStatusCode status, string code)
            => Content(status, new UserImportCheckpointError { Code = code });
    }
}
