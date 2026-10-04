using Common.Entities.UserFilters;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Feeds the portal's user filter: which attributes a report can be filtered on, and which values
    /// each of them holds.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately not part of any one report's API. The filter - standard Entra ID attributes
    /// plus every custom organisation type an admin has defined - is the same wherever it appears, so a
    /// report that adopts it reuses this picker and only has to accept the <c>userFilter</c> parameter
    /// (see <see cref="UserFilterCodec"/>) and narrow its rows by user id.</para>
    /// <para>Read-only, served from the shared <see cref="IUserDirectorySource"/> snapshot, so browsing
    /// values never touches the database after the snapshot is loaded.</para>
    /// </remarks>
    [Authorize]
    [RequirePortalPermission(PortalPermission.SeePii)]
    [RoutePrefix("api/UserFilter")]
    public class UserFilterAPIController : ApiController
    {
        public UserFilterAPIController() : this(CachedUserDirectorySource.Default, ReportScopeResolver.Default)
        {
        }

        /// <summary>For tests: no global filter unless <paramref name="scopes"/> supplies one.</summary>
        internal UserFilterAPIController(IUserDirectorySource directory, ReportScopeResolver scopes = null)
        {
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            Scopes = scopes ?? new ReportScopeResolver(GlobalFilterProviders.None, directory);
        }

        internal IUserDirectorySource Directory { get; }

        internal ReportScopeResolver Scopes { get; }

        /// <summary>GET api/UserFilter/dimensions - every attribute a filter can use right now.</summary>
        [HttpGet]
        [Route("dimensions")]
        public Task<IHttpActionResult> Dimensions(CancellationToken cancellationToken = default(CancellationToken))
        {
            return GuardAsync(async () =>
            {
                var restriction = await RestrictionAsync(cancellationToken).ConfigureAwait(false);
                var snapshot = restriction?.Snapshot ?? await Directory.GetAsync(cancellationToken).ConfigureAwait(false);
                return Ok(UserFilterCatalogue.ListDimensions(snapshot, restriction));
            }, cancellationToken);
        }

        /// <summary>
        /// GET api/UserFilter/values?dimension=department&amp;search=sal&amp;take=200 - one dimension's
        /// values, largest first, each with how many people hold it.
        /// </summary>
        [HttpGet]
        [Route("values")]
        public Task<IHttpActionResult> Values(
            string dimension,
            string search = null,
            int take = UserFilterCatalogue.DefaultTake,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return GuardAsync(async () =>
            {
                if (!UserFilterDimensions.IsWellFormed(dimension))
                {
                    return Content(HttpStatusCode.BadRequest, new ApiErrorModel("That is not an attribute a report can be filtered on."));
                }

                var restriction = await RestrictionAsync(cancellationToken).ConfigureAwait(false);
                var snapshot = restriction?.Snapshot ?? await Directory.GetAsync(cancellationToken).ConfigureAwait(false);
                var page = UserFilterCatalogue.ListValues(snapshot, dimension, search, take, restriction);

                // A custom organisation type deleted or disabled since the page loaded its list.
                if (page == null)
                {
                    return Content(HttpStatusCode.NotFound, new ApiErrorModel("That attribute is no longer available."));
                }

                return Ok(page);
            }, cancellationToken);
        }

        /// <summary>
        /// The administrator's global filter as it applies to the caller, so the picker offers only the values
        /// held by people they may see - or <c>null</c> for no narrowing.
        /// </summary>
        /// <remarks>
        /// A portal administrator always sees every value: they can change or switch off the global filter
        /// itself, and its editor picks values here - narrowed to their own view, it could only ever offer the
        /// values it already selects.
        /// </remarks>
        private async Task<CompiledUserFilter> RestrictionAsync(CancellationToken cancellationToken)
        {
            if (PortalAccess.Evaluate(Request, User).Administration) return null;

            var scope = await Scopes.ResolveAsync(Request, User, null, cancellationToken).ConfigureAwait(false);
            return scope.Restriction;
        }

        /// <summary>
        /// Turns the outcome into a response, reporting only genuine faults - the same shape as the user
        /// organisation API's guard.
        /// </summary>
        /// <remarks>
        /// The picker aborts its request every time the reader types another letter, so a cancelled
        /// request is routine and is answered 499 without telemetry. Anything else is reported to
        /// Application Insights and answered with a fixed message: this site ships with customErrors
        /// off, and a SQL exception would otherwise reach the browser carrying object names.
        /// </remarks>
        private async Task<IHttpActionResult> GuardAsync(Func<Task<IHttpActionResult>> work, CancellationToken cancellationToken)
        {
            try
            {
                return await work().ConfigureAwait(false);
            }
            catch (HttpResponseException)
            {
                // A deliberate refusal - the global filter could not be evaluated - already shaped for the caller.
                throw;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return StatusCode((HttpStatusCode)499);
            }
            catch (Exception ex)
            {
                WebExceptionTelemetry.Report(ex, "UserFilterAPI");

                return Content(
                    HttpStatusCode.InternalServerError,
                    new ApiErrorModel("The directory could not be read. Check the service logs for details."));
            }
        }
    }
}
