using Common.Entities.Config;
using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    [Route("api/UserDataLookup")]
    public class UserDataLookupAPIController  : ControllerBase
    {
        private readonly IUserDataLookupService _service;

        public UserDataLookupAPIController() : this(new UserDataLookupService(new SqlUserDataLookupQuery()))
        {
        }

        internal UserDataLookupAPIController(IUserDataLookupService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>
        /// GET api/UserDataLookup/summary?upn=user@contoso.com
        /// Profile + per-category record counts for the user.
        /// </summary>
        [HttpGet]
        [Route("summary")]
        public async Task<IActionResult> Summary(string upn = "")
        {
            // AppConfig is read lazily so a bad request costs nothing, exactly as before the extraction.
            return ToActionResult(await _service.GetSummaryAsync(upn, () => new AppConfig().ImportJobSettings), upn);
        }

        /// <summary>
        /// GET api/UserDataLookup/detail?upn=user@contoso.com&amp;category=audit-events&amp;take=50
        /// The most recent rows for one category for the user.
        /// </summary>
        [HttpGet]
        [Route("detail")]
        public async Task<IActionResult> Detail(string upn = "", string category = "", int take = UserDataLookupRules.DefaultTake)
        {
            return ToActionResult(await _service.GetDetailAsync(upn, category, take), upn);
        }

        private IActionResult ToActionResult<T>(UserDataLookupResult<T> result, string upn) where T : class
        {
            switch (result.Status)
            {
                case UserDataLookupStatus.BadRequest:
                    return StatusCode((int)HttpStatusCode.BadRequest, BadRequestError(result.ErrorMessage));
                case UserDataLookupStatus.UserNotFound:
                    return StatusCode((int)HttpStatusCode.NotFound, new ApiErrorModel(result.ErrorMessage, "userNotFound")
                    {
                        Upn = UserDataLookupRules.Normalise(upn),
                    });
                default:
                    return Ok(result.Value);
            }
        }

        private static ApiErrorModel BadRequestError(string message)
        {
            if (message == "A 'upn' query parameter is required.")
                return new ApiErrorModel(message, "missingUpn");

            const string unknownPrefix = "Unknown category '";
            if (message.StartsWith(unknownPrefix) && message.EndsWith("'."))
                return new ApiErrorModel(message, "unknownCategory", message.Substring(
                    unknownPrefix.Length,
                    message.Length - unknownPrefix.Length - 2));

            const string noDrilldownPrefix = "Category '";
            const string noDrilldownSuffix = "' does not support drill-down.";
            if (message.StartsWith(noDrilldownPrefix) && message.EndsWith(noDrilldownSuffix))
                return new ApiErrorModel(message, "categoryNoDrilldown", message.Substring(
                    noDrilldownPrefix.Length,
                    message.Length - noDrilldownPrefix.Length - noDrilldownSuffix.Length));

            return new ApiErrorModel(message);
        }
    }
}