using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Net;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.UserImport;
using Web.AnalyticsWeb.Security;

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
    [RequirePortalPermission(PortalPermission.Administration)]
    [Route("api/UserImportCheckpoint")]
    public class UserImportCheckpointAPIController : ControllerBase
    {
        /// <summary>Storage is configured but could not be reached.</summary>
        internal const string StorageUnavailableCode = "storageUnavailable";

        /// <summary>There is no Storage connection string, so there is no checkpoint to clear.</summary>
        internal const string StorageNotConfiguredCode = "storageNotConfigured";

        /// <summary>
        /// The user organisation types that decide the checkpoint's key could not be read (a SQL failure other than
        /// their tables not existing yet), so the page can neither say whether a checkpoint is stored nor clear it.
        /// </summary>
        internal const string OrganisationTypesUnavailableCode = "organisationTypesUnavailable";

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
        public async Task<IActionResult> Get()
        {
            try
            {
                return Ok(await _createService().GetStatusAsync());
            }
            catch (UserImportCheckpointUnavailableException)
            {
                return Failure(HttpStatusCode.ServiceUnavailable, StorageUnavailableCode);
            }
            catch (UserImportCheckpointKeyUnavailableException)
            {
                return Failure(HttpStatusCode.ServiceUnavailable, OrganisationTypesUnavailableCode);
            }
        }

        // POST: api/UserImportCheckpoint/clear   { "runOnNextCycle": true }
        [HttpPost]
        [Route("clear")]
        [RequireSameOriginXhr]
        public async Task<IActionResult> Clear([FromBody] UserImportCheckpointClearRequest request)
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
            catch (UserImportCheckpointKeyUnavailableException)
            {
                // Nothing was deleted: clearing only the key it could work out would have reported success while the
                // checkpoint the importer resumes from survived.
                return Failure(HttpStatusCode.ServiceUnavailable, OrganisationTypesUnavailableCode);
            }
        }

        private IActionResult Failure(HttpStatusCode status, string code)
            => StatusCode((int)status, new UserImportCheckpointError { Code = code });
    }
}
