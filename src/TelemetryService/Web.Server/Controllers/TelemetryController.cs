using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsageReporting;
using Web.Auth;
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
        private readonly IClientAnnotationStore _annotationStore;
        private readonly WebAppConfig _configuration;
        private readonly ILogger<TelemetryController> _logger;

        public TelemetryController(
            StatsSaveService statsSaveService,
            DashboardService dashboardService,
            IClientAnnotationStore annotationStore,
            WebAppConfig configuration,
            ILogger<TelemetryController> logger)
        {
            _statsSaveService = statsSaveService;
            _dashboardService = dashboardService;
            _annotationStore = annotationStore;
            _configuration = configuration;
            _logger = logger;
        }

        // Uploaders authenticate the payload itself with the shared signature.
        [HttpPost]
        [AllowAnonymous]
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

        [HttpGet("stats")]
        [Authorize(Policy = DashboardAuthorization.PolicyName)]
        public async Task<ActionResult<DashboardStats>> GetStats()
        {
            var stats = await _dashboardService.GetStatsAsync();
            return Ok(stats);
        }

        [HttpGet("clients")]
        [Authorize(Policy = DashboardAuthorization.PolicyName)]
        public async Task<ActionResult<IReadOnlyList<ClientSummary>>> GetClients()
        {
            var clients = await _dashboardService.GetClientsAsync();
            return Ok(clients);
        }

        // ---------------------------------------------------------------------------------------
        // Maintainer annotations: which anonymous client is which customer.
        //
        // Every endpoint below is maintainer-only. This is the ONE place in the system where a real
        // organisation is tied to a reporting client, so it must never share the uploader's anonymous
        // shared-secret path - unlike Post above, none of these may be [AllowAnonymous].
        //
        // Nothing here changes what clients upload. A customer is only identifiable because they chose
        // to tell us their id, which they find in their own importer logs.
        // ---------------------------------------------------------------------------------------

        [HttpGet("annotations")]
        [Authorize(Policy = DashboardAuthorization.PolicyName)]
        public async Task<ActionResult<IReadOnlyList<ClientAnnotation>>> GetAnnotations()
        {
            return Ok(await _annotationStore.LoadAllAsync());
        }

        [HttpPut("annotations/{anonClientId}")]
        [Authorize(Policy = DashboardAuthorization.PolicyName)]
        public async Task<IActionResult> PutAnnotation(string anonClientId, [FromBody] ClientAnnotationUpdate update)
        {
            if (string.IsNullOrWhiteSpace(anonClientId)) return BadRequest();
            if (update == null) return BadRequest();

            var displayName = Trim(update.DisplayName, ClientAnnotation.MaxDisplayNameLength);
            var notes = Trim(update.Notes, ClientAnnotation.MaxNotesLength);

            var annotation = new ClientAnnotation
            {
                id = anonClientId,
                DisplayName = displayName,
                Notes = notes,
                UpdatedUtc = DateTime.UtcNow,

                // Attribute the edit. Falls back to the raw name claim so an unusual token shape
                // records something rather than nothing.
                UpdatedBy = User?.Identity?.Name ?? User?.FindFirst("preferred_username")?.Value,
            };

            if (annotation.IsEmpty)
            {
                // Clearing both fields means "forget this one", not "store two blanks".
                await _annotationStore.DeleteAsync(anonClientId);
                _logger.LogInformation("Client annotation cleared for a reporting client by {User}.", annotation.UpdatedBy);
                return NoContent();
            }

            await _annotationStore.SaveAsync(annotation);

            // Deliberately does NOT log the display name: these logs are far less protected than the
            // Cosmos account, and the name is the only customer-identifying value in the system.
            _logger.LogInformation("Client annotation saved for a reporting client by {User}.", annotation.UpdatedBy);
            return Ok(annotation);
        }

        [HttpDelete("annotations/{anonClientId}")]
        [Authorize(Policy = DashboardAuthorization.PolicyName)]
        public async Task<IActionResult> DeleteAnnotation(string anonClientId)
        {
            if (string.IsNullOrWhiteSpace(anonClientId)) return BadRequest();

            await _annotationStore.DeleteAsync(anonClientId);
            return NoContent();
        }

        private static string? Trim(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
        }
    }

    /// <summary>What a maintainer can set on an annotation. The id comes from the route.</summary>
    public class ClientAnnotationUpdate
    {
        public string? DisplayName { get; set; }

        public string? Notes { get; set; }
    }
}
