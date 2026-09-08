using Common.Entities.Config;
using Common.Entities.LicenceActivity;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
        private static readonly LicenceActivitySnapshotCache<LicenceActivityOverview> OverviewCache =
            new LicenceActivitySnapshotCache<LicenceActivityOverview>(16, TimeSpan.FromMinutes(5));
        private static readonly LicenceActivitySnapshotCache<LicenceActivityUsers> UsersCache =
            new LicenceActivitySnapshotCache<LicenceActivityUsers>(32, TimeSpan.FromMinutes(2));
        private static readonly LicenceActivityReadModelCache ReadModelCache = new LicenceActivityReadModelCache();

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
            var result = new LicenceActivityAvailability { Available = sources.UserMetadata };
            if (!sources.UserMetadata)
                result.Messages.Add("This report needs the user details import turned on, so that licences can be matched to the people who hold them. Ask whoever installed the product to tick \"User Entra ID extended metadata\" in the installer. The tab stays visible in the meantime.");
            result.Messages.Add("People are identified by their sign-in address. Staff names are not collected, so search and the user lists show the sign-in address instead. Department and country come from your directory.");
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
                }, isCurrent: snapshot => !(context.Store is ILicenceActivitySnapshotValidator validator)
                    || validator.IsCurrent(snapshot, context.Sources));
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
                var context = _context();
                if (!context.Sources.UserMetadata) return MissingMetadata();
                var overview = _overviews.Find(context.Scope, overviewId);
                if (!overview.Licences.Any(sku => sku.LicenceTypeId == licenceTypeId))
                    return Reply(HttpStatusCode.NotFound, new { message = "That licence is not part of the figures currently on screen. Refresh the report and try again." });
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
                var context = _context();
                if (!context.Sources.UserMetadata) return Task.FromResult(MissingMetadata());
                var overview = _overviews.Find(context.Scope, overviewId);
                var users = usersId == null ? null : _users.Find(context.Scope, usersId);
                if (users != null && users.OverviewId != overviewId)
                    return Task.FromResult(Reply(HttpStatusCode.Conflict, new { message = "The summary and the user list are no longer from the same set of figures. Refresh the report before exporting." }));
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

        private async Task<IHttpActionResult> ExecuteAsync(Func<Task<IHttpActionResult>> action)
        {
            if (!ModelState.IsValid) return Reply(HttpStatusCode.BadRequest, new { message = "That request wasn't valid. Check the selected dates and filters." });
            try { return await action(); }
            catch (ArgumentException ex) { return Reply(HttpStatusCode.BadRequest, new { message = ex.Message }); }
            catch (LicenceActivityExpiredException)
            {
                return Reply(HttpStatusCode.Gone, new { message = "These figures are no longer being held. Refresh the report to bring back an up-to-date set before continuing or exporting." });
            }
            catch (LicenceActivityReadModelExpiredException)
            {
                return Reply(HttpStatusCode.Gone, new { message = "These figures are no longer being held. Refresh the report to bring back an up-to-date set." });
            }
            catch (LicenceActivityReadModelBusyException)
            {
                return Reply(HttpStatusCode.ServiceUnavailable, new { message = "Another licence report is being prepared right now. Try again in a few seconds." }, true);
            }
            catch (LicenceActivityBusyException)
            {
                return Reply(HttpStatusCode.ServiceUnavailable, new { message = "Licence reporting is busy. Try again in a few seconds." }, true);
            }
            catch (LicenceActivityFailedException ex)
            {
                return Reply(HttpStatusCode.ServiceUnavailable, new { message = ex.Message }, true);
            }
        }

        private IHttpActionResult MissingMetadata() =>
            Reply(HttpStatusCode.PreconditionFailed, new { message = "This report needs the user details import turned on, so that licences can be matched to the people who hold them." });

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
                new CachedLicenceActivityStore(
                    new SqlLicenceActivityStore(config.ConnectionStrings.DatabaseConnectionString), ReadModelCache, scope));
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
