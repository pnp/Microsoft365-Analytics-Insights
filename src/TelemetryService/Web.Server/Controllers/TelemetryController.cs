
using Microsoft.AspNetCore.Mvc;
using UsageReporting;
using Web.Config;

namespace Web
{
    [ApiController]
    [Route("api/[controller]")]
    public class TelemetryController : ControllerBase
    {
        private readonly StatsSaveService _statsSaveService;
        private readonly WebAppConfig _configuration;
        private readonly ILogger<TelemetryController> _logger;

        public TelemetryController(StatsSaveService statsSaveService, WebAppConfig configuration, ILogger<TelemetryController> logger)
        {
            _statsSaveService = statsSaveService;
            _configuration = configuration;
            _logger = logger;
        }

        // POST: api/Telemetry
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
    }
}
