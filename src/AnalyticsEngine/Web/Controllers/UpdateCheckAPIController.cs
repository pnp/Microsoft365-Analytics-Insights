using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models.UpdateCheck;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// "Is there a newer release than the build we're running?" for the portal's Administration area.
    /// </summary>
    /// <remarks>
    /// Read-only and on demand - nothing here polls GitHub in the background, so a deployment that never
    /// opens the page never makes an outbound call. The result is cached briefly server-side to protect
    /// GitHub's anonymous rate limit, which the installer also depends on.
    /// </remarks>
    [Authorize]
    [RoutePrefix("api/UpdateCheck")]
    public class UpdateCheckAPIController : ApiController
    {
        // GET: api/UpdateCheck
        [HttpGet]
        [Route("")]
        public async Task<IHttpActionResult> Get()
        {
            // Failures are reported inside the model (CheckError) rather than as an HTTP error, so the
            // page can always show which build is running even when GitHub is unreachable.
            return Ok(await UpdateChecker.CheckAsync());
        }
    }
}
