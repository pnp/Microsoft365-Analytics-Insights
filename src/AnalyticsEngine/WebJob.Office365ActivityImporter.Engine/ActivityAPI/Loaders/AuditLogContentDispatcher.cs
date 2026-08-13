using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders
{
    /// <summary>
    /// Routes a raw audit-log JSON object to the workload-specific <see cref="AbstractAuditLogContent"/>
    /// subclass and applies per-workload validation/filtering (for example, PowerBI ViewReport-only
    /// and Power Automate records that identify a concrete flow).
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
        public static AbstractAuditLogContent Dispatch(JToken reportItem, WorkloadOnlyAuditLogContent logBase, ILogger logger, bool importPowerPlatform = true)
        {
            if (reportItem == null || logBase == null)
            {
                return null;
            }

            // Copilot Studio authoring records publish a top-level BotId. Route by that documented
            // record shape rather than relying exclusively on a workload string that Microsoft does
            // not publish in the Copilot Studio schema.
            var isCopilotStudioRecord = reportItem.Type == JTokenType.Object
                && !string.IsNullOrEmpty((string)reportItem["BotId"]);

            // Power Platform (the unified PowerPlatform record + the legacy per-product PowerApps / Power
            // Automate / Power BI schemas + Copilot Studio) all arrive via the Audit.General subscription.
            // When the workload is turned off, drop these events here so they are neither imported (no base
            // audit_events row) nor staged (no Power Platform merges) - the whole workload's cost is skipped.
            if (!importPowerPlatform
                && (isCopilotStudioRecord
                    || logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_PLATFORM
                    || logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_APPS
                    || logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_AUTOMATE
                    || logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_BI
                    || logBase.Workload == ActivityImportConstants.WORKLOAD_COPILOT_STUDIO))
            {
                return null;
            }

            // Workload string values below come from the Office 365 Management Activity API
            // Common schema (the "Workload" field) and are cross-checked against the
            // AuditLogRecordType enum. Authoritative reference:
            // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema

            // Workload "SharePoint" / "OneDrive" -> AuditLogRecordType 4 SharePoint, 6
            // SharePointFileOperation, 14 SharePointSharingOperation, 7 OneDrive.
            // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#auditlogrecordtype
            if (logBase.Workload == ActivityImportConstants.WORKLOAD_SP || logBase.Workload == ActivityImportConstants.WORKLOAD_OD)
            {
                return reportItem.ToObject<SharePointAuditLogContent>();
            }

            // Workload "Exchange" -> AuditLogRecordType 1 ExchangeAdmin, 2 ExchangeItem,
            // 3 ExchangeItemGroup, 19 ExchangeAggregatedOperation, 50 ExchangeItemAggregated.
            // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#auditlogrecordtype
            if (logBase.Workload == ActivityImportConstants.WORKLOAD_EXCHANGE)
            {
                return reportItem.ToObject<ExchangeAuditLogContent>();
            }

            // Workload "AzureActiveDirectory" (Microsoft Entra ID) -> AuditLogRecordType 8
            // AzureActiveDirectory and 15 AzureActiveDirectoryStsLogon.
            // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#auditlogrecordtype
            if (logBase.Workload == ActivityImportConstants.WORKLOAD_AZURE_AD)
            {
                return reportItem.ToObject<AzureADAuditLogContent>();
            }

            // Workload "MicrosoftStream" -> AuditLogRecordType 32 MicrosoftStream.
            // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#auditlogrecordtype
            if (logBase.Workload == ActivityImportConstants.WORKLOAD_STREAM)
            {
                return reportItem.ToObject<StreamAuditLogContent>();
            }

            // Workload "Copilot" (M365 Copilot user interactions) -> AuditLogRecordType 261
            // CopilotInteraction. The CopilotInteractionAuditRecord entity is explicitly
            // annotated WorkloadType=Copilot in the published EDM schema.
            // https://learn.microsoft.com/en-us/office/office-365-management-api/copilot-schema
            if (logBase.Workload == ActivityImportConstants.WORKLOAD_COPILOT)
            {
                // Copilot's custom parser takes a JSON string.
                return CopilotAuditLogContent.FromJson(reportItem.ToString());
            }

            if (isCopilotStudioRecord)
            {
                return reportItem.ToObject<CopilotStudioAuditLogContent>();
            }

            // Workload "PowerPlatform" -> AuditLogRecordType 256 PowerPlatformAdministratorActivity.
            // Unified Power Platform admin activity record where data lives in a
            // PropertyCollection (OpenTelemetry-style key/value pairs) rather than top-level
            // fields, so it needs its own deserialisation + mapping to a workload-specific
            // content class for the existing downstream save path to consume.
            // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#auditlogrecordtype
            if (logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_PLATFORM)
            {
                var ppRecord = reportItem.ToObject<PowerPlatformAdminActivityRecordContent>();
                if (ppRecord == null)
                {
                    return null;
                }

                var mapped = ppRecord.ToWorkloadSpecificContent(logger);

                if (mapped is PowerAutomateAuditLogContent
                    && !PowerPlatformAuditLogFilter.ShouldPersistPowerAutomateRecord((PowerAutomateAuditLogContent)mapped, logger))
                {
                    return null;
                }

                return mapped;
            }

            // Workload "PowerApps" -> AuditLogRecordType 45 PowerAppsApp (and 46 PowerAppsPlan,
            // 79 PowerAppsResource). Legacy per-product schema, delivered alongside the unified
            // PowerPlatform workload above.
            // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#auditlogrecordtype
            if (logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_APPS)
            {
                return reportItem.ToObject<PowerAppsAuditLogContent>();
            }

            // Workload "MicrosoftFlow" (Power Automate) -> AuditLogRecordType 30 MicrosoftFlow.
            // Current records expose lifecycle, connector, and permission metadata through
            // FlowDetailsUrl / FlowConnectorNames / RecipientUPN / SharingPermission.
            // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#auditlogrecordtype
            if (logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_AUTOMATE)
            {
                var flowRecord = reportItem.ToObject<PowerAutomateAuditLogContent>();
                return PowerPlatformAuditLogFilter.ShouldPersistPowerAutomateRecord(flowRecord, logger)
                    ? flowRecord
                    : null;
            }

            // Workload "PowerBI" -> AuditLogRecordType 20 PowerBIAudit. We only persist ViewReport:
            // the PowerBI workload emits a long tail of operations (Login, AddDatasetUser,
            // PublishReport, ...) but most do not carry the WorkspaceId/ReportId we depend on,
            // so they would otherwise land NULL-FK rows in event_meta_power_bi.
            // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#auditlogrecordtype
            // Power BI service-specific schema:
            // https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#power-bi-schema
            if (logBase.Workload == ActivityImportConstants.WORKLOAD_POWER_BI)
            {
                if (!ActivityImportConstants.PowerBIOps.IsSupported(logBase.Operation))
                {
                    return null;
                }
                return reportItem.ToObject<PowerBIAuditLogContent>();
            }

            // Retain the legacy workload route for records that identify the product by workload.
            // Current records with BotId are routed by shape above.
            // https://learn.microsoft.com/en-us/microsoft-copilot-studio/admin-logging-copilot-studio
            if (logBase.Workload == ActivityImportConstants.WORKLOAD_COPILOT_STUDIO)
            {
                return reportItem.ToObject<CopilotStudioAuditLogContent>();
            }

            // Unknown workload - nothing to do.
            return null;
        }
    }
}
