using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{
    /// <summary>
    /// Loads activity summary objects for a given time-period.
    /// </summary>
    public abstract class ContentMetaDataLoader<SUMMARYTYPE>
    {
        protected readonly ILogger _telemetry;
        protected readonly AppConfig _settings;

        protected ContentMetaDataLoader(ILogger telemetry, AppConfig settings)
        {
            _telemetry = telemetry;
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
            return TimePeriod.GetScanningTimeChunksFrom(extractStart, DateTime.UtcNow);
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
                _telemetry.LogWarning("Audit events import: ERROR: Could not download activity - no time-chunks for activity scanning using configured values.");
            }
            else
            {
                // https://msdn.microsoft.com/en-us/office-365/office-365-management-activity-api-reference
                _telemetry.LogInformation($"Audit events import: getting changes summary from Office 365 Activity API from '{timeChunks.First().Start}' to '{timeChunks.Last().End}'...");

                int batchId = 0;
                var downloadListThreads = new List<Task<List<SUMMARYTYPE>>>();

                // For every valid content type in the configuration
                foreach (var auditContentType in active)
                {
                    // For every time chunk we need
                    foreach (var chunk in timeChunks)
                    {
                        batchId++;

                        // Create new downloader async
                        var loaderThread = LoadAllActivityReports(auditContentType, chunk, batchId);

                        // Add task to list to wait for
                        downloadListThreads.Add(loaderThread);
                    }
                }

                // Wait for all the selects to finish
                await Task.WhenAll(downloadListThreads);

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
    }
}
