using Common.Entities.Config;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{
    /// <summary>
    /// Activity API implementation for ActivitySummaryLoader
    /// </summary>
    public class WebContentMetaDataLoader : ContentMetaDataLoader<ActivityReportInfo>
    {
        private readonly ConfidentialClientApplicationThrottledHttpClient _httpClient;
        private int _metadataDownloadErrors = 0;

        public WebContentMetaDataLoader(ILogger logger, ConfidentialClientApplicationThrottledHttpClient httpClient, AppConfig settings) : base(logger, settings)
        {
            _httpClient = httpClient;
        }

        /// <summary>
        /// Gets the count of metadata download errors that occurred
        /// </summary>
        public int MetadataDownloadErrorCount => _metadataDownloadErrors;

        /// <summary>
        /// Get all metadata for an event query URL
        /// </summary>
        /// <returns>List of events</returns>
        protected override async Task<List<ActivityReportInfo>> LoadAllActivityReports(string auditContentType, TimePeriod chunk, int batchId)
        {
            // Build the uri to download 
            // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-reference#list-available-content
            var metadataUri = $"https://manage.office.com/api/v1.0/{_settings.TenantGUID}" +
                $"/activity/feed/subscriptions/content?ContentType={auditContentType}&" +
                $"PublisherIdentifier={_settings.TenantGUID}&" +
                $"startTime={FormatDate(chunk.Start)}&" +
                $"endTime={FormatDate(chunk.End)}";

            var data = await DownloadMetadata(metadataUri, batchId);
#if DEBUG
            if (data.Count > 0)
            {
                Console.WriteLine($"DEBUG: GET METADATA {batchId}: {data.Count.ToString("N0")} change reports found between '{chunk.Start}'-'{chunk.End}'.");
            }
#endif
            return data;
        }

        /// <summary>
        /// Downloads change details for a change report, all pages
        /// </summary>
        public async Task<List<ActivityReportInfo>> DownloadMetadata(string changeReportUri, int batchId)
        {
            var allResults = new List<ActivityReportInfo>();
            string currentUri = changeReportUri;
            const string NEXT_PAGE_PARAM = "NextPageUri";

            while (!string.IsNullOrEmpty(currentUri))
            {
                string nextPageUri = null;

                // Get this batch. A timeout surfaces as a cancelled task (HttpClient.Timeout elapsed) and a
                // persistent HTTP error surfaces as HttpRequestException once throttle-retries are exhausted;
                // both are wrapped in the try so a single hung/failed page doesn't crash the whole import.
                HttpResponseMessage response = null;
                string responseFromServer = null;
                try
                {
                    response = await _httpClient.GetAsyncWithThrottleRetries(currentUri, _logger);

                    // Read the content.
                    responseFromServer = await response.Content.ReadAsStringAsync();

                    response.EnsureSuccessStatusCode();

                    // More data to get for events?
                    if (response.Headers.Contains(NEXT_PAGE_PARAM))
                    {
                        nextPageUri = response.Headers.GetValues(NEXT_PAGE_PARAM).First();
                    }
                }
                catch (HttpRequestException ex)
                {
                    Interlocked.Increment(ref _metadataDownloadErrors);
                    _logger.LogError(ex, $"Error downloading metadata {currentUri} with error '{ex.Message}'. " +
                        $"If this happens every time, this may be an issue. Ignoring for now.");
#if DEBUG
                    _logger.LogInformation("DEBUG: Response body was:\n" + responseFromServer);
#endif
                    break; // Exit the loop on error
                }
                catch (TaskCanceledException ex)
                {
                    // HTTP timeout (HttpClient.Timeout elapsed) surfaces as a cancelled task. Treat it as a
                    // transient download failure so a single hung request doesn't crash the whole import; the
                    // metadata is retried on the next cycle.
                    Interlocked.Increment(ref _metadataDownloadErrors);
                    _logger.LogError(ex, $"Timed out downloading metadata {currentUri}: '{ex.Message}'. Will try again on next cycle.");
                    break; // Exit the loop on error
                }
                finally
                {
                    response?.Dispose();
                }

                // Process the response for this URL
                if (!string.IsNullOrEmpty(responseFromServer))
                {
                    // Deserialise the results from the HTTP response
                    try
                    {
                        var responseMeta = JsonConvert.DeserializeObject<List<ActivityReportInfo>>(responseFromServer);

                        if (responseMeta != null && responseMeta.Count > 0)
                        {
                            // Add our own batch ID variable to each response
                            foreach (var metaData in responseMeta)
                            {
                                metaData.BatchID = batchId;
                            }

                            allResults.AddRange(responseMeta);
                        }
                    }
                    catch (JsonSerializationException)
                    {
                        _logger.LogError($"Could not deserialise to list of {nameof(ActivityReportInfo)} response: '{responseFromServer}'");
                    }
                }

                // Prepare next iteration
                if (!string.IsNullOrEmpty(nextPageUri))
                {
                    currentUri = $"{nextPageUri}&PublisherIdentifier={_settings.TenantGUID}";
                }
                else
                {
                    currentUri = null;
                }
            }

            return allResults;
        }

        private string FormatDate(DateTime d)
        {
            // Activity API format: YYYY-MM-DDTHH:MM:SS
            var utc = d.ToUniversalTime();
            return utc.ToString("yyyy-MM-ddTHH:mm:ss");
        }
    }

    /// <summary>
    /// Data class to deserialise the metadata into. 
    /// https://msdn.microsoft.com/en-us/office-365/office-365-management-activity-api-reference
    /// </summary>
    public class ActivityReportInfo : BaseActivityReportInfo
    {
        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("contentId")]
        public string ContentId { get; set; }

        [JsonProperty("contentUri")]
        public Uri ContentUri { get; set; }

        /// <summary>The Activity API content id is this blob's checkpoint key.</summary>
        [JsonIgnore]
        public override string BlobId => ContentId;


        /// <summary>
        /// The batch number this activity report was found on. Generated for a specific time-chunk.
        /// </summary>
        public int BatchID { get; set; }

        public override string ToString()
        {
            return $"{ContentType}: {Created}, ID:{ContentId}";
        }
    }
}
