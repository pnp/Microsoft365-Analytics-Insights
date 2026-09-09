using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Runs the optional agent-cost imports for one cycle.
    ///
    /// <para>Deliberately <b>not</b> an <c>IGraphImportSection</c>. The Graph section factory builds a Graph
    /// client and a Graph-audience token; these two imports talk to <c>api.powerplatform.com</c> and
    /// <c>management.azure.com</c>, which are different audiences with different role requirements. Sharing
    /// the Graph composition root would mean building Graph plumbing for an import that never uses it, and
    /// would couple two unrelated failure domains.</para>
    ///
    /// <para>The importer factories are <b>lazy</b> for the same reason the Graph sections are: a disabled
    /// import must construct nothing, so a tenant that has not turned these on never creates an HTTP client
    /// or acquires a token for an API it does not use.</para>
    /// </summary>
    public class AgentCostImportPhase
    {
        /// <summary>
        /// Cadence key for the Copilot Studio credit import. Stored unprefixed in Redis db 0 like the Graph
        /// ones, so it can be cleared by hand with e.g. <c>redis-cli DEL CopilotStudioCreditsLastImported</c>
        /// to force a re-import on the next cycle.
        /// </summary>
        public const string CopilotStudioCreditsLastImportedKey = "CopilotStudioCreditsLastImported";

        /// <summary>Cadence key for the Azure Cost Management import.</summary>
        public const string AzureCostLastImportedKey = "AzureCostLastImported";

        private readonly ILogger _logger;
        private readonly AppConfig _settings;
        private readonly IImportLastRunStore _lastRunStore;
        private readonly IClock _clock;
        private readonly Func<CopilotStudioCreditImporter> _creditImporterFactory;
        private readonly Func<AzureCostImporter> _azureCostImporterFactory;

        public AgentCostImportPhase(
            ILogger logger,
            AppConfig settings,
            IImportLastRunStore lastRunStore,
            Func<CopilotStudioCreditImporter> creditImporterFactory,
            Func<AzureCostImporter> azureCostImporterFactory,
            IClock clock = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _lastRunStore = lastRunStore ?? new InMemoryImportLastRunStore();
            _creditImporterFactory = creditImporterFactory ?? throw new ArgumentNullException(nameof(creditImporterFactory));
            _azureCostImporterFactory = azureCostImporterFactory ?? throw new ArgumentNullException(nameof(azureCostImporterFactory));
            _clock = clock ?? SystemClock.Instance;
        }

        /// <summary>
        /// Runs whichever agent-cost imports are enabled and due. Never throws: each import already turns its
        /// own failures into an import-log row, and this phase must not be able to abort the cycle.
        /// </summary>
        public async Task RunAsync()
        {
            await RunCopilotStudioCreditsAsync();
            await RunAzureCostsAsync();
        }

        private async Task RunCopilotStudioCreditsAsync()
        {
            if (!_settings.ImportJobSettings.CopilotStudioCredits)
            {
                _logger.LogInformation("Skipping Copilot Studio credit import.");
                return;
            }

            if (!await IsDueAsync(CopilotStudioCreditsLastImportedKey, _settings.CopilotStudioCreditsIntervalHours,
                    "Copilot Studio credit import"))
            {
                return;
            }

            try
            {
                var importer = _creditImporterFactory();

                var consumption = await importer.ImportAsync();
                var capacity = await importer.ImportCapacityAsync();

                // Only stamp the gate on a clean run. Stamping after a failure would hide the problem for a
                // whole interval - which is exactly how a missing role assignment becomes "it silently
                // imported nothing all day".
                if (string.IsNullOrEmpty(consumption.Error) && string.IsNullOrEmpty(capacity.Error))
                {
                    await _lastRunStore.SetLastRunUtc(CopilotStudioCreditsLastImportedKey, _clock.UtcNow);
                }
                else
                {
                    _logger.LogWarning("Copilot Studio credit import did not fully succeed, so it has NOT been recorded "
                        + "as up to date and will be retried on the next cycle.");
                }
            }
            catch (Exception ex)
            {
                // The importer handles its own errors, so reaching here means construction failed - bad
                // configuration, or a token that could not be acquired at all.
                _logger.LogError(ex, $"Copilot Studio credit import could not start: {ex.Message}. "
                    + "No other import is affected.");
            }
        }

        private async Task RunAzureCostsAsync()
        {
            if (!_settings.ImportJobSettings.AzureCostManagement)
            {
                _logger.LogInformation("Skipping Azure Cost Management import.");
                return;
            }

            var azureSettings = _settings.AzureCostImport ?? new AzureCostImportSettings();

            if (!await IsDueAsync(AzureCostLastImportedKey, azureSettings.IntervalHours,
                    "Azure Cost Management import"))
            {
                return;
            }

            try
            {
                var log = await _azureCostImporterFactory().ImportAsync();

                if (string.IsNullOrEmpty(log.Error))
                {
                    await _lastRunStore.SetLastRunUtc(AzureCostLastImportedKey, _clock.UtcNow);
                }
                else
                {
                    _logger.LogWarning("Azure Cost Management import did not fully succeed, so it has NOT been recorded "
                        + "as up to date and will be retried on the next cycle.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Azure Cost Management import could not start: {ex.Message}. "
                    + "No other import is affected.");
            }
        }

        private async Task<bool> IsDueAsync(string cadenceKey, int intervalHours, string description)
        {
            var lastRun = await _lastRunStore.GetLastRunUtc(cadenceKey);
            if (ImportCadenceGate.ShouldRun(lastRun, intervalHours, force: false, nowUtc: _clock.UtcNow))
            {
                return true;
            }

            _logger.LogInformation($"Skipping {description} - last ran {lastRun:u}, and the interval is {intervalHours}h.");
            return false;
        }
    }
}
