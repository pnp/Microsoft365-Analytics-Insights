using Common.Entities.Config;
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
                    return new AzureTableProcessedBlobStore(storageConn, retention, logger);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Blob checkpoint: could not initialise the Azure Table store; falling back to in-memory (persists only while this process runs).");
                }
            }
            else
            {
                logger?.LogInformation("Blob checkpoint: no storage connection string configured; using an in-memory store (persists across cycles only while this process runs).");
            }

            return new InMemoryProcessedBlobStore(retention);
        }
    }
}
