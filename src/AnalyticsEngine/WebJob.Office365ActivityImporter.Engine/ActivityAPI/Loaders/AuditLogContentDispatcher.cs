using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders
{
    /// <summary>
    /// Routes a raw audit-log JSON object to the workload-specific <see cref="AbstractAuditLogContent"/>
    /// subclass and applies the per-workload operation filters (e.g. PowerBI ViewReport-only,
    /// Power Automate flow-run-only).
    ///
    /// The activity report loader cares about IO, retries and batching; this class owns the
    /// "what deserialisation does this workload need" decision so the two concerns stay separate
    /// and adding a new workload (or tightening an existing filter) is a one-place change.
    /// </summary>
    public static class AuditLogContentDispatcher
    {
        /// <summary>
        /// Deserialise <paramref name="reportItem"/> into the appropriate workload-specific content
        /// class. Returns null when the workload is unknown, when the operation is filtered out by
        /// a per-workload gate, or when the inner mapping (e.g. <see cref="PowerPlatformAdminActivityRecordContent.ToWorkloadSpecificContent"/>)
        /// declines the record.
        ///
        /// JSON deserialisation exceptions are intentionally not caught here - the caller already
        /// logs them with the originating workload name and continues to the next record.
        /// </summary>
        public static AbstractAuditLogContent Dispatch(JToken reportItem, WorkloadOnlyAuditLogContent logBase, ILogger logger)
        {
            if (reportItem == null || logBase == null)
            {
                return null;
            }

            if (logBase.Workload == ActivityImportConstants.WORKLOAD_SP || logBase.Workload == ActivityImportConstants.WORKLOAD_OD)
            {
                return reportItem.ToObject<SharePointAuditLogContent>();
            }

            if (logBase.Workload == ActivityImportConstants.WORKLOAD_EXCHANGE)
            {
                return reportItem.ToObject<ExchangeAuditLogContent>();
            }

            if (logBase.Workload == ActivityImportConstants.WORKLOAD_AZURE_AD)
            {
                return reportItem.ToObject<AzureADAuditLogContent>();
            }

            if (logBase.Workload == ActivityImportConstants.WORKLOAD_STREAM)
            {
                return reportItem.ToObject<StreamAuditLogContent>();
            }

            if (logBase.Workload == ActivityImportConstants.WORKLOAD_COPILOT)
            {
                // Copilot's custom parser takes a JSON string.
                return CopilotAuditLogContent.FromJson(reportItem.ToString());
            }

            if (logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_PLATFORM)
            {
                // Unified Power Platform admin activity record (RecordType 256). Data lives in a
                // PropertyCollection rather than top-level fields, so it needs its own
                // deserialisation + mapping to a workload-specific content class for the
                // existing downstream save path to consume.
                var ppRecord = reportItem.ToObject<PowerPlatformAdminActivityRecordContent>();
                if (ppRecord == null)
                {
                    return null;
                }

                var mapped = ppRecord.ToWorkloadSpecificContent(logger);

                // Power Automate: only persist flow-run events; lifecycle events are filtered
                // out by PowerPlatformAuditLogFilter.
                if (mapped is PowerAutomateAuditLogContent
                    && !PowerPlatformAuditLogFilter.ShouldPersistPowerAutomateOperation(logBase.Operation, logger))
                {
                    return null;
                }

                return mapped;
            }

            if (logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_APPS)
            {
                return reportItem.ToObject<PowerAppsAuditLogContent>();
            }

            if (logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_AUTOMATE)
            {
                // Legacy 'MicrosoftFlow' workload. Same flow-run-only gate as the unified
                // PowerPlatform schema above; filtered here so nothing reaches the staging tables.
                if (!PowerPlatformAuditLogFilter.ShouldPersistPowerAutomateOperation(logBase.Operation, logger))
                {
                    return null;
                }
                return reportItem.ToObject<PowerAutomateAuditLogContent>();
            }

            if (logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_BI)
            {
                // Only persist ViewReport. The PowerBI workload emits a long tail of operations
                // (Login, AddDatasetUser, PublishReport, ...) but most do not carry the
                // WorkspaceId/ReportId we depend on, so they would otherwise land NULL-FK rows
                // in event_meta_power_bi.
                if (!ActivityImportConstants.PowerBIOps.IsSupported(logBase.Operation))
                {
                    return null;
                }
                return reportItem.ToObject<PowerBIAuditLogContent>();
            }

            if (logBase.Workload == ActivityImportConstants.WORKLOAD_COPILOT_STUDIO)
            {
                return reportItem.ToObject<CopilotStudioAuditLogContent>();
            }

            if (logBase.Workload == ActivityImportConstants.WORKLOAD_DATAVERSE)
            {
                return reportItem.ToObject<DataverseAuditLogContent>();
            }

            // Unknown workload - nothing to do.
            return null;
        }
    }
}
