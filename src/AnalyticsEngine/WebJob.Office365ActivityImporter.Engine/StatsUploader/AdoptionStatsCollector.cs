using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.Installer;
using Common.Entities.LicenceActivity;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using UsageReporting;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    /// <summary>
    /// The two heavy reads the adoption block is built from, behind a seam so the collector's gating
    /// and failure behaviour can be tested without a database.
    /// </summary>
    public interface IAdoptionAnalysisSource
    {
        Task<CopilotAdoptionSummary> GetCopilotSummary(CancellationToken cancellationToken);

        Task<LicenceActivityOverview> GetLicenceOverview(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Builds the anonymised adoption block for the telemetry payload: decides whether it is due,
    /// whether the deployment can produce it at all, runs the analysis under a time cap, and maps the
    /// result through <see cref="AnonAdoptionStatsMapper"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cadence.</b> The Copilot analysis is the most expensive read in the product - many queries,
    /// each allowed up to 90 seconds - so it runs WEEKLY rather than on the daily telemetry cycle. On
    /// the other six days this returns null and the caller re-sends the block it stored last time,
    /// stamped with the date it was actually computed.
    /// </para>
    /// <para>
    /// <b>Concurrency.</b> The interactive path deliberately overlaps two analysis steps to shorten a
    /// user's wait. This one does not: nobody is waiting, and the importer shares a database with the
    /// portal and every other query, so a background job should take the cheapest path rather than the
    /// fastest one.
    /// </para>
    /// <para>
    /// <b>Fixed window.</b> The window comes from <see cref="CopilotAdoptionOptions.Default"/>, never
    /// from whatever an operator last chose in the UI. Figures from different tenants are only
    /// comparable if they cover the same period.
    /// </para>
    /// </remarks>
    public class AdoptionStatsCollector : IAnonAdoptionStatsProvider
    {
        /// <summary>Cadence-gate key. Shares the store used by the Graph import gates.</summary>
        public const string LastRunKey = "telemetryAdoptionStatsLastRun";

        /// <summary>Weekly.</summary>
        public const int CadenceHours = 168;

        /// <summary>
        /// Hard ceiling on the whole analysis. A struggling tenant must not stall the import cycle:
        /// telemetry is the least important thing this process does.
        /// </summary>
        public static readonly TimeSpan MaxAnalysisTime = TimeSpan.FromMinutes(30);

        private readonly IAdoptionAnalysisSource _source;
        private readonly IImportLastRunStore _lastRunStore;
        private readonly ISkuAllowList _skuAllowList;
        private readonly ImportTaskSettings _settings;
        private readonly ILogger _logger;

        public AdoptionStatsCollector(
            AppConfig config,
            IImportLastRunStore lastRunStore,
            ILogger logger)
            : this(
                new SqlAdoptionAnalysisSource(config),
                lastRunStore,
                new EmbeddedCsvSkuAllowList(),
                config?.ImportJobSettings,
                logger)
        {
        }

        internal AdoptionStatsCollector(
            IAdoptionAnalysisSource source,
            IImportLastRunStore lastRunStore,
            ISkuAllowList skuAllowList,
            ImportTaskSettings settings,
            ILogger logger)
        {
            _source = source;
            _lastRunStore = lastRunStore;
            _skuAllowList = skuAllowList;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// Whether this deployment imports enough to measure Copilot adoption at all.
        /// </summary>
        /// <remarks>
        /// Mirrors the portal's own availability rule (<c>CopilotAdoptionAPIController.Availability</c>):
        /// without user metadata there is no way to know who holds a seat, and without at least one
        /// Copilot signal there is no usage to measure.
        /// </remarks>
        internal static bool CanMeasureAdoption(ImportTaskSettings settings)
        {
            if (settings == null) return false;

            return settings.GraphUsersMetadata
                   && (settings.Copilot || settings.GraphCopilotUsageReports);
        }

        public async Task<AnonAdoptionStats> GetAdoptionStats(BaseSolutionInstallConfig lastSettings)
        {
            try
            {
                var settings = _settings ?? lastSettings?.SolutionConfig?.ImportTaskSettings;

                if (!CanMeasureAdoption(settings))
                {
                    _logger?.LogInformation(
                        $"{UsageStatsManager.LOG_PREFIX}adoption metrics need the user metadata import plus at least one "
                        + "Copilot import; not all are enabled, so none will be reported.");
                    return null;
                }

                var lastRun = await _lastRunStore.GetLastRunUtc(LastRunKey);
                if (!ImportCadenceGate.ShouldRun(lastRun, CadenceHours, force: false, nowUtc: DateTime.UtcNow))
                {
                    _logger?.LogInformation(
                        $"{UsageStatsManager.LOG_PREFIX}adoption metrics were last computed {lastRun} - next run is due "
                        + $"{CadenceHours}h after that. Re-sending the stored block for now.");
                    return null;
                }

                return await Analyse();
            }
            catch (Exception ex)
            {
                // Fail soft, always. The rest of the telemetry report is still worth uploading, and
                // telemetry must never be the reason an import cycle fails.
                _logger?.LogWarning(
                    ex,
                    $"{UsageStatsManager.LOG_PREFIX}could not build anonymised adoption metrics ({ex.Message}); "
                    + "the rest of the report will still be uploaded.");
                return null;
            }
        }

        private async Task<AnonAdoptionStats> Analyse()
        {
            var startedUtc = DateTime.UtcNow;
            _logger?.LogInformation($"{UsageStatsManager.LOG_PREFIX}computing anonymised adoption metrics...");

            using (var cts = new CancellationTokenSource(MaxAnalysisTime))
            {
                CopilotAdoptionSummary summary;
                LicenceActivityOverview overview;

                try
                {
                    // Sequential on purpose: see the class remarks on concurrency.
                    summary = await _source.GetCopilotSummary(cts.Token);
                    overview = await _source.GetLicenceOverview(cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    _logger?.LogWarning(
                        $"{UsageStatsManager.LOG_PREFIX}adoption analysis exceeded its {MaxAnalysisTime.TotalMinutes:N0} minute "
                        + "cap and was abandoned. It will be retried on the next cycle; no stats will be reported this time.");
                    return null;
                }

                var stats = AnonAdoptionStatsMapper.Map(
                    summary,
                    overview,
                    _skuAllowList,
                    startedUtc,
                    summary?.WindowDays ?? CopilotAdoptionOptions.Default.WindowDays);

                // Only stamp the cadence gate on success, so a failed run retries next cycle rather
                // than waiting out the full week.
                await _lastRunStore.SetLastRunUtc(LastRunKey, DateTime.UtcNow);

                _logger?.LogInformation(
                    $"{UsageStatsManager.LOG_PREFIX}anonymised adoption metrics computed in "
                    + $"{(DateTime.UtcNow - startedUtc).TotalSeconds:N0}s - {stats}");

                return stats;
            }
        }
    }

    /// <summary>
    /// Production <see cref="IAdoptionAnalysisSource"/>: the real Copilot adoption analysis and the
    /// real licence activity overview.
    /// </summary>
    internal class SqlAdoptionAnalysisSource : IAdoptionAnalysisSource
    {
        private readonly AppConfig _config;

        internal SqlAdoptionAnalysisSource(AppConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public async Task<CopilotAdoptionSummary> GetCopilotSummary(CancellationToken cancellationToken)
        {
            // maxConcurrentSteps: 1 - nobody is waiting on this, so do not double the load the
            // interactive path accepts in exchange for latency.
            var service = new CopilotAdoptionService(
                CopilotAdoptionOptions.Default, contextFactory: null, maxConcurrentSteps: 1);

            var analysis = await service.AnalyseAsync(cancellationToken: cancellationToken);
            return analysis?.Summary;
        }

        public async Task<LicenceActivityOverview> GetLicenceOverview(CancellationToken cancellationToken)
        {
            var settings = _config.ImportJobSettings ?? new ImportTaskSettings();
            var sources = new LicenceActivitySources
            {
                UserMetadata = settings.GraphUsersMetadata,
                UsageReports = settings.GraphUsageReports,
                CopilotUsageReports = settings.GraphCopilotUsageReports,
                CopilotAudit = settings.Copilot,
                CopilotInteractions = settings.CopilotInteractionHistory,
                UsageReportsGroupFiltered = !string.IsNullOrWhiteSpace(_config.UserGroupsFilter),
                NowUtc = DateTime.UtcNow,
            };

            if (!sources.UserMetadata)
            {
                // The store throws without it; the caller has already decided that is not fatal, but
                // there is no point provoking the exception.
                return null;
            }

            // Nulls for from/to give the default settled, week-aligned 28-day window - fixed, so the
            // figures mean the same thing on every tenant.
            var query = LicenceActivityQuery.Create(from: null, to: null, nowUtc: DateTime.UtcNow);

            var store = new SqlLicenceActivityStore(_config.ConnectionStrings.DatabaseConnectionString);
            return await store.LoadOverviewAsync(query, sources, null, cancellationToken);
        }
    }
}
