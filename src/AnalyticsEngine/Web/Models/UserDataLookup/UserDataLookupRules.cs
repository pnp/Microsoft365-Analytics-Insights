using Common.Entities;
using System.Collections.Generic;
using System.Linq;

namespace Web.AnalyticsWeb.Models.UserDataLookup
{
    /// <summary>
    /// Describes one category of data held for a user: how it links to the user in SQL, which import
    /// workloads feed it, and whether it supports drill-down.
    /// </summary>
    public sealed class UserDataCategoryMeta
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public bool SupportsDetail { get; set; }

        /// <summary>SQL table this category counts. Null when the row count is reached indirectly.</summary>
        public string Table { get; set; }

        /// <summary>The user foreign-key column on <see cref="Table"/>.</summary>
        public string UserColumn { get; set; }

        /// <summary>Web hits link to a user indirectly via sessions, so they need a nested query.</summary>
        public bool IndirectViaSession { get; set; }

        /// <summary>
        /// Audit sub-type tables (copilot_chats, event_meta_*) link to a user via
        /// event_id -&gt; audit_events.user_id, so they need a join through audit_events.
        /// </summary>
        public bool ViaAuditEvent { get; set; }

        /// <summary>Import-workload flags (<see cref="ImportTaskSettings"/> property names) that feed this category.</summary>
        public string[] WorkloadFlags { get; set; } = new string[0];
    }

    /// <summary>An import workload (job), with the friendly name / description shown on the lookup page.</summary>
    public sealed class UserDataWorkloadDef
    {
        public string Flag { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// The pure decision logic behind the admin user-data lookup: the category catalogue, the mapping
    /// from a category to the import workloads that feed it, request-parameter normalisation and the
    /// display-SQL builder. No EF, no HTTP and no configuration reading, so it is unit testable with
    /// zero SQL Server dependency. See issues #379 / #381.
    /// </summary>
    public static class UserDataLookupRules
    {
        public const int DefaultTake = 50;
        public const int MaxTake = 200;
        public const int MaxEventDataChars = 300;

        // Category keys (shared between the summary counts and the detail drill-down).
        public const string CatAuditEvents = "audit-events";
        public const string CatSentEmails = "sent-emails";
        public const string CatWebHits = "web-hits";
        public const string CatTeamMemberships = "team-memberships";
        public const string CatTeamOwnerships = "team-ownerships";
        public const string CatTeamsReactions = "teams-reactions";
        public const string CatCallsOrganised = "calls-organised";
        public const string CatCallSessions = "call-sessions";
        public const string CatCallFeedback = "call-feedback";
        public const string CatPageLikes = "page-likes";
        public const string CatPageComments = "page-comments";
        public const string CatUsageOutlook = "usage-outlook";
        public const string CatUsageOneDrive = "usage-onedrive";
        public const string CatUsageSharePoint = "usage-sharepoint";
        public const string CatUsageYammer = "usage-yammer";
        public const string CatUsageTeams = "usage-teams";
        public const string CatUsageTeamsDevice = "usage-teams-device";
        public const string CatUsageAppPlatform = "usage-app-platform";
        public const string CatPowerAppShares = "powerapp-shares";
        public const string CatFlowShares = "flow-shares";

        // Audit sub-types: each is a child of audit_events (linked via event_id), so they break the
        // single "audit-events" total down by workload (Copilot, SharePoint, Power Platform, ...).
        public const string CatCopilot = "copilot-interactions";
        public const string CatAuditSharePoint = "audit-sharepoint";
        public const string CatAuditExchange = "audit-exchange";
        public const string CatAuditEntra = "audit-entra";
        public const string CatAuditGeneral = "audit-general";
        public const string CatAuditStream = "audit-stream";
        public const string CatPowerAppEvents = "powerapp-events";
        public const string CatFlowEvents = "flow-events";
        public const string CatPowerBiEvents = "powerbi-events";
        public const string CatCopilotStudioEvents = "copilot-studio-events";

        /// <summary>Import-workload flag names (must match <see cref="ImportTaskSettings"/> property names).</summary>
        public static class Wf
        {
            public const string Calls = "Calls";
            public const string UsersMetadata = "GraphUsersMetadata";
            public const string UsageReports = "GraphUsageReports";
            public const string Teams = "GraphTeams";
            public const string AuditLog = "ActivityLog";
            public const string WebTraffic = "WebTraffic";
            public const string SentEmails = "SentEmails";
            public const string Copilot = "Copilot";
        }

        // Friendly names / descriptions for the import workloads, shown on the lookup page so an
        // admin can see why a category might legitimately have 0 records.
        private static readonly UserDataWorkloadDef[] _workloads =
        {
            new UserDataWorkloadDef { Flag = Wf.AuditLog, Name = "Audit log", Description = "SharePoint / Exchange / Entra ID audit activity (Audit.SharePoint feed)." },
            new UserDataWorkloadDef { Flag = Wf.Copilot, Name = "Copilot & Power Platform", Description = "Copilot interactions and Power Platform events (Audit.General feed)." },
            new UserDataWorkloadDef { Flag = Wf.WebTraffic, Name = "Web traffic", Description = "SharePoint page views, likes and comments (App Insights tracker)." },
            new UserDataWorkloadDef { Flag = Wf.SentEmails, Name = "Sent emails", Description = "Messages sent from mailboxes (Graph)." },
            new UserDataWorkloadDef { Flag = Wf.Teams, Name = "Teams", Description = "Team memberships, owners, channels and reactions (Graph)." },
            new UserDataWorkloadDef { Flag = Wf.Calls, Name = "Teams calls", Description = "Teams call records: organiser and attended sessions." },
            new UserDataWorkloadDef { Flag = Wf.UsageReports, Name = "Usage reports", Description = "Daily per-user activity reports (Outlook, OneDrive, SharePoint, Teams, Viva Engage)." },
            new UserDataWorkloadDef { Flag = Wf.UsersMetadata, Name = "User metadata", Description = "User profile metadata: department, job title, licences, manager, location." },
        };

        // Ordered list that drives both the summary and the detail label lookup.
        private static readonly UserDataCategoryMeta[] _categories =
        {
            new UserDataCategoryMeta { Key = CatAuditEvents, Label = "Audit events (all, total)", SupportsDetail = true,
                Table = "audit_events", UserColumn = "user_id", WorkloadFlags = new[] { Wf.AuditLog, Wf.Copilot },
                Description = "Total of all audit activity. The workload-specific rows below (Copilot, SharePoint, Power Platform, ...) break this down; their sum can be less than the total as some events have no workload-specific metadata." },
            new UserDataCategoryMeta { Key = CatCopilot, Label = "Copilot interactions", SupportsDetail = true,
                Table = "copilot_chats", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Microsoft 365 Copilot interactions (delivered via the Audit.General feed)." },
            new UserDataCategoryMeta { Key = CatAuditSharePoint, Label = "SharePoint / OneDrive audit", SupportsDetail = true,
                Table = "event_meta_sharepoint", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.AuditLog },
                Description = "SharePoint and OneDrive audit events (file access, sharing, etc.)." },
            new UserDataCategoryMeta { Key = CatAuditExchange, Label = "Exchange audit", SupportsDetail = true,
                Table = "event_meta_exchange", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.AuditLog },
                Description = "Exchange / mailbox audit events." },
            new UserDataCategoryMeta { Key = CatAuditEntra, Label = "Entra ID audit", SupportsDetail = true,
                Table = "event_meta_azure_ad", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.AuditLog },
                Description = "Entra ID (Azure AD) audit events." },
            new UserDataCategoryMeta { Key = CatAuditGeneral, Label = "General audit", SupportsDetail = true,
                Table = "event_meta_general", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Other 'general' workload audit events (Audit.General feed)." },
            new UserDataCategoryMeta { Key = CatAuditStream, Label = "Stream events", SupportsDetail = true,
                Table = "event_meta_stream", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Microsoft Stream audit events." },
            new UserDataCategoryMeta { Key = CatPowerAppEvents, Label = "Power Apps events", SupportsDetail = true,
                Table = "event_meta_power_app", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Power Apps launch / usage events (Audit.General feed)." },
            new UserDataCategoryMeta { Key = CatFlowEvents, Label = "Power Automate events", SupportsDetail = true,
                Table = "event_meta_power_automate_flow", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Power Automate lifecycle and permission events (Audit.General feed)." },
            new UserDataCategoryMeta { Key = CatPowerBiEvents, Label = "Power BI events", SupportsDetail = true,
                Table = "event_meta_power_bi", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Power BI audit events (Audit.General feed)." },
            new UserDataCategoryMeta { Key = CatCopilotStudioEvents, Label = "Copilot Studio events", SupportsDetail = true,
                Table = "event_meta_copilot_studio", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Copilot Studio (bot) audit events (Audit.General feed)." },
            new UserDataCategoryMeta { Key = CatSentEmails, Label = "Sent emails", SupportsDetail = true,
                Table = "sent_emails", UserColumn = "user_id", WorkloadFlags = new[] { Wf.SentEmails },
                Description = "Messages sent from the user's mailbox." },
            new UserDataCategoryMeta { Key = CatWebHits, Label = "Web page hits", SupportsDetail = true,
                Table = "hits", IndirectViaSession = true, WorkloadFlags = new[] { Wf.WebTraffic },
                Description = "SharePoint page views captured by the web traffic tracker." },
            new UserDataCategoryMeta { Key = CatTeamMemberships, Label = "Team memberships", SupportsDetail = true,
                Table = "team_membership_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.Teams },
                Description = "Teams the user has been a member of." },
            new UserDataCategoryMeta { Key = CatTeamOwnerships, Label = "Team ownerships", SupportsDetail = true,
                Table = "team_owners", UserColumn = "owner_id", WorkloadFlags = new[] { Wf.Teams },
                Description = "Teams the user owns / has owned." },
            new UserDataCategoryMeta { Key = CatTeamsReactions, Label = "Teams reactions", SupportsDetail = true,
                Table = "teams_user_channel_reactions", UserColumn = "user_id", WorkloadFlags = new[] { Wf.Teams },
                Description = "Reactions the user made on Teams channel messages." },
            new UserDataCategoryMeta { Key = CatCallsOrganised, Label = "Calls / meetings organised", SupportsDetail = true,
                Table = "call_records", UserColumn = "organizer_id", WorkloadFlags = new[] { Wf.Calls },
                Description = "Teams calls / meetings the user organised." },
            new UserDataCategoryMeta { Key = CatCallSessions, Label = "Call sessions attended", SupportsDetail = true,
                Table = "call_sessions", UserColumn = "attendee_user_id", WorkloadFlags = new[] { Wf.Calls },
                Description = "Teams call sessions the user attended." },
            new UserDataCategoryMeta { Key = CatCallFeedback, Label = "Call feedback", SupportsDetail = false,
                Table = "call_feedback", UserColumn = "user_id", WorkloadFlags = new[] { Wf.Calls },
                Description = "Call quality feedback recorded for the user." },
            new UserDataCategoryMeta { Key = CatPageLikes, Label = "Page likes", SupportsDetail = true,
                Table = "page_likes", UserColumn = "user_id", WorkloadFlags = new[] { Wf.WebTraffic },
                Description = "SharePoint page likes by the user." },
            new UserDataCategoryMeta { Key = CatPageComments, Label = "Page comments", SupportsDetail = true,
                Table = "page_comments", UserColumn = "user_id", WorkloadFlags = new[] { Wf.WebTraffic },
                Description = "SharePoint page comments by the user." },
            new UserDataCategoryMeta { Key = CatUsageOutlook, Label = "Outlook usage (daily)", SupportsDetail = true,
                Table = "outlook_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily Outlook activity report rows." },
            new UserDataCategoryMeta { Key = CatUsageOneDrive, Label = "OneDrive usage (daily)", SupportsDetail = true,
                Table = "onedrive_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily OneDrive activity report rows." },
            new UserDataCategoryMeta { Key = CatUsageSharePoint, Label = "SharePoint usage (daily)", SupportsDetail = true,
                Table = "sharepoint_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily SharePoint activity report rows." },
            new UserDataCategoryMeta { Key = CatUsageYammer, Label = "Viva Engage usage (daily)", SupportsDetail = true,
                Table = "yammer_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily Viva Engage (Yammer) activity report rows." },
            new UserDataCategoryMeta { Key = CatUsageTeams, Label = "Teams usage (daily)", SupportsDetail = true,
                Table = "teams_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily Teams activity report rows." },
            new UserDataCategoryMeta { Key = CatUsageTeamsDevice, Label = "Teams device usage (daily)", SupportsDetail = true,
                Table = "teams_user_device_usage_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily Teams device activity report rows." },
            new UserDataCategoryMeta { Key = CatUsageAppPlatform, Label = "App platform usage (daily)", SupportsDetail = true,
                Table = "platform_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily per-app platform activity report rows." },
            new UserDataCategoryMeta { Key = CatPowerAppShares, Label = "Power App shares received", SupportsDetail = false,
                Table = "event_meta_power_app_share", UserColumn = "shared_with_user_id", WorkloadFlags = new[] { Wf.Copilot },
                Description = "Power Apps shared with the user." },
            new UserDataCategoryMeta { Key = CatFlowShares, Label = "Flow shares received", SupportsDetail = false,
                Table = "event_meta_power_automate_flow_share", UserColumn = "shared_with_user_id", WorkloadFlags = new[] { Wf.Copilot },
                Description = "Power Automate flows shared with the user." },
        };

        /// <summary>Every category, in the order the summary page shows them.</summary>
        public static IReadOnlyList<UserDataCategoryMeta> Categories => _categories;

        /// <summary>Every import workload, in the order the summary page shows them.</summary>
        public static IReadOnlyList<UserDataWorkloadDef> Workloads => _workloads;

        /// <summary>The category with this key, or null when the key is not one we know about.</summary>
        public static UserDataCategoryMeta FindCategory(string key)
        {
            return _categories.FirstOrDefault(m => m.Key == key);
        }

        /// <summary>Trims a UPN / category query-string value, treating null as empty.</summary>
        public static string Normalise(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        /// <summary>
        /// Clamps the drill-down row limit: anything below 1 (including a negative value) falls back to
        /// <see cref="DefaultTake"/>, and the ceiling is <see cref="MaxTake"/>.
        /// </summary>
        public static int ClampTake(int take)
        {
            if (take < 1) return DefaultTake;
            if (take > MaxTake) return MaxTake;
            return take;
        }

        /// <summary>Is this import workload turned on for this deployment?</summary>
        public static bool WorkloadEnabled(ImportTaskSettings s, string flag)
        {
            if (s == null) return false;
            switch (flag)
            {
                case Wf.Calls: return s.Calls;
                case Wf.UsersMetadata: return s.GraphUsersMetadata;
                case Wf.UsageReports: return s.GraphUsageReports;
                case Wf.Teams: return s.GraphTeams;
                case Wf.AuditLog: return s.ActivityLog;
                case Wf.WebTraffic: return s.WebTraffic;
                case Wf.SentEmails: return s.SentEmails;
                case Wf.Copilot: return s.Copilot;
                default: return false;
            }
        }

        /// <summary>The friendly name for an import-workload flag (the flag itself when unknown).</summary>
        public static string WorkloadName(string flag)
        {
            var def = _workloads.FirstOrDefault(w => w.Flag == flag);
            return def != null ? def.Name : flag;
        }

        /// <summary>Builds a copy-pasteable COUNT query that reproduces a category's count for a UPN.</summary>
        public static string BuildCountSql(UserDataCategoryMeta meta, string upn)
        {
            return DataUtils.Sql.UserDataCountSql.BuildCountSql(meta.Table, meta.UserColumn, meta.IndirectViaSession, meta.ViaAuditEvent, upn);
        }

        /// <summary>Caps free-text detail (event payloads, comments) so one row can't dominate the response.</summary>
        public static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value;
            }
            return value.Substring(0, maxChars) + "…";
        }
    }
}
