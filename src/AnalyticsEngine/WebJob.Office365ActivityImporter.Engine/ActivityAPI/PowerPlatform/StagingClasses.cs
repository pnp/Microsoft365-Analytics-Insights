using DataUtils.Sql;
using System;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform
{
    /// <summary>
    /// Staging row for a single PowerApps audit event.
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_POWER_APP)]
    internal class PowerAppLogTempEntity
    {
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("app_id", true)]
        public string AppId { get; set; }

        [Column("app_name", true)]
        public string AppName { get; set; }

        [Column("environment_id", true)]
        public string EnvironmentId { get; set; }

        [Column("app_session_id", true)]
        public string AppSessionId { get; set; }
    }

    /// <summary>
    /// Staging row for a single Microsoft Flow (Power Automate) audit event.
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_POWER_AUTOMATE)]
    internal class PowerAutomateFlowLogTempEntity
    {
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("flow_id", true)]
        public string FlowId { get; set; }

        [Column("flow_name", true)]
        public string FlowName { get; set; }

        [Column("environment_id", true)]
        public string EnvironmentId { get; set; }

        [Column("run_id", true)]
        public string RunId { get; set; }

        [Column("recurrence_type", true)]
        public string RecurrenceType { get; set; }
    }

    /// <summary>
    /// Staging row for a single Power Platform admin audit event.
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_POWER_PLATFORM_ADMIN)]
    internal class PowerPlatformAdminLogTempEntity
    {
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("environment_id", true)]
        public string EnvironmentId { get; set; }

        [Column("event_json", true)]
        public string EventJson { get; set; }
    }
}
