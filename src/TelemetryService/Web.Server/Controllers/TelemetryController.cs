
using Microsoft.AspNetCore.Mvc;
using UsageReporting;
using Web.Config;
using Web.Dashboard;

namespace Web
{
    [ApiController]
    [Route("api/[controller]")]
    public class TelemetryController : ControllerBase
    {
        private readonly StatsSaveService _statsSaveService;
        private readonly DashboardService _dashboardService;
        private readonly WebAppConfig _configuration;
        private readonly ILogger<TelemetryController> _logger;

        public TelemetryController(StatsSaveService statsSaveService, DashboardService dashboardService, WebAppConfig configuration, ILogger<TelemetryController> logger)
        {
            _statsSaveService = statsSaveService;
            _dashboardService = dashboardService;
            _configuration = configuration;
            _logger = logger;
        }

        // POST: api/Telemetry — receiver endpoint called by WebApiStatsUploader on the importer side.
        [HttpPost]
        public async Task<IActionResult> Post(TelemetryPayload payload)
        {
            if (string.IsNullOrEmpty(_configuration.TelemetrySecret))
            {
                throw new Exception("Server configuration error");
            }


            if (payload?.StatsModel == null || !payload.StatsModel.IsValid)
            {
                return BadRequest();
            }

            // Verify hash
            if (!payload.StatsModel.IsValidSecretForThisObject(payload.Secret, _configuration.TelemetrySecret)) 
            { 
                return Unauthorized(); 
            }

            await _statsSaveService.SaveOrUpdate(payload.StatsModel);

            return Ok();
        }

        // GET: api/Telemetry/stats — aggregate headline figures for the dashboard.
        [HttpGet("stats")]
        public async Task<ActionResult<DashboardStats>> GetStats()
        {
            var stats = await _dashboardService.GetStatsAsync();
            return Ok(stats);
        }

        // GET: api/Telemetry/clients — per-client summary rows for the dashboard table.
        [HttpGet("clients")]
        public async Task<ActionResult<IReadOnlyList<ClientSummary>>> GetClients()
        {
            var clients = await _dashboardService.GetClientsAsync();
            return Ok(clients);
        }
    }
}
