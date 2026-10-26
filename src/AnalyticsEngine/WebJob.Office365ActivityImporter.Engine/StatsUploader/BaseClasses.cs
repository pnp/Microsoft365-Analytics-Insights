using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using UsageReporting;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{

    public abstract class BaseUsageStatsBuilder
    {
        protected readonly ILogger _logger;
        protected readonly Guid _tenantId;

        protected BaseUsageStatsBuilder(ILogger logger, Guid tenantId)
        {
            _logger = logger;
            _tenantId = tenantId;
        }

        public abstract Task<BaseSolutionInstallConfig> GetLastAppliedSolutionConfig();

        public abstract Task<AnonUsageStatsModel> LoadUsageStatsModel(BaseSolutionInstallConfig lastSettings);
        public abstract Task SaveUsageStatsModelToDatabase(AnonUsageStatsModel latestStats);

        /// <summary>
        /// Called once a report has been both uploaded and stored. Anything that must only count as delivered when
        /// the report carrying it was - the weekly adoption block above all - is committed here, not when it is built.
        /// </summary>
        public virtual Task OnReportDeliveredAsync(AnonUsageStatsModel deliveredStats) => Task.CompletedTask;
    }

    public interface IStatsDatesLoader
    {
        Task<DateTime?> GetLastUploadDt();

        Task RegisterLastUploadDt();
    }

    public interface IStatsUploader
    {
        Task UploadToServer(AnonUsageStatsModel stats);
    }

    /// <summary>
    /// Supplies the anonymised adoption block for a telemetry payload, or null when there is nothing
    /// new to report.
    /// </summary>
    /// <remarks>
    /// Null is a normal, expected answer, not a failure: the analysis is expensive so it runs weekly
    /// while the payload uploads daily, the deployment may not import what the analysis needs, and a
    /// failed or timed-out analysis must never stop the rest of the report being uploaded. The caller
    /// falls back to the last block it stored.
    /// </remarks>
    public interface IAnonAdoptionStatsProvider
    {
        Task<AnonAdoptionStats> GetAdoptionStats(BaseSolutionInstallConfig lastSettings);

        /// <summary>
        /// The report carrying <paramref name="delivered"/> has been uploaded and stored. Until this is called a
        /// freshly computed block has not been delivered, so it is offered again rather than counted as done.
        /// </summary>
        Task AcknowledgeDeliveredAsync(AnonAdoptionStats delivered);
    }
}
