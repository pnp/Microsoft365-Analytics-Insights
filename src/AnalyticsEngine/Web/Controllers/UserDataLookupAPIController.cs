using Common.Entities;
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

        private sealed class CategoryMeta
        {
            public string Key;
            public string Label;
            public string Description;
            public bool SupportsDetail;
        }

        // Ordered list that drives both the summary and the detail label lookup.
        private static readonly CategoryMeta[] CategoryMetas =
        {
            new CategoryMeta { Key = CatAuditEvents, Label = "Audit events", SupportsDetail = true,
                Description = "SharePoint, Exchange, Entra ID, Copilot, Power Platform and other audit activity." },
            new CategoryMeta { Key = CatSentEmails, Label = "Sent emails", SupportsDetail = true,
                Description = "Messages sent from the user's mailbox." },
            new CategoryMeta { Key = CatWebHits, Label = "Web page hits", SupportsDetail = true,
                Description = "SharePoint page views captured by the web traffic tracker." },
            new CategoryMeta { Key = CatTeamMemberships, Label = "Team memberships", SupportsDetail = true,
                Description = "Teams the user has been a member of." },
            new CategoryMeta { Key = CatTeamOwnerships, Label = "Team ownerships", SupportsDetail = true,
                Description = "Teams the user owns / has owned." },
            new CategoryMeta { Key = CatTeamsReactions, Label = "Teams reactions", SupportsDetail = true,
                Description = "Reactions the user made on Teams channel messages." },
            new CategoryMeta { Key = CatCallsOrganised, Label = "Calls / meetings organised", SupportsDetail = true,
                Description = "Teams calls / meetings the user organised." },
            new CategoryMeta { Key = CatCallSessions, Label = "Call sessions attended", SupportsDetail = true,
                Description = "Teams call sessions the user attended." },
            new CategoryMeta { Key = CatPageLikes, Label = "Page likes", SupportsDetail = true,
                Description = "SharePoint page likes by the user." },
            new CategoryMeta { Key = CatPageComments, Label = "Page comments", SupportsDetail = true,
                Description = "SharePoint page comments by the user." },
            new CategoryMeta { Key = CatAppInstalls, Label = "Teams app installs", SupportsDetail = true,
                Description = "Teams apps installed by the user." },
            new CategoryMeta { Key = CatUsageOutlook, Label = "Outlook usage (daily)", SupportsDetail = true,
                Description = "Daily Outlook activity report rows." },
            new CategoryMeta { Key = CatUsageOneDrive, Label = "OneDrive usage (daily)", SupportsDetail = true,
                Description = "Daily OneDrive activity report rows." },
            new CategoryMeta { Key = CatUsageSharePoint, Label = "SharePoint usage (daily)", SupportsDetail = true,
                Description = "Daily SharePoint activity report rows." },
            new CategoryMeta { Key = CatUsageYammer, Label = "Viva Engage usage (daily)", SupportsDetail = true,
                Description = "Daily Viva Engage (Yammer) activity report rows." },
            new CategoryMeta { Key = CatUsageTeams, Label = "Teams usage (daily)", SupportsDetail = true,
                Description = "Daily Teams activity report rows." },
            new CategoryMeta { Key = CatUsageTeamsDevice, Label = "Teams device usage (daily)", SupportsDetail = true,
                Description = "Daily Teams device activity report rows." },
            new CategoryMeta { Key = CatUsageAppPlatform, Label = "App platform usage (daily)", SupportsDetail = true,
                Description = "Daily per-app platform activity report rows." },
            new CategoryMeta { Key = CatPowerAppShares, Label = "Power App shares received", SupportsDetail = false,
                Description = "Power Apps shared with the user." },
            new CategoryMeta { Key = CatFlowShares, Label = "Flow shares received", SupportsDetail = false,
                Description = "Power Automate flows shared with the user." },
        };

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
                default:
                    return new List<UserDataDetailRowModel>();
            }
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
