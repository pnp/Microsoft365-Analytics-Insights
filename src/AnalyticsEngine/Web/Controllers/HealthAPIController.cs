using Common.Entities.Config;
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models.Health;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Serves the system-health data ("is it working?") consumed by the SPA's Health page. Split into
    /// independently-cached sub-sections (summary / data / liveness / exceptions / components / config)
    /// so the SPA fetches only the sub-section the user is looking at, and a slow / failing data source
    /// degrades that one section instead of the whole page. Best-effort throughout: a data-source hiccup
    /// sets an error field on its section, never a non-200. Reuses the app's existing Entra credential +
    /// App Insights connection string - no new config. See HEALTH-MONITORING-DESIGN.md (#144).
    /// </summary>
    [Authorize]
    [Route("api/Health")]
    public class HealthAPIController  : ControllerBase
    {
        private readonly HealthService _health;

        /// <summary>
        /// Serves from the process-wide <see cref="HealthService.Default"/>: the per-section caches and
        /// single-flight gates are instance state, so every request must share one instance.
        /// </summary>
        public HealthAPIController() : this(HealthService.Default)
        {
        }

        public HealthAPIController(HealthService health)
        {
            _health = health ?? throw new ArgumentNullException(nameof(health));
        }

        /// <summary>The service this controller will serve from. Exposed so a test can prove the wiring above.</summary>
        internal HealthService Service => _health;

        // GET: api/Health  (and api/Health/summary)
        // Lightweight overview: the overall traffic-light + per-section grid. Skips the heavy SQL scans
        // (only probes DB reachability), so opening the Health page stays cheap on a big tenant.
        [HttpGet]
        [Route("")]
        [Route("summary")]
        public async Task<IActionResult> Summary()
        {
            return Ok(await _health.LoadSummaryAsync(new AppConfig()));
        }

        // GET: api/Health/data
        // SQL data overview: approximate counts + DB size (cheap DMVs) and bounded, timeout-capped
        // recent-volume + freshness scans. This is the only heavy section, loaded on demand.
        [HttpGet]
        [Route("data")]
        public async Task<IActionResult> Data()
        {
            return Ok(await _health.LoadDataAsync());
        }

        // GET: api/Health/liveness
        [HttpGet]
        [Route("liveness")]
        public async Task<IActionResult> Liveness()
        {
            return Ok(await _health.LoadLivenessAsync(new AppConfig()));
        }

        // GET: api/Health/exceptions
        [HttpGet]
        [Route("exceptions")]
        public async Task<IActionResult> Exceptions()
        {
            return Ok(await _health.LoadExceptionsAsync(new AppConfig()));
        }

        // GET: api/Health/components
        [HttpGet]
        [Route("components")]
        public async Task<IActionResult> Components()
        {
            return Ok(await _health.LoadComponentsAsync(new AppConfig()));
        }

        // GET: api/Health/config
        [HttpGet]
        [Route("config")]
        public async Task<IActionResult> Config()
        {
            return Ok(await _health.LoadConfigAsync(new AppConfig()));
        }
    }
}