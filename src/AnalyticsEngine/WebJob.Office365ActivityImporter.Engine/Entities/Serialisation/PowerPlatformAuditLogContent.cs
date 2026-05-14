using Common.Entities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    /// <summary>
    /// One recipient entry on a Power Apps / Power Automate share or permission-grant event.
    /// Schema: https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema
    /// </summary>
    public class PowerPlatformPermissionEntry
    {
        [JsonProperty("PrincipalObjectId")]
        public string PrincipalObjectId { get; set; }

        [JsonProperty("PrincipalName")]
        public string PrincipalName { get; set; }

        [JsonProperty("PrincipalType")]
        public string PrincipalType { get; set; }

        [JsonProperty("RoleName")]
        public string RoleName { get; set; }
    }

    /// <summary>
    /// A connector binding emitted on Power Apps / Power Automate publish / save events.
    /// </summary>
    public class PowerPlatformConnectionRef
    {
        [JsonProperty("ConnectorName")]
        public string ConnectorName { get; set; }

        [JsonProperty("DisplayName")]
        public string DisplayName { get; set; }
    }

    /// <summary>
    /// Audit-log payload for the 'PowerApps' workload (launch, edit, publish, share, delete).
    /// Schema: https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#power-apps-schema
    /// </summary>
    public class PowerAppsAuditLogContent : AbstractAuditLogContent
    {
        [JsonProperty("AppName")]
        public string AppName { get; set; }

        /// <summary>Human-readable display name for the app (when emitted).</summary>
        [JsonProperty("AppDisplayName")]
        public string AppDisplayName { get; set; }

        [JsonProperty("EnvironmentName")]
        public string EnvironmentName { get; set; }

        [JsonProperty("AppSessionId")]
        public string AppSessionId { get; set; }

        /// <summary>"Canvas", "ModelDriven", "TeamsApp", "Portal".</summary>
        [JsonProperty("AppType")]
        public string AppType { get; set; }

        /// <summary>"Mobile", "Web", "Desktop", "Teams" - derived/normalised by sender.</summary>
        [JsonProperty("ClientType")]
        public string ClientType { get; set; }

        [JsonProperty("UserAgent")]
        public string UserAgent { get; set; }

        /// <summary>Connector bindings as currently configured on the app (publish events).</summary>
        [JsonProperty("ConnectionReferences")]
        public List<PowerPlatformConnectionRef> ConnectionReferences { get; set; }

        /// <summary>Recipients of share / permission-grant events.</summary>
        [JsonProperty("Permissions")]
        public List<PowerPlatformPermissionEntry> Permissions { get; set; }

        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            await saveBatch.PowerPlatformEventResolver.SaveSinglePowerAppEventToSqlStaging(this, relatedAuditEvent);
            return true;
        }
    }

    /// <summary>
    /// Audit-log payload for the 'MicrosoftFlow' workload (create, edit, run, share, delete).
    /// Schema: https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#microsoft-flow-schema
    /// </summary>
    public class PowerAutomateAuditLogContent : AbstractAuditLogContent
    {
        [JsonProperty("FlowId")]
        public string FlowId { get; set; }

        [JsonProperty("FlowDisplayName")]
        public string FlowDisplayName { get; set; }

        [JsonProperty("EnvironmentName")]
        public string EnvironmentName { get; set; }

        [JsonProperty("RecurrenceType")]
        public string RecurrenceType { get; set; }

        [JsonProperty("RunId")]
        public string RunId { get; set; }

        /// <summary>Connectors used by this flow (populated on save / publish events).</summary>
        [JsonProperty("ConnectionReferences")]
        public List<PowerPlatformConnectionRef> ConnectionReferences { get; set; }

        /// <summary>Recipients on share / permission-grant events.</summary>
        [JsonProperty("Permissions")]
        public List<PowerPlatformPermissionEntry> Permissions { get; set; }

        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            await saveBatch.PowerPlatformEventResolver.SaveSinglePowerAutomateEventToSqlStaging(this, relatedAuditEvent);
            return true;
        }
    }

    /// <summary>
    /// Audit-log payload for the 'PowerBI' workload (ViewReport, ViewDashboard, CreateReport, ShareReport).
    /// Schema: https://learn.microsoft.com/en-us/power-bi/enterprise/service-admin-auditing
    /// </summary>
    public class PowerBIAuditLogContent : AbstractAuditLogContent
    {
        [JsonProperty("WorkspaceId")]
        public string WorkspaceId { get; set; }

        [JsonProperty("WorkSpaceName")]
        public string WorkspaceName { get; set; }

        [JsonProperty("ReportId")]
        public string ReportId { get; set; }

        [JsonProperty("ReportName")]
        public string ReportName { get; set; }

        /// <summary>"PowerBIReport", "PaginatedReport".</summary>
        [JsonProperty("ReportType")]
        public string ReportType { get; set; }

        [JsonProperty("DashboardId")]
        public string DashboardId { get; set; }

        [JsonProperty("DashboardName")]
        public string DashboardName { get; set; }

        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            await saveBatch.PowerPlatformEventResolver.SaveSinglePowerBIEventToSqlStaging(this, relatedAuditEvent);
            return true;
        }
    }

    /// <summary>
    /// Audit-log payload for the 'MicrosoftCopilotStudio' workload (bot created, published, message sent).
    /// </summary>
    public class CopilotStudioAuditLogContent : AbstractAuditLogContent
    {
        [JsonProperty("BotId")]
        public string BotId { get; set; }

        [JsonProperty("BotName")]
        public string BotName { get; set; }

        [JsonProperty("EnvironmentName")]
        public string EnvironmentName { get; set; }

        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            await saveBatch.PowerPlatformEventResolver.SaveSingleCopilotStudioEventToSqlStaging(this, relatedAuditEvent);
            return true;
        }
    }

    /// <summary>
    /// Audit-log payload for the 'Dynamics365' workload (Dataverse CreateRecord / UpdateRecord / DeleteRecord).
    /// Captures depth-of-engagement signal for Dataverse.
    /// </summary>
    public class DataverseAuditLogContent : AbstractAuditLogContent
    {
        [JsonProperty("EnvironmentName")]
        public string EnvironmentName { get; set; }

        [JsonProperty("EntityName")]
        public string EntityName { get; set; }

        [JsonProperty("RecordId")]
        public string RecordId { get; set; }

        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            await saveBatch.PowerPlatformEventResolver.SaveSingleDataverseEventToSqlStaging(this, relatedAuditEvent);
            return true;
        }
    }
}
