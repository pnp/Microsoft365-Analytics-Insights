using Common.Entities.Installer;
using Common.UsageReporting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{

    public abstract class BaseUsageStatsBuilder
    {
        protected readonly ILogger _tracer;
        protected readonly Guid _tenantId;

        protected BaseUsageStatsBuilder(ILogger tracer, Guid tenantId)
        {
            _tracer = tracer;
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
}
