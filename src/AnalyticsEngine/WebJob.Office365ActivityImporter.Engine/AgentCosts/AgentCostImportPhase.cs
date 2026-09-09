using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
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
                var users = await importer.ImportUserCreditsAsync();
                var capacity = await importer.ImportCapacityAsync();

                var parts = new[] { consumption, users, capacity };

                // Back off ONLY when every failing part was refused. The per-user route in particular may sit
                // in a permanent 403 on tenants where Microsoft has not enabled application-only access - and
                // if that were allowed to mark the whole run "refused", a transient failure of the far more
                // important consumption import would be suppressed for a whole interval alongside it.
                await StampOrRetry("Copilot Studio credit import", CopilotStudioCreditsLastImportedKey,
                    succeeded: parts.All(p => p.Succeeded),
                    isAuthorisationFailure: parts.Any(p => p.IsAuthorisationFailure) && !parts.Any(p => p.IsTransientFailure));
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
                var outcome = await _azureCostImporterFactory().ImportAsync();

                await StampOrRetry("Azure Cost Management import", AzureCostLastImportedKey,
                    outcome.Succeeded, outcome.IsAuthorisationFailure);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Azure Cost Management import could not start: {ex.Message}. "
                    + "No other import is affected.");
            }
        }

        private async Task<bool> IsDueAsync(string cadenceKey, int intervalHours, string description)        {
            var lastRun = await _lastRunStore.GetLastRunUtc(cadenceKey);
            if (ImportCadenceGate.ShouldRun(lastRun, intervalHours, force: false, nowUtc: _clock.UtcNow))
            {
                return true;
            }

            _logger.LogInformation($"Skipping {description} - last ran {lastRun:u}, and the interval is {intervalHours}h.");
            return false;
        }

        /// <summary>
        /// Decides whether a finished run counts as "up to date" for cadence purposes.
        /// </summary>
        /// <remarks>
        /// <para>A clean run stamps the gate, obviously. A <b>transient</b> failure does not, so the next
        /// cycle retries promptly instead of hiding the problem for a whole interval - the same reasoning as
        /// the Graph interaction-history section.</para>
        /// <para>An <b>authorisation</b> failure is treated as a completed run and stamps the gate. Nothing
        /// the importer does will fix a missing role assignment or an unset scope, so retrying every cycle
        /// would send a request Microsoft has already refused every few minutes for as long as the toggle
        /// stays on, and write an identical error row each time. The error is on the Health page and in
        /// <c>agent_cost_import_log</c> either way; repeating it faster does not make it more visible.</para>
        /// </remarks>
        private async Task StampOrRetry(string description, string cadenceKey, bool succeeded, bool isAuthorisationFailure)
        {
            if (succeeded)
            {
                await _lastRunStore.SetLastRunUtc(cadenceKey, _clock.UtcNow);
                return;
            }

            if (isAuthorisationFailure)
            {
                await _lastRunStore.SetLastRunUtc(cadenceKey, _clock.UtcNow);
                _logger.LogWarning($"{description} was refused, and retrying cannot fix that, so it will wait for the "
                    + "normal interval rather than re-asking every cycle. Fix the permission (see the error above); "
                    + "the retry then happens on the next scheduled run. To force it sooner, clear the Redis key "
                    + $"'{cadenceKey}' - restarting the web-job does not, because the stamp is stored in Redis.");
                return;
            }

            _logger.LogWarning($"{description} did not fully succeed, so it has NOT been recorded as up to date and "
                + "will be retried on the next cycle.");
        }
    }
}
