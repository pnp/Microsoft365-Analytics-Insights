using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{
    #region Lookup tables

    /// <summary>
    /// A Power Platform environment (shared by Power Apps and Power Automate flows).
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
    }

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
    }

    /// <summary>
    /// Lookup of how a flow was triggered ("Manual", "Recurrence", "Automated", ...)
    /// </summary>
    [Table("flow_recurrence_types")]
    public class FlowRecurrenceType : AbstractEFEntityWithName
    {
    }

    #endregion

    #region Per-event metadata

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

        [ForeignKey(nameof(RecurrenceType))]
        [Column("recurrence_type_id")]
        public int? RecurrenceTypeId { get; set; }
        public FlowRecurrenceType RecurrenceType { get; set; }
    }

    /// <summary>
    /// Per-event metadata for an audit_events row of workload 'PowerPlatformAdmin'
    /// (DLP policy changes, environment create/delete, connector governance, etc.).
    /// </summary>
    [Table("event_meta_power_platform_admin")]
    public class PowerPlatformAdminEventMetadata : BaseOfficeEvent
    {
        [ForeignKey(nameof(Environment))]
        [Column("environment_id")]
        public int? EnvironmentId { get; set; }
        public PowerAppEnvironment Environment { get; set; }

        /// <summary>
        /// The raw event JSON – admin events are low-volume but highly heterogeneous,
        /// so we keep the body for forensic queries.
        /// </summary>
        [Column("json")]
        public string Json { get; set; }
    }

    #endregion
}
