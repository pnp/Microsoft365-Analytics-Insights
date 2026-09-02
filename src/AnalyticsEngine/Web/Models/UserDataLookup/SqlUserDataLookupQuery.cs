using Common.Entities;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserDataLookup
{
    /// <summary>
    /// EF/SQL adapter for <see cref="IUserDataLookupQuery"/> - the only place in the user-data lookup
    /// that knows about <see cref="AnalyticsEntitiesContext"/>.
    /// </summary>
    /// <remarks>
    /// Designed for large tenants (~200k users): the user is found by a direct equality compare on the
    /// indexed, case-insensitive user_name column (no LOWER()/ToLower(), which would force a scan),
    /// counts hit indexed FK columns, and every detail query is bounded by Take(n).
    /// </remarks>
    public class SqlUserDataLookupQuery : IUserDataLookupQuery
    {
        private readonly IAnalyticsDbContextFactory _contextFactory;

        public SqlUserDataLookupQuery() : this(DefaultAnalyticsDbContextFactory.Instance)
        {
        }

        public SqlUserDataLookupQuery(IAnalyticsDbContextFactory contextFactory)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        public async Task<UserProfileModel> GetProfileAsync(string upn)
        {
            using (var db = _contextFactory.Create())
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

                return user == null ? null : BuildProfile(user);
            }
        }

        public async Task<int?> GetUserIdAsync(string upn)
        {
            using (var db = _contextFactory.Create())
            {
                return await db.users
                    .Where(u => u.UserPrincipalName == upn)
                    .Select(u => (int?)u.ID)
                    .FirstOrDefaultAsync();
            }
        }

        /// <summary>
        /// Every category count in ONE round trip. Each count is the same LINQ predicate the
        /// per-category query uses, projected as a sub-select, so EF emits a single statement instead of
        /// the ~30 sequential COUNT round trips this replaces. Behaviour is unchanged: the counts are
        /// identical, and <c>UserDataLookupSqlIntegrationTests</c> asserts that against
        /// <see cref="GetCountForCategoryAsync"/> for every category.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, int>> GetCountsByCategoryAsync(int userId)
        {
            using (var db = _contextFactory.Create())
            {
                var counts = await db.users
                    .Where(u => u.ID == userId)
                    .Select(u => new AllCategoryCounts
                    {
                        AuditEvents = db.AuditEventsCommon.Count(e => e.UserId == userId),
                        SentEmails = db.SentEmails.Count(e => e.UserID == userId),
                        WebHits = db.hits.Count(h => h.session.user.ID == userId),
                        TeamMemberships = db.TeamMembershipLogs.Count(t => t.UserID == userId),
                        TeamOwnerships = db.TeamOwners.Count(t => t.OwnerID == userId),
                        TeamsReactions = db.TeamsUserReactions.Count(r => r.UserID == userId),
                        CallsOrganised = db.CallRecords.Count(c => c.OrganizerID == userId),
                        CallSessions = db.CallSessions.Count(s => s.AttendeeUserID == userId),
                        CallFeedback = db.CallFeedback.Count(f => f.UserID == userId),
                        PageLikes = db.UrlLikes.Count(l => l.UserID == userId),
                        PageComments = db.UrlComments.Count(c => c.UserID == userId),
                        UsageOutlook = db.OutlookUsageActivityLogs.Count(x => x.UserID == userId),
                        UsageOneDrive = db.OneDriveUserActivityLogs.Count(x => x.UserID == userId),
                        UsageSharePoint = db.SharePointUserActivityLogs.Count(x => x.UserID == userId),
                        UsageYammer = db.YammerUserActivityLogs.Count(x => x.UserID == userId),
                        UsageTeams = db.TeamUserActivityLogs.Count(x => x.UserID == userId),
                        UsageTeamsDevice = db.TeamsUserDeviceUsageLog.Count(x => x.UserID == userId),
                        UsageAppPlatform = db.AppPlatformUserUsageLog.Count(x => x.UserID == userId),
                        PowerAppShares = db.power_app_share_events.Count(s => s.SharedWithUserId == userId),
                        FlowShares = db.power_automate_flow_share_events.Count(s => s.SharedWithUserId == userId),
                        Copilot = db.CopilotChats.Count(c => c.AuditEvent.UserId == userId),
                        AuditSharePoint = db.sharepoint_events.Count(c => c.AuditEvent.UserId == userId),
                        AuditExchange = db.exchange_events.Count(c => c.AuditEvent.UserId == userId),
                        AuditEntra = db.azure_ad_events.Count(c => c.AuditEvent.UserId == userId),
                        AuditGeneral = db.general_audit_events.Count(c => c.AuditEvent.UserId == userId),
                        AuditStream = db.StreamEvents.Count(c => c.AuditEvent.UserId == userId),
                        PowerAppEvents = db.power_app_events.Count(c => c.AuditEvent.UserId == userId),
                        FlowEvents = db.power_automate_flow_events.Count(c => c.AuditEvent.UserId == userId),
                        PowerBiEvents = db.power_bi_events.Count(c => c.AuditEvent.UserId == userId),
                        CopilotStudioEvents = db.copilot_studio_events.Count(c => c.AuditEvent.UserId == userId),
                    })
                    .FirstOrDefaultAsync();

                // The user row was deleted between finding it and counting: every per-category count
                // would have returned 0, so return zeros rather than an incomplete dictionary.
                return ToDictionary(counts ?? new AllCategoryCounts());
            }
        }

        public async Task<int> GetCountForCategoryAsync(int userId, string categoryKey)
        {
            using (var db = _contextFactory.Create())
            {
                return await CountForCategoryAsync(db, userId, categoryKey);
            }
        }

        public async Task<IReadOnlyList<UserDataDetailRowModel>> GetRowsForCategoryAsync(int userId, string categoryKey, int take)
        {
            using (var db = _contextFactory.Create())
            {
                return await DetailForCategoryAsync(db, userId, categoryKey, take);
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
                case UserDataLookupRules.CatAuditEvents:
                    return db.AuditEventsCommon.Where(e => e.UserId == userId).CountAsync();
                case UserDataLookupRules.CatSentEmails:
                    return db.SentEmails.Where(e => e.UserID == userId).CountAsync();
                case UserDataLookupRules.CatWebHits:
                    return db.hits.Where(h => h.session.user.ID == userId).CountAsync();
                case UserDataLookupRules.CatTeamMemberships:
                    return db.TeamMembershipLogs.Where(t => t.UserID == userId).CountAsync();
                case UserDataLookupRules.CatTeamOwnerships:
                    return db.TeamOwners.Where(t => t.OwnerID == userId).CountAsync();
                case UserDataLookupRules.CatTeamsReactions:
                    return db.TeamsUserReactions.Where(r => r.UserID == userId).CountAsync();
                case UserDataLookupRules.CatCallsOrganised:
                    return db.CallRecords.Where(c => c.OrganizerID == userId).CountAsync();
                case UserDataLookupRules.CatCallSessions:
                    return db.CallSessions.Where(s => s.AttendeeUserID == userId).CountAsync();
                case UserDataLookupRules.CatCallFeedback:
                    return db.CallFeedback.Where(f => f.UserID == userId).CountAsync();
                case UserDataLookupRules.CatPageLikes:
                    return db.UrlLikes.Where(l => l.UserID == userId).CountAsync();
                case UserDataLookupRules.CatPageComments:
                    return db.UrlComments.Where(c => c.UserID == userId).CountAsync();
                case UserDataLookupRules.CatUsageOutlook:
                    return db.OutlookUsageActivityLogs.Where(x => x.UserID == userId).CountAsync();
                case UserDataLookupRules.CatUsageOneDrive:
                    return db.OneDriveUserActivityLogs.Where(x => x.UserID == userId).CountAsync();
                case UserDataLookupRules.CatUsageSharePoint:
                    return db.SharePointUserActivityLogs.Where(x => x.UserID == userId).CountAsync();
                case UserDataLookupRules.CatUsageYammer:
                    return db.YammerUserActivityLogs.Where(x => x.UserID == userId).CountAsync();
                case UserDataLookupRules.CatUsageTeams:
                    return db.TeamUserActivityLogs.Where(x => x.UserID == userId).CountAsync();
                case UserDataLookupRules.CatUsageTeamsDevice:
                    return db.TeamsUserDeviceUsageLog.Where(x => x.UserID == userId).CountAsync();
                case UserDataLookupRules.CatUsageAppPlatform:
                    return db.AppPlatformUserUsageLog.Where(x => x.UserID == userId).CountAsync();
                case UserDataLookupRules.CatPowerAppShares:
                    return db.power_app_share_events.Where(s => s.SharedWithUserId == userId).CountAsync();
                case UserDataLookupRules.CatFlowShares:
                    return db.power_automate_flow_share_events.Where(s => s.SharedWithUserId == userId).CountAsync();
                case UserDataLookupRules.CatCopilot:
                    return db.CopilotChats.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case UserDataLookupRules.CatAuditSharePoint:
                    return db.sharepoint_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case UserDataLookupRules.CatAuditExchange:
                    return db.exchange_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case UserDataLookupRules.CatAuditEntra:
                    return db.azure_ad_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case UserDataLookupRules.CatAuditGeneral:
                    return db.general_audit_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case UserDataLookupRules.CatAuditStream:
                    return db.StreamEvents.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case UserDataLookupRules.CatPowerAppEvents:
                    return db.power_app_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case UserDataLookupRules.CatFlowEvents:
                    return db.power_automate_flow_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case UserDataLookupRules.CatPowerBiEvents:
                    return db.power_bi_events.Where(c => c.AuditEvent.UserId == userId).CountAsync();
                case UserDataLookupRules.CatCopilotStudioEvents:
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
                case UserDataLookupRules.CatAuditEvents:
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
                        Detail = UserDataLookupRules.Truncate(r.EventData, UserDataLookupRules.MaxEventDataChars),
                    }).ToList();
                }
                case UserDataLookupRules.CatSentEmails:
                {
                    var raw = await db.SentEmails
                        .Where(e => e.UserID == userId)
                        .OrderByDescending(e => e.SentDate)
                        .Take(take)
                        .Select(e => new { e.SentDate, e.Subject })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.SentDate, Title = r.Subject }).ToList();
                }
                case UserDataLookupRules.CatWebHits:
                {
                    var raw = await db.hits
                        .Where(h => h.session.user.ID == userId)
                        .OrderByDescending(h => h.hit_timestamp)
                        .Take(take)
                        .Select(h => new { h.hit_timestamp, Url = h.url != null ? h.url.FullUrl : null })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.hit_timestamp, Title = r.Url }).ToList();
                }
                case UserDataLookupRules.CatTeamMemberships:
                {
                    var raw = await db.TeamMembershipLogs
                        .Where(t => t.UserID == userId)
                        .OrderByDescending(t => t.Date)
                        .Take(take)
                        .Select(t => new { t.Date, Team = t.Team != null ? t.Team.Name : null })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.Date, Title = r.Team }).ToList();
                }
                case UserDataLookupRules.CatTeamOwnerships:
                {
                    var raw = await db.TeamOwners
                        .Where(t => t.OwnerID == userId)
                        .OrderByDescending(t => t.Discovered)
                        .Take(take)
                        .Select(t => new { t.Discovered, Team = t.Team != null ? t.Team.Name : null })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.Discovered, Title = r.Team }).ToList();
                }
                case UserDataLookupRules.CatTeamsReactions:
                {
                    var raw = await db.TeamsUserReactions
                        .Where(r => r.UserID == userId)
                        .OrderByDescending(r => r.Date)
                        .Take(take)
                        .Select(r => new { r.Date, Reaction = r.Reaction != null ? r.Reaction.Name : null })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.Date, Title = r.Reaction }).ToList();
                }
                case UserDataLookupRules.CatCallsOrganised:
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
                case UserDataLookupRules.CatCallSessions:
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
                case UserDataLookupRules.CatPageLikes:
                {
                    var raw = await db.UrlLikes
                        .Where(l => l.UserID == userId)
                        .OrderByDescending(l => l.Created)
                        .Take(take)
                        .Select(l => new { l.Created, Url = l.Url != null ? l.Url.FullUrl : null })
                        .ToListAsync();
                    return raw.Select(r => new UserDataDetailRowModel { Timestamp = r.Created, Title = r.Url }).ToList();
                }
                case UserDataLookupRules.CatPageComments:
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
                        Detail = UserDataLookupRules.Truncate(r.Comment, UserDataLookupRules.MaxEventDataChars),
                    }).ToList();
                }
                case UserDataLookupRules.CatUsageOutlook:
                    return await UsageDetailAsync(db.OutlookUsageActivityLogs.Where(x => x.UserID == userId), take);
                case UserDataLookupRules.CatUsageOneDrive:
                    return await UsageDetailAsync(db.OneDriveUserActivityLogs.Where(x => x.UserID == userId), take);
                case UserDataLookupRules.CatUsageSharePoint:
                    return await UsageDetailAsync(db.SharePointUserActivityLogs.Where(x => x.UserID == userId), take);
                case UserDataLookupRules.CatUsageYammer:
                    return await UsageDetailAsync(db.YammerUserActivityLogs.Where(x => x.UserID == userId), take);
                case UserDataLookupRules.CatUsageTeams:
                    return await UsageDetailAsync(db.TeamUserActivityLogs.Where(x => x.UserID == userId), take);
                case UserDataLookupRules.CatUsageTeamsDevice:
                    return await UsageDetailAsync(db.TeamsUserDeviceUsageLog.Where(x => x.UserID == userId), take);
                case UserDataLookupRules.CatUsageAppPlatform:
                    return await UsageDetailAsync(db.AppPlatformUserUsageLog.Where(x => x.UserID == userId), take);
                case UserDataLookupRules.CatCopilot:
                    return await AuditChildDetailAsync(db.CopilotChats.Where(c => c.AuditEvent.UserId == userId), take);
                case UserDataLookupRules.CatAuditSharePoint:
                    return await AuditChildDetailAsync(db.sharepoint_events.Where(c => c.AuditEvent.UserId == userId), take);
                case UserDataLookupRules.CatAuditExchange:
                    return await AuditChildDetailAsync(db.exchange_events.Where(c => c.AuditEvent.UserId == userId), take);
                case UserDataLookupRules.CatAuditEntra:
                    return await AuditChildDetailAsync(db.azure_ad_events.Where(c => c.AuditEvent.UserId == userId), take);
                case UserDataLookupRules.CatAuditGeneral:
                    return await AuditChildDetailAsync(db.general_audit_events.Where(c => c.AuditEvent.UserId == userId), take);
                case UserDataLookupRules.CatAuditStream:
                    return await AuditChildDetailAsync(db.StreamEvents.Where(c => c.AuditEvent.UserId == userId), take);
                case UserDataLookupRules.CatPowerAppEvents:
                    return await AuditChildDetailAsync(db.power_app_events.Where(c => c.AuditEvent.UserId == userId), take);
                case UserDataLookupRules.CatFlowEvents:
                    return await AuditChildDetailAsync(db.power_automate_flow_events.Where(c => c.AuditEvent.UserId == userId), take);
                case UserDataLookupRules.CatPowerBiEvents:
                    return await AuditChildDetailAsync(db.power_bi_events.Where(c => c.AuditEvent.UserId == userId), take);
                case UserDataLookupRules.CatCopilotStudioEvents:
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

        private static IReadOnlyDictionary<string, int> ToDictionary(AllCategoryCounts c)
        {
            return new Dictionary<string, int>
            {
                { UserDataLookupRules.CatAuditEvents, c.AuditEvents },
                { UserDataLookupRules.CatSentEmails, c.SentEmails },
                { UserDataLookupRules.CatWebHits, c.WebHits },
                { UserDataLookupRules.CatTeamMemberships, c.TeamMemberships },
                { UserDataLookupRules.CatTeamOwnerships, c.TeamOwnerships },
                { UserDataLookupRules.CatTeamsReactions, c.TeamsReactions },
                { UserDataLookupRules.CatCallsOrganised, c.CallsOrganised },
                { UserDataLookupRules.CatCallSessions, c.CallSessions },
                { UserDataLookupRules.CatCallFeedback, c.CallFeedback },
                { UserDataLookupRules.CatPageLikes, c.PageLikes },
                { UserDataLookupRules.CatPageComments, c.PageComments },
                { UserDataLookupRules.CatUsageOutlook, c.UsageOutlook },
                { UserDataLookupRules.CatUsageOneDrive, c.UsageOneDrive },
                { UserDataLookupRules.CatUsageSharePoint, c.UsageSharePoint },
                { UserDataLookupRules.CatUsageYammer, c.UsageYammer },
                { UserDataLookupRules.CatUsageTeams, c.UsageTeams },
                { UserDataLookupRules.CatUsageTeamsDevice, c.UsageTeamsDevice },
                { UserDataLookupRules.CatUsageAppPlatform, c.UsageAppPlatform },
                { UserDataLookupRules.CatPowerAppShares, c.PowerAppShares },
                { UserDataLookupRules.CatFlowShares, c.FlowShares },
                { UserDataLookupRules.CatCopilot, c.Copilot },
                { UserDataLookupRules.CatAuditSharePoint, c.AuditSharePoint },
                { UserDataLookupRules.CatAuditExchange, c.AuditExchange },
                { UserDataLookupRules.CatAuditEntra, c.AuditEntra },
                { UserDataLookupRules.CatAuditGeneral, c.AuditGeneral },
                { UserDataLookupRules.CatAuditStream, c.AuditStream },
                { UserDataLookupRules.CatPowerAppEvents, c.PowerAppEvents },
                { UserDataLookupRules.CatFlowEvents, c.FlowEvents },
                { UserDataLookupRules.CatPowerBiEvents, c.PowerBiEvents },
                { UserDataLookupRules.CatCopilotStudioEvents, c.CopilotStudioEvents },
            };
        }

        /// <summary>Projection target for the single-round-trip category counts.</summary>
        private class AllCategoryCounts
        {
            public int AuditEvents { get; set; }
            public int SentEmails { get; set; }
            public int WebHits { get; set; }
            public int TeamMemberships { get; set; }
            public int TeamOwnerships { get; set; }
            public int TeamsReactions { get; set; }
            public int CallsOrganised { get; set; }
            public int CallSessions { get; set; }
            public int CallFeedback { get; set; }
            public int PageLikes { get; set; }
            public int PageComments { get; set; }
            public int UsageOutlook { get; set; }
            public int UsageOneDrive { get; set; }
            public int UsageSharePoint { get; set; }
            public int UsageYammer { get; set; }
            public int UsageTeams { get; set; }
            public int UsageTeamsDevice { get; set; }
            public int UsageAppPlatform { get; set; }
            public int PowerAppShares { get; set; }
            public int FlowShares { get; set; }
            public int Copilot { get; set; }
            public int AuditSharePoint { get; set; }
            public int AuditExchange { get; set; }
            public int AuditEntra { get; set; }
            public int AuditGeneral { get; set; }
            public int AuditStream { get; set; }
            public int PowerAppEvents { get; set; }
            public int FlowEvents { get; set; }
            public int PowerBiEvents { get; set; }
            public int CopilotStudioEvents { get; set; }
        }
    }
}
