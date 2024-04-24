namespace App.ControlPanel.Engine
{
    public class InstallerConstants
    {
        public static string PARAM_INITDB => "--initdb";
        public static string PARAM_REGISTERCONFIG => "--registerconfig";

        public static int EVENT_LOG_CATEGORY_ID => 42920;

        public static string FILENAME_ZIP_WEBJOB_APPINSIGHTS => "AppInsightsImporter.zip";
        public static string FILENAME_ZIP_WEBSITE => "Website.zip";
        public static string FILENAME_ZIP_WEBJOB_ACTIVITY => "Office365ActivityImporter.zip";
        public static string FILENAME_ZIP_AITRACKER => "AITrackerInstaller.zip";
        public static string FILENAME_ZIP_CONTROL_PANEL => "ControlPanelApp.zip";

        public static string FILENAME_PS_PROFILING_SUB_DIR => @"AutomationPS\\ProfilingJobs";

        public static string FILENAME_PS_PROFILING_AUTOMATION_Weekly => "Weekly.ps1";
        public static string FILENAME_PS_PROFILING_AUTOMATION_AggregationStatus => "Aggregation_Status.ps1";
        public static string FILENAME_PS_PROFILING_AUTOMATION_DatabaseMaintenance => "Database_Maintenance.ps1";

        public static string FILENAME_EXE_INSTALLER => "AnalyticsInstaller.exe";

        public static string MasterBuildBlobPrefix => "master - build";


        public const string AI_TRACKER_FILE_TITLE = "aitracker.js";
        public const string AI_TRACKER_SPFX_FILE_TITLE = "spoinsights-modern-ui-aitracker.sppkg";

        public const string TEMPLATE_APPSTORE = "APPCATALOG";
    }
}
