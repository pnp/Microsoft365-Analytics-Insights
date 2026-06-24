using Common.Entities;
using Common.Entities.Config;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Admin lookup of everything held in SQL for a single user, keyed by UPN.
    /// Returns a profile + per-category record counts, and (per category) the most recent rows.
    /// </summary>
    /// <remarks>
    /// Designed for large tenants (~200k users): the user is found by a direct equality compare on
    /// the indexed, case-insensitive user_name column (no LOWER()/ToLower(), which would force a
    /// scan), counts hit indexed FK columns, and every detail query is bounded by Take(n).
    /// </remarks>
    [Authorize]
    [RoutePrefix("api/UserDataLookup")]
    public class UserDataLookupAPIController : ApiController
    {
        private const int DefaultTake = 50;
        private const int MaxTake = 200;
        private const int MaxEventDataChars = 300;

        // Category keys (shared between the summary counts and the detail drill-down).
        private const string CatAuditEvents = "audit-events";
        private const string CatSentEmails = "sent-emails";
        private const string CatWebHits = "web-hits";
        private const string CatTeamMemberships = "team-memberships";
        private const string CatTeamOwnerships = "team-ownerships";
        private const string CatTeamsReactions = "teams-reactions";
        private const string CatCallsOrganised = "calls-organised";
        private const string CatCallSessions = "call-sessions";
        private const string CatCallFeedback = "call-feedback";
        private const string CatPageLikes = "page-likes";
        private const string CatPageComments = "page-comments";
        private const string CatAppInstalls = "app-installs";
        private const string CatUsageOutlook = "usage-outlook";
        private const string CatUsageOneDrive = "usage-onedrive";
        private const string CatUsageSharePoint = "usage-sharepoint";
        private const string CatUsageYammer = "usage-yammer";
        private const string CatUsageTeams = "usage-teams";
        private const string CatUsageTeamsDevice = "usage-teams-device";
        private const string CatUsageAppPlatform = "usage-app-platform";
        private const string CatPowerAppShares = "powerapp-shares";
        private const string CatFlowShares = "flow-shares";

        // Audit sub-types: each is a child of audit_events (linked via event_id), so they break the
        // single "audit-events" total down by workload (Copilot, SharePoint, Power Platform, ...).
        private const string CatCopilot = "copilot-interactions";
        private const string CatAuditSharePoint = "audit-sharepoint";
        private const string CatAuditExchange = "audit-exchange";
        private const string CatAuditEntra = "audit-entra";
        private const string CatAuditGeneral = "audit-general";
        private const string CatAuditStream = "audit-stream";
        private const string CatPowerAppEvents = "powerapp-events";
        private const string CatFlowEvents = "flow-events";
        private const string CatPowerBiEvents = "powerbi-events";
        private const string CatCopilotStudioEvents = "copilot-studio-events";

        private sealed class CategoryMeta
        {
            public string Key;
            public string Label;
            public string Description;
            public bool SupportsDetail;
            /// <summary>SQL table this category counts. Null when the row count is reached indirectly.</summary>
            public string Table;
            /// <summary>The user foreign-key column on <see cref="Table"/>.</summary>
            public string UserColumn;
            /// <summary>Web hits link to a user indirectly via sessions, so they need a nested query.</summary>
            public bool IndirectViaSession;
            /// <summary>
            /// Audit sub-type tables (copilot_chats, event_meta_*) link to a user via
            /// event_id -&gt; audit_events.user_id, so they need a join through audit_events.
            /// </summary>
            public bool ViaAuditEvent;
            /// <summary>Import-workload flags (ImportTaskSettings property names) that feed this category.</summary>
            public string[] WorkloadFlags = new string[0];
        }

        // Import-workload flag names (must match ImportTaskSettings property names).
        private static class Wf
        {
            public const string Calls = "Calls";
            public const string UsersMetadata = "GraphUsersMetadata";
            public const string UserApps = "GraphUserApps";
            public const string UsageReports = "GraphUsageReports";
            public const string Teams = "GraphTeams";
            public const string AuditLog = "ActivityLog";
            public const string WebTraffic = "WebTraffic";
            public const string SentEmails = "SentEmails";
            public const string Copilot = "Copilot";
        }

        private sealed class WorkloadDef
        {
            public string Flag;
            public string Name;
            public string Description;
        }

        // Friendly names / descriptions for the import workloads, shown on the lookup page so an
        // admin can see why a category might legitimately have 0 records.
        private static readonly WorkloadDef[] WorkloadDefs =
        {
            new WorkloadDef { Flag = Wf.AuditLog, Name = "Audit log", Description = "SharePoint / Exchange / Entra ID audit activity (Audit.SharePoint feed)." },
            new WorkloadDef { Flag = Wf.Copilot, Name = "Copilot & Power Platform", Description = "Copilot interactions and Power Platform events (Audit.General feed)." },
            new WorkloadDef { Flag = Wf.WebTraffic, Name = "Web traffic", Description = "SharePoint page views, likes and comments (App Insights tracker)." },
            new WorkloadDef { Flag = Wf.SentEmails, Name = "Sent emails", Description = "Messages sent from mailboxes (Graph)." },
            new WorkloadDef { Flag = Wf.Teams, Name = "Teams", Description = "Team memberships, owners, channels and reactions (Graph)." },
            new WorkloadDef { Flag = Wf.Calls, Name = "Teams calls", Description = "Teams call records: organiser and attended sessions." },
            new WorkloadDef { Flag = Wf.UsageReports, Name = "Usage reports", Description = "Daily per-user activity reports (Outlook, OneDrive, SharePoint, Teams, Viva Engage)." },
            new WorkloadDef { Flag = Wf.UserApps, Name = "User Teams apps", Description = "Teams apps installed per user (Graph)." },
            new WorkloadDef { Flag = Wf.UsersMetadata, Name = "User metadata", Description = "User profile metadata: department, job title, licences, manager, location." },
        };

        // Ordered list that drives both the summary and the detail label lookup.
        private static readonly CategoryMeta[] CategoryMetas =
        {
            new CategoryMeta { Key = CatAuditEvents, Label = "Audit events (all, total)", SupportsDetail = true,
                Table = "audit_events", UserColumn = "user_id", WorkloadFlags = new[] { Wf.AuditLog, Wf.Copilot },
                Description = "Total of all audit activity. The workload-specific rows below (Copilot, SharePoint, Power Platform, ...) break this down; their sum can be less than the total as some events have no workload-specific metadata." },
            new CategoryMeta { Key = CatCopilot, Label = "Copilot interactions", SupportsDetail = true,
                Table = "copilot_chats", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Microsoft 365 Copilot interactions (delivered via the Audit.General feed)." },
            new CategoryMeta { Key = CatAuditSharePoint, Label = "SharePoint / OneDrive audit", SupportsDetail = true,
                Table = "event_meta_sharepoint", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.AuditLog },
                Description = "SharePoint and OneDrive audit events (file access, sharing, etc.)." },
            new CategoryMeta { Key = CatAuditExchange, Label = "Exchange audit", SupportsDetail = true,
                Table = "event_meta_exchange", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.AuditLog },
                Description = "Exchange / mailbox audit events." },
            new CategoryMeta { Key = CatAuditEntra, Label = "Entra ID audit", SupportsDetail = true,
                Table = "event_meta_azure_ad", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.AuditLog },
                Description = "Entra ID (Azure AD) audit events." },
            new CategoryMeta { Key = CatAuditGeneral, Label = "General audit", SupportsDetail = true,
                Table = "event_meta_general", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Other 'general' workload audit events (Audit.General feed)." },
            new CategoryMeta { Key = CatAuditStream, Label = "Stream events", SupportsDetail = true,
                Table = "event_meta_stream", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Microsoft Stream audit events." },
            new CategoryMeta { Key = CatPowerAppEvents, Label = "Power Apps events", SupportsDetail = true,
                Table = "event_meta_power_app", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Power Apps launch / usage events (Audit.General feed)." },
            new CategoryMeta { Key = CatFlowEvents, Label = "Power Automate events", SupportsDetail = true,
                Table = "event_meta_power_automate_flow", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Power Automate flow run events (Audit.General feed)." },
            new CategoryMeta { Key = CatPowerBiEvents, Label = "Power BI events", SupportsDetail = true,
                Table = "event_meta_power_bi", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Power BI audit events (Audit.General feed)." },
            new CategoryMeta { Key = CatCopilotStudioEvents, Label = "Copilot Studio events", SupportsDetail = true,
                Table = "event_meta_copilot_studio", ViaAuditEvent = true, WorkloadFlags = new[] { Wf.Copilot },
                Description = "Copilot Studio (bot) audit events (Audit.General feed)." },
            new CategoryMeta { Key = CatSentEmails, Label = "Sent emails", SupportsDetail = true,
                Table = "sent_emails", UserColumn = "user_id", WorkloadFlags = new[] { Wf.SentEmails },
                Description = "Messages sent from the user's mailbox." },
            new CategoryMeta { Key = CatWebHits, Label = "Web page hits", SupportsDetail = true,
                Table = "hits", IndirectViaSession = true, WorkloadFlags = new[] { Wf.WebTraffic },
                Description = "SharePoint page views captured by the web traffic tracker." },
            new CategoryMeta { Key = CatTeamMemberships, Label = "Team memberships", SupportsDetail = true,
                Table = "team_membership_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.Teams },
                Description = "Teams the user has been a member of." },
            new CategoryMeta { Key = CatTeamOwnerships, Label = "Team ownerships", SupportsDetail = true,
                Table = "team_owners", UserColumn = "owner_id", WorkloadFlags = new[] { Wf.Teams },
                Description = "Teams the user owns / has owned." },
            new CategoryMeta { Key = CatTeamsReactions, Label = "Teams reactions", SupportsDetail = true,
                Table = "teams_user_channel_reactions", UserColumn = "user_id", WorkloadFlags = new[] { Wf.Teams },
                Description = "Reactions the user made on Teams channel messages." },
            new CategoryMeta { Key = CatCallsOrganised, Label = "Calls / meetings organised", SupportsDetail = true,
                Table = "call_records", UserColumn = "organizer_id", WorkloadFlags = new[] { Wf.Calls },
                Description = "Teams calls / meetings the user organised." },
            new CategoryMeta { Key = CatCallSessions, Label = "Call sessions attended", SupportsDetail = true,
                Table = "call_sessions", UserColumn = "attendee_user_id", WorkloadFlags = new[] { Wf.Calls },
                Description = "Teams call sessions the user attended." },
            new CategoryMeta { Key = CatCallFeedback, Label = "Call feedback", SupportsDetail = false,
                Table = "call_feedback", UserColumn = "user_id", WorkloadFlags = new[] { Wf.Calls },
                Description = "Call quality feedback recorded for the user." },
            new CategoryMeta { Key = CatPageLikes, Label = "Page likes", SupportsDetail = true,
                Table = "page_likes", UserColumn = "user_id", WorkloadFlags = new[] { Wf.WebTraffic },
                Description = "SharePoint page likes by the user." },
            new CategoryMeta { Key = CatPageComments, Label = "Page comments", SupportsDetail = true,
                Table = "page_comments", UserColumn = "user_id", WorkloadFlags = new[] { Wf.WebTraffic },
                Description = "SharePoint page comments by the user." },
            new CategoryMeta { Key = CatAppInstalls, Label = "Teams app installs", SupportsDetail = true,
                Table = "teams_addons_user_installed_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UserApps },
                Description = "Teams apps installed by the user." },
            new CategoryMeta { Key = CatUsageOutlook, Label = "Outlook usage (daily)", SupportsDetail = true,
                Table = "outlook_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily Outlook activity report rows." },
            new CategoryMeta { Key = CatUsageOneDrive, Label = "OneDrive usage (daily)", SupportsDetail = true,
                Table = "onedrive_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily OneDrive activity report rows." },
            new CategoryMeta { Key = CatUsageSharePoint, Label = "SharePoint usage (daily)", SupportsDetail = true,
                Table = "sharepoint_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily SharePoint activity report rows." },
            new CategoryMeta { Key = CatUsageYammer, Label = "Viva Engage usage (daily)", SupportsDetail = true,
                Table = "yammer_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily Viva Engage (Yammer) activity report rows." },
            new CategoryMeta { Key = CatUsageTeams, Label = "Teams usage (daily)", SupportsDetail = true,
                Table = "teams_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily Teams activity report rows." },
            new CategoryMeta { Key = CatUsageTeamsDevice, Label = "Teams device usage (daily)", SupportsDetail = true,
                Table = "teams_user_device_usage_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily Teams device activity report rows." },
            new CategoryMeta { Key = CatUsageAppPlatform, Label = "App platform usage (daily)", SupportsDetail = true,
                Table = "platform_user_activity_log", UserColumn = "user_id", WorkloadFlags = new[] { Wf.UsageReports },
                Description = "Daily per-app platform activity report rows." },
            new CategoryMeta { Key = CatPowerAppShares, Label = "Power App shares received", SupportsDetail = false,
                Table = "event_meta_power_app_share", UserColumn = "shared_with_user_id", WorkloadFlags = new[] { Wf.Copilot },
                Description = "Power Apps shared with the user." },
            new CategoryMeta { Key = CatFlowShares, Label = "Flow shares received", SupportsDetail = false,
                Table = "event_meta_power_automate_flow_share", UserColumn = "shared_with_user_id", WorkloadFlags = new[] { Wf.Copilot },
                Description = "Power Automate flows shared with the user." },
        };

        /// <summary>Builds a copy-pasteable COUNT query that reproduces a category's count for a UPN.</summary>
        private static string BuildCountSql(CategoryMeta meta, string upn)
        {
            var literal = EscapeSqlLiteral(upn);
            if (meta.IndirectViaSession)
            {
                return
                    "SELECT COUNT(*) FROM hits\r\n" +
                    "WHERE session_id IN (\r\n" +
                    "    SELECT id FROM sessions\r\n" +
                    $"    WHERE user_id = (SELECT id FROM users WHERE user_name = '{literal}'));";
            }

            if (meta.ViaAuditEvent)
            {
                return
                    $"SELECT COUNT(*) FROM {meta.Table} c\r\n" +
                    "INNER JOIN audit_events e ON c.event_id = e.id\r\n" +
                    $"WHERE e.user_id = (SELECT id FROM users WHERE user_name = '{literal}');";
            }

            return
                $"SELECT COUNT(*) FROM {meta.Table}\r\n" +
                $"WHERE {meta.UserColumn} = (SELECT id FROM users WHERE user_name = '{literal}');";
        }

        /// <summary>Escapes a string literal for safe embedding in the displayed SQL (doubles quotes).</summary>
        private static string EscapeSqlLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static bool WorkloadEnabled(ImportTaskSettings s, string flag)
        {
            if (s == null) return false;
            switch (flag)
            {
                case Wf.Calls: return s.Calls;
                case Wf.UsersMetadata: return s.GraphUsersMetadata;
                case Wf.UserApps: return s.GraphUserApps;
                case Wf.UsageReports: return s.GraphUsageReports;
                case Wf.Teams: return s.GraphTeams;
                case Wf.AuditLog: return s.ActivityLog;
                case Wf.WebTraffic: return s.WebTraffic;
                case Wf.SentEmails: return s.SentEmails;
                case Wf.Copilot: return s.Copilot;
                default: return false;
            }
        }

        private static string WorkloadName(string flag)
        {
            var def = WorkloadDefs.FirstOrDefault(w => w.Flag == flag);
            return def != null ? def.Name : flag;
        }

        /// <summary>
        /// GET api/UserDataLookup/summary?upn=user@contoso.com
        /// Profile + per-category record counts for the user.
        /// </summary>
        [HttpGet]
        [Route("summary")]
        public async Task<IHttpActionResult> Summary(string upn = "")
        {
            upn = (upn ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(upn))
            {
                return Content(HttpStatusCode.BadRequest, new ApiErrorModel("A 'upn' query parameter is required."));
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                // Direct, case-insensitive equality compare - do NOT use ToLower() (it would make
                // the predicate non-SARGable and scan the whole users table on a big tenant).
                var user = await db.users
                    .Include(u => u.Department)
                    .Include(u => u.JobTitle)
                    .Include(u => u.CompanyName)
                    .Include(u => u.UserCountry)
                    .Include(u => u.OfficeLocation)
                    .Include(u => u.UsageLocation)
                    .Include(u => u.StateOrProvince)
                    .Include(u => u.Manager)
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .FirstOrDefaultAsync(u => u.UserPrincipalName == upn);

                if (user == null)
                {
                    return Content(HttpStatusCode.NotFound, new ApiErrorModel($"No user found with UPN '{upn}'."));
                }

                var summary = new UserDataSummaryModel
                {
                    Profile = BuildProfile(user),
                };

                // Which import workloads are enabled for this deployment - shown so an admin can see
                // why a category might legitimately have 0 records (nothing is importing it).
                var importSettings = new AppConfig().ImportJobSettings;
                foreach (var def in WorkloadDefs)
                {
                    summary.Workloads.Add(new WorkloadModel
                    {
                        Name = def.Name,
                        Description = def.Description,
                        Enabled = WorkloadEnabled(importSettings, def.Flag),
                    });
                }

                foreach (var meta in CategoryMetas)
                {
                    var count = await CountForCategoryAsync(db, user.ID, meta.Key);
                    summary.Categories.Add(new UserDataCategoryModel
                    {
                        Key = meta.Key,
                        Label = meta.Label,
                        Description = meta.Description,
                        Count = count,
                        SupportsDetail = meta.SupportsDetail,
                        SqlQuery = BuildCountSql(meta, upn),
                        Workloads = meta.WorkloadFlags.Select(WorkloadName).ToList(),
                        WorkloadsEnabled = meta.WorkloadFlags.Any(f => WorkloadEnabled(importSettings, f)),
                    });
                }

                return Ok(summary);
            }
        }

        /// <summary>
        /// GET api/UserDataLookup/detail?upn=user@contoso.com&amp;category=audit-events&amp;take=50
        /// The most recent rows for one category for the user.
        /// </summary>
        [HttpGet]
        [Route("detail")]
        public async Task<IHttpActionResult> Detail(string upn = "", string category = "", int take = DefaultTake)
        {
            upn = (upn ?? string.Empty).Trim();
            category = (category ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(upn))
            {
                return Content(HttpStatusCode.BadRequest, new ApiErrorModel("A 'upn' query parameter is required."));
            }

            var meta = CategoryMetas.FirstOrDefault(m => m.Key == category);
            if (meta == null)
            {
                return Content(HttpStatusCode.BadRequest, new ApiErrorModel($"Unknown category '{category}'."));
            }
            if (!meta.SupportsDetail)
            {
                return Content(HttpStatusCode.BadRequest, new ApiErrorModel($"Category '{category}' does not support drill-down."));
            }

            if (take < 1) take = DefaultTake;
            if (take > MaxTake) take = MaxTake;

            using (var db = new AnalyticsEntitiesContext())
            {
                var userId = await db.users
                    .Where(u => u.UserPrincipalName == upn)
                    .Select(u => (int?)u.ID)
                    .FirstOrDefaultAsync();

                if (userId == null)
                {
                    return Content(HttpStatusCode.NotFound, new ApiErrorModel($"No user found with UPN '{upn}'."));
                }

                var total = await CountForCategoryAsync(db, userId.Value, meta.Key);
                var rows = await DetailForCategoryAsync(db, userId.Value, meta.Key, take);

                return Ok(new UserDataDetailResponseModel
                {
                    Category = meta.Key,
                    Label = meta.Label,
                    TotalCount = total,
                    ReturnedCount = rows.Count,
                    Rows = rows,
                });
            }
        }

        private static UserProfileModel BuildProfile(User user)
        {
            return new UserProfileModel
            {
                UserId = user.ID,
                UserPrincipalName = user.UserPrincipalName,
                Mail = user.Mail,
                AzureAdId = user.AzureAdId,
                AccountEnabled = user.AccountEnabled,
                LastUpdated = user.LastUpdated,
                Department = user.Department?.Name,
                JobTitle = user.JobTitle?.Name,
                CompanyName = user.CompanyName?.Name,
                CountryOrRegion = user.UserCountry?.Name,
                OfficeLocation = user.OfficeLocation?.Name,
                UsageLocation = user.UsageLocation?.Name,
                StateOrProvince = user.StateOrProvince?.Name,
                PostalCode = user.PostalCode,
                ManagerUserPrincipalName = user.Manager?.UserPrincipalName,
                Licenses = user.LicenseLookups?
                    .Select(l => new UserLicenseModel { Name = l.License?.Name, SkuId = l.License?.SKUID })
                    .ToList() ?? new List<UserLicenseModel>(),
            };
        }

        private static Task<int> CountForCategoryAsync(AnalyticsEntitiesContext db, int userId, string key)
        {
            switch (key)
            {
                case CatAuditEvents:
                    return db.AuditEventsCommon.Where(e => e.UserId == userId).CountAsync();
                case CatSentEmails:
                    return db.SentEmails.Where(e => e.UserID == userId).CountAsync();
                case CatWebHits:
                    return db.hits.Where(h => h.session.user.ID == userId).CountAsync();
                case CatTeamMemberships:
                    return db.TeamMembershipLogs.Where(t => t.UserID == userId).CountAsync();
                case CatTeamOwnerships:
                    return db.TeamOwners.Where(t => t.OwnerID == userId).CountAsync();
                case CatTeamsReactions:
                    return db.TeamsUserReactions.Where(r => r.UserID == userId).CountAsync();
                case CatCallsOrganised:
                    return db.CallRecords.Where(c => c.OrganizerID == userId).CountAsync();
                case CatCallSessions:
                    return db.CallSessions.Where(s => s.AttendeeUserID == userId).CountAsync();
                case CatCallFeedback:
                    return db.CallFeedback.Where(f => f.UserID == userId).CountAsync();
                case CatPageLikes:
                    return db.UrlLikes.Where(l => l.UserID == userId).CountAsync();
                case CatPageComments:
                    return db.UrlComments.Where(c => c.UserID == userId).CountAsync();
                case CatAppInstalls:
                    return db.UserAppsLog.Where(a => a.UserID == userId).CountAsync();
                case CatUsageOutlook:
                    return db.OutlookUsageActivityLogs.Where(x => x.UserID == userId).CountAsync();
                case CatUsageOneDrive:
                    return db.OneDriveUserActivityLogs.Where(x => x.UserID == userId).CountAsync();
                case CatUsageSharePoint:
                    return db.SharePointUserActivityLogs.Where(x => x.UserID == userId).CountAsync();
                case CatUsageYammer:
                    return db.YammerUserActivityLogs.Where(x => x.UserID == userId).CountAsync();
                case CatUsageTeams:
                    return db.TeamUserActivityLogs.Where(x => x.UserID == userId).CountAsync();
                case CatUsageTeamsDevice:
                    return db.TeamsUserDeviceUsageLog.Where(x => x.UserID == userId).CountAsync();
                case CatUsageAppPlatform:
                    return db.AppPlatformUserUsageLog.Where(x => x.UserID == userId).CountAsync();
                case CatPowerAppShares:
                    return db.power_app_share_events.Where(s => s.SharedWithUserId == userId).CountAsync();
                case CatFlowShares:
                    return db.power_automate_flow_share_events.Where(s => s.SharedWithUserId == userId).CountAsync();
                case CatCopilot:
                    return db.CopilotChats.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case CatAuditSharePoint:
                    return db.sharepoint_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case CatAuditExchange:
                    return db.exchange_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case CatAuditEntra:
                    return db.azure_ad_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case CatAuditGeneral:
                    return db.general_audit_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case CatAuditStream:
                    return db.StreamEvents.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case CatPowerAppEvents:
                    return db.power_app_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case CatFlowEvents:
                    return db.power_automate_flow_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case CatPowerBiEvents:
                    return db.power_bi_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case CatCopilotStudioEvents:
                    return db.copilot_studio_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                default:
                    return Task.FromResult(0);
            }
        }

        private static async Task<List<UserDataDetailRowModel>> DetailForCategoryAsync(
            AnalyticsEntitiesContext db, int userId, string key, int take)
        {
            switch (key)
            {
                case CatAuditEvents:
                {
                    var raw = await db.AuditEventsCommon
                        .Where(e => e.UserId == userId)
                        .OrderByDescending(e => e.TimeStamp)
                        .Take(take)
                        .Select(e => new { e.TimeStamp, Operation = e.Operation != null ? e.Operation.Name : null, e.EventData })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel
                    {
                        Timestamp = r.TimeStamp,
                        Title = r.Operation,
                        Detail = Truncate(r.EventData, MaxEventDataChars),
                    }).ToList();
                }
                case CatSentEmails:
                {
                    var raw = await db.SentEmails
                        .Where(e => e.UserID == userId)
                        .OrderByDescending(e => e.SentDate)
                        .Take(take)
                        .Select(e => new { e.SentDate, e.Subject })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.SentDate, Title = r.Subject }).ToList();
                }
                case CatWebHits:
                {
                    var raw = await db.hits
                        .Where(h => h.session.user.ID == userId)
                        .OrderByDescending(h => h.hit_timestamp)
                        .Take(take)
                        .Select(h => new { h.hit_timestamp, Url = h.url != null ? h.url.FullUrl : null })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.hit_timestamp, Title = r.Url }).ToList();
                }
                case CatTeamMemberships:
                {
                    var raw = await db.TeamMembershipLogs
                        .Where(t => t.UserID == userId)
                        .OrderByDescending(t => t.Date)
                        .Take(take)
                        .Select(t => new { t.Date, Team = t.Team != null ? t.Team.Name : null })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.Date, Title = r.Team }).ToList();
                }
                case CatTeamOwnerships:
                {
                    var raw = await db.TeamOwners
                        .Where(t => t.OwnerID == userId)
                        .OrderByDescending(t => t.Discovered)
                        .Take(take)
                        .Select(t => new { t.Discovered, Team = t.Team != null ? t.Team.Name : null })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.Discovered, Title = r.Team }).ToList();
                }
                case CatTeamsReactions:
                {
                    var raw = await db.TeamsUserReactions
                        .Where(r => r.UserID == userId)
                        .OrderByDescending(r => r.Date)
                        .Take(take)
                        .Select(r => new { r.Date, Reaction = r.Reaction != null ? r.Reaction.Name : null })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.Date, Title = r.Reaction }).ToList();
                }
                case CatCallsOrganised:
                {
                    var raw = await db.CallRecords
                        .Where(c => c.OrganizerID == userId)
                        .OrderByDescending(c => c.StartDateTime)
                        .Take(take)
                        .Select(c => new { c.StartDateTime, c.EndDateTime })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel
                    {
                        Timestamp = r.StartDateTime,
                        Title = "Call / meeting",
                        Detail = "Ended " + r.EndDateTime.ToString("u"),
                    }).ToList();
                }
                case CatCallSessions:
                {
                    var raw = await db.CallSessions
                        .Where(s => s.AttendeeUserID == userId)
                        .OrderByDescending(s => s.Start)
                        .Take(take)
                        .Select(s => new { s.Start, s.End })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel
                    {
                        Timestamp = r.Start,
                        Title = "Call session attended",
                        Detail = "Ended " + r.End.ToString("u"),
                    }).ToList();
                }
                case CatPageLikes:
                {
                    var raw = await db.UrlLikes
                        .Where(l => l.UserID == userId)
                        .OrderByDescending(l => l.Created)
                        .Take(take)
                        .Select(l => new { l.Created, Url = l.Url != null ? l.Url.FullUrl : null })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.Created, Title = r.Url }).ToList();
                }
                case CatPageComments:
                {
                    var raw = await db.UrlComments
                        .Where(c => c.UserID == userId)
                        .OrderByDescending(c => c.Created)
                        .Take(take)
                        .Select(c => new { c.Created, Url = c.Url != null ? c.Url.FullUrl : null, c.Comment })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel
                    {
                        Timestamp = r.Created,
                        Title = r.Url,
                        Detail = Truncate(r.Comment, MaxEventDataChars),
                    }).ToList();
                }
                case CatAppInstalls:
                {
                    var raw = await db.UserAppsLog
                        .Where(a => a.UserID == userId)
                        .OrderByDescending(a => a.Date)
                        .Take(take)
                        .Select(a => new { a.Date, AddOn = a.AddOn != null ? a.AddOn.Name : null })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.Date, Title = r.AddOn }).ToList();
                }
                case CatUsageOutlook:
                    return await UsageDetailAsync(db.OutlookUsageActivityLogs.Where(x => x.UserID == userId), take);
                case CatUsageOneDrive:
                    return await UsageDetailAsync(db.OneDriveUserActivityLogs.Where(x => x.UserID == userId), take);
                case CatUsageSharePoint:
                    return await UsageDetailAsync(db.SharePointUserActivityLogs.Where(x => x.UserID == userId), take);
                case CatUsageYammer:
                    return await UsageDetailAsync(db.YammerUserActivityLogs.Where(x => x.UserID == userId), take);
                case CatUsageTeams:
                    return await UsageDetailAsync(db.TeamUserActivityLogs.Where(x => x.UserID == userId), take);
                case CatUsageTeamsDevice:
                    return await UsageDetailAsync(db.TeamsUserDeviceUsageLog.Where(x => x.UserID == userId), take);
                case CatUsageAppPlatform:
                    return await UsageDetailAsync(db.AppPlatformUserUsageLog.Where(x => x.UserID == userId), take);
                case CatCopilot:
                    return await AuditChildDetailAsync(db.CopilotChats.Where(c => c.AuditEvent.UserId == userId), take);
                case CatAuditSharePoint:
                    return await AuditChildDetailAsync(db.sharepoint_events.Where(c => c.AuditEvent.UserId == userId), take);
                case CatAuditExchange:
                    return await AuditChildDetailAsync(db.exchange_events.Where(c => c.AuditEvent.UserId == userId), take);
                case CatAuditEntra:
                    return await AuditChildDetailAsync(db.azure_ad_events.Where(c => c.AuditEvent.UserId == userId), take);
                case CatAuditGeneral:
                    return await AuditChildDetailAsync(db.general_audit_events.Where(c => c.AuditEvent.UserId == userId), take);
                case CatAuditStream:
                    return await AuditChildDetailAsync(db.StreamEvents.Where(c => c.AuditEvent.UserId == userId), take);
                case CatPowerAppEvents:
                    return await AuditChildDetailAsync(db.power_app_events.Where(c => c.AuditEvent.UserId == userId), take);
                case CatFlowEvents:
                    return await AuditChildDetailAsync(db.power_automate_flow_events.Where(c => c.AuditEvent.UserId == userId), take);
                case CatPowerBiEvents:
                    return await AuditChildDetailAsync(db.power_bi_events.Where(c => c.AuditEvent.UserId == userId), take);
                case CatCopilotStudioEvents:
                    return await AuditChildDetailAsync(db.copilot_studio_events.Where(c => c.AuditEvent.UserId == userId), take);
                default:
                    return new List<UserDataDetailRowModel>();
            }
        }

        /// <summary>
        /// Shared drill-down projection for audit sub-type tables (copilot_chats, event_meta_*) that
        /// link to a user through audit_events. Projects the parent event's timestamp + operation.
        /// Generic because IQueryable&lt;T&gt; is invariant, so the concrete child type must flow through.
        /// </summary>
        private static async Task<List<UserDataDetailRowModel>> AuditChildDetailAsync<T>(IQueryable<T> query, int take)
            where T : Common.Entities.Entities.BaseOfficeEvent
        {
            var raw = await query
                .OrderByDescending(x => x.AuditEvent.TimeStamp)
                .Take(take)
                .Select(x => new
                {
                    x.AuditEvent.TimeStamp,
                    OperationName = x.AuditEvent.Operation != null ? x.AuditEvent.Operation.Name : null,
                })
                .ToListAsync();
            return raw.Select(r => new UserDataDetailRowModel
            {
                Timestamp = r.TimeStamp,
                Title = r.OperationName ?? "(audit event)",
            }).ToList();
        }

        /// <summary>
        /// Shared drill-down projection for the daily usage-report logs (date + last-activity date).
        /// Generic because IQueryable&lt;T&gt; is invariant, so the concrete log type must flow through.
        /// </summary>
        private static async Task<List<UserDataDetailRowModel>> UsageDetailAsync<T>(IQueryable<T> query, int take)
            where T : Common.Entities.ActivityReports.UserRelatedAbstractUsageActivity
        {
            var raw = await query
                .OrderByDescending(x => x.Date)
                .Take(take)
                .Select(x => new { x.Date, x.LastActivityDate })
                .ToListAsync();
            return raw.Select(r => new UserDataDetailRowModel
            {
                Timestamp = r.Date,
                Title = "Activity report day",
                Detail = r.LastActivityDate.HasValue ? "Last activity " + r.LastActivityDate.Value.ToString("d") : null,
            }).ToList();
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value;
            }
            return value.Substring(0, maxChars) + "…";
        }
    }
}
