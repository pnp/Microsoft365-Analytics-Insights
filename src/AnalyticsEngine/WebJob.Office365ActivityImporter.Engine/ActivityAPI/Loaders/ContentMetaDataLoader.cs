using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{
    /// <summary>
    /// Loads activity summary objects for a given time-period.
    /// </summary>
    public abstract class ContentMetaDataLoader<SUMMARYTYPE>
    {
        protected readonly ILogger _logger;
        protected readonly AppConfig _settings;

        protected ContentMetaDataLoader(ILogger logger, AppConfig settings)
        {
            _logger = logger;
            _settings = settings;
        }

        /// <summary>
        /// Load all summaries for a specific content type & time.
        /// </summary>
        protected abstract Task<List<SUMMARYTYPE>> LoadAllActivityReports(string auditContentType, TimePeriod chunk, int batchId);

        /// <summary>
        /// Enumerates the period of time were retrieving metadata for bearing in mind the configuration
        /// and the maximum chunk size and earliest date supported by the API
        /// </summary>
        public List<TimePeriod> GetScanningTimeChunksFromNow()
        {
            var daysToAdd = -1;
            if (_settings.DaysBeforeNowToDownload > 1)
            {
                daysToAdd = _settings.DaysBeforeNowToDownload * -1;
            }
            var extractStart = DateTime.UtcNow.AddDays(daysToAdd);
            return TimePeriod.GetScanningTimeChunksFrom(extractStart, DateTime.UtcNow, _settings.TimeChunkOverlapMinutes);
        }

        /// <summary>
        /// Fetch all the metadata from the service in time chunk sized peices, but return it as a single stream.
        /// It will request metadata for the next time chunk asychronously while the prevoious one is being processed.
        /// Sometimes a single time chunk will come back in pages requiring several loops
        /// </summary>
        public async Task<List<SUMMARYTYPE>> GetChangesSummary(List<string> active, List<TimePeriod> timeChunks)
        {
            // Request URL template
            // Reference: https://msdn.microsoft.com/en-us/office-365/office-365-management-activity-api-reference

            var allResults = new List<SUMMARYTYPE>();

            if (timeChunks.Count == 0)
            {
                _logger.LogWarning("Audit events import: ERROR: Could not download activity - no time-chunks for activity scanning using configured values.");
            }
            else
            {
                // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-reference
                _logger.LogInformation($"Audit events import: getting changes summary from Office 365 Activity API from '{timeChunks.First().Start}' to '{timeChunks.Last().End}'...");

                int batchId = 0;
                var downloadListThreads = new List<Task<List<SUMMARYTYPE>>>();

                // Cap simultaneous fetches to avoid burst-throttling when contentTypes × timeChunks is large.
                var concurrencyLimit = _settings.MaxSummaryFetchConcurrency > 0 ? _settings.MaxSummaryFetchConcurrency : 8;
                using (var fetchGate = new SemaphoreSlim(concurrencyLimit, concurrencyLimit))
                {
                    // For every valid content type in the configuration
                    foreach (var auditContentType in active)
                    {
                        // For every time chunk we need
                        foreach (var chunk in timeChunks)
                        {
                            batchId++;

                            var capturedContentType = auditContentType;
                            var capturedChunk = chunk;
                            var capturedBatchId = batchId;

                            // Create new downloader async, gated by the concurrency semaphore
                            var loaderThread = Task.Run(async () =>
                            {
                                await fetchGate.WaitAsync();
                                try
                                {
                                    return await LoadAllActivityReports(capturedContentType, capturedChunk, capturedBatchId);
                                }
                                finally
                                {
                                    fetchGate.Release();
                                }
                            });

                            // Add task to list to wait for
                            downloadListThreads.Add(loaderThread);
                        }
                    }

                    // Wait for all the selects to finish
                    await Task.WhenAll(downloadListThreads);
                }

                // Combine results
                foreach (var t in downloadListThreads)
                {
                    if (t.Result.Count > 0)
                    {
                        allResults.AddRange(t.Result);
                    }
                }
            }

            return allResults;
        }
    }

    public abstract class BaseActivityReportInfo
    {
        [JsonProperty("contentCreated")]
        public DateTime Created { get; set; }

        /// <summary>
        /// Stable id of this content blob, used by the blob-level checkpoint to skip re-downloading blobs
        /// already fully committed in a previous cycle. Null when the summary type has no such id (the
        /// checkpoint then simply never skips it). Concrete summary types (e.g. <c>ActivityReportInfo</c>)
        /// override this to expose their content id.
        /// </summary>
        [JsonIgnore]
        public virtual string BlobId => null;
    }
}
