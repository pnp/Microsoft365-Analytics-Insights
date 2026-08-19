using UsageReporting;

namespace Web.Startup
{
    /// <summary>
    /// Creates the Cosmos database and containers if they don't already exist.
    ///
    /// Runs as a hosted service rather than inline during startup: previously a Cosmos outage (or a
    /// misconfigured endpoint) threw before the host was built, so the whole site failed to start and
    /// even the anonymous <c>/health</c> endpoint was unreachable — which makes an outage look like a
    /// deployment failure. Failures here are logged and swallowed, because the containers almost always
    /// already exist and the per-request code paths surface their own errors.
    /// </summary>
    public class CosmosSchemaInitializer : IHostedService
    {
        private readonly CosmosTelemetrySaveAdaptor _adaptor;
        private readonly ILogger<CosmosSchemaInitializer> _logger;

        public CosmosSchemaInitializer(CosmosTelemetrySaveAdaptor adaptor, ILogger<CosmosSchemaInitializer> logger)
        {
            _adaptor = adaptor;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _adaptor.Init();
                _logger.LogInformation("Cosmos database and containers verified.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Could not verify the Cosmos database/containers on startup ({Message}). The site will " +
                    "still start; telemetry reads and writes will report their own errors until Cosmos is " +
                    "reachable.", ex.Message);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
