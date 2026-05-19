namespace WebJob.Office365ActivityImporter.Engine
{
    public static class ActivityImportConstants
    {

        // workload strings - https://msdn.microsoft.com/en-us/office-365/office-365-management-activity-api-schema
        public static string WORKLOAD_SP { get { return "SharePoint"; } }

        public static string WORKLOAD_OD { get { return "OneDrive"; } }

        public static string WORKLOAD_EXCHANGE { get { return "Exchange"; } }

        public static string WORKLOAD_AZURE_AD { get { return "AzureActiveDirectory"; } }

        public static string WORKLOAD_COPILOT { get { return "Copilot"; } }
        public static string WORKLOAD_DLP { get { return "DLP"; } }
        public static string WORKLOAD_TEAMS { get { return "MicrosoftTeams"; } }
        public static string WORKLOAD_STREAM { get { return "MicrosoftStream"; } }

        // Power Platform - all delivered via the Audit.General content-type subscription.
        public static string WORKLOAD_POWER_APPS { get { return "PowerApps"; } }
        public static string WORKLOAD_POWER_AUTOMATE { get { return "MicrosoftFlow"; } }
        public static string WORKLOAD_POWER_BI { get { return "PowerBI"; } }
        public static string WORKLOAD_COPILOT_STUDIO { get { return "MicrosoftCopilotStudio"; } }
        public static string WORKLOAD_DATAVERSE { get { return "Dynamics365"; } }

        /// <summary>
        /// New unified Power Platform admin activity workload (RecordType 256,
        /// type=PowerPlatformAdministratorActivityRecord). Carries its event data inside a
        /// PropertyCollection of OpenTelemetry-style key/value pairs rather than as top-level
        /// fields, so it needs its own deserialisation path before being mapped to the
        /// workload-specific content classes.
        /// </summary>
        public static string WORKLOAD_POWER_PLATFORM { get { return "PowerPlatform"; } }

        /// <summary>
        /// Property names inside a PowerPlatformAdministratorActivityRecord PropertyCollection.
        /// </summary>
        public static class PowerPlatformProps
        {
            public const string ResourceType = "powerplatform.analytics.resource.type";

            // Power Apps
            public const string PowerAppId = "powerplatform.analytics.resource.power_app.id";
            public const string PowerAppDisplayName = "powerplatform.analytics.resource.power_app.display_name";

            // Power Automate / Cloud Flows. Microsoft has not yet published a confirmed
            // sample; these keys mirror the power_app.* naming convention and are best-effort.
            public const string CloudFlowId = "powerplatform.analytics.resource.cloud_flow.id";
            public const string CloudFlowDisplayName = "powerplatform.analytics.resource.cloud_flow.display_name";

            // Environment + correlation
            public const string EnvironmentId = "powerplatform.analytics.resource.environment.id";
            public const string EnvironmentName = "powerplatform.analytics.resource.environment.name";
            public const string CorrelationId = "powerplatform.analytics.correlation.id";

            // User agent (mirrors OpenTelemetry semantic convention)
            public const string UserAgent = "user_agent.original";

            // Share-event properties (SharePowerApp etc.). Microsoft hasn't published a confirmed
            // sample for the unified schema yet - these keys mirror the OpenTelemetry naming
            // convention used for the other resources and are best-effort. Capture a real
            // SharePowerApp event and verify them before relying on share data.
            public const string PrincipalId = "powerplatform.analytics.resource.principal.id";
            public const string PrincipalName = "powerplatform.analytics.resource.principal.name";
            public const string PrincipalType = "powerplatform.analytics.resource.principal.type";
            public const string RoleName = "powerplatform.analytics.resource.role.name";
        }

        /// <summary>
        /// Values of the "powerplatform.analytics.resource.type" property.
        /// </summary>
        public static class PowerPlatformResourceTypes
        {
            public const string PowerApp = "PowerApp";

            /// <summary>
            /// Power Automate cloud flow. Awaiting a confirmed sample event - see PowerPlatformProps.
            /// </summary>
            public const string CloudFlow = "CloudFlow";
        }

        /// <summary>
        /// Operation names emitted on PowerPlatformAdministratorActivityRecord events. Only the
        /// operations listed here are persisted; anything else (edit / publish / delete /
        /// future activities) is intentionally dropped until we have a confirmed sample and a
        /// real downstream consumer for it.
        /// </summary>
        public static class PowerPlatformOps
        {
            public const string LaunchPowerApp = "LaunchPowerApp";

            /// <summary>
            /// Share / permission-grant for a Power App. The unified schema's exact activity name
            /// is best-effort - capture a real sample and confirm. We also accept the legacy names
            /// (ShareApp / AddPermissionsToApp / EditPowerAppRolePermission) case-insensitively.
            /// </summary>
            public const string SharePowerApp = "SharePowerApp";

            /// <summary>
            /// Activity names we treat as Power App share / permission-grant events. Matched
            /// case-insensitively against <c>powerplatform.analytics.activity.name</c> / Operation.
            /// </summary>
            public static readonly string[] PowerAppShareOps = new[]
            {
                SharePowerApp,
                "ShareApp",
                "AddPermissionsToApp",
                "EditPowerAppRolePermission",
            };

            public static bool IsPowerAppShareOp(string operation)
            {
                if (string.IsNullOrEmpty(operation)) return false;
                foreach (var op in PowerAppShareOps)
                {
                    if (string.Equals(operation, op, System.StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Operation names we persist on the legacy 'PowerBI' workload. Microsoft emits a long
        /// tail of audit operations on this workload (Login, AddDatasetUser, PublishReport, ...)
        /// but most do not carry the workspace/report metadata we depend on, and would otherwise
        /// land NULL-FK rows in <c>event_meta_power_bi</c>. We deliberately persist only the
        /// operations listed here.
        /// </summary>
        public static class PowerBIOps
        {
            /// <summary>
            /// Report-view event - the only Power BI operation we currently persist. Carries
            /// WorkspaceId / WorkspaceName / ReportId / ReportName / ReportType.
            /// </summary>
            public const string ViewReport = "ViewReport";

            public static bool IsSupported(string operation)
            {
                return string.Equals(operation, ViewReport, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        public static string PARAM_WEBHOOK_OVERRIDE { get { return "--webhook"; } }
        public static string PARAM_CALL_ID { get { return "--callId"; } }
        // New params for tracing audit log imports containing a specific email address
        public static string PARAM_TRACE_AUDIT_EMAIL { get { return "--traceAuditEmail"; } }
        public static string PARAM_TRACE_AUDIT_DIR { get { return "--traceAuditDir"; } }

        public const string STAGING_TABLE_VARNAME = "${STAGING_TABLE_ACTIVITY}";

        /// <summary>
        /// TeamsMeeting
        /// </summary>
        public static string COPILOT_CONTEXT_TYPE_TEAMS_MEETING { get { return "TeamsMeeting"; } }

        /// <summary>
        /// TeamsChat
        /// </summary>
        public static string COPILOT_CONTEXT_TYPE_TEAMS_CHAT { get { return "TeamsChat"; } }

#if DEBUG
        public const string STAGING_TABLE_ACTIVITY = "debug_import_staging_event_lookups";
#else
        public const string STAGING_TABLE_ACTIVITY = "##import_staging_event_lookups";
#endif


#if DEBUG
        public const string STAGING_TABLE_ACTIVITY_SP = "debug_import_staging_events_sp";
#else
        public const string STAGING_TABLE_ACTIVITY_SP = "##import_staging_event_lookups";
#endif


#if DEBUG
        public const string STAGING_TABLE_COPILOT_SP = "debug_import_staging_copilot_sp";
#else
        public const string STAGING_TABLE_COPILOT_SP = "##debug_import_staging_copilot_sp";
#endif

#if DEBUG
        public const string STAGING_TABLE_COPILOT_TEAMS = "debug_import_staging_copilot_teams";
#else
        public const string STAGING_TABLE_COPILOT_TEAMS = "##debug_import_staging_copilot_teams";
#endif

#if DEBUG
        public const string STAGING_TABLE_COPILOT_SIMPLE = "debug_import_staging_copilot_simple";
#else
        public const string STAGING_TABLE_COPILOT_SIMPLE = "##debug_import_staging_copilot_simple";
#endif

#if DEBUG
        public const string STAGING_TABLE_COPILOT_CHATONLY = "debug_import_staging_copilot_chatonly";
#else
        public const string STAGING_TABLE_COPILOT_CHATONLY = "##debug_import_staging_copilot_chatonly";
#endif

#if DEBUG
        public const string STAGING_TABLE_POWER_APP = "debug_import_staging_power_app";
        public const string STAGING_TABLE_POWER_APP_SHARE = "debug_import_staging_power_app_share";
        public const string STAGING_TABLE_POWER_AUTOMATE = "debug_import_staging_power_automate";
        public const string STAGING_TABLE_POWER_AUTOMATE_SHARE = "debug_import_staging_power_automate_share";
        public const string STAGING_TABLE_POWER_BI = "debug_import_staging_power_bi";
        public const string STAGING_TABLE_COPILOT_STUDIO = "debug_import_staging_copilot_studio";
        public const string STAGING_TABLE_DATAVERSE = "debug_import_staging_dataverse";
#else
        public const string STAGING_TABLE_POWER_APP = "##import_staging_power_app";
        public const string STAGING_TABLE_POWER_APP_SHARE = "##import_staging_power_app_share";
        public const string STAGING_TABLE_POWER_AUTOMATE = "##import_staging_power_automate";
        public const string STAGING_TABLE_POWER_AUTOMATE_SHARE = "##import_staging_power_automate_share";
        public const string STAGING_TABLE_POWER_BI = "##import_staging_power_bi";
        public const string STAGING_TABLE_COPILOT_STUDIO = "##import_staging_copilot_studio";
        public const string STAGING_TABLE_DATAVERSE = "##import_staging_dataverse";
#endif
    }
}
