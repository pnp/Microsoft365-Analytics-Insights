using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{
    #region Shared lookups

    /// <summary>
    /// A Power Platform environment (shared by Power Apps, Power Automate flows, etc.).
    /// </summary>
    [Table("power_app_environments")]
    public class PowerAppEnvironment : AbstractEFEntity
    {
        [Column("environment_id")]
        [MaxLength(200)]
        public string EnvironmentId { get; set; }

        [Column("name")]
        [MaxLength(255)]
        public string Name { get; set; }
    }

    /// <summary>
    /// Lookup of client surfaces (Mobile / Web / Desktop / Teams).
    /// Drives the "% of Power App launches inside Teams" adoption KPI.
    /// </summary>
    [Table("power_platform_client_types")]
    public class PowerPlatformClientType : AbstractEFEntityWithName
    {
    }

    /// <summary>
    /// A connector available to Power Apps / Power Automate (SharePoint, Outlook, Teams, ...).
    /// The list is bounded (a few hundred publisher-defined connectors).
    /// </summary>
    [Table("power_platform_connectors")]
    public class PowerPlatformConnector : AbstractEFEntity
    {
        [Column("name")]
        [MaxLength(255)]
        public string Name { get; set; }

        [Column("publisher")]
        [MaxLength(255)]
        public string Publisher { get; set; }

        [Column("is_premium")]
        public bool? IsPremium { get; set; }
    }

    #endregion

    #region Power Apps

    /// <summary>
    /// A Power App. Lookup so each execution row can FK back here and we can see
    /// all the events / users that touched a given app.
    /// </summary>
    [Table("power_apps")]
    public class PowerApp : AbstractEFEntity
    {
        /// <summary>
        /// AppName from the audit-event payload (a GUID for canvas apps,
        /// or "msdyn_..." for model-driven apps).
        /// </summary>
        [Column("app_id")]
        [MaxLength(200)]
        public string AppId { get; set; }

        [Column("name")]
        [MaxLength(255)]
        public string Name { get; set; }

        [ForeignKey(nameof(Environment))]
        [Column("environment_id")]
        public int? EnvironmentId { get; set; }
        public PowerAppEnvironment Environment { get; set; }

        /// <summary>
        /// First time we saw this app in the audit feed - powers "new apps this month" reports.
        /// </summary>
        [Column("first_seen_at")]
        public DateTime? FirstSeenAt { get; set; }
    }

    /// <summary>
    /// Per-event metadata for an audit_events row of workload 'PowerApps'.
    /// "Who used which app, and when" = users JOIN audit_events JOIN event_meta_power_app JOIN power_apps.
    /// </summary>
    [Table("event_meta_power_app")]
    public class PowerAppEventMetadata : BaseOfficeEvent
    {
        [ForeignKey(nameof(PowerApp))]
        [Column("power_app_id")]
        public int? PowerAppId { get; set; }
        public PowerApp PowerApp { get; set; }

        /// <summary>
        /// Correlation id present on most PowerApps events (canvas-app session).
        /// </summary>
        [Column("app_session_id")]
        [MaxLength(200)]
        public string AppSessionId { get; set; }

        [ForeignKey(nameof(ClientType))]
        [Column("client_type_id")]
        public int? ClientTypeId { get; set; }
        public PowerPlatformClientType ClientType { get; set; }
    }

    /// <summary>
    /// One row per (audit event, recipient) for a Power App share / permission grant.
    /// </summary>
    [Table("event_meta_power_app_share")]
    public class PowerAppShareEventMetadata : AbstractEFEntity
    {
        [ForeignKey(nameof(AuditEvent))]
        [Column("event_id")]
        public Guid EventId { get; set; }
        public CommonAuditEvent AuditEvent { get; set; }

        [ForeignKey(nameof(PowerApp))]
        [Column("power_app_id")]
        public int? PowerAppId { get; set; }
        public PowerApp PowerApp { get; set; }

        [ForeignKey(nameof(SharedWithUser))]
        [Column("shared_with_user_id")]
        public int? SharedWithUserId { get; set; }
        public User SharedWithUser { get; set; }

        /// <summary>
        /// Role granted: "CanView", "CanEdit", "Owner", etc.
        /// </summary>
        [Column("role_name")]
        [MaxLength(100)]
        public string RoleName { get; set; }
    }

    /// <summary>
    /// Junction: which connectors a Power App currently uses (refreshed on publish events).
    /// </summary>
    [Table("power_app_connectors")]
    public class PowerAppConnector : AbstractEFEntity
    {
        [ForeignKey(nameof(PowerApp))]
        [Column("power_app_id")]
        public int PowerAppId { get; set; }
        public PowerApp PowerApp { get; set; }

        [ForeignKey(nameof(Connector))]
        [Column("connector_id")]
        public int ConnectorId { get; set; }
        public PowerPlatformConnector Connector { get; set; }
    }

    #endregion

    #region Power Automate

    /// <summary>
    /// A Power Automate (Flow) definition.
    /// </summary>
    [Table("power_automate_flows")]
    public class PowerAutomateFlow : AbstractEFEntity
    {
        /// <summary>
        /// FlowId from the audit-event payload.
        /// </summary>
        [Column("flow_id")]
        [MaxLength(200)]
        public string FlowId { get; set; }

        [Column("name")]
        [MaxLength(255)]
        public string Name { get; set; }

        [ForeignKey(nameof(Environment))]
        [Column("environment_id")]
        public int? EnvironmentId { get; set; }
        public PowerAppEnvironment Environment { get; set; }

        /// <summary>
        /// First time we saw this flow in the audit feed.
        /// </summary>
        [Column("first_seen_at")]
        public DateTime? FirstSeenAt { get; set; }
    }

    /// <summary>
    /// Per-event metadata for an audit_events row of workload 'MicrosoftFlow'.
    /// </summary>
    [Table("event_meta_power_automate_flow")]
    public class PowerAutomateFlowEventMetadata : BaseOfficeEvent
    {
        [ForeignKey(nameof(Flow))]
        [Column("flow_id")]
        public int? FlowId { get; set; }
        public PowerAutomateFlow Flow { get; set; }

        /// <summary>
        /// Per-execution correlation id from the flow run.
        /// </summary>
        [Column("run_id")]
        [MaxLength(200)]
        public string RunId { get; set; }
    }

    /// <summary>
    /// One row per (audit event, recipient) for a Power Automate flow share.
    /// </summary>
    [Table("event_meta_power_automate_flow_share")]
    public class PowerAutomateFlowShareEventMetadata : AbstractEFEntity
    {
        [ForeignKey(nameof(AuditEvent))]
        [Column("event_id")]
        public Guid EventId { get; set; }
        public CommonAuditEvent AuditEvent { get; set; }

        [ForeignKey(nameof(Flow))]
        [Column("flow_id")]
        public int? FlowId { get; set; }
        public PowerAutomateFlow Flow { get; set; }

        [ForeignKey(nameof(SharedWithUser))]
        [Column("shared_with_user_id")]
        public int? SharedWithUserId { get; set; }
        public User SharedWithUser { get; set; }

        [Column("role_name")]
        [MaxLength(100)]
        public string RoleName { get; set; }
    }

    /// <summary>
    /// Junction: which connectors a Power Automate flow currently uses.
    /// </summary>
    [Table("power_automate_flow_connectors")]
    public class PowerAutomateFlowConnector : AbstractEFEntity
    {
        [ForeignKey(nameof(Flow))]
        [Column("flow_id")]
        public int FlowId { get; set; }
        public PowerAutomateFlow Flow { get; set; }

        [ForeignKey(nameof(Connector))]
        [Column("connector_id")]
        public int ConnectorId { get; set; }
        public PowerPlatformConnector Connector { get; set; }
    }

    #endregion

    #region Power BI

    [Table("power_bi_workspaces")]
    public class PowerBIWorkspace : AbstractEFEntity
    {
        [Column("workspace_id")]
        [MaxLength(200)]
        public string WorkspaceId { get; set; }

        [Column("name")]
        [MaxLength(255)]
        public string Name { get; set; }
    }

    [Table("power_bi_reports")]
    public class PowerBIReport : AbstractEFEntity
    {
        [Column("report_id")]
        [MaxLength(200)]
        public string ReportId { get; set; }

        [Column("name")]
        [MaxLength(255)]
        public string Name { get; set; }

        [Column("report_type")]
        [MaxLength(100)]
        public string ReportType { get; set; }

        [ForeignKey(nameof(Workspace))]
        [Column("workspace_id")]
        public int? WorkspaceId { get; set; }
        public PowerBIWorkspace Workspace { get; set; }

        [Column("first_seen_at")]
        public DateTime? FirstSeenAt { get; set; }
    }

    [Table("power_bi_dashboards")]
    public class PowerBIDashboard : AbstractEFEntity
    {
        [Column("dashboard_id")]
        [MaxLength(200)]
        public string DashboardId { get; set; }

        [Column("name")]
        [MaxLength(255)]
        public string Name { get; set; }

        [ForeignKey(nameof(Workspace))]
        [Column("workspace_id")]
        public int? WorkspaceId { get; set; }
        public PowerBIWorkspace Workspace { get; set; }

        [Column("first_seen_at")]
        public DateTime? FirstSeenAt { get; set; }
    }

    /// <summary>
    /// Per-event metadata for an audit_events row of workload 'PowerBI'.
    /// A single table covers view/edit/create operations on reports + dashboards
    /// (either FK is optional - whichever was touched is populated).
    /// </summary>
    [Table("event_meta_power_bi")]
    public class PowerBIEventMetadata : BaseOfficeEvent
    {
        [ForeignKey(nameof(Workspace))]
        [Column("workspace_id")]
        public int? WorkspaceId { get; set; }
        public PowerBIWorkspace Workspace { get; set; }

        [ForeignKey(nameof(Report))]
        [Column("report_id")]
        public int? ReportId { get; set; }
        public PowerBIReport Report { get; set; }

        [ForeignKey(nameof(Dashboard))]
        [Column("dashboard_id")]
        public int? DashboardId { get; set; }
        public PowerBIDashboard Dashboard { get; set; }
    }

    #endregion

    #region Copilot Studio (formerly Power Virtual Agents)

    [Table("copilot_studio_bots")]
    public class CopilotStudioBot : AbstractEFEntity
    {
        [Column("bot_id")]
        [MaxLength(200)]
        public string BotId { get; set; }

        [Column("name")]
        [MaxLength(255)]
        public string Name { get; set; }

        [ForeignKey(nameof(Environment))]
        [Column("environment_id")]
        public int? EnvironmentId { get; set; }
        public PowerAppEnvironment Environment { get; set; }

        [Column("first_seen_at")]
        public DateTime? FirstSeenAt { get; set; }
    }

    /// <summary>
    /// Per-event metadata for an audit_events row of workload 'MicrosoftCopilotStudio'.
    /// </summary>
    [Table("event_meta_copilot_studio")]
    public class CopilotStudioEventMetadata : BaseOfficeEvent
    {
        [ForeignKey(nameof(Bot))]
        [Column("bot_id")]
        public int? BotId { get; set; }
        public CopilotStudioBot Bot { get; set; }
    }

    #endregion
}
