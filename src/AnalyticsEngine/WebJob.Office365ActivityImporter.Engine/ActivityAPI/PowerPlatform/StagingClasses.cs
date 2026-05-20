using DataUtils.Sql;
using System;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform
{
    /// <summary>
    /// Staging row for a single Power Apps audit event (launch / edit / publish / etc.).
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

        [Column("environment_name", true)]
        public string EnvironmentName { get; set; }

        [Column("app_session_id", true)]
        public string AppSessionId { get; set; }

        [Column("client_type", true)]
        public string ClientType { get; set; }

        [Column("connectors_csv", true)]
        public string ConnectorsCsv { get; set; }

        [Column("event_time")]
        public DateTime EventTime { get; set; }
    }

    /// <summary>
    /// Staging row for a single Power Apps share / permission-grant audit event.
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_POWER_APP_SHARE)]
    internal class PowerAppShareLogTempEntity
    {
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("app_id", true)]
        public string AppId { get; set; }

        [Column("shared_with_upn", true)]
        public string SharedWithUpn { get; set; }

        [Column("role_name", true)]
        public string RoleName { get; set; }
    }

    /// <summary>
    /// Staging row for a single Power Automate (Microsoft Flow) audit event.
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

        [Column("environment_name", true)]
        public string EnvironmentName { get; set; }

        [Column("run_id", true)]
        public string RunId { get; set; }

        [Column("connectors_csv", true)]
        public string ConnectorsCsv { get; set; }

        [Column("event_time")]
        public DateTime EventTime { get; set; }
    }

    /// <summary>
    /// Staging row for a single Power Automate flow share audit event.
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_POWER_AUTOMATE_SHARE)]
    internal class PowerAutomateFlowShareLogTempEntity
    {
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("flow_id", true)]
        public string FlowId { get; set; }

        [Column("shared_with_upn", true)]
        public string SharedWithUpn { get; set; }

        [Column("role_name", true)]
        public string RoleName { get; set; }
    }

    /// <summary>
    /// Staging row for a single Power BI audit event (report / dashboard view, share, create).
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_POWER_BI)]
    internal class PowerBILogTempEntity
    {
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("workspace_id", true)]
        public string WorkspaceId { get; set; }

        [Column("workspace_name", true)]
        public string WorkspaceName { get; set; }

        [Column("report_id", true)]
        public string ReportId { get; set; }

        [Column("report_name", true)]
        public string ReportName { get; set; }

        [Column("report_type", true)]
        public string ReportType { get; set; }

        [Column("dashboard_id", true)]
        public string DashboardId { get; set; }

        [Column("dashboard_name", true)]
        public string DashboardName { get; set; }

        [Column("event_time")]
        public DateTime EventTime { get; set; }
    }

    /// <summary>
    /// Staging row for a single Copilot Studio (formerly Power Virtual Agents) audit event.
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_COPILOT_STUDIO)]
    internal class CopilotStudioLogTempEntity
    {
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("bot_id", true)]
        public string BotId { get; set; }

        [Column("bot_name", true)]
        public string BotName { get; set; }

        [Column("environment_id", true)]
        public string EnvironmentId { get; set; }

        [Column("event_time")]
        public DateTime EventTime { get; set; }
    }

    /// <summary>
    /// Staging row for a single Dataverse audit event (CreateRecord / UpdateRecord / DeleteRecord).
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_DATAVERSE)]
    internal class DataverseLogTempEntity
    {
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("environment_id", true)]
        public string EnvironmentId { get; set; }

        [Column("entity_name", true)]
        public string EntityName { get; set; }

        [Column("record_id", true)]
        public string RecordId { get; set; }
    }
}
