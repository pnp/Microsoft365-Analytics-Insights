using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
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
        private int _reportDownloadErrors = 0;

        private static readonly char[] _debugTraceFileNameInvalidChars = new char[] { '@', '.', ':', ';', '/', '\\', '"', '\'', '*', '?', '<', '>', '|' };

        public ActivityReportWebLoader(AutoThrottleHttpClient httpClient, ILogger telemetry, string tenantId)
        {
            _httpClient = httpClient;
            _telemetry = telemetry;
            _tenantId = tenantId;
        }

        /// <summary>
        /// Gets the count of report download errors that occurred
        /// </summary>
        public int ReportDownloadErrorCount => _reportDownloadErrors;

        /// <summary>
        /// Load full activity reports from summary links
        /// </summary>
        public async Task<ActivityReportSet> Load(ActivityReportInfo metadata)
        {
            // Apply the PublisherIdentifier value as a parameter to each audit event fetch from the API
            var newUri = $"{metadata.ContentUri}?PublisherIdentifier={_tenantId}";

            HttpResponseMessage response = null;
            try
            {
                response = await _httpClient.GetAsyncWithThrottleRetries(newUri, _telemetry);
            }
            catch (HttpRequestException ex)
            {
                Interlocked.Increment(ref _reportDownloadErrors);
                _telemetry.LogError(ex, $"Got error '{ex.Message}' downloading {metadata.ContentUri}. Will try again on next cycle.");
                return new WebActivityReportSet();
            }

            // Use 'using' to ensure response is disposed and memory is freed
            using (response)
            {
                var logs = new WebActivityReportSet();

                // Stream one JSON object at a time from the response, so the entire array is never materialised
                // in memory at once. Each JObject is processed and dropped before the next is read.
                try
                {
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var streamReader = new StreamReader(stream))
                    using (var jsonReader = new JsonTextReader(streamReader))
                    {
                        // Advance to the start of the array
                        while (await jsonReader.ReadAsync() && jsonReader.TokenType != JsonToken.StartArray)
                        {
                            // Skip any preamble (whitespace, etc.). If the response isn't a JSON array we'll
                            // fall through to the EndOfFile / TokenType-mismatch path below.
                        }

                        if (jsonReader.TokenType != JsonToken.StartArray)
                        {
                            // Empty or non-array response - treat as no data, consistent with prior behaviour
                            // when JArray.LoadAsync returned an empty/null result.
                            return logs;
                        }

                        while (await jsonReader.ReadAsync() && jsonReader.TokenType != JsonToken.EndArray)
                        {
                            if (jsonReader.TokenType != JsonToken.StartObject)
                            {
                                continue;
                            }

                            JObject reportItem;
                            try
                            {
                                reportItem = await JObject.LoadAsync(jsonReader);
                            }
                            catch (JsonReaderException ex)
                            {
                                // A single malformed object will desync the reader; bail out of the stream
                                // so we don't accidentally mis-parse subsequent siblings.
                                Interlocked.Increment(ref _reportDownloadErrors);
                                _telemetry.LogWarning($"Invalid JSON object in stream from URL '{newUri}': {ex.Message}. Aborting stream for this batch.");
                                break;
                            }

                            ProcessReportItem(reportItem, logs);
                        }
                    }
                }
                catch (OutOfMemoryException)
                {
                    Interlocked.Increment(ref _reportDownloadErrors);
                    _telemetry.LogError($"Out of memory streaming response from {metadata.ContentUri}. Will try again on next cycle.");
                    return new WebActivityReportSet();
                }
                catch (JsonReaderException ex)
                {
                    Interlocked.Increment(ref _reportDownloadErrors);
                    _telemetry.LogWarning($"Invalid JSON response for URL '{newUri}': {ex.Message}. Ignoring");
                    return new WebActivityReportSet();
                }

                logs.OriginalMetadata = metadata;
                // Note: We're NOT storing the JSON string in each log item to avoid multiplying memory usage.
                // The OriginalImportFileContents is not persisted to the database, only used during processing.
                // If needed for debugging, it could be stored once at the ActivityReportSet level instead.

                return logs;
            }
        }

        private void ProcessReportItem(JObject reportItem, WebActivityReportSet logs)
        {
            AbstractAuditLogContent thisAuditLogReport = null;
            WorkloadOnlyAuditLogContent logBase = null;

            #region Debug Disk Logging if Configure

            // Only convert to string if trace logging is enabled to save memory
            string logJson = null;
            if (!string.IsNullOrWhiteSpace(AuditTraceConfig.TraceEmail) && !string.IsNullOrWhiteSpace(AuditTraceConfig.TraceDirectory))
            {
                try
                {
                    logJson = reportItem.ToString();
                    if (logJson.IndexOf(AuditTraceConfig.TraceEmail, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var safeEmail = AuditTraceConfig.TraceEmail.Trim().ToLower();
                        // Sanitize for filesystem
                        foreach (var c in _debugTraceFileNameInvalidChars)
                        {
                            safeEmail = safeEmail.Replace(c, '_');
                        }
                        var fileName = $"audit_trace_{safeEmail}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}_{DateTime.UtcNow.Ticks % 1000000}.json";
                        var fullPath = Path.Combine(AuditTraceConfig.TraceDirectory, fileName);
                        File.WriteAllText(fullPath, logJson);
                        _telemetry.LogInformation($"TRACE: Saved matching audit log to '{fullPath}'.");
                    }
                }
                catch (Exception ex)
                {
                    _telemetry.LogWarning($"TRACE: Failed to write trace audit log file: {ex.Message}");
                }
            }
            #endregion

            // Deserialize directly from JToken to determine workload type
            try
            {
                logBase = reportItem.ToObject<WorkloadOnlyAuditLogContent>();
            }
            catch (JsonSerializationException)
            {
                return; // Skip this report if we can't determine workload
            }

            if (logBase == null)
            {
                return;
            }

            // Determine which deserialization to use, depending on the workload
            // Deserialize directly from JToken to avoid creating intermediate string
            try
            {
                if (logBase.Workload == ActivityImportConstants.WORKLOAD_SP || logBase.Workload == ActivityImportConstants.WORKLOAD_OD)
                {
                    thisAuditLogReport = reportItem.ToObject<SharePointAuditLogContent>();
                }
                else if (logBase.Workload == ActivityImportConstants.WORKLOAD_EXCHANGE)
                {
                    thisAuditLogReport = reportItem.ToObject<ExchangeAuditLogContent>();
                }
                else if (logBase.Workload == ActivityImportConstants.WORKLOAD_AZURE_AD)
                {
                    thisAuditLogReport = reportItem.ToObject<AzureADAuditLogContent>();
                }
                else if (logBase.Workload == ActivityImportConstants.WORKLOAD_STREAM)
                {
                    thisAuditLogReport = reportItem.ToObject<StreamAuditLogContent>();
                }
                else if (logBase.Workload == ActivityImportConstants.WORKLOAD_COPILOT)
                {
                    // Convert to string only if needed for Copilot's custom parser
                    if (logJson == null)
                    {
                        logJson = reportItem.ToString();
                    }
                    thisAuditLogReport = CopilotAuditLogContent.FromJson(logJson);
                }
            }
            catch (JsonReaderException ex)
            {
                _telemetry.LogWarning($"Failed to deserialize {logBase.Workload} log: {ex.Message}");
                return;
            }
            catch (JsonSerializationException ex)
            {
                _telemetry.LogWarning($"Failed to deserialize {logBase.Workload} log: {ex.Message}");
                return;
            }

            if (thisAuditLogReport != null)
            {
                logs.Add(thisAuditLogReport);
            }
        }
    }
}
