using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace Tests.FakeDataGen.Demo
{
    internal sealed class DemoColumn
    {
        public string Name { get; }
        public SqlDbType Type { get; }
        public int Size { get; }
        public DemoColumn(string name, SqlDbType type, int size = 0) { Name = name; Type = type; Size = size; }
    }

    internal sealed class DemoTable
    {
        public string Name { get; }
        public bool SupplyIdentity { get; }
        public IReadOnlyList<DemoColumn> Columns { get; }
        private readonly int[] _textColumns;
        public DemoTable(string name, bool identity, params DemoColumn[] columns)
        {
            Name = name; SupplyIdentity = identity; Columns = columns;
            _textColumns = Enumerable.Range(0, columns.Length).Where(i =>
                columns[i].Type == SqlDbType.VarChar || columns[i].Type == SqlDbType.NVarChar).ToArray();
        }
        public int BatchLimit(int requested) => Math.Min(requested, 2000 / Columns.Count);

        public void ValidateValues(object[] values)
        {
            if (values.Length != Columns.Count) throw new InvalidOperationException("Wrong row width for " + Name);
            foreach (int i in _textColumns)
            {
                if (!(values[i] is string text)) continue;
                if (Columns[i].Size > 0 && text.Length > Columns[i].Size)
                    throw new InvalidOperationException("Generated text exceeds the declared column width: " + Name + "." + Columns[i].Name);
                if (Columns[i].Type == SqlDbType.VarChar && text.Any(c => c > 127))
                    throw new InvalidOperationException("Non-ASCII synthetic text cannot be stored safely in " + Name + "." + Columns[i].Name);
            }
        }
    }

    internal interface IDemoSink : IDisposable
    {
        void Write(DemoTable table, params object[] values);
        void Flush();
    }

    internal static class DemoTables
    {
        // Dependency order is also the SQL buffer-flush order.
        private static readonly List<DemoTable> Tables = new List<DemoTable>();
        public static IReadOnlyList<DemoTable> All => Tables;
        private static DemoColumn I(string name) => new DemoColumn(name, SqlDbType.Int);
        private static DemoColumn L(string name) => new DemoColumn(name, SqlDbType.BigInt);
        private static DemoColumn B(string name) => new DemoColumn(name, SqlDbType.Bit);
        private static DemoColumn D(string name) => new DemoColumn(name, SqlDbType.DateTime);
        private static DemoColumn N(string name, int size = 100) => new DemoColumn(name, SqlDbType.NVarChar, size);
        private static DemoColumn A(string name, int size) => new DemoColumn(name, SqlDbType.VarChar, size);
        private static DemoColumn G(string name) => new DemoColumn(name, SqlDbType.UniqueIdentifier);
        private static DemoColumn F(string name) => new DemoColumn(name, SqlDbType.Float);
        private static DemoTable T(string name, bool identity, params DemoColumn[] columns)
        {
            var table = new DemoTable(name, identity, columns);
            Tables.Add(table);
            return table;
        }
        private static DemoTable Named(string table) => T(table, true, I("id"), N("name"));
        private static DemoTable Daily(string table, params DemoColumn[] columns) =>
            T(table, false, new[] { I("user_id"), D("date"), D("last_activity_date") }.Concat(columns).ToArray());

        public static readonly DemoTable Departments = Named("user_departments");
        public static readonly DemoTable Jobs = Named("user_job_titles");
        public static readonly DemoTable Companies = Named("user_company_name");
        public static readonly DemoTable States = Named("user_state_or_province");
        public static readonly DemoTable Countries = Named("user_country_or_region");
        public static readonly DemoTable Offices = Named("user_office_locations");
        public static readonly DemoTable UsageLocations = Named("user_usage_locations");
        public static readonly DemoTable Licences = T("license_types", true, I("id"), N("name"), N("sku_id", 400));
        public static readonly DemoTable Operations = T("event_operations", true, I("id"), A("operation_name", 250));
        public static readonly DemoTable Agents = T("copilot_agents", true, I("id"), N("name"), N("agent_id", 400), B("is_custom_agent"));
        public static readonly DemoTable Sites = T("sites", true, I("id"), N("url_base", 500), N("site_id"));
        public static readonly DemoTable Webs = T("webs", true, I("id"), N("url_base", 500), N("title", 250), I("site_id"));
        public static readonly DemoTable Urls = T("urls", true, I("id"), N("full_url", 850));
        public static readonly DemoTable Titles = T("page_titles", true, I("id"), N("title", 250));
        public static readonly DemoTable Extensions = T("event_file_ext", true, I("id"), A("extension_name", 250));
        public static readonly DemoTable FileNames = T("event_file_names", true, I("id"), N("file_name", 250));
        public static readonly DemoTable ItemTypes = T("event_types", true, I("id"), A("type_name", 250));
        public static readonly DemoTable Browsers = T("browsers", true, I("id"), A("browser_name", 250));
        public static readonly DemoTable Devices = T("devices", true, I("id"), A("device_name", 200));
        public static readonly DemoTable OperatingSystems = T("operating_systems", true, I("id"), A("os_name", 200));
        public static readonly DemoTable WebCountries = T("countries", true, I("id"), A("country_name", 250));
        public static readonly DemoTable WebCities = T("cities", true, I("id"), A("city_name", 250));
        public static readonly DemoTable ResourceNames = T("copilot_event_accessed_resource_names", true, I("id"), N("name", 850));
        public static readonly DemoTable ResourceSites = T("copilot_event_accessed_resource_site_urls", true, I("id"), N("site_url", 850));
        public static readonly DemoTable ResourceTypes = Named("copilot_event_accessed_resource_types");
        public static readonly DemoTable InteractionTypes = Named("copilot_interaction_types");
        public static readonly DemoTable InteractionApps = Named("copilot_interaction_app_classes");
        public static readonly DemoTable ConversationTypes = Named("copilot_interaction_conversation_types");
        public static readonly DemoTable Users = T("users", true, I("id"), A("user_name", 250), N("mail", 400),
            N("azure_ad_id", 400), B("account_enabled"), D("last_updated"), N("postalcode", 50), I("department_id"),
            I("company_name_id"), I("job_title_id"), I("state_or_province_id"), I("country_or_region_id"),
            I("office_location_id"), I("usage_location_id"), I("manager_id"));
        public static readonly DemoTable Assignments = T("user_license_type_lookups", false, I("user_id"), I("license_type_id"));
        public static readonly DemoTable Sessions = T("sessions", true, I("id"), A("ai_session_id", 50), I("user_id"));
        public static readonly DemoTable InteractionSessions = T("copilot_interaction_sessions", true,
            I("id"), N("session_ref", 450), I("user_id"));
        public static readonly DemoTable Audit = T("audit_events", false, G("id"), I("user_id"), I("operation_id"), D("time_stamp"));
        public static readonly DemoTable Chats = T("copilot_chats", false, G("event_id"), N("app_host"), I("agent_id"),
            N("thread_id", 450), N("client_region", 50), N("copilot_log_version", 50), I("user_id"), D("time_stamp"));
        public static readonly DemoTable Resources = T("copilot_event_accessed_resources", false,
            G("copilot_chat_id"), I("resource_name_id"), I("resource_site_url_id"), I("resource_type_id"));
        public static readonly DemoTable SharePointAudit = T("event_meta_sharepoint", false, G("event_id"), I("url_id"),
            I("file_extension_id"), I("file_name_id"), I("related_web_id"), I("item_type_id"));
        public static readonly DemoTable Hits = T("hits", false, I("url_id"), D("hit_timestamp"), I("session_id"),
            I("page_title_id"), I("web_id"), I("agent_id"), I("device_id"), I("os_id"),
            F("seconds_on_page"), F("page_load_time"), G("page_request_id"), I("country_id"), I("city_id"));
        public static readonly DemoTable Interactions = T("copilot_interactions", false,
            N("graph_interaction_id", 200), I("session_id"), I("user_id"), N("request_id", 200),
            I("interaction_type_id"), I("app_class_id"), I("conversation_type_id"), D("created_utc"),
            I("body_char_count"), I("body_word_count"), I("attachment_count"), I("link_count"),
            I("mention_count"), I("context_count"), I("response_latency_ms"));
        public static readonly DemoTable Teams = Daily("teams_user_activity_log", L("private_chat_count"), L("team_chat_count"),
            L("post_messages"), L("reply_messages"), L("urgent_messages"), L("calls_count"), L("meetings_count"),
            L("adhoc_meetings_attended_count"), L("adhoc_meetings_organized_count"), L("meetings_attended_count"),
            L("meetings_organized_count"), L("scheduled_onetime_meetings_attended_count"), L("scheduled_onetime_meetings_organized_count"),
            L("scheduled_recurring_meetings_attended_count"), L("scheduled_recurring_meetings_organized_count"),
            I("audio_duration_seconds"), I("video_duration_seconds"), I("screenshare_duration_seconds"));
        public static readonly DemoTable Outlook = Daily("outlook_user_activity_log", L("email_send_count"),
            L("email_receive_count"), L("email_read_count"), L("meeting_created_count"), L("meeting_interacted_count"));
        public static readonly DemoTable SharePoint = Daily("sharepoint_user_activity_log",
            L("viewed_or_edited"), L("synced"), L("shared_internally"), L("shared_externally"));
        public static readonly DemoTable OneDrive = Daily("onedrive_user_activity_log",
            L("viewed_or_edited"), L("synced"), L("shared_internally"), L("shared_externally"));
        public static readonly DemoTable Engage = Daily("yammer_user_activity_log", I("posted_count"), I("read_count"), I("liked_count"));
        public static readonly DemoTable TeamsDevices = Daily("teams_user_device_usage_log", B("used_web"), B("used_win_phone"),
            B("used_linux"), B("used_chrome_os"), B("used_ios"), B("used_android"), B("used_mac"), B("used_windows"));
        public static readonly DemoTable EngageDevices = Daily("yammer_device_activity_log", B("used_web"), B("used_win_phone"),
            B("used_android"), B("used_ipad"), B("used_iphone"), B("used_others"));
        public static readonly DemoTable Platforms = Daily("platform_user_activity_log",
            new[] { "windows", "mac", "mobile", "web", "outlook", "word", "excel", "powerpoint", "onenote", "teams",
                "outlook_windows", "word_windows", "excel_windows", "powerpoint_windows", "onenote_windows", "teams_windows",
                "outlook_mac", "word_mac", "excel_mac", "powerpoint_mac", "onenote_mac", "teams_mac",
                "outlook_mobile", "word_mobile", "excel_mobile", "powerpoint_mobile", "onenote_mobile", "teams_mobile",
                "outlook_web", "word_web", "excel_web", "powerpoint_web", "onenote_web", "teams_web" }.Select(B).ToArray());
        public static readonly DemoTable CopilotUsage = Daily("copilot_usage_user_activity_log", I("report_period_days"),
            I("prompts_all_apps"), I("prompts_chat_work"), I("prompts_chat_web"), I("active_usage_days"),
            D("chat_last_activity_date"), D("teams_last_activity_date"), D("word_last_activity_date"),
            D("outlook_last_activity_date"), D("excel_last_activity_date"), D("powerpoint_last_activity_date"),
            D("onenote_last_activity_date"), D("chat_work_last_activity_date"), D("agent_last_activity_date"),
            B("is_upn_obfuscated"));
        public static readonly DemoTable CopilotCounts = T("copilot_user_count_log", false,
            D("report_refresh_date"), D("report_date"), N("report_type", 20), I("report_period_days"), N("app_name"),
            I("enabled_users"), I("active_users"), L("prompts_submitted"), F("average_prompts_submitted"));
    }
}
