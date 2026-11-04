using Web.Storage;

namespace Web.Startup
{
    /// <summary>
    /// Checks on startup that the Cosmos containers the service uses exist and are partitioned as the code
    /// expects, and logs an error naming any that are not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It does not create them. It used to call <c>CreateDatabaseIfNotExistsAsync</c> and
    /// <c>CreateContainerIfNotExistsAsync</c>, but the service authenticates to Cosmos DB with Microsoft Entra ID,
    /// whose data-plane roles cannot create containers (see <see cref="CosmosSchema"/>). Those calls only worked
    /// because they read first and the containers were already there: on a fresh deployment the annotations
    /// container, which the template did not provision, failed with 403 and the failure was swallowed here. Every
    /// container is now provisioned by <c>infra/TelemetryService</c>, and this only reports one that is missing.
    /// </para>
    /// <para>
    /// Runs as a hosted service rather than inline during startup: a Cosmos outage (or a misconfigured endpoint)
    /// that threw before the host was built stopped the whole site starting, even the anonymous <c>/health</c>
    /// endpoint, which makes an outage look like a deployment failure. Failures are logged, not thrown, and the
    /// per-request code paths still report their own errors.
    /// </para>
    /// </remarks>
    public class CosmosSchemaVerifier : IHostedService
    {
        private readonly CosmosTelemetrySaveAdaptor _adaptor;
        private readonly CosmosClientAnnotationStore _annotationStore;
        private readonly ILogger<CosmosSchemaVerifier> _logger;

        public CosmosSchemaVerifier(
            CosmosTelemetrySaveAdaptor adaptor,
            CosmosClientAnnotationStore annotationStore,
            ILogger<CosmosSchemaVerifier> logger)
        {
            _adaptor = adaptor;
            _annotationStore = annotationStore;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var problems = new List<string>();
                problems.AddRange(await _adaptor.VerifySchemaAsync(cancellationToken));
                problems.AddRange(await _annotationStore.VerifySchemaAsync(cancellationToken));

                if (problems.Count == 0)
                {
                    _logger.LogInformation("Cosmos database and containers verified.");
                    return;
                }

                foreach (var problem in problems)
                {
                    _logger.LogError(
                        "{Problem} The service does not create or change Cosmos containers, because Microsoft Entra " +
                        "data-plane access cannot. Deploy infra/TelemetryService (deploy.ps1), which provisions every " +
                        "container, or create it through Azure Resource Manager.", problem);
                }
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
