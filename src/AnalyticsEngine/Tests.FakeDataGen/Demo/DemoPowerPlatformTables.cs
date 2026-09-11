namespace Tests.FakeDataGen.Demo
{
    internal static partial class DemoTables
    {
        public static readonly DemoTable PowerEnvironments = T("power_app_environments", true,
            I("id"), N("environment_id", 200), N("name", 255));
        public static readonly DemoTable PowerClients = Named("power_platform_client_types");
        public static readonly DemoTable PowerConnectors = T("power_platform_connectors", true,
            I("id"), N("name", 255), N("publisher", 255), B("is_premium"));
        public static readonly DemoTable PowerApps = T("power_apps", true, I("id"), N("app_id", 200),
            N("name", 255), I("environment_id"), D("first_seen_at"));
        public static readonly DemoTable PowerFlows = T("power_automate_flows", true, I("id"), N("flow_id", 200),
            N("name", 255), I("environment_id"), D("first_seen_at"));
        public static readonly DemoTable PowerWorkspaces = T("power_bi_workspaces", true,
            I("id"), N("workspace_id", 200), N("name", 255));
        public static readonly DemoTable PowerReports = T("power_bi_reports", true, I("id"), N("report_id", 200),
            N("name", 255), N("report_type"), I("workspace_id"), D("first_seen_at"));
        public static readonly DemoTable PowerDashboards = T("power_bi_dashboards", true, I("id"), N("dashboard_id", 200),
            N("name", 255), I("workspace_id"), D("first_seen_at"));
        public static readonly DemoTable StudioBots = T("copilot_studio_bots", true, I("id"), N("bot_id", 200),
            N("name", 255), I("environment_id"), D("first_seen_at"));
        public static readonly DemoTable PowerAppConnectors = T("power_app_connectors", false,
            I("power_app_id"), I("connector_id"));
        public static readonly DemoTable PowerFlowConnectors = T("power_automate_flow_connectors", false,
            I("flow_id"), I("connector_id"));
        public static readonly DemoTable PowerAppEvents = T("event_meta_power_app", false,
            G("event_id"), I("power_app_id"), N("app_session_id", 200), I("client_type_id"));
        public static readonly DemoTable PowerAppShares = T("event_meta_power_app_share", false,
            G("event_id"), I("power_app_id"), I("shared_with_user_id"), N("role_name"));
        public static readonly DemoTable PowerFlowEvents = T("event_meta_power_automate_flow", false,
            G("event_id"), I("flow_id"), N("run_id", 200));
        public static readonly DemoTable PowerFlowShares = T("event_meta_power_automate_flow_share", false,
            G("event_id"), I("flow_id"), I("shared_with_user_id"), N("role_name"));
        public static readonly DemoTable PowerBIEvents = T("event_meta_power_bi", false,
            G("event_id"), I("workspace_id"), I("report_id"), I("dashboard_id"));
        public static readonly DemoTable StudioEvents = T("event_meta_copilot_studio", false,
            G("event_id"), I("bot_id"));
    }
}
