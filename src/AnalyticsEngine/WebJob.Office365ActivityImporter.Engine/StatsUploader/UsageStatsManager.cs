using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    /// <summary>
    /// Handles stats gathering & uploading. 
    /// </summary>
    public class UsageStatsManager
    {
        public const string LOG_PREFIX = "Usage Stats Reporter - ";
        private readonly BaseUsageStatsBuilder _statsBuilder;
        private readonly IStatsDatesLoader _statsDatesLoader;
        private readonly IStatsUploader _statsUploader;
        private readonly ILogger _tracer;
        private static TimeSpan MIN_WAIT = TimeSpan.FromDays(1);

        public UsageStatsManager(BaseUsageStatsBuilder statsBuilder, IStatsDatesLoader statsDatesLoader, IStatsUploader statsUploader, ILogger tracer)
        {
            _statsBuilder = statsBuilder;
            _statsDatesLoader = statsDatesLoader;
            _statsUploader = statsUploader;
            _tracer = tracer;
        }

        /// <summary>
        /// Execute and catch any exception
        /// </summary>
        public async Task<bool> ProcessAndFailSilently()
        {
            try
            {
                return await ProcessAndUploadStats();
            }
            catch (Exception ex)
            {
                _tracer.LogWarning($"{LOG_PREFIX}error uploading anonymised runtime stats - {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ProcessAndUploadStats()
        {
            var lastSettings = await _statsBuilder.GetLastAppliedSolutionConfig();
            if (lastSettings != null)
            {
                var lastStatsUpload = await _statsDatesLoader.GetLastUploadDt();
                if (lastStatsUpload == null || DateTime.Now.Subtract(lastStatsUpload.Value) > MIN_WAIT)
                {
                    if (lastSettings.AllowTelemetry)
                    {
                        if (lastStatsUpload.HasValue)
                        {
                            _tracer.LogInformation($"{LOG_PREFIX}Last usage report was uploaded {lastStatsUpload.Value} - uploading new report now");
                        }
                        else
                        {
                            _tracer.LogInformation($"{LOG_PREFIX}no last telemetry date found in redis - uploading new report now");
                        }

                        var latestStats = await _statsBuilder.LoadUsageStatsModel(lastSettings);
                        if (latestStats != null)
                        {
                            await _statsUploader.UploadToServer(latestStats);
                            _tracer.LogInformation($"{LOG_PREFIX}uploaded stats to server");

                            await _statsBuilder.SaveUsageStatsModelToDatabase(latestStats);
                            _tracer.LogInformation($"{LOG_PREFIX}saved latest stats to database - see table 'sys_telemetry_reports'");

                            // Remember last upload dt
                            await _statsDatesLoader.RegisterLastUploadDt();
                            return true;
                        }
                        else
                            _tracer.LogInformation($"{LOG_PREFIX}Invalid stats loaded from system. Will ignore.");
                    }
                    else
                    {
                        _tracer.LogInformation($"{LOG_PREFIX}usage reports are disabled");
                    }
                }
                else
                {
                    _tracer.LogInformation($"{LOG_PREFIX}telemetry stats uploaded recently (less than {MIN_WAIT.TotalHours} hours ago) - skipping for now");
                }
            }
            else
                _tracer.LogInformation($"{LOG_PREFIX}invalid solution configuration found in DB. Cannot continue.");
            return false;
        }
    }
}
