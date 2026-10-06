using Common.Entities.ActivityAnalysis;
using Common.Entities.Config;
using Common.Entities.UserFilters;
using Microsoft.Data.SqlClient;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models.ActivityAnalysis;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// The Activity analysis page: the weekly profiling roll-up (<c>profiling.ActivitiesWeeklyColumns</c>) by metric,
    /// week, company and department - the portal's version of the Power BI report's "Analytics" page.
    /// </summary>
    /// <remarks>
    /// <para><b>Who sees what (#661).</b> The availability and the report are for every signed-in reader. A reader
    /// without See PII cannot single anybody out: when 1 to 4 people match their filters every figure is suppressed,
    /// and companies and departments of fewer than 5 matching people are folded into one unnamed row. The people
    /// themselves - <c>people</c> - need See PII.</para>
    /// <para><b>Who is counted.</b> Everyone in <c>profiling.users</c> (enabled, with an Entra ID and a licence) with at
    /// least one compiled week in the period, narrowed by the administrator's global filter - then by the reader's
    /// user filter, licences and activity ranges, all applied in memory to a read model of the period shared by
    /// every reader (<see cref="ActivityAnalysisReadModel"/>).</para>
    /// <para>Errors carry a stable <c>code</c> the portal words, and English for anyone else.</para>
    /// </remarks>
    [Authorize]
    [RoutePrefix("api/ActivityAnalysis")]
    public sealed class ActivityAnalysisAPIController : ApiController
    {
        /// <summary>The people list's default and largest page.</summary>
        internal const int DefaultTop = 100;
        internal const int MaximumTop = 500;

        /// <summary>Any failure that is not one of the documented refusals. Facts only: the failure is logged.</summary>
        internal const string FailedCode = "activityAnalysisFailed";

        private readonly Func<ActivityAnalysisService> _service;
        private readonly ReportScopeResolver _scopes;
        private readonly Func<DateTime> _utcNow;

        public ActivityAnalysisAPIController() : this(CreateService, ReportScopeResolver.Default) { }

        /// <summary>For tests: no global filter unless <paramref name="scopes"/> supplies one.</summary>
        internal ActivityAnalysisAPIController(
            Func<ActivityAnalysisService> service, ReportScopeResolver scopes = null, Func<DateTime> utcNow = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _scopes = scopes ?? new ReportScopeResolver(GlobalFilterProviders.None, CachedUserDirectorySource.Default);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>Whether there are figures, for which weeks, and which metrics this database has compiled.</summary>
        // GET: api/ActivityAnalysis/availability
        [HttpGet, Route("availability")]
        public Task<IHttpActionResult> Availability(CancellationToken cancellationToken = default(CancellationToken)) =>
            ExecuteAsync("Availability", async () =>
            {
                var schema = await _service().GetSchemaAsync(cancellationToken);
                return Reply(HttpStatusCode.OK, ActivityAnalysisAvailability.From(schema));
            });

        /// <summary>Every figure on the page for one period, metric selection and set of filters.</summary>
        // GET: api/ActivityAnalysis/report?from=2025-10-06&to=2026-09-28&metrics=teams.calls,teams.meetings&ranges=teams.calls:5:
        [HttpGet, Route("report")]
        public Task<IHttpActionResult> Report(
            string from = null, string to = null, string metrics = null, string userFilter = null,
            string licences = null, string ranges = null, CancellationToken cancellationToken = default(CancellationToken)) =>
            ExecuteAsync("Report", async () =>
            {
                if (!CopilotAdoptionAPIController.TryParseUserFilter(userFilter, out var filter, out var filterError))
                {
                    return Refuse(HttpStatusCode.BadRequest, ActivityAnalysisErrorCodes.InvalidFilter, filterError);
                }

                var parsed = ActivityAnalysisQuery.Parse(from, to, metrics, ranges, licences);
                var request = await PrepareAsync(parsed, filter, cancellationToken);
                if (request.Refusal != null) return request.Refusal;

                var report = await request.Service.GetReportAsync(
                    request.Query, request.Audience, request.Scope.UserFilter?.ToEcho(request.Scope.Restriction), cancellationToken);
                return Reply(HttpStatusCode.OK, report);
            });

        /// <summary>
        /// The matching people themselves - in one department, in none, or across every department (the champions) -
        /// with their totals of the selected metrics.
        /// </summary>
        // GET: api/ActivityAnalysis/people?metrics=teams.calls&department=Sales&sort=teams.calls&top=100
        [HttpGet, Route("people")]
        [RequirePortalPermission(PortalPermission.SeePii)]
        public Task<IHttpActionResult> People(
            string from = null, string to = null, string metrics = null, string userFilter = null,
            string licences = null, string ranges = null, string department = null, bool noDepartment = false,
            string sort = null, int top = DefaultTop, CancellationToken cancellationToken = default(CancellationToken)) =>
            ExecuteAsync("People", async () =>
            {
                if (!CopilotAdoptionAPIController.TryParseUserFilter(userFilter, out var filter, out var filterError))
                {
                    return Refuse(HttpStatusCode.BadRequest, ActivityAnalysisErrorCodes.InvalidFilter, filterError);
                }

                if (noDepartment && !string.IsNullOrWhiteSpace(department))
                {
                    return Refuse(HttpStatusCode.BadRequest, ActivityAnalysisErrorCodes.InvalidFilter,
                        "Choose a department or the people with no department, not both.");
                }

                var parsed = ActivityAnalysisQuery.Parse(from, to, metrics, ranges, licences);
                var sortMetric = string.IsNullOrWhiteSpace(sort)
                    ? parsed.Metrics[0]
                    : parsed.Metrics.FirstOrDefault(m => string.Equals(m.Key, sort.Trim(), StringComparison.Ordinal));
                if (sortMetric == null)
                {
                    return Refuse(HttpStatusCode.BadRequest, ActivityAnalysisErrorCodes.InvalidMetric, "Sort by one of the selected metrics.");
                }

                var request = await PrepareAsync(parsed, filter, cancellationToken);
                if (request.Refusal != null) return request.Refusal;

                var people = await request.Service.GetPeopleAsync(
                    request.Query, request.Audience, department, noDepartment, sortMetric,
                    Math.Max(1, Math.Min(MaximumTop, top)), cancellationToken);
                return Reply(HttpStatusCode.OK, people);
            });

        /// <summary>
        /// Checks the query against the database and works out who the reader may see: the administrator's global
        /// filter (which can refuse the request outright - see <see cref="ReportScopeResolver"/>), the reader's own
        /// filter, and whether they hold See PII.
        /// </summary>
        private async Task<PreparedRequest> PrepareAsync(ActivityAnalysisQuery parsed, UserFilterExpression filter, CancellationToken cancellationToken)
        {
            var service = _service();
            var schema = await service.GetSchemaAsync(cancellationToken);
            if (!schema.Installed) return PreparedRequest.Refused(NotInstalled());

            var query = parsed.Resolve(schema, _utcNow());
            var scope = await _scopes.ResolveAsync(Request, User, filter, cancellationToken);

            // Department and company come from the directory whether or not a filter applies. Read from the same
            // snapshot as the filters when there are any, so a person's department and their filter match agree.
            var directory = scope.Restriction?.Snapshot ?? scope.UserFilter?.Snapshot ?? await ReadDirectoryAsync(cancellationToken);
            var audience = new ActivityAnalysisAudience
            {
                Directory = directory,
                Population = scope.Restriction,
                UserFilter = scope.UserFilter,
                SeesIndividuals = PortalAccess.Evaluate(Request, User).SeePii,
                MinimumPeopleWithoutSeePii = ReportScopeResolver.MinimumPeopleWithoutSeePii,
            };

            return new PreparedRequest(service, query, scope, audience);
        }

        /// <summary>The directory, or the same 503 a report gets when a filter's directory cannot be read.</summary>
        private async Task<UserDirectorySnapshot> ReadDirectoryAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _scopes.Directory.GetAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                WebExceptionTelemetry.Report(ex, "ActivityAnalysis.Directory");
                throw ReportScopeFailure.Exception(Request, ReportScopeFailure.DirectoryUnavailable, ReportScopeFailure.DirectoryUnavailableMessage);
            }
        }

        private async Task<IHttpActionResult> ExecuteAsync(string action, Func<Task<IHttpActionResult>> run)
        {
            if (!ModelState.IsValid)
            {
                return Refuse(HttpStatusCode.BadRequest, ActivityAnalysisErrorCodes.InvalidFilter,
                    "That request wasn't valid. Check the department, the page size and the other parameters.");
            }

            try
            {
                return await run();
            }
            catch (ActivityAnalysisQueryException ex)
            {
                return Refuse(HttpStatusCode.BadRequest, ex.Code, ex.Message);
            }
            catch (ActivityAnalysisNotInstalledException)
            {
                return NotInstalled();
            }
            catch (ActivityAnalysisBusyException ex)
            {
                return Refuse(HttpStatusCode.ServiceUnavailable, ActivityAnalysisErrorCodes.Busy, ex.Message, retry: true);
            }
            catch (HttpResponseException)
            {
                // A refusal already worded for the portal - the global filter's, or the permission check's.
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Never let a SQL message - object names, a server name - reach the browser, and never give the
                // general exception reporter SQL text or request values: the type and SQL error number say enough.
                var sql = ex.GetBaseException() as SqlException ?? ex as SqlException;
                WebExceptionTelemetry.Report(new InvalidOperationException(
                    "Activity analysis " + action + " failed (" + ex.GetBaseException().GetType().Name
                    + (sql != null ? ", SQL error " + sql.Number : string.Empty) + ")."), "ActivityAnalysis");
                return Refuse(HttpStatusCode.InternalServerError, FailedCode,
                    "The activity analysis could not be prepared. The failure has been logged. Try again shortly.");
            }
        }

        private IHttpActionResult NotInstalled() =>
            Refuse(HttpStatusCode.PreconditionFailed, ActivityAnalysisErrorCodes.NotInstalled,
                "This page reads the weekly profiling tables that the profiling runbooks compile, and they are not installed in this database.");

        private IHttpActionResult Refuse(HttpStatusCode status, string code, string message, bool retry = false) =>
            Reply(status, new ActivityAnalysisError { Code = code, Message = message }, retry);

        /// <summary>Always JSON, never cached: the figures depend on who is asking.</summary>
        private IHttpActionResult Reply(HttpStatusCode status, object body, bool retry = false)
        {
            var formatter = (MediaTypeFormatter)Request.GetConfiguration()?.Formatters.JsonFormatter ?? new JsonMediaTypeFormatter();
            var response = Request.CreateResponse(status, body, formatter);
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, Private = true };
            if (retry) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return ResponseMessage(response);
        }

        private static ActivityAnalysisService CreateService()
        {
            var config = new AppConfig();
            var connectionString = config.ConnectionStrings.DatabaseConnectionString;
            string scope;
            using (var sha = SHA256.Create())
            {
                scope = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(config.TenantGUID + "\n" + connectionString)));
            }

            return new ActivityAnalysisService(
                new ActivityAnalysisTelemetrySource(new SqlActivityAnalysisSource(connectionString)), scope, ActivityAnalysisCaches.Shared);
        }

        private sealed class PreparedRequest
        {
            internal PreparedRequest(ActivityAnalysisService service, ActivityAnalysisQuery query, ReportScope scope, ActivityAnalysisAudience audience)
            {
                Service = service;
                Query = query;
                Scope = scope;
                Audience = audience;
            }

            private PreparedRequest(IHttpActionResult refusal)
            {
                Refusal = refusal;
            }

            internal static PreparedRequest Refused(IHttpActionResult refusal) => new PreparedRequest(refusal);

            internal ActivityAnalysisService Service { get; }
            internal ActivityAnalysisQuery Query { get; }
            internal ReportScope Scope { get; }
            internal ActivityAnalysisAudience Audience { get; }
            internal IHttpActionResult Refusal { get; }
        }
    }

    /// <summary><c>{ "code": "&lt;stableKey&gt;", "message": "&lt;English&gt;" }</c> - the portal words the code.</summary>
    internal sealed class ActivityAnalysisError
    {
        [Newtonsoft.Json.JsonProperty("code")]
        public string Code { get; set; }

        [Newtonsoft.Json.JsonProperty("message")]
        public string Message { get; set; }
    }
}
