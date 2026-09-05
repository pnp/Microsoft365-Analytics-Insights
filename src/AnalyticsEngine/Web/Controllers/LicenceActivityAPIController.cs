using Common.Entities.Config;
using Common.Entities.LicenceActivity;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models.LicenceActivity;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    [RoutePrefix("api/LicenceActivity")]
    public sealed class LicenceActivityAPIController : ApiController
    {
        public const string UserDetailRole = "LicenceActivity.ReadUsers";
        private static readonly LicenceActivitySnapshotCache<LicenceActivityOverview> OverviewCache =
            new LicenceActivitySnapshotCache<LicenceActivityOverview>(16, TimeSpan.FromMinutes(5));
        private static readonly LicenceActivitySnapshotCache<LicenceActivityUsers> UsersCache =
            new LicenceActivitySnapshotCache<LicenceActivityUsers>(32, TimeSpan.FromMinutes(2));

        private readonly Func<LicenceActivityRequestContext> _context;
        private readonly LicenceActivitySnapshotCache<LicenceActivityOverview> _overviews;
        private readonly LicenceActivitySnapshotCache<LicenceActivityUsers> _users;

        public LicenceActivityAPIController() : this(CreateContext, OverviewCache, UsersCache) { }

        internal LicenceActivityAPIController(
            Func<LicenceActivityRequestContext> context,
            LicenceActivitySnapshotCache<LicenceActivityOverview> overviews,
            LicenceActivitySnapshotCache<LicenceActivityUsers> users)
        {
            _context = context;
            _overviews = overviews;
            _users = users;
        }

        [HttpGet, Route("availability")]
        public IHttpActionResult Availability()
        {
            var sources = _context().Sources;
            var result = new LicenceActivityAvailability { Available = sources.UserMetadata, CanViewUsers = CanViewUsers(User) };
            if (!sources.UserMetadata)
                result.Messages.Add("Enable GraphUsersMetadata (user metadata) in the installer to import licence-to-user assignments. This tab remains visible while the prerequisite is disabled.");
            if (!result.CanViewUsers)
                result.Messages.Add("Individual users require the LicenceActivity.ReadUsers application role. Aggregate reports and aggregate Excel snapshots remain available.");
            result.Messages.Add("User display names are not imported. Search uses UPN or email; department and country filters use imported metadata.");
            return Reply(HttpStatusCode.OK, result);
        }

        [HttpGet, Route("overview")]
        public Task<IHttpActionResult> Overview(
            string from = null, string to = null, int? departmentId = null, int? countryId = null,
            CancellationToken cancellationToken = default(CancellationToken)) =>
            ExecuteAsync(async () =>
            {
                var context = _context();
                var query = LicenceActivityQuery.Create(from, to, context.Sources.NowUtc, departmentId, countryId);
                if (!context.Sources.UserMetadata) return MissingMetadata();
                var task = _overviews.GetAsync(context.Scope, query.CacheKey(), async (diagnostics, lifetime) =>
                {
                    var result = await context.Store.LoadOverviewAsync(query, context.Sources, diagnostics, lifetime).ConfigureAwait(false);
                    result.Query = query;
                    result.Messages.Add(LicenceActivityRules.AssignmentCaveat);
                    result.Messages.Add(LicenceActivityRules.InterpretationCaveat);
                    result.Messages.Add(LicenceActivityRules.Method);
                    return result;
                });
                return Reply(HttpStatusCode.OK,
                    await LicenceActivitySnapshotCache<LicenceActivityOverview>.WaitForCallerAsync(task, cancellationToken));
            });

        [HttpGet, Route("users")]
        public Task<IHttpActionResult> Users(
            string overviewId, int licenceTypeId, string workload = "teams", int top = 10, string search = null,
            string sort = "upn", string direction = "asc", int page = 1, int pageSize = 50,
            CancellationToken cancellationToken = default(CancellationToken)) =>
            ExecuteAsync(async () =>
            {
                if (!CanViewUsers(User)) return ForbiddenUsers();
                var context = _context();
                if (!context.Sources.UserMetadata) return MissingMetadata();
                var overview = _overviews.Find(context.Scope, overviewId);
                if (!overview.Licences.Any(sku => sku.LicenceTypeId == licenceTypeId))
                    return Reply(HttpStatusCode.NotFound, new { message = "That licence is not part of this snapshot." });
                var query = overview.Query.ForUsers(licenceTypeId, workload, search, sort, direction, top, page, pageSize, context.Sources.NowUtc);
                var task = _users.GetAsync(context.Scope, overviewId + "\n" + query.CacheKey(), async (diagnostics, lifetime) =>
                {
                    var result = await context.Store.LoadUsersAsync(overview, query, context.Sources, diagnostics, lifetime).ConfigureAwait(false);
                    result.OverviewId = overviewId;
                    result.Query = query;
                    return result;
                }, overview.ExpiresUtc);
                return Reply(HttpStatusCode.OK,
                    await LicenceActivitySnapshotCache<LicenceActivityUsers>.WaitForCallerAsync(task, cancellationToken));
            });

        [HttpGet, Route("export")]
        public Task<IHttpActionResult> Export(string overviewId, string usersId = null) =>
            ExecuteAsync(() =>
            {
                if (usersId != null && !CanViewUsers(User)) return Task.FromResult(ForbiddenUsers());
                var context = _context();
                if (!context.Sources.UserMetadata) return Task.FromResult(MissingMetadata());
                var overview = _overviews.Find(context.Scope, overviewId);
                var users = usersId == null ? null : _users.Find(context.Scope, usersId);
                if (users != null && users.OverviewId != overviewId)
                    return Task.FromResult(Reply(HttpStatusCode.Conflict, new { message = "The snapshots do not match. Refresh the current view before exporting." }));
                var response = Request.CreateResponse(HttpStatusCode.OK);
                response.Content = new ByteArrayContent(LicenceActivityWorkbook.Build(overview, users));
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileName = "licence-activity-" + overview.GeneratedUtc.ToString("yyyy-MM-dd") + ".xlsx"
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, Private = true };
                return Task.FromResult<IHttpActionResult>(ResponseMessage(response));
            });

        internal static bool CanViewUsers(IPrincipal principal)
        {
            if (principal?.Identity?.IsAuthenticated != true) return false;
            if (principal.IsInRole(UserDetailRole)) return true;
            var claims = principal as ClaimsPrincipal;
            return claims?.Claims.Any(c => (c.Type == "roles" || c.Type == ClaimTypes.Role)
                && c.Value == UserDetailRole) == true;
        }

        private async Task<IHttpActionResult> ExecuteAsync(Func<Task<IHttpActionResult>> action)
        {
            if (!ModelState.IsValid) return Reply(HttpStatusCode.BadRequest, new { message = "The request contains an invalid parameter." });
            try { return await action(); }
            catch (ArgumentException ex) { return Reply(HttpStatusCode.BadRequest, new { message = ex.Message }); }
            catch (LicenceActivityExpiredException)
            {
                return Reply(HttpStatusCode.Gone, new { message = "This snapshot has expired or was evicted. Refresh the current view before continuing or exporting." });
            }
            catch (LicenceActivityBusyException)
            {
                return Reply(HttpStatusCode.ServiceUnavailable, new { message = "Licence reporting is busy. Retry in a few seconds." }, true);
            }
            catch (LicenceActivityFailedException ex)
            {
                return Reply(HttpStatusCode.ServiceUnavailable, new { message = ex.Message }, true);
            }
        }

        private IHttpActionResult MissingMetadata() =>
            Reply(HttpStatusCode.PreconditionFailed, new { message = "Enable GraphUsersMetadata to import licence assignments." });

        private IHttpActionResult ForbiddenUsers() =>
            Reply(HttpStatusCode.Forbidden, new { message = "Individual licence activity requires the LicenceActivity.ReadUsers application role." });

        private IHttpActionResult Reply(HttpStatusCode status, object body, bool retry = false)
        {
            var response = Request.CreateResponse(status, body);
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, Private = true };
            if (retry) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return ResponseMessage(response);
        }

        private static LicenceActivityRequestContext CreateContext()
        {
            var config = new AppConfig();
            var settings = config.ImportJobSettings ?? new Common.Entities.ImportTaskSettings();
            var sources = new LicenceActivitySources
            {
                UserMetadata = settings.GraphUsersMetadata, UsageReports = settings.GraphUsageReports,
                CopilotUsageReports = settings.GraphCopilotUsageReports, CopilotAudit = settings.Copilot,
                CopilotInteractions = settings.CopilotInteractionHistory, NowUtc = DateTime.UtcNow
            };
            string scope;
            using (var sha = SHA256.Create())
                scope = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    config.TenantGUID + "\n" + config.ConnectionStrings.DatabaseConnectionString + "\n" + sources.CacheKey)));
            return new LicenceActivityRequestContext(scope, sources,
                new SqlLicenceActivityStore(config.ConnectionStrings.DatabaseConnectionString));
        }
    }

    internal sealed class LicenceActivityRequestContext
    {
        internal LicenceActivityRequestContext(string scope, LicenceActivitySources sources, ILicenceActivityStore store)
        {
            Scope = scope; Sources = sources; Store = store;
        }
        internal string Scope { get; }
        internal LicenceActivitySources Sources { get; }
        internal ILicenceActivityStore Store { get; }
    }
}
