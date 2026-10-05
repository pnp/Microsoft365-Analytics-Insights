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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models.LicenceActivity;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// The Licence activity report. The overview and its totals-only workbook are for every signed-in
    /// reader; the people holding a licence, and a workbook that includes them, need the portal's See PII
    /// permission (#661).
    /// </summary>
    [Authorize]
    [Route("api/LicenceActivity")]
    public sealed class LicenceActivityAPIController  : ControllerBase
    {
        private static readonly LicenceActivitySnapshotCache<LicenceActivityOverview> OverviewCache =
            new LicenceActivitySnapshotCache<LicenceActivityOverview>(16, TimeSpan.FromMinutes(5));
        private static readonly LicenceActivitySnapshotCache<LicenceActivityUsers> UsersCache =
            new LicenceActivitySnapshotCache<LicenceActivityUsers>(32, TimeSpan.FromMinutes(2));
        private static readonly LicenceActivityReadModelCache ReadModelCache = new LicenceActivityReadModelCache();

        private readonly Func<LicenceActivityRequestContext> _context;
        private readonly LicenceActivitySnapshotCache<LicenceActivityOverview> _overviews;
        private readonly LicenceActivitySnapshotCache<LicenceActivityUsers> _users;
        private readonly ReportScopeResolver _scopes;

        public LicenceActivityAPIController() : this(CreateContext, OverviewCache, UsersCache, ReportScopeResolver.Default) { }

        /// <summary>For tests: no global filter unless <paramref name="scopes"/> supplies one.</summary>
        internal LicenceActivityAPIController(
            Func<LicenceActivityRequestContext> context,
            LicenceActivitySnapshotCache<LicenceActivityOverview> overviews,
            LicenceActivitySnapshotCache<LicenceActivityUsers> users,
            ReportScopeResolver scopes = null)
        {
            _context = context;
            _overviews = overviews;
            _users = users;
            _scopes = scopes ?? new ReportScopeResolver(GlobalFilterProviders.None, CachedUserDirectorySource.Default);
        }

        [HttpGet, Route("availability")]
        public IActionResult Availability()
        {
            var sources = _context().Sources;
            var result = new LicenceActivityAvailability { Available = sources.UserMetadata };
            if (!sources.UserMetadata)
                result.Messages.Add("This report needs the user details import turned on, so that licences can be matched to the people who hold them. Ask whoever installed the product to tick \"User Entra ID extended metadata\" in the installer. The tab stays visible in the meantime.");
            result.Messages.Add("People are identified by their sign-in address. Staff names are not collected, so search and the user lists show the sign-in address instead. Department and country come from your directory.");
            return Reply(HttpStatusCode.OK, result);
        }

        [HttpGet, Route("overview")]
        public Task<IActionResult> Overview(
            string from = null, string to = null, int? departmentId = null, int? countryId = null,
            CancellationToken cancellationToken = default(CancellationToken)) =>
            ExecuteAsync(async () =>
            {
                var context = _context();
                var query = LicenceActivityQuery.Create(from, to, context.Sources.NowUtc, departmentId, countryId);
                if (!context.Sources.UserMetadata) return MissingMetadata();

                // The administrator's global filter narrows every figure - and is part of the cache key, so
                // two readers it treats differently never share an overview. Keyed on the hash of the people it
                // admits, not on when the directory was read, so a re-read that changes nobody keeps the figures.
                var scope = await _scopes.ResolveAsync(Request, User, null, cancellationToken).ConfigureAwait(false);
                if (scope.IsRestricted) query = query.WithPeopleScope(scope.Includes, scope.Sql.Key);

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
        [RequirePortalPermission(PortalPermission.SeePii)]
        public Task<IActionResult> Users(
            string overviewId, int licenceTypeId, string workload = "teams", int top = 10, string search = null,
            string sort = "upn", string direction = "asc", int page = 1, int pageSize = 50,
            CancellationToken cancellationToken = default(CancellationToken)) =>
            ExecuteAsync(async () =>
            {
                var context = _context();
                if (!context.Sources.UserMetadata) return MissingMetadata();
                var overview = _overviews.Find(context.Scope, overviewId);
                await RequireSamePeopleScopeAsync(overview, cancellationToken).ConfigureAwait(false);
                if (!overview.Licences.Any(sku => sku.LicenceTypeId == licenceTypeId))
                    return Reply(HttpStatusCode.NotFound, Error("licenceNotOnScreen", "That licence is not part of the figures currently on screen. Refresh the report and try again."));
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
        public Task<IActionResult> Export(string overviewId, string usersId = null) =>
            ExecuteAsync(async () =>
            {
                // The totals-only workbook is for everyone. Asking for the people as well is asking for
                // exactly what api/LicenceActivity/users refuses, so it is refused the same way - checked
                // on every export rather than trusted from when the user list was loaded, so a snapshot
                // taken while the permission was held cannot be exported after it is withdrawn.
                if (usersId != null && !PortalAccess.Evaluate(Request, User).SeePii)
                    return PortalPermissionDenied.Result(Request, PortalPermission.SeePii);
                var context = _context();
                if (!context.Sources.UserMetadata) return MissingMetadata();
                var overview = _overviews.Find(context.Scope, overviewId);
                // Likewise the people scope: figures prepared for someone the global filter treats differently
                // - or before it changed - are not exported to this reader.
                var scope = await RequireSamePeopleScopeAsync(overview, CancellationToken.None).ConfigureAwait(false);
                var users = usersId == null ? null : _users.Find(context.Scope, usersId);
                if (users != null && users.OverviewId != overviewId)
                    return Reply(HttpStatusCode.Conflict, Error("summaryUsersMismatch", "The summary and the user list are no longer from the same set of figures. Refresh the report before exporting."));
                Response.Headers.CacheControl = "no-store, private";

                // The filter is described for this reader, not taken from the cached overview: it may have been
                // built for someone allowed to see the sign-in names in it.
                return new FileContentResult(
                    LicenceActivityWorkbook.Build(overview, users, scope.Global?.DescribeInEnglish()),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
                {
                    FileDownloadName = "licence-activity-" + overview.GeneratedUtc.ToString("yyyy-MM-dd") + ".xlsx",
                };
            });

        /// <summary>
        /// Refuses figures held for a different people scope from the one that applies to this request now -
        /// prepared for a reader the global filter treats differently, or before the filter or the directory
        /// changed who it admits. Answered as expired figures, which the page already handles by refreshing the
        /// report. Returns the scope that applies now.
        /// </summary>
        private async Task<ReportScope> RequireSamePeopleScopeAsync(LicenceActivityOverview overview, CancellationToken cancellationToken)
        {
            var scope = await _scopes.ResolveAsync(Request, User, null, cancellationToken).ConfigureAwait(false);
            var current = scope.IsRestricted ? scope.Sql.Key : null;
            if (!string.Equals(overview?.Query?.PeopleScopeKey, current, StringComparison.Ordinal))
                throw new LicenceActivityExpiredException();
            return scope;
        }

        private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
        {
            if (!ModelState.IsValid) return Reply(HttpStatusCode.BadRequest, Error("invalidRequest", "That request wasn't valid. Check the selected dates and filters."));
            try { return await action(); }
            catch (ArgumentException ex) { return Reply(HttpStatusCode.BadRequest, Error(ValidationErrorCode(ex.Message), ex.Message)); }
            catch (LicenceActivityExpiredException)
            {
                return Reply(HttpStatusCode.Gone, Error("figuresExpiredForAction", "These figures are no longer being held. Refresh the report to bring back an up-to-date set before continuing or exporting."));
            }
            catch (LicenceActivityReadModelExpiredException)
            {
                return Reply(HttpStatusCode.Gone, Error("figuresExpired", "These figures are no longer being held. Refresh the report to bring back an up-to-date set."));
            }
            catch (LicenceActivityReadModelBusyException)
            {
                return Reply(HttpStatusCode.ServiceUnavailable, Error("anotherReportPreparing", "Another licence report is being prepared right now. Try again in a few seconds."), true);
            }
            catch (LicenceActivityBusyException)
            {
                return Reply(HttpStatusCode.ServiceUnavailable, Error("licenceReportingBusy", "Licence reporting is busy. Try again in a few seconds."), true);
            }
            catch (LicenceActivityFailedException ex)
            {
                return Reply(HttpStatusCode.ServiceUnavailable, new { code = "loadFailed", message = ex.Message, reference = ex.RunId }, true);
            }
        }

        private IActionResult MissingMetadata() =>
            Reply(HttpStatusCode.PreconditionFailed, Error("userDetailsImportOff", "This report needs the user details import turned on, so that licences can be matched to the people who hold them."));

        private static object Error(string code, string message) => new { code, message };

        private static string ValidationErrorCode(string message)
        {
            switch (message)
            {
                case "Supply both from and to dates in YYYY-MM-DD format.": return "supplyBothDates";
                case "Choose 7 to 180 inclusive UTC dates, ending before today. Custom ranges are never rounded.": return "dateRange";
                case "The earliest supported date is 1753-01-01.": return "earliestDate";
                case "Licence IDs must be positive; demographic IDs must be zero (unknown) or positive.": return "invalidIds";
                case "Choose teams, outlook, onedrive, sharepoint or copilot.": return "invalidWorkload";
                case "Choose a supported sort and asc or desc direction.": return "invalidSort";
                case "Top and pageSize must be 1 to 100; page must be 1 to 10000.": return "invalidPaging";
                case "Search must contain at most 100 characters and no control characters.": return "invalidSearch";
                case "Dates must use YYYY-MM-DD format.": return "dateFormat";
                default: return null;
            }
        }

        private IActionResult Reply(HttpStatusCode status, object body, bool retry = false)
        {
            Response.Headers.CacheControl = "no-store, private";
            if (retry) Response.Headers.RetryAfter = "5";
            return StatusCode((int)status, body);
        }

        private static LicenceActivityRequestContext CreateContext()
        {
            var config = new AppConfig();
            var settings = config.ImportJobSettings ?? new Common.Entities.ImportTaskSettings();
            var sources = new LicenceActivitySources
            {
                UserMetadata = settings.GraphUsersMetadata, UsageReports = settings.GraphUsageReports,
                CopilotUsageReports = settings.GraphCopilotUsageReports, CopilotAudit = settings.Copilot,
                CopilotInteractions = settings.CopilotInteractionHistory,
                // A match-everything filter ('*') narrows nothing, so it is not a group filter.
                UsageReportsGroupFiltered = new Common.Entities.Config.UserGroupsFilterModel(config.UserGroupsFilter).IsNarrowing,
                NowUtc = DateTime.UtcNow
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