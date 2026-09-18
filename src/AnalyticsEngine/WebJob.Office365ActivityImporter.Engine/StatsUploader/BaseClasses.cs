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
    }
}
