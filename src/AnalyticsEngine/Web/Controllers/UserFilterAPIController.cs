using Common.Entities.UserFilters;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.UserFilters;

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
    [RoutePrefix("api/UserFilter")]
    public class UserFilterAPIController : ApiController
    {
        public UserFilterAPIController() : this(CachedUserDirectorySource.Default)
        {
        }

        internal UserFilterAPIController(IUserDirectorySource directory)
        {
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        internal IUserDirectorySource Directory { get; }

        /// <summary>GET api/UserFilter/dimensions - every attribute a filter can use right now.</summary>
        [HttpGet]
        [Route("dimensions")]
        public Task<IHttpActionResult> Dimensions(CancellationToken cancellationToken = default(CancellationToken))
        {
            return GuardAsync(async () =>
            {
                var snapshot = await Directory.GetAsync(cancellationToken).ConfigureAwait(false);
                return Ok(UserFilterCatalogue.ListDimensions(snapshot));
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

                var snapshot = await Directory.GetAsync(cancellationToken).ConfigureAwait(false);
                var page = UserFilterCatalogue.ListValues(snapshot, dimension, search, take);

                // A custom organisation type deleted or disabled since the page loaded its list.
                if (page == null)
                {
                    return Content(HttpStatusCode.NotFound, new ApiErrorModel("That attribute is no longer available."));
                }

                return Ok(page);
            }, cancellationToken);
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
