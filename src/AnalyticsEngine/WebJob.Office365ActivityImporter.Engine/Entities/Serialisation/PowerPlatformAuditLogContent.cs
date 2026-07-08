using Common.Entities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
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

        /// <summary>
        /// Legacy field that actually carries the environment GUID (the schema's name is misleading).
        /// Mapped to <c>power_app_environments.environment_id</c> downstream.
        /// </summary>
        [JsonProperty("EnvironmentName")]
        public string EnvironmentName { get; set; }

        /// <summary>
        /// Human-readable environment name (e.g. "dev-na-ba50088f"). Only populated by the unified
        /// PowerPlatformAdministratorActivityRecord schema; the legacy PowerApps schema never emits it.
        /// Mapped to <c>power_app_environments.name</c> downstream.
        /// </summary>
        [JsonIgnore]
        public string EnvironmentDisplayName { get; set; }

        [JsonProperty("AppSessionId")]
        public string AppSessionId { get; set; }

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

        /// <summary>
        /// Legacy field that actually carries the environment GUID (the schema's name is misleading).
        /// Mapped to <c>power_app_environments.environment_id</c> downstream.
        /// </summary>
        [JsonProperty("EnvironmentName")]
        public string EnvironmentName { get; set; }

        /// <summary>
        /// Human-readable environment name. Only populated by the unified
        /// PowerPlatformAdministratorActivityRecord schema; the legacy MicrosoftFlow schema never emits it.
        /// Mapped to <c>power_app_environments.name</c> downstream.
        /// </summary>
        [JsonIgnore]
        public string EnvironmentDisplayName { get; set; }

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
    /// A single OpenTelemetry-style key/value pair on a PowerPlatformAdministratorActivityRecord.
    /// </summary>
    public class PowerPlatformProperty
    {
        [JsonProperty("Name")]
        public string Name { get; set; }

        [JsonProperty("Value")]
        public string Value { get; set; }
    }

    /// <summary>
    /// Audit-log payload for the unified 'PowerPlatform' workload
    /// (RecordType 256 / type=PowerPlatformAdministratorActivityRecord).
    ///
    /// Unlike the legacy 'PowerApps' / 'MicrosoftFlow' workloads, this schema does not put the
    /// event data on top-level fields; instead it ships a PropertyCollection of OpenTelemetry
    /// semantic-convention key/value pairs (e.g. powerplatform.analytics.resource.power_app.id).
    /// The loader calls <see cref="ToWorkloadSpecificContent"/> to produce one of the existing
    /// workload-specific content classes so the downstream save path is unchanged.
    ///
    /// Schema: https://learn.microsoft.com/en-us/power-platform/admin/use-activity-logging
    /// </summary>
    public class PowerPlatformAdminActivityRecordContent : AbstractAuditLogContent
    {
        [JsonProperty("PropertyCollection")]
        public List<PowerPlatformProperty> PropertyCollection { get; set; }

        /// <summary>
        /// Case-insensitive lookup against PropertyCollection. Returns null when missing.
        /// </summary>
        public string GetProperty(string name)
        {
            if (PropertyCollection == null || string.IsNullOrEmpty(name)) return null;
            foreach (var p in PropertyCollection)
            {
                if (p != null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return p.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// Value of the powerplatform.analytics.resource.type property (e.g. "PowerApp", "CloudFlow").
        /// </summary>
        public string ResourceType => GetProperty(ActivityImportConstants.PowerPlatformProps.ResourceType);

        /// <summary>
        /// Map this admin-activity record to a workload-specific content class for downstream
        /// staging. Returns null when the resource type is unknown, the operation is one we
        /// don't yet persist (launch + share are supported today; everything else is dropped),
        /// or required fields are missing.
        /// </summary>
        public AbstractAuditLogContent ToWorkloadSpecificContent(ILogger logger)
        {
            var resourceType = ResourceType;
            if (string.Equals(resourceType, ActivityImportConstants.PowerPlatformResourceTypes.PowerApp, StringComparison.OrdinalIgnoreCase))
            {
                // We currently only persist Power App launch + share events. Other operations
                // (edit, publish, delete, ...) have a different downstream shape and no
                // verified sample, so we silently drop them rather than write half-mapped rows.
                var isLaunch = string.Equals(Operation, ActivityImportConstants.PowerPlatformOps.LaunchPowerApp, StringComparison.OrdinalIgnoreCase);
                var isShare = ActivityImportConstants.PowerPlatformOps.IsPowerAppShareOp(Operation);
                if (!isLaunch && !isShare)
                {
                    logger?.LogDebug($"PowerPlatform admin activity: ignoring PowerApp event with unsupported operation '{Operation}' (id='{Id}'). Only LaunchPowerApp + share operations are persisted today.");
                    return null;
                }
                return ToPowerAppsContent(logger, isShare);
            }
            if (string.Equals(resourceType, ActivityImportConstants.PowerPlatformResourceTypes.CloudFlow, StringComparison.OrdinalIgnoreCase))
            {
                return ToPowerAutomateContent(logger);
            }

            if (string.IsNullOrEmpty(resourceType))
            {
                logger?.LogDebug($"PowerPlatform admin activity: skipping record - no '{ActivityImportConstants.PowerPlatformProps.ResourceType}' property, so this event is not tied to a tracked Power App or Cloud Flow resource (operation='{Operation}', id='{Id}'). This is expected for operations like ApiEndpointCallEvent and connector-only events.");
            }
            else
            {
                logger?.LogDebug($"PowerPlatform admin activity: skipping record with unsupported resource type '{resourceType}' - only PowerApp and CloudFlow are persisted today (operation='{Operation}', id='{Id}').");
            }
            return null;
        }

        private PowerAppsAuditLogContent ToPowerAppsContent(ILogger logger, bool isShareEvent)
        {
            var appId = GetProperty(ActivityImportConstants.PowerPlatformProps.PowerAppId);
            if (string.IsNullOrEmpty(appId))
            {
                logger?.LogWarning($"PowerPlatform admin activity: PowerApp record is missing '{ActivityImportConstants.PowerPlatformProps.PowerAppId}' (operation='{Operation}', id='{Id}'). Skipping.");
                return null;
            }

            var mapped = new PowerAppsAuditLogContent
            {
                Id = this.Id,
                CreationTime = this.CreationTime,
                Operation = this.Operation,
                UserId = this.UserId,
                Workload = this.Workload,
                ObjectId = this.ObjectId,
                ItemType = this.ItemType,
                ExtendedProperties = this.ExtendedProperties,

                AppName = appId,
                AppDisplayName = GetProperty(ActivityImportConstants.PowerPlatformProps.PowerAppDisplayName),
                EnvironmentName = GetProperty(ActivityImportConstants.PowerPlatformProps.EnvironmentId),
                EnvironmentDisplayName = GetProperty(ActivityImportConstants.PowerPlatformProps.EnvironmentName),
                AppSessionId = GetProperty(ActivityImportConstants.PowerPlatformProps.CorrelationId),
                UserAgent = GetProperty(ActivityImportConstants.PowerPlatformProps.UserAgent),
            };

            if (isShareEvent)
            {
                // The unified schema is flat: one event == one recipient (unlike the legacy
                // schema's Permissions array). Property names are best-effort; if we can't
                // identify the recipient skip rather than write a row with no upn.
                var principalName = GetProperty(ActivityImportConstants.PowerPlatformProps.PrincipalName);
                var principalId = GetProperty(ActivityImportConstants.PowerPlatformProps.PrincipalId);
                var principalType = GetProperty(ActivityImportConstants.PowerPlatformProps.PrincipalType);
                var roleName = GetProperty(ActivityImportConstants.PowerPlatformProps.RoleName);

                if (string.IsNullOrEmpty(principalName))
                {
                    logger?.LogWarning($"PowerPlatform admin activity: share event for app '{appId}' is missing '{ActivityImportConstants.PowerPlatformProps.PrincipalName}' (operation='{Operation}', id='{Id}'). The share property names are best-effort - capture a real SharePowerApp event and verify them. Skipping.");
                    return null;
                }

                mapped.Permissions = new List<PowerPlatformPermissionEntry>
                {
                    new PowerPlatformPermissionEntry
                    {
                        PrincipalName = principalName,
                        PrincipalObjectId = principalId,
                        PrincipalType = principalType,
                        RoleName = roleName,
                    }
                };
            }

            return mapped;
        }

        private PowerAutomateAuditLogContent ToPowerAutomateContent(ILogger logger)
        {
            var flowId = GetProperty(ActivityImportConstants.PowerPlatformProps.CloudFlowId);
            if (string.IsNullOrEmpty(flowId))
            {
                logger?.LogWarning($"PowerPlatform admin activity: CloudFlow record is missing '{ActivityImportConstants.PowerPlatformProps.CloudFlowId}' (operation='{Operation}', id='{Id}'). The Power Automate property names are best-effort - capture an example and verify them. Skipping.");
                return null;
            }

            return new PowerAutomateAuditLogContent
            {
                Id = this.Id,
                CreationTime = this.CreationTime,
                Operation = this.Operation,
                UserId = this.UserId,
                Workload = this.Workload,
                ObjectId = this.ObjectId,
                ItemType = this.ItemType,
                ExtendedProperties = this.ExtendedProperties,

                FlowId = flowId,
                FlowDisplayName = GetProperty(ActivityImportConstants.PowerPlatformProps.CloudFlowDisplayName),
                EnvironmentName = GetProperty(ActivityImportConstants.PowerPlatformProps.EnvironmentId),
                EnvironmentDisplayName = GetProperty(ActivityImportConstants.PowerPlatformProps.EnvironmentName),
            };
        }

        /// <summary>
        /// Should never be invoked - the loader converts to a workload-specific content first.
        /// </summary>
        public override Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            logger?.LogWarning($"PowerPlatformAdminActivityRecordContent.ProcessExtendedProperties was called directly - this indicates a bug in the loader. ResourceType='{ResourceType}', Operation='{Operation}'.");
            return Task.FromResult(false);
        }
    }
}
