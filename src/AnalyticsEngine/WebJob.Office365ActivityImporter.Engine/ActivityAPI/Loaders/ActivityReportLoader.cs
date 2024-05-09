using Common.DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders
{
    /// <summary>
    /// Loads activity data from the Activity API
    /// https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-reference
    /// </summary>
    public class ActivityReportWebLoader : IActivityReportLoader<ActivityReportInfo>
    {
        private AutoThrottleHttpClient _httpClient;
        private readonly ILogger _telemetry;
        private readonly string _tenantId;
        public ActivityReportWebLoader(AutoThrottleHttpClient httpClient, ILogger telemetry, string tenantId)
        {
            _httpClient = httpClient;
            _telemetry = telemetry;
            _tenantId = tenantId;
        }

        /// <summary>
        /// Load full activity reports from summary links
        /// </summary>
        public async Task<ActivityReportSet> Load(ActivityReportInfo metadata)
        {
            // Apply the PublisherIdentifier value as a parameter to each audit event fetch from the API
            var newUri = metadata.ContentUri.ToString() + "?PublisherIdentifier=" + _tenantId;

            // Get the HTTP response from Activity API. Block if a reauth request is happening though. 
            HttpResponseMessage response = null;
            try
            {
                response = await _httpClient.GetAsyncWithThrottleRetries(newUri, _telemetry);
            }
            catch (HttpRequestException ex)
            {
                _telemetry.LogError(ex, $"Got error '{ex.Message}' downloading {metadata.ContentUri}. Will try again on next cycle.");
                return new WebActivityReportSet();
            }

            // Otherwise parse response
            var jSonBody = await response.Content.ReadAsStringAsync();

            var logs = new WebActivityReportSet();

            // A report download can have multiple reports in a Json array.
            var allReportsData = Newtonsoft.Json.Linq.JArray.Parse(jSonBody);
            var reportsArray = allReportsData.Children();
            foreach (var reportItem in reportsArray)
            {
                var logJson = reportItem.ToString();
                var logBase = JsonConvert.DeserializeObject<WorkloadOnlyAuditLogContent>(logJson);
                AbstractAuditLogContent thisAuditLogReport = null;

                // Determine which deserialization to use, depending on the workload
                if (logBase.Workload == ActivityImportConstants.WORKLOAD_SP || logBase.Workload == ActivityImportConstants.WORKLOAD_OD)
                {
                    thisAuditLogReport = JsonConvert.DeserializeObject<SharePointAuditLogContent>(logJson);
                }
                else if (logBase.Workload == ActivityImportConstants.WORKLOAD_EXCHANGE)
                {
                    thisAuditLogReport = JsonConvert.DeserializeObject<ExchangeAuditLogContent>(logJson);
                }
                else if (logBase.Workload == ActivityImportConstants.WORKLOAD_AZURE_AD)
                {
                    thisAuditLogReport = JsonConvert.DeserializeObject<AzureADAuditLogContent>(logJson);
                }
                else if (logBase.Workload == ActivityImportConstants.WORKLOAD_STREAM)
                {
                    thisAuditLogReport = JsonConvert.DeserializeObject<StreamAuditLogContent>(logJson);
                }
                else if (logBase.Workload == ActivityImportConstants.WORKLOAD_COPILOT)
                {
                    try
                    {
                        thisAuditLogReport = JsonConvert.DeserializeObject<CopilotAuditLogContent>(logJson);
                        var asCopilotReport = (CopilotAuditLogContent)thisAuditLogReport;
                        asCopilotReport.EventRaw = JsonConvert.SerializeObject(asCopilotReport.CopilotEventData);
                    }
                    catch (JsonReaderException)
                    {
                        Console.WriteLine($"Failed to deserialize Copilot log: {logJson}");
                        throw;
                    }
                }

                if (thisAuditLogReport != null)
                {
                    logs.Add(thisAuditLogReport);
                }
            }

            logs.OriginalMetadata = metadata;
            foreach (var log in logs)
            {
                // Save original file content for each item, so we don't have to associated a content-set on save
                log.OriginalImportFileContents = jSonBody;
            }

            return logs;
        }
    }
}
