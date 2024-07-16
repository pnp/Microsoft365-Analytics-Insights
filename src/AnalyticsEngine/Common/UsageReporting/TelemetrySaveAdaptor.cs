using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace UsageReporting
{
    /// <summary>
    /// Something that save telemetry data somewhere
    /// </summary>
    public interface ITelemetrySaveAdaptor
    {
        Task<AnonUsageStatsModel> LoadCurrentRecordByClientId(AnonUsageStatsModel model);
        Task SaveOrUpdate(AnonUsageStatsModel newVersion);
    }

    public class StatsSaveService
    {
        private readonly ITelemetrySaveAdaptor _telemetrySaveAdaptor;
        private readonly ILogger _logger;

        public StatsSaveService(ITelemetrySaveAdaptor telemetrySaveAdaptor, ILogger logger)
        {
            _telemetrySaveAdaptor = telemetrySaveAdaptor;
            _logger = logger;
        }

        public async Task SaveOrUpdate(AnonUsageStatsModel updateFromClientWithNewId)
        {
            if (updateFromClientWithNewId is null)
            {
                throw new ArgumentNullException(nameof(updateFromClientWithNewId));
            }

            var report = await _telemetrySaveAdaptor.LoadCurrentRecordByClientId(updateFromClientWithNewId);
            if (report is null)
            {
                _logger.LogInformation($"Saving new stats from unseen client");
                await _telemetrySaveAdaptor.SaveOrUpdate(updateFromClientWithNewId);
            }
            else
            {
                // Merge reports
                _logger.LogInformation($"Updating stats from previous client");
                var newReport = report.UpdateWith(updateFromClientWithNewId);
                await _telemetrySaveAdaptor.SaveOrUpdate(newReport);
            }
        }
    }
}
