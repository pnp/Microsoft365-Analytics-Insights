using Common.Entities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    /// <summary>
    /// Audit-log payload for the 'PowerApps' workload.
    /// Schema: https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#power-apps-schema
    /// </summary>
    public class PowerAppsAuditLogContent : AbstractAuditLogContent
    {
        [JsonProperty("AppName")]
        public string AppName { get; set; }

        /// <summary>
        /// Human-readable display name for the app (when emitted).
        /// </summary>
        [JsonProperty("AppDisplayName")]
        public string AppDisplayName { get; set; }

        [JsonProperty("EnvironmentName")]
        public string EnvironmentName { get; set; }

        [JsonProperty("AppSessionId")]
        public string AppSessionId { get; set; }

        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            await saveBatch.PowerPlatformEventResolver.SaveSinglePowerAppEventToSqlStaging(this, relatedAuditEvent);
            return true;
        }
    }

    /// <summary>
    /// Audit-log payload for the 'MicrosoftFlow' workload.
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

        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            await saveBatch.PowerPlatformEventResolver.SaveSinglePowerAutomateEventToSqlStaging(this, relatedAuditEvent);
            return true;
        }
    }

    /// <summary>
    /// Audit-log payload for the 'PowerPlatformAdmin' workload (DLP policy changes,
    /// environment create/delete, connector governance, etc.).
    /// </summary>
    public class PowerPlatformAdminAuditLogContent : AbstractAuditLogContent
    {
        [JsonProperty("EnvironmentName")]
        public string EnvironmentName { get; set; }

        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            await saveBatch.PowerPlatformEventResolver.SaveSinglePowerPlatformAdminEventToSqlStaging(this, relatedAuditEvent);
            return true;
        }
    }
}
