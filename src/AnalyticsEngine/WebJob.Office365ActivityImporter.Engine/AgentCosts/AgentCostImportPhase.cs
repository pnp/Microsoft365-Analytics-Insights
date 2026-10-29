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
        /// Cadence key for the Copilot Studio credit import. A row key in the ImportSchedule partition of the
        /// AnalyticsState Azure Table, like the Graph ones, so it can be deleted by hand (e.g. in Azure Storage
        /// Explorer) to force a re-import on the next cycle.
        /// </summary>
        public const string CopilotStudioCreditsLastImportedKey = "CopilotStudioCreditsLastImported";

        /// <summary>Cadence key for the Azure Cost Management import.</summary>
        public const string AzureCostLastImportedKey = "AzureCostLastImported";

        /// <summary>
        /// Row key prefix holding the earliest time the Copilot Studio credit import may try again after a failure
        /// that retrying might fix. The connection generation follows it, like the cadence key; deleting the row
        /// retries on the next cycle.
        /// </summary>
        public const string CopilotStudioCreditsRetryAfterKey = "CopilotStudioCreditsRetryAfter";

        /// <summary>
        /// How long the Copilot Studio credit import waits after a failure that retrying might fix. A retry is not
        /// cheap - the per-agent figures are one call per active user per day - so it is not repeated every cycle;
        /// the days already stored are not re-read (see <see cref="CopilotStudioCreditCheckpointStore"/>).
        /// </summary>
        public static readonly TimeSpan CopilotStudioCreditsRetryBackoff = TimeSpan.FromHours(1);

        /// <summary>
        /// Row key prefix holding the earliest time the Azure import may try again. A hash of the scopes, filter and
        /// grouping follows it (<see cref="AzureCostRetryAfterKeyFor"/>), so correcting the configuration is tried on
        /// the next cycle rather than after the back-off.
        /// </summary>
        public const string AzureCostRetryAfterKey = "AzureCostRetryAfter";

        /// <summary>When the Azure import first failed in the current run of failures. Cleared by any success.</summary>
        public const string AzureCostFailingSinceKey = "AzureCostFailingSince";

        /// <summary>
        /// How long the Azure import may fail on every attempt before it stops retrying every cycle. A blip is retried
        /// promptly; a failure that has outlasted this is retried hourly, so it no longer re-reads every healthy scope
        /// alongside the failing one every few minutes.
        /// </summary>
        public static readonly TimeSpan AzureCostPersistentFailureThreshold = TimeSpan.FromHours(1);

        /// <summary>How long the Azure import waits between attempts once a failure has proved persistent.</summary>
        public static readonly TimeSpan AzureCostPersistentFailureBackoff = TimeSpan.FromHours(1);

        private readonly ILogger _logger;
        private readonly AppConfig _settings;
        private readonly IImportLastRunStore _lastRunStore;
        private readonly IClock _clock;
        private readonly Func<CopilotStudioCreditImporter> _creditImporterFactory;
        private readonly Func<AzureCostImporter> _azureCostImporterFactory;
        private readonly Func<Task<string>> _creditConnectionVersion;
        private readonly Func<string, string, Task> _creditHealth;

        public AgentCostImportPhase(
            ILogger logger,
            AppConfig settings,
            IImportLastRunStore lastRunStore,
            Func<CopilotStudioCreditImporter> creditImporterFactory,
            Func<AzureCostImporter> azureCostImporterFactory,
            IClock clock = null,
            Func<Task<string>> creditConnectionVersion = null,
            Func<string, string, Task> creditHealth = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _lastRunStore = lastRunStore ?? new InMemoryImportLastRunStore();
            _creditImporterFactory = creditImporterFactory ?? throw new ArgumentNullException(nameof(creditImporterFactory));
            _azureCostImporterFactory = azureCostImporterFactory ?? throw new ArgumentNullException(nameof(azureCostImporterFactory));
            _clock = clock ?? SystemClock.Instance;
            _creditConnectionVersion = creditConnectionVersion;
            _creditHealth = creditHealth;
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

            string version = null;
            var attempted = false;
            var consumptionFinished = false;
            try
            {
                // A new connection (including disconnect) gets its own gate. An older run cannot stamp it.
                version = _creditConnectionVersion == null ? null : await _creditConnectionVersion();
                var suffix = version == null ? "" : "_" + version;
                var cadenceKey = CopilotStudioCreditsLastImportedKey + suffix;
                var retryAfterKey = CopilotStudioCreditsRetryAfterKey + suffix;
                var intervalHours = _settings.CopilotStudioCreditsIntervalHours;
                if (!await IsDueAsync(cadenceKey, intervalHours, "Copilot Studio credit import", retryAfterKey))
                    return;
                attempted = true;
                var importer = _creditImporterFactory();

                var consumptionResult = await importer.ImportConsumptionAsync();
                var consumption = consumptionResult.Agents;
                var users = consumptionResult.Users;
                if (!consumption.IsConnectionRequired && !users.IsConnectionRequired)
                    await RecordCreditHealth(version,
                        consumption.IsAuthorisationFailure || users.IsAuthorisationFailure ? "permissionDenied"
                        : consumption.Succeeded && users.Succeeded ? null : "sourceUnavailable");
                consumptionFinished = true;
                var capacity = await importer.ImportCapacityAsync();

                // The per-user read is BEST EFFORT and deliberately excluded from the cadence decision: the
                // per-agent figures are derived from the same reads, and the failure is not swallowed - it is
                // written to agent_cost_import_log and surfaced on the Health page and the report.
                if (!users.Succeeded)
                {
                    _logger.LogWarning("The per-user Copilot Studio credit read did not succeed. "
                        + "See the agent_cost_import_log entry for the reason.");
                }

                // Rows outside the trailing window are never re-read by the import, so a user who only
                // appeared in the directory after their credits were first stored would stay unattributed
                // for ever. This is what makes the nullable link self-healing. It runs whether or not the
                // read above succeeded - older rows are still worth attributing - and never throws.
                await importer.LinkOutstandingUsersAsync();

                var parts = new[] { consumption, capacity };

                // Back off ONLY when every failing part was refused. A transient failure alongside a refusal
                // still deserves a prompt retry, so it must not be absorbed into "refused". "Prompt" is not "next
                // cycle" for this import, though: a retry repeats thousands of per-user reads, so it waits a short
                // back-off (never longer than the interval itself) instead of a few minutes.
                await StampOrRetry("Copilot Studio credit import", cadenceKey,
                    succeeded: parts.All(p => p.Succeeded),
                    isAuthorisationFailure: parts.Any(p => p.IsAuthorisationFailure) && !parts.Any(p => p.IsTransientFailure),
                    retryAfterKey: retryAfterKey,
                    transientBackoff: intervalHours > 0
                        ? TimeSpan.FromHours(Math.Min(intervalHours, CopilotStudioCreditsRetryBackoff.TotalHours))
                        : TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                if (attempted && !consumptionFinished) await RecordCreditHealth(version, "sourceFailure");
                // The importer handles its own errors, so reaching here means construction failed - bad
                // configuration, or a token that could not be acquired at all.
                _logger.LogError(ex, $"Copilot Studio credit import could not start: {ex.Message}. "
                    + "No other import is affected.");
            }
        }

        private async Task RecordCreditHealth(string version, string failureCode)
        {
            if (_creditHealth == null || version == null) return;
            try { await _creditHealth(version, failureCode); }
            catch (Exception)
            {
                _logger.LogWarning("Copilot Studio consumption health could not be saved.");
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
            var retryAfterKey = AzureCostRetryAfterKeyFor(azureSettings);

            if (!await IsDueAsync(AzureCostLastImportedKey, azureSettings.IntervalHours,
                    "Azure Cost Management import", retryAfterKey))
            {
                return;
            }

            try
            {
                var outcome = await _azureCostImporterFactory().ImportAsync();

                if (outcome.IsConfigurationFailure)
                {
                    await BackOffRejectedAzureQuery(retryAfterKey, azureSettings.IntervalHours);
                    return;
                }

                await StampOrRetry("Azure Cost Management import", AzureCostLastImportedKey,
                    outcome.Succeeded, outcome.IsAuthorisationFailure, retryAfterKey,
                    transientBackoff: await AzurePersistentFailureBackoff(outcome, azureSettings.IntervalHours));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Azure Cost Management import could not start: {ex.Message}. "
                    + "No other import is affected.");
            }
        }

        /// <summary>
        /// The retry-after key for the Azure import's current configuration: a back-off recorded for one set of scopes,
        /// filter and grouping does not apply to another.
        /// </summary>
        public static string AzureCostRetryAfterKeyFor(AzureCostImportSettings settings)
            => AzureCostRetryAfterKey + "_" + AzureConfigurationFingerprint(settings);

        /// <summary>
        /// A short, stable hash of everything that shapes the Cost Management query. Order-insensitive for the lists,
        /// which are sets as far as the query is concerned.
        /// </summary>
        internal static string AzureConfigurationFingerprint(AzureCostImportSettings settings)
        {
            settings = settings ?? new AzureCostImportSettings();
            string Set(System.Collections.Generic.IEnumerable<string> values) => string.Join(";",
                (values ?? Enumerable.Empty<string>()).Select(v => (v ?? string.Empty).Trim()).OrderBy(v => v, StringComparer.Ordinal));

            return AgentCostRowHasher.Hash(
                Set(settings.Scopes),
                settings.MeterFilterDimension ?? string.Empty,
                Set(settings.MeterFilterValues),
                string.Join(";", settings.ResolvedGroupBy)).Substring(0, 16);
        }

        /// <summary>
        /// Cost Management rejected the query itself (a 400 or 404 on every failing scope). Sending it again cannot
        /// succeed, so it waits the normal interval - like a refusal - unless the configuration changes first.
        /// </summary>
        private async Task BackOffRejectedAzureQuery(string retryAfterKey, int intervalHours)
        {
            await _lastRunStore.Clear(AzureCostFailingSinceKey);
            if (intervalHours <= 0)
            {
                _logger.LogWarning("Azure Cost Management rejected the configured query (see the error above). The interval is 0, "
                    + "so it will be retried on the next cycle anyway.");
                return;
            }

            var retryAt = _clock.UtcNow.AddHours(intervalHours);
            await _lastRunStore.SetLastRunUtc(retryAfterKey, retryAt);
            _logger.LogWarning($"Azure Cost Management rejected the configured query (see the error above). Sending the same "
                + $"query again cannot succeed, so it will not be retried before {retryAt:u} - but it WILL run on the next "
                + "cycle once 'AzureCostScopes', 'AzureCostMeterFilterDimension', 'AzureCostMeterFilterValues' or "
                + "'AzureCostGroupBy' is changed. The error stays on the Health page and in agent_cost_import_log until then.");
        }

        /// <summary>
        /// How long a transient Azure failure should wait before the next attempt: zero (the next cycle) for a blip,
        /// <see cref="AzureCostPersistentFailureBackoff"/> once every attempt for <see cref="AzureCostPersistentFailureThreshold"/>
        /// has failed - never longer than the interval, and never with the gate disabled (0). Also maintains the
        /// "failing since" record the decision rests on.
        /// </summary>
        private async Task<TimeSpan> AzurePersistentFailureBackoff(AgentCostImportOutcome outcome, int intervalHours)
        {
            if (!outcome.IsTransientFailure)
            {
                await _lastRunStore.Clear(AzureCostFailingSinceKey);
                return TimeSpan.Zero;
            }

            var failingSince = await _lastRunStore.GetLastRunUtc(AzureCostFailingSinceKey);
            if (!failingSince.HasValue)
            {
                await _lastRunStore.SetLastRunUtc(AzureCostFailingSinceKey, _clock.UtcNow);
                return TimeSpan.Zero;
            }

            if (intervalHours <= 0 || _clock.UtcNow - failingSince.Value < AzureCostPersistentFailureThreshold) return TimeSpan.Zero;

            var backoff = TimeSpan.FromHours(Math.Min(intervalHours, AzureCostPersistentFailureBackoff.TotalHours));
            _logger.LogWarning($"Azure Cost Management import has failed on every attempt since {failingSince.Value:u}, so it "
                + "is now retried hourly instead of every cycle - every attempt re-reads all the scopes, including the healthy ones.");
            return backoff;
        }

        private async Task<bool> IsDueAsync(string cadenceKey, int intervalHours, string description, string retryAfterKey = null)
        {
            var lastRun = await _lastRunStore.GetLastRunUtc(cadenceKey);
            if (!ImportCadenceGate.ShouldRun(lastRun, intervalHours, force: false, nowUtc: _clock.UtcNow))
            {
                _logger.LogInformation($"Skipping {description} - last ran {lastRun:u}, and the interval is {intervalHours}h.");
                return false;
            }

            if (retryAfterKey != null)
            {
                var retryAfter = await _lastRunStore.GetLastRunUtc(retryAfterKey);
                if (retryAfter.HasValue && _clock.UtcNow < retryAfter.Value)
                {
                    _logger.LogInformation($"Skipping {description} - its last attempt did not succeed, and it is backing off "
                        + $"until {retryAfter.Value:u} (see the warning logged then). To retry sooner, delete the row "
                        + $"'{retryAfterKey}' (partition 'ImportSchedule') from the 'AnalyticsState' table.");
                    return false;
                }
            }

            return true;
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
        /// <para>A <paramref name="transientBackoff"/> above zero delays the retry of a transient failure by that
        /// long (recorded under <paramref name="retryAfterKey"/>) instead of leaving it to the next cycle. Any other
        /// outcome clears that record.</para>
        /// </remarks>
        private async Task StampOrRetry(string description, string cadenceKey, bool succeeded, bool isAuthorisationFailure,
            string retryAfterKey = null, TimeSpan transientBackoff = default(TimeSpan))
        {
            if (succeeded)
            {
                await _lastRunStore.SetLastRunUtc(cadenceKey, _clock.UtcNow);
                if (retryAfterKey != null) await _lastRunStore.Clear(retryAfterKey);
                return;
            }

            if (isAuthorisationFailure)
            {
                await _lastRunStore.SetLastRunUtc(cadenceKey, _clock.UtcNow);
                if (retryAfterKey != null) await _lastRunStore.Clear(retryAfterKey);
                _logger.LogWarning($"{description} was refused, and retrying cannot fix that, so it will wait for the "
                    + "normal interval rather than re-asking every cycle. Fix the permission (see the error above); "
                    + "the retry then happens on the next scheduled run. To force it sooner: where Storage is configured, "
                    + $"delete the row '{cadenceKey}' (partition 'ImportSchedule') from the 'AnalyticsState' table in the "
                    + "solution's storage account (restarting the web-job does not clear it); without Storage "
                    + "the time is held in memory and a restart of the web-job clears it.");
                return;
            }

            if (retryAfterKey != null && transientBackoff > TimeSpan.Zero)
            {
                var retryAt = _clock.UtcNow + transientBackoff;
                await _lastRunStore.SetLastRunUtc(retryAfterKey, retryAt);
                _logger.LogWarning($"{description} did not fully succeed, so it has NOT been recorded as up to date. It will "
                    + $"be retried after {retryAt:u} rather than on every cycle, because each attempt repeats its reads. "
                    + $"To retry sooner: where Storage is configured, delete the row '{retryAfterKey}' (partition "
                    + "'ImportSchedule') from the 'AnalyticsState' table; without Storage, restart the web-job.");
                return;
            }

            _logger.LogWarning($"{description} did not fully succeed, so it has NOT been recorded as up to date and "
                + "will be retried on the next cycle.");
        }
    }
}
