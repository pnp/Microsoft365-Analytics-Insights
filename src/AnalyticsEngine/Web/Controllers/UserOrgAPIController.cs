using Common.Entities.Config;
using Common.Entities.UserOrgs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.UserFilters;
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

        /// <summary>
        /// Largest multipart request read: a file at <see cref="MaxUploadBytes"/> plus its boundaries and
        /// part headers. The same 36 MB the web.config allows the two upload routes, so neither the host -
        /// which cannot explain itself - nor this controller turns away a file the limit accepts.
        /// </summary>
        internal const int MaxMultipartRequestBytes = 36 * 1024 * 1024;

        private const string RequestedWithHeader = "X-Requested-With";

        private readonly Func<UserOrgAdminService> _serviceFactory;
        private readonly Func<UserOrgMembershipService> _membershipFactory;
        private readonly Action _invalidateUserFilterDirectory;

        public UserOrgAPIController() : this(BuildService, BuildMembershipService)
        {
        }

        public UserOrgAPIController(Func<UserOrgAdminService> serviceFactory, Func<UserOrgMembershipService> membershipFactory)
            : this(serviceFactory, membershipFactory, null)
        {
        }

        /// <param name="invalidateUserFilterDirectory">
        /// Makes the reports' user filter read the directory again; the process-wide cache when <c>null</c>.
        /// </param>
        internal UserOrgAPIController(
            Func<UserOrgAdminService> serviceFactory,
            Func<UserOrgMembershipService> membershipFactory,
            Action invalidateUserFilterDirectory)
        {
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            _membershipFactory = membershipFactory ?? throw new ArgumentNullException(nameof(membershipFactory));
            _invalidateUserFilterDirectory = invalidateUserFilterDirectory ?? (() => CachedUserDirectorySource.Default.Invalidate());
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

            // Built per use rather than captured, like the import's job store: it runs after the request.
            Func<UserOrgChangeLogShipper> shipper = () => new UserOrgChangeLogShipper(
                UserOrgStores.CreateImportJobStore(connectionString),
                UserOrgStores.CreateChangeOutbox(connectionString),
                UserOrgStores.CreateTypeStore(connectionString),
                UserOrgChangeLogs.ForWriting,
                UserOrgImportAppInsights.Default);

            return new UserOrgAdminService(
                UserOrgStores.CreateTypeStore(connectionString),
                UserOrgStores.CreateAssignmentStore(connectionString),
                jobs,
                UserOrgStores.CreateUserLookup(connectionString),
                new UserOrgGraphProbe(config),
                // A store built here rather than captured from the request: the import outlives the
                // request, so anything scoped to it would already be disposed by the time it ran.
                jobId => UserOrgImportDispatcher.Start(
                    jobId, UserOrgStores.CreateImportJobStore(connectionString), changeLogShipper: shipper),
                telemetry: UserOrgImportAppInsights.Default,
                changeOutbox: UserOrgStores.CreateChangeOutbox(connectionString),
                changeLogFor: UserOrgChangeLogs.ForReading,
                dispatchChangeLog: jobId => UserOrgImportDispatcher.StartChangeLog(jobId, shipper));
        }

        /// <summary>
        /// Resumes imports a restart interrupted, shortly after the web app starts.
        /// </summary>
        /// <remarks>
        /// Without this, an import cut off by a recycle waits until somebody next opens the admin page.
        /// Twice, because a job queued just before the restart is not presumed lost until
        /// <see cref="UserOrgImportJobLimits.LostDispatchGrace"/> has passed. Off the start-up thread,
        /// and never throws: a missing connection string or an unreachable database must not stop the
        /// site starting.
        /// </remarks>
        internal static void ResumeInterruptedImportsAfterStartup()
        {
            Task.Run(async () =>
            {
                foreach (var delay in new[] { TimeSpan.FromSeconds(15), UserOrgImportJobLimits.LostDispatchGrace + TimeSpan.FromSeconds(30) })
                {
                    try
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                        await BuildService().ResumeInterruptedImportsAsync(CancellationToken.None, force: true).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        UserOrgImportAppInsights.Default.Record(new UserOrgImportTelemetryEvent
                        {
                            Stage = UserOrgImportStages.ResumeFailed,
                            ExceptionType = ex.GetBaseException().GetType().Name,
                        });
                    }
                }
            });
        }

        #region Org types

        /// <summary>GET api/UserOrg/types</summary>
        [HttpGet]
        [Route("types")]
        public async Task<IHttpActionResult> GetTypes(CancellationToken cancellationToken)
        {
            return await RunAsync(svc => svc.ListAsync(cancellationToken), "types", cancellationToken).ConfigureAwait(false);
        }

        /// <summary>POST api/UserOrg/types</summary>
        [HttpPost]
        [Route("types")]
        public async Task<IHttpActionResult> CreateType([FromBody] UserOrgTypeSaveModel model, CancellationToken cancellationToken)
        {
            var forged = RejectIfNotXhr();
            if (forged != null) return forged;

            return await RunAsync(
                svc => InvalidatingAfter(() => svc.CreateAsync(model, cancellationToken)),
                "create-type",
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>PUT api/UserOrg/types/{id}</summary>
        [HttpPut]
        [Route("types/{id:int}")]
        public async Task<IHttpActionResult> UpdateType(int id, [FromBody] UserOrgTypeSaveModel model, CancellationToken cancellationToken)
        {
            var forged = RejectIfNotXhr();
            if (forged != null) return forged;

            return await RunAsync(
                svc => InvalidatingAfter(() => svc.UpdateAsync(id, model, cancellationToken)),
                "update-type",
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>DELETE api/UserOrg/types/{id}</summary>
        [HttpDelete]
        [Route("types/{id:int}")]
        public async Task<IHttpActionResult> DeleteType(int id, CancellationToken cancellationToken)
        {
            var forged = RejectIfNotXhr();
            if (forged != null) return forged;

            return await RunAsync(
                svc => InvalidatingAfter(async () =>
                {
                    await svc.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                    return (object)new { deleted = true };
                }),
                "delete-type",
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs a change to the org types, then has the reports' user filter read the directory again -
        /// unless the change was refused.
        /// </summary>
        /// <remarks>
        /// A refusal (<see cref="UserOrgValidationException"/>: a name taken, a type gone, an import
        /// running, an edit overtaken) changed nothing, so the snapshot is still right, and throwing it
        /// away would cost every report reader a full directory reload - at 200,000 users, seconds of
        /// SQL - for each rejected click. Any other failure leaves it unknown whether the change
        /// committed, so the directory is read again to be sure.
        /// </remarks>
        private async Task<T> InvalidatingAfter<T>(Func<Task<T>> change)
        {
            T result;
            try
            {
                result = await change().ConfigureAwait(false);
            }
            catch (UserOrgValidationException)
            {
                throw;
            }
            catch
            {
                InvalidateUserFilterDirectory();
                throw;
            }

            InvalidateUserFilterDirectory();
            return result;
        }

        /// <summary>
        /// Makes the reports' user filter read the directory again, so an org type an admin has just
        /// created, renamed, disabled or re-imported is offered - and filtered on - straight away rather
        /// than when the cached snapshot next expires. Only this web process's cache is cleared; another
        /// scaled-out instance catches up when its own snapshot expires.
        /// </summary>
        private void InvalidateUserFilterDirectory()
        {
            _invalidateUserFilterDirectory();
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

            return await RunAsync(svc => svc.TestAsync(request, cancellationToken), "test-entra", cancellationToken).ConfigureAwait(false);
        }

        /// <summary>GET api/UserOrg/attributes - the attribute picker's contents.</summary>
        [HttpGet]
        [Route("attributes")]
        public async Task<IHttpActionResult> GetAttributes(CancellationToken cancellationToken)
        {
            return await RunAsync(svc => svc.DiscoverAttributesAsync(cancellationToken), "attributes", cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region CSV

        /// <summary>
        /// POST api/UserOrg/preview-csv?orgTypeId=1 - parses an upload, stages it as a draft and reports
        /// what importing it would do. Imports nothing; <see cref="ImportCsv"/> commits the draft.
        /// </summary>
        /// <param name="userColumn">The 0-based user column, when the admin chose one.</param>
        /// <param name="valueColumn">The 0-based value column, when the admin chose one.</param>
        [HttpPost]
        [Route("preview-csv")]
        public async Task<IHttpActionResult> PreviewCsv(
            int orgTypeId,
            CancellationToken cancellationToken,
            int? userColumn = null,
            int? valueColumn = null)
        {
            // When the request arrived - before its body, which for a large file takes a while, was read.
            // A preview replaces only this admin's drafts asked for before it (CreateDraftAsync).
            var requestedUtc = System.Web.HttpContext.Current?.Timestamp.ToUniversalTime() ?? DateTime.UtcNow;

            var forged = RejectIfNotXhr();
            if (forged != null) return forged;

            var upload = await TryReadUploadAsync(orgTypeId).ConfigureAwait(false);
            if (upload.Failure != null) return upload.Failure;

            using (upload.File)
            {
                var startedBy = User?.Identity?.Name ?? "unknown";
                return await RunAsync(svc => svc.PreviewAsync(
                    upload.File.Content, upload.File.FileName, orgTypeId, startedBy, userColumn, valueColumn, cancellationToken, requestedUtc),
                    "preview-csv", cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// POST api/UserOrg/import-csv?orgTypeId=1&amp;draftId=2&amp;mode=replace&amp;confirmedClearCount=0 -
        /// imports a previewed draft in the background.
        /// </summary>
        /// <remarks>
        /// No file: the preview already staged it, so what is imported is exactly what was previewed.
        /// <paramref name="confirmedClearCount"/> is how many users the admin agreed may lose their value;
        /// the import is refused if it would now clear more.
        /// </remarks>
        [HttpPost]
        [Route("import-csv")]
        public async Task<IHttpActionResult> ImportCsv(
            int orgTypeId,
            string mode,
            CancellationToken cancellationToken,
            int? draftId = null,
            int confirmedClearCount = 0)
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
                return Content(HttpStatusCode.BadRequest, new ApiErrorModel(
                    "The import mode must be either 'replace' or 'merge'.", UserOrgImportRefusalCodes.InvalidMode));
            }

            if (!draftId.HasValue)
            {
                // Most likely a portal page loaded before an upgrade, which still posts the file here.
                return Content(HttpStatusCode.BadRequest, new ApiErrorModel(
                    "Preview the file before importing it. Reload the page and choose the file again.",
                    UserOrgImportRefusalCodes.DraftNotFound));
            }

            var startedBy = User?.Identity?.Name ?? "unknown";
            return await RunAsync(svc => svc.CommitImportAsync(
                orgTypeId, draftId.Value, importMode, confirmedClearCount, startedBy, cancellationToken),
                "import-csv", cancellationToken).ConfigureAwait(false);
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
                    throw new UserOrgNotFoundException("That import job was not found.", UserOrgMessageCodes.JobGone);
                }

                // The worker clears this process's cached directory itself when an import succeeds.
                // This covers a scaled-out site, where the admin's polls may land on an instance that
                // did not run it - once per job, not on every poll: the page keeps the finished job on
                // screen and a snapshot rebuild on a 200,000-user tenant is not free.
                if (string.Equals(job.Status, "succeeded", StringComparison.OrdinalIgnoreCase)
                    && DirectoryRefreshedForJobs.TryAdd(job.Id, 0))
                {
                    if (DirectoryRefreshedForJobs.Count > 1000)
                    {
                        DirectoryRefreshedForJobs.Clear();
                        DirectoryRefreshedForJobs.TryAdd(job.Id, 0);
                    }

                    InvalidateUserFilterDirectory();
                }

                return job;
            }, "jobs", cancellationToken).ConfigureAwait(false);
        }

        /// <summary>GET api/UserOrg/types/{id}/imports?take=10 - the org type's recent imports, newest first.</summary>
        [HttpGet]
        [Route("types/{id:int}/imports")]
        public async Task<IHttpActionResult> GetImports(int id, CancellationToken cancellationToken, int take = 10)
        {
            return await RunAsync<object>(async svc =>
            {
                var imports = await svc.ListImportsAsync(id, take, cancellationToken).ConfigureAwait(false);
                if (imports == null)
                {
                    throw new UserOrgNotFoundException("That organisation type no longer exists.", UserOrgMessageCodes.TypeGone);
                }

                return imports;
            }, "imports", cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Jobs whose success has already refreshed this process's user directory.</summary>
        private static readonly ConcurrentDictionary<int, byte> DirectoryRefreshedForJobs = new ConcurrentDictionary<int, byte>();

        /// <summary>
        /// GET api/UserOrg/jobs/{id}/changes?search=&amp;continuation=&amp;pageSize=50 - what an import changed,
        /// user by user.
        /// </summary>
        /// <remarks>
        /// The request's token goes to the guard: the dialog aborts its request each time the admin types
        /// in the search box, and that is not a fault.
        /// </remarks>
        [HttpGet]
        [Route("jobs/{id:int}/changes")]
        public async Task<IHttpActionResult> GetChanges(
            int id,
            CancellationToken cancellationToken,
            string search = null,
            string continuation = null,
            int pageSize = UserOrgAdminService.DefaultChangePageSize)
        {
            return await GuardAsync<object>(async () =>
            {
                var page = await _serviceFactory()
                    .GetChangesAsync(id, search, continuation, pageSize, cancellationToken)
                    .ConfigureAwait(false);
                if (page == null)
                {
                    throw new UserOrgNotFoundException("That import job was not found.", UserOrgMessageCodes.JobGone);
                }

                return page;
            }, cancellationToken, "changes").ConfigureAwait(false);
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
                    throw new UserOrgNotFoundException("That organisation type no longer exists.", UserOrgMessageCodes.TypeGone);
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
                    throw new UserOrgNotFoundException("That organisation no longer exists.", UserOrgMessageCodes.ValueGone);
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
        /// files behind on an App Service instance that may be recycled at any moment. The buffer is
        /// handed to the parser as-is - exposed, not copied - so the file sits in memory once.
        /// </remarks>
        private async Task<UploadResult> TryReadUploadAsync(int orgTypeId)
        {
            if (Request.Content == null)
            {
                return Rejected(orgTypeId, NoFile());
            }

            // Only a request that cannot hold an acceptable file is turned away before it is read. A
            // multipart body is the file plus its boundaries and part headers, so a file right at the limit
            // arrives in a slightly larger request - measured against the whole request, it was refused as
            // "larger than 32 MB" when it was not. The file itself is measured once it has been read, below.
            var multipart = Request.Content.IsMimeMultipartContent();
            var length = Request.Content.Headers.ContentLength;
            if (length.HasValue && length.Value > (multipart ? MaxMultipartRequestBytes : MaxUploadBytes))
            {
                return Rejected(orgTypeId, TooLarge(), UserOrgImportRefusalCodes.UploadTooLarge, length.Value);
            }

            UploadedFile file;

            try
            {
                string fileName;
                byte[] bytes;
                if (multipart)
                {
                    var provider = await Request.Content.ReadAsMultipartAsync().ConfigureAwait(false);
                    var part = provider.Contents.FirstOrDefault(c => c.Headers.ContentDisposition?.FileName != null)
                               ?? provider.Contents.FirstOrDefault();

                    if (part == null)
                    {
                        return Rejected(orgTypeId, NoFile());
                    }

                    bytes = await part.ReadAsByteArrayAsync().ConfigureAwait(false);
                    fileName = (part.Headers.ContentDisposition?.FileName ?? "upload.csv").Trim('"');
                }
                else
                {
                    bytes = await Request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    fileName = "upload.csv";
                }

                if (bytes.Length == 0)
                {
                    return Rejected(orgTypeId, NoFile());
                }

                file = new UploadedFile
                {
                    Content = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true),
                    FileName = fileName,
                };
            }
            catch (Exception ex)
            {
                // Usually a client problem - a truncated or malformed multipart body - so it is answered
                // as one. Recorded rather than swallowed, because the same symptom from a proxy or a
                // request limit would otherwise be invisible.
                UserOrgImportAppInsights.Default.Record(new UserOrgImportTelemetryEvent
                {
                    Stage = UserOrgImportStages.UploadRejected,
                    OrgTypeId = orgTypeId,
                    Code = UserOrgImportRefusalCodes.UploadUnreadable,
                    ExceptionType = ex.GetBaseException().GetType().Name,
                    Bytes = length,
                });

                return Failed(Content(
                    HttpStatusCode.BadRequest,
                    new ApiErrorModel(
                        "The upload could not be read. Check the file is a plain CSV and try again.",
                        UserOrgImportRefusalCodes.UploadUnreadable)));
            }

            // Re-checked after reading, because Content-Length is absent on a chunked upload.
            if (file.Content.Length > MaxUploadBytes)
            {
                var size = file.Content.Length;
                file.Dispose();
                return Rejected(orgTypeId, TooLarge(), UserOrgImportRefusalCodes.UploadTooLarge, size);
            }

            return new UploadResult { File = file };
        }

        private static UploadResult Failed(IHttpActionResult failure)
        {
            return new UploadResult { Failure = failure };
        }

        private static UploadResult Rejected(
            int orgTypeId,
            IHttpActionResult failure,
            string code = UserOrgImportRefusalCodes.NoFile,
            long? bytes = null)
        {
            UserOrgImportAppInsights.Default.Record(new UserOrgImportTelemetryEvent
            {
                Stage = UserOrgImportStages.UploadRejected,
                OrgTypeId = orgTypeId,
                Code = code,
                Bytes = bytes,
            });
            return Failed(failure);
        }

        private IHttpActionResult NoFile()
        {
            return Content(HttpStatusCode.BadRequest, new ApiErrorModel("No file was uploaded.", UserOrgImportRefusalCodes.NoFile));
        }

        private IHttpActionResult TooLarge()
        {
            var maxMb = MaxUploadBytes / (1024 * 1024);
            return Content(
                HttpStatusCode.RequestEntityTooLarge,
                new ApiErrorModel(
                    $"That file is larger than the {maxMb} MB limit for an organisation import.",
                    UserOrgImportRefusalCodes.UploadTooLarge)
                {
                    Values = new Dictionary<string, object> { { "maxMb", maxMb } },
                });
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
                new ApiErrorModel(
                    "This request did not come from the portal. Reload the page and try again.",
                    UserOrgMessageCodes.NotFromPortal));
        }

        private Task<IHttpActionResult> RunAsync<T>(Func<UserOrgAdminService, Task<T>> work, string route = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            // The factory runs inside the guard, so a missing connection string is a sanitised 500
            // rather than an unhandled exception.
            return GuardAsync(() => work(_serviceFactory()), cancellationToken, route);
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
        /// <param name="route">Which endpoint failed, for the exception report.</param>
        private async Task<IHttpActionResult> GuardAsync<T>(
            Func<Task<T>> work,
            CancellationToken cancellationToken = default(CancellationToken),
            string route = null)
        {
            try
            {
                return Ok(await work().ConfigureAwait(false));
            }
            catch (UserOrgValidationException ex)
            {
                // Written for an IT admin and safe to display: these messages never echo a Graph
                // response body or a SQL error verbatim. The code lets the portal word it in the
                // reader's language, with the message as the fallback.
                return Content(HttpStatusCode.BadRequest, new ApiErrorModel(ex.Message, ex.Code)
                {
                    Values = ex.Values == null ? null : ex.Values.ToDictionary(v => v.Key, v => v.Value),
                });
            }
            catch (UserOrgNotFoundException ex)
            {
                return Content(HttpStatusCode.NotFound, new ApiErrorModel(ex.Message, ex.Code));
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
                WebExceptionTelemetry.Report(ex, route == null ? "UserOrgAPI" : "UserOrgAPI " + route);

                return Content(
                    HttpStatusCode.InternalServerError,
                    new ApiErrorModel(
                        "Something went wrong handling that request. Check the service logs for details.",
                        UserOrgMessageCodes.Unexpected));
            }
        }

        #endregion
    }

    /// <summary>Thrown when an org resource does not exist, so the controller can answer 404.</summary>
    public sealed class UserOrgNotFoundException : Exception
    {
        /// <param name="code">What is gone, from <see cref="UserOrgMessageCodes"/> - the portal words it.</param>
        public UserOrgNotFoundException(string message, string code) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
