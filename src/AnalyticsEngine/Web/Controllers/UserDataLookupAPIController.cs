using Common.Entities.Config;
using System;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.UserDataLookup;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Admin lookup of everything held in SQL for a single user, keyed by UPN.
    /// Returns a profile + per-category record counts, and (per category) the most recent rows.
    /// </summary>
    /// <remarks>
    /// Model binding and the HTTP result only - the validation, category mapping and shaping live in
    /// <see cref="IUserDataLookupService"/>, and the EF queries in <see cref="SqlUserDataLookupQuery"/>,
    /// so both are testable without a database or an ASP.NET pipeline (issue #379).
    /// </remarks>
    [Authorize]
    [RoutePrefix("api/UserDataLookup")]
    public class UserDataLookupAPIController : ApiController
    {
        private readonly IUserDataLookupService _service;

        public UserDataLookupAPIController() : this(new UserDataLookupService(new SqlUserDataLookupQuery()))
        {
        }

        public UserDataLookupAPIController(IUserDataLookupService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>
        /// GET api/UserDataLookup/summary?upn=user@contoso.com
        /// Profile + per-category record counts for the user.
        /// </summary>
        [HttpGet]
        [Route("summary")]
        public async Task<IHttpActionResult> Summary(string upn = "")
        {
            // AppConfig is read lazily so a bad request costs nothing, exactly as before the extraction.
            return ToActionResult(await _service.GetSummaryAsync(upn, () => new AppConfig().ImportJobSettings));
        }

        /// <summary>
        /// GET api/UserDataLookup/detail?upn=user@contoso.com&amp;category=audit-events&amp;take=50
        /// The most recent rows for one category for the user.
        /// </summary>
        [HttpGet]
        [Route("detail")]
        public async Task<IHttpActionResult> Detail(string upn = "", string category = "", int take = UserDataLookupRules.DefaultTake)
        {
            return ToActionResult(await _service.GetDetailAsync(upn, category, take));
        }

        private IHttpActionResult ToActionResult<T>(UserDataLookupResult<T> result) where T : class
        {
            switch (result.Status)
            {
                case UserDataLookupStatus.BadRequest:
                    return Content(HttpStatusCode.BadRequest, new ApiErrorModel(result.ErrorMessage));
                case UserDataLookupStatus.UserNotFound:
                    return Content(HttpStatusCode.NotFound, new ApiErrorModel(result.ErrorMessage));
                default:
                    return Ok(result.Value);
            }
        }
    }
}
