using Common.Entities.Config;
using Common.Entities.UserOrgs;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.UserOrgs;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Administration of configurable user organisations: defining the org types, validating an Entra
    /// attribute against a real user, previewing a CSV and running the import.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Model binding and HTTP status codes only. The behaviour lives in
    /// <see cref="UserOrgAdminService"/>, which depends on ports and is therefore testable without SQL
    /// Server, Microsoft Graph or an ASP.NET pipeline.
    /// </para>
    /// <para>
    /// <b>Cross-site request forgery.</b> This is the first substantial authenticated write surface in
    /// this portal, so the posture is worth stating rather than assuming. Every mutating route here
    /// requires the <c>X-Requested-With: XMLHttpRequest</c> header, which the portal's
    /// <c>apiFetch</c> helper sets on every call. A browser will not attach that header to a
    /// cross-origin form post or navigation, and it cannot be added cross-origin without a CORS
    /// preflight that this site does not answer for its own API - so a third-party page cannot forge
    /// one of these calls using the admin's cookie. The check is explicit rather than implied, because
    /// the alternative is relying on cookie <c>SameSite</c> defaults that vary by browser and can be
    /// changed in configuration without anyone noticing this depended on them.
    /// </para>
    /// </remarks>
    [Authorize]
    [RoutePrefix("api/UserOrg")]
    public class UserOrgAPIController : ApiController
    {
        /// <summary>Largest upload accepted, before parsing. A 200,000-row file is well under this.</summary>
        internal const int MaxUploadBytes = 32 * 1024 * 1024;

        private const string RequestedWithHeader = "X-Requested-With";

        private readonly Func<UserOrgAdminService> _serviceFactory;
        private readonly Func<UserOrgMembershipService> _membershipFactory;

        public UserOrgAPIController() : this(BuildService, BuildMembershipService)
        {
        }

        public UserOrgAPIController(Func<UserOrgAdminService> serviceFactory, Func<UserOrgMembershipService> membershipFactory)
        {
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            _membershipFactory = membershipFactory ?? throw new ArgumentNullException(nameof(membershipFactory));
        }

        private static UserOrgMembershipService BuildMembershipService()
        {
            var connectionString = new AppConfig().ConnectionStrings.SQL;
            return new UserOrgMembershipService(
                UserOrgStores.CreateTypeStore(connectionString),
                UserOrgStores.CreateMembershipReader(connectionString));
        }

        private static UserOrgAdminService BuildService()
        {
            var config = new AppConfig();
            var connectionString = config.ConnectionStrings.SQL;

            var jobs = UserOrgStores.CreateImportJobStore(connectionString);

            return new UserOrgAdminService(
                UserOrgStores.CreateTypeStore(connectionString),
                UserOrgStores.CreateAssignmentStore(connectionString),
                jobs,
                UserOrgStores.CreateUserLookup(connectionString),
                new UserOrgGraphProbe(config),
                // A store built here rather than captured from the request: the import outlives the
                // request, so anything scoped to it would already be disposed by the time it ran.
                jobId => UserOrgImportDispatcher.Start(jobId, UserOrgStores.CreateImportJobStore(connectionString)));
        }

        #region Org types

        /// <summary>GET api/UserOrg/types</summary>
        [HttpGet]
        [Route("types")]
        public async Task<IHttpActionResult> GetTypes(CancellationToken cancellationToken)
        {
            return await RunAsync(svc => svc.ListAsync(cancellationToken)).ConfigureAwait(false);
        }

        /// <summary>POST api/UserOrg/types</summary>
        [HttpPost]
        [Route("types")]
        public async Task<IHttpActionResult> CreateType([FromBody] UserOrgTypeSaveModel model, CancellationToken cancellationToken)
        {
            var forged = RejectIfNotXhr();
            if (forged != null) return forged;

            return await RunAsync(svc => svc.CreateAsync(model, cancellationToken)).ConfigureAwait(false);
        }

        /// <summary>PUT api/UserOrg/types/{id}</summary>
        [HttpPut]
        [Route("types/{id:int}")]
        public async Task<IHttpActionResult> UpdateType(int id, [FromBody] UserOrgTypeSaveModel model, CancellationToken cancellationToken)
        {
            var forged = RejectIfNotXhr();
            if (forged != null) return forged;

            return await RunAsync(svc => svc.UpdateAsync(id, model, cancellationToken)).ConfigureAwait(false);
        }

        /// <summary>DELETE api/UserOrg/types/{id}</summary>
        [HttpDelete]
        [Route("types/{id:int}")]
        public async Task<IHttpActionResult> DeleteType(int id, CancellationToken cancellationToken)
        {
            var forged = RejectIfNotXhr();
            if (forged != null) return forged;

            return await RunAsync(async svc =>
            {
                await svc.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                return (object)new { deleted = true };
            }).ConfigureAwait(false);
        }

        #endregion

        #region Validation

        /// <summary>
        /// POST api/UserOrg/test-entra - resolves an attribute for one user against live Graph.
        /// </summary>
        /// <remarks>
        /// Accepts an <b>unsaved</b> attribute on purpose. Validating before committing is the whole
        /// point: Graph fails an entire <c>/users/delta</c> request when <c>$select</c> names a property
        /// it does not recognise, so an unchecked typo would break the next user import.
        /// </remarks>
        [HttpPost]
        [Route("test-entra")]
        public async Task<IHttpActionResult> TestEntra([FromBody] UserOrgTestRequestModel request, CancellationToken cancellationToken)
        {
            var forged = RejectIfNotXhr();
            if (forged != null) return forged;

            return await RunAsync(svc => svc.TestAsync(request, cancellationToken)).ConfigureAwait(false);
        }

        /// <summary>GET api/UserOrg/attributes - the attribute picker's contents.</summary>
        [HttpGet]
        [Route("attributes")]
        public async Task<IHttpActionResult> GetAttributes(CancellationToken cancellationToken)
        {
            return await RunAsync(svc => svc.DiscoverAttributesAsync(cancellationToken)).ConfigureAwait(false);
        }

        #endregion

        #region CSV

        /// <summary>
        /// POST api/UserOrg/preview-csv - parses the first rows of an upload. Persists nothing.
        /// </summary>
        [HttpPost]
        [Route("preview-csv")]
        public async Task<IHttpActionResult> PreviewCsv(int orgTypeId, CancellationToken cancellationToken)
        {
            var forged = RejectIfNotXhr();
            if (forged != null) return forged;

            var upload = await TryReadUploadAsync().ConfigureAwait(false);
            if (upload.Failure != null) return upload.Failure;

            using (upload.File)
            {
                return await RunAsync(svc => svc.PreviewAsync(
                    upload.File.Content, upload.File.FileName, orgTypeId, cancellationToken)).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// POST api/UserOrg/import-csv?orgTypeId=1&amp;mode=replace - stages an upload and queues the import.
        /// </summary>
        [HttpPost]
        [Route("import-csv")]
        public async Task<IHttpActionResult> ImportCsv(int orgTypeId, string mode, CancellationToken cancellationToken, bool confirmClear = false)
        {
            var forged = RejectIfNotXhr();
            if (forged != null) return forged;

            UserOrgImportMode importMode;
            if (string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase))
            {
                importMode = UserOrgImportMode.Replace;
            }
            else if (string.Equals(mode, "merge", StringComparison.OrdinalIgnoreCase))
            {
                importMode = UserOrgImportMode.Merge;
            }
            else
            {
                return Content(HttpStatusCode.BadRequest, new ApiErrorModel("The import mode must be either 'replace' or 'merge'."));
            }

            var upload = await TryReadUploadAsync().ConfigureAwait(false);
            if (upload.Failure != null) return upload.Failure;

            using (upload.File)
            {
                var startedBy = User?.Identity?.Name ?? "unknown";
                return await RunAsync(svc => svc.QueueImportAsync(
                    orgTypeId, importMode, upload.File.Content, upload.File.FileName, startedBy, confirmClear, cancellationToken)).ConfigureAwait(false);
            }
        }
        /// <summary>GET api/UserOrg/jobs/{id} - import progress.</summary>
        [HttpGet]
        [Route("jobs/{id:int}")]
        public async Task<IHttpActionResult> GetJob(int id, CancellationToken cancellationToken)
        {
            return await RunAsync<object>(async svc =>
            {
                var job = await svc.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
                if (job == null)
                {
                    throw new UserOrgNotFoundException("That import job was not found.");
                }
                return job;
            }).ConfigureAwait(false);
        }

        #endregion

        #region Browse

        /// <summary>
        /// GET api/UserOrg/types/{id}/values - one page of an org type's organisations, largest first,
        /// each with how many users are in it.
        /// </summary>
        [HttpGet]
        [Route("types/{id:int}/values")]
        public async Task<IHttpActionResult> GetValues(
            int id,
            CancellationToken cancellationToken,
            string search = null,
            int page = 1,
            int pageSize = 0)
        {
            return await GuardAsync<object>(async () =>
            {
                var result = await _membershipFactory()
                    .ListValuesAsync(id, search, page, pageSize, cancellationToken)
                    .ConfigureAwait(false);
                if (result == null)
                {
                    throw new UserOrgNotFoundException("That organisation type no longer exists.");
                }
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// GET api/UserOrg/types/{id}/values/{valueId}/members - one page of the users in an
        /// organisation, by user principal name.
        /// </summary>
        [HttpGet]
        [Route("types/{id:int}/values/{valueId:int}/members")]
        public async Task<IHttpActionResult> GetMembers(
            int id,
            int valueId,
            CancellationToken cancellationToken,
            string search = null,
            int page = 1,
            int pageSize = 0)
        {
            return await GuardAsync<object>(async () =>
            {
                var result = await _membershipFactory()
                    .ListMembersAsync(id, valueId, search, page, pageSize, cancellationToken)
                    .ConfigureAwait(false);
                if (result == null)
                {
                    throw new UserOrgNotFoundException("That organisation no longer exists.");
                }
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region Plumbing

        private sealed class UploadedFile : IDisposable
        {
            public Stream Content { get; set; }
            public string FileName { get; set; }
            public void Dispose() => Content?.Dispose();
        }

        /// <summary>Either the uploaded file, or the HTTP result explaining why it could not be read.</summary>
        private sealed class UploadResult
        {
            public UploadedFile File { get; set; }
            public IHttpActionResult Failure { get; set; }
        }

        /// <summary>
        /// Reads the uploaded file, whether posted as multipart form data or as a raw body.
        /// </summary>
        /// <remarks>
        /// Buffered into memory rather than streamed to disk. A 200,000-row two-column CSV is only a
        /// few megabytes, the cap below bounds it explicitly, and buffering avoids leaving temporary
        /// files behind on an App Service instance that may be recycled at any moment.
        /// </remarks>
        private async Task<UploadResult> TryReadUploadAsync()
        {
            if (Request.Content == null)
            {
                return Failed(Content(HttpStatusCode.BadRequest, new ApiErrorModel("No file was uploaded.")));
            }

            var length = Request.Content.Headers.ContentLength;
            if (length.HasValue && length.Value > MaxUploadBytes)
            {
                return Failed(TooLarge());
            }

            UploadedFile file;

            try
            {
                if (Request.Content.IsMimeMultipartContent())
                {
                    var provider = await Request.Content.ReadAsMultipartAsync().ConfigureAwait(false);
                    var part = provider.Contents.FirstOrDefault(c => c.Headers.ContentDisposition?.FileName != null)
                               ?? provider.Contents.FirstOrDefault();

                    if (part == null)
                    {
                        return Failed(Content(HttpStatusCode.BadRequest, new ApiErrorModel("No file was uploaded.")));
                    }

                    var bytes = await part.ReadAsByteArrayAsync().ConfigureAwait(false);
                    file = new UploadedFile
                    {
                        Content = new MemoryStream(bytes),
                        FileName = (part.Headers.ContentDisposition?.FileName ?? "upload.csv").Trim('"'),
                    };
                }
                else
                {
                    var bytes = await Request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes.Length == 0)
                    {
                        return Failed(Content(HttpStatusCode.BadRequest, new ApiErrorModel("No file was uploaded.")));
                    }

                    file = new UploadedFile { Content = new MemoryStream(bytes), FileName = "upload.csv" };
                }
            }
            catch (Exception)
            {
                return Failed(Content(
                    HttpStatusCode.BadRequest,
                    new ApiErrorModel("The upload could not be read. Check the file is a plain CSV and try again.")));
            }

            // Re-checked after reading, because Content-Length is absent on a chunked upload.
            if (file.Content.Length > MaxUploadBytes)
            {
                file.Dispose();
                return Failed(TooLarge());
            }

            return new UploadResult { File = file };
        }

        private static UploadResult Failed(IHttpActionResult failure)
        {
            return new UploadResult { Failure = failure };
        }

        private IHttpActionResult TooLarge()
        {
            return Content(
                HttpStatusCode.RequestEntityTooLarge,
                new ApiErrorModel($"That file is larger than the {MaxUploadBytes / (1024 * 1024)} MB limit for an organisation import."));
        }

        /// <summary>
        /// Refuses a mutating call that did not come from the portal's own fetch helper.
        /// </summary>
        /// <remarks>
        /// See the class remarks. A browser will not attach this header cross-origin without a CORS
        /// preflight this site does not answer for its own API, so requiring it prevents a third-party
        /// page forging a call with the signed-in admin's cookie.
        /// </remarks>
        private IHttpActionResult RejectIfNotXhr()
        {
            System.Collections.Generic.IEnumerable<string> values;
            if (Request.Headers.TryGetValues(RequestedWithHeader, out values)
                && values.Any(v => string.Equals(v, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return Content(
                HttpStatusCode.BadRequest,
                new ApiErrorModel("This request did not come from the portal. Reload the page and try again."));
        }

        private Task<IHttpActionResult> RunAsync<T>(Func<UserOrgAdminService, Task<T>> work)
        {
            // The factory runs inside the guard, so a missing connection string is a sanitised 500
            // rather than an unhandled exception.
            return GuardAsync(() => work(_serviceFactory()));
        }

        /// <summary>
        /// Turns the outcome of <paramref name="work"/> into a response, reporting only genuine faults.
        /// </summary>
        /// <param name="cancellationToken">
        /// The request's token. When it has fired, the browser has gone: the "who is in each
        /// organisation" panel aborts its request every time the admin picks another organisation, page
        /// or search. Whatever that surfaces as is not a fault, and reporting it would bury the real
        /// errors - the same call <see cref="AnalyticsWebApiExceptionLogger"/> makes for the rest of the
        /// API. The token is checked rather than the exception type, because SqlClient can report a
        /// cancelled command as a <c>SqlException</c>.
        /// </param>
        private async Task<IHttpActionResult> GuardAsync<T>(
            Func<Task<T>> work,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                return Ok(await work().ConfigureAwait(false));
            }
            catch (UserOrgValidationException ex)
            {
                // Written for an IT admin and safe to display: these messages never echo a Graph
                // response body or a SQL error verbatim.
                return Content(HttpStatusCode.BadRequest, new ApiErrorModel(ex.Message));
            }
            catch (UserOrgNotFoundException ex)
            {
                return Content(HttpStatusCode.NotFound, new ApiErrorModel(ex.Message));
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // 499 "client closed request": nobody is left to read it, and it keeps the request log
                // honest about what happened.
                return StatusCode((HttpStatusCode)499);
            }
            catch (Exception ex)
            {
                // Anything else is a fault, not a message for the admin. Without this it would reach
                // the Web API pipeline, and this site ships with customErrors off - so a SQL exception
                // would arrive in the browser carrying object names, index names and key values. The
                // exception still goes to Application Insights, which is where an engineer reads it.
                WebExceptionTelemetry.Report(ex, "UserOrgAPI");

                return Content(
                    HttpStatusCode.InternalServerError,
                    new ApiErrorModel("Something went wrong handling that request. Check the service logs for details."));
            }
        }

        #endregion
    }

    /// <summary>Thrown when an org resource does not exist, so the controller can answer 404.</summary>
    public sealed class UserOrgNotFoundException : Exception
    {
        public UserOrgNotFoundException(string message) : base(message)
        {
        }
    }
}
