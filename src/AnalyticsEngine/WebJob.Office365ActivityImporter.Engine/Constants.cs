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
