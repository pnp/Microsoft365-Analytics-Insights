using Common.Entities.Config;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Serves the system-health data ("is it working?") consumed by the SPA's Health page. Best-effort
    /// aggregation of App Insights (import liveness, exceptions overview, component health) and SQL
    /// (data counts + freshness); a data-source hiccup degrades a single card, never the whole payload.
    /// Reuses the app's existing Entra credential + App Insights connection string - no new config.
    /// See HEALTH-MONITORING-DESIGN.md (#144).
    /// </summary>
    [Authorize]
    [RoutePrefix("api/Health")]
    public class HealthAPIController : ApiController
    {
        // GET: api/Health
        [HttpGet]
        [Route("")]
        public async Task<IHttpActionResult> Get()
        {
            var model = await HealthDashboard.LoadFrom(new AppConfig());
            return Ok(model);
        }
    }
}
