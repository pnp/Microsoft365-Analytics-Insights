using Common.Entities.Config;
using DataUtils;
using DataUtils.Health;
using Microsoft.Extensions.Logging;
using System;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint
{
    /// <summary>
    /// Builds the <see cref="IProcessedBlobStore"/> for a run: an Azure Table-backed store when a storage
    /// connection string is configured (durable across process restarts), otherwise an in-memory store that
    /// persists only for the life of this (long-running) process. The checkpoint is deliberately kept OUT of
    /// the analytics SQL database.
    /// </summary>
    public static class ProcessedBlobStoreFactory
    {
        public static IProcessedBlobStore Create(AppConfig config, ILogger logger)
        {
            // Retain checkpoint entries a little longer than the API lookback window - a blob older than
            // that can never be re-listed, so it is safe to forget (keeps the store bounded).
            int lookbackDays = config != null && config.DaysBeforeNowToDownload > 0 ? config.DaysBeforeNowToDownload : 7;
            var retention = TimeSpan.FromDays(lookbackDays + 1);

            var storageConn = config?.ConnectionStrings?.StorageConnectionString;
            if (!string.IsNullOrWhiteSpace(storageConn))
            {
                try
                {
                    var store = new AzureTableProcessedBlobStore(storageConn, retention, logger);
                    (logger as AnalyticsLogger)?.TrackHealthCheck(HealthComponent.BlobCheckpoint, HealthStatus.Healthy,
                        "Durable Azure Table checkpoint active.");
                    return store;
                }
                catch (Exception ex)
                {
                    // Inline the exception detail (type + flattened inner-exception chain) into the message
                    // itself. The ILogger/App Insights provider routes the exception object to a separate
                    // 'exception' telemetry item, so a trace-only log export shows this line with no reason
                    // otherwise. Matches the rest of the codebase (e.g. ActivityReportLoader). The likely
                    // causes are called out so operators can act without pulling the exceptions table.
                    var detail = ex.Message;
                    for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                        detail += " -> " + inner.Message;
                    logger?.LogError(ex, $"Blob checkpoint: could not initialise the Azure Table store ({ex.GetType().Name}: {detail}); " +
                        "falling back to in-memory (dedupes across cycles but only for the life of this process - lost on restart/redeploy). " +
                        "Check the Storage connection string is valid and the account's Table service is reachable: " +
                        "shared-key access enabled, and storage firewall / private endpoint / selected-networks not blocking the importer.");
                    // Surface the degraded (non-durable) checkpoint on the Health page instead of only in the log.
                    (logger as AnalyticsLogger)?.TrackHealthCheck(HealthComponent.BlobCheckpoint, HealthStatus.Degraded,
                        $"Azure Table init failed ({ex.GetType().Name}); using non-durable in-memory checkpoint (lost on restart). See importer error log.");
                }
            }
            else
            {
                logger?.LogInformation("Blob checkpoint: no storage connection string configured; using an in-memory store (persists across cycles only while this process runs).");
                (logger as AnalyticsLogger)?.TrackHealthCheck(HealthComponent.BlobCheckpoint, HealthStatus.Degraded,
                    "No Storage connection string configured; using non-durable in-memory checkpoint (lost on restart).");
            }

            return new InMemoryProcessedBlobStore(retention);
        }
    }
}
