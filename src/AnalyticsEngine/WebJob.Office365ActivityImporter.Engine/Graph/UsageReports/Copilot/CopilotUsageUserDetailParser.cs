using DataUtils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// One parsed row of getMicrosoft365CopilotUsageUserDetail - a single user for a single report period -
    /// before it is matched to a user in our database. Kept separate from the EF entity so the tenant's
    /// identity concealment can be detected from the raw report, before anything is written and before any
    /// user records would be created.
    /// </summary>
    public class CopilotUsageUserDetailRow
    {
        public DateTime ReportRefreshDate { get; set; }
        public string UserPrincipalName { get; set; }
        public int? ReportPeriodDays { get; set; }
        public DateTime? LastActivityDate { get; set; }

        public int? PromptsAllApps { get; set; }
        public int? PromptsChatWork { get; set; }
        public int? PromptsChatWeb { get; set; }
        public int? ActiveUsageDays { get; set; }

        public DateTime? ChatLastActivityDate { get; set; }
        public DateTime? TeamsLastActivityDate { get; set; }
        public DateTime? WordLastActivityDate { get; set; }
        public DateTime? ExcelLastActivityDate { get; set; }
        public DateTime? PowerPointLastActivityDate { get; set; }
        public DateTime? OutlookLastActivityDate { get; set; }
        public DateTime? OneNoteLastActivityDate { get; set; }
        public DateTime? LoopLastActivityDate { get; set; }
        public DateTime? ChatWorkLastActivityDate { get; set; }
        public DateTime? ChatWebLastActivityDate { get; set; }
        public DateTime? Microsoft365CopilotLastActivityDate { get; set; }
        public DateTime? EdgeLastActivityDate { get; set; }
        public DateTime? AgentLastActivityDate { get; set; }

        /// <summary>True when this row carried any report-version 2 value.</summary>
        public bool HasVersion2Data =>
            PromptsAllApps.HasValue || PromptsChatWork.HasValue || PromptsChatWeb.HasValue
            || ActiveUsageDays.HasValue || ChatWorkLastActivityDate.HasValue || ChatWebLastActivityDate.HasValue
            || Microsoft365CopilotLastActivityDate.HasValue || EdgeLastActivityDate.HasValue
            || AgentLastActivityDate.HasValue;

        /// <summary>
        /// True when this row's identity is a hash rather than a real user principal name, because the tenant
        /// conceals user information in Microsoft 365 usage reports. Microsoft's own documentation example for
        /// this report shows a hashed UPN, so this is a normal tenant configuration, not a rare edge case.
        ///
        /// Deliberately stricter than "does <c>MailAddress</c> accept it": a bare <c>MailAddress</c> check
        /// accepts things like <c>hash@hash</c>. This is defence in depth only - syntax can never prove an
        /// identity belongs to the tenant, so the loader's real safety boundary is that it will not invent a
        /// user for an unrecognised email domain.
        /// </summary>
        public bool IsIdentityConcealed => !LooksLikeRealUpn(UserPrincipalName);

        /// <summary>Length of the hashes Microsoft substitutes when identities are concealed.</summary>
        private const int ConcealedIdentityHashLength = 32;

        public static bool LooksLikeRealUpn(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (!StringUtils.IsEmail(value)) return false;

            var at = value.LastIndexOf('@');
            if (at <= 0 || at == value.Length - 1) return false;

            // A domain with no dot is not a routable UPN suffix, and is what a "hash@hash" pseudonym looks like.
            var domain = value.Substring(at + 1);
            if (domain.IndexOf('.') <= 0 || domain.EndsWith(".")) return false;

            // A local part that is exactly a hash - 32 unbroken hex characters - is a pseudonym. The length is
            // matched exactly rather than "16 or more" so that a genuine account which happens to be spellable
            // in hex (0123456789abcdef@contoso.com) is not rejected.
            var localPart = value.Substring(0, at);
            if (localPart.Length == ConcealedIdentityHashLength && IsAllHex(localPart)) return false;

            return true;
        }

        private static bool IsAllHex(string value)
        {
            foreach (var c in value)
            {
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) return false;
            }
            return true;
        }

        public override string ToString() => $"{UserPrincipalName} @ {ReportRefreshDate:yyyy-MM-dd} (D{ReportPeriodDays})";
    }

    /// <summary>
    /// Parses getMicrosoft365CopilotUsageUserDetail.
    ///
    /// Each user object carries the per-app last-activity dates at the top level and a
    /// <c>copilotActivityUserDetailsByPeriod</c> array underneath - one entry per report period, which is
    /// where the version 2 prompt and active-usage-day counters live. One user therefore yields one row per
    /// period, matching the table's (date, user, period) key.
    ///
    /// Every version 2 field is read optionally. Microsoft has not published the beta JSON schema for
    /// version 2, so each is looked up under more than one plausible name and simply stays null if absent.
    /// Null rather than 0 matters: "Graph didn't tell us" and "the user submitted no prompts" mean very
    /// different things in an adoption report, and the loader uses the difference to avoid overwriting good
    /// version 2 data with a version 1 response.
    /// </summary>
    public static class CopilotUsageUserDetailParser
    {
        private const string ReportRefreshDateProperty = "reportRefreshDate";
        private const string UserPrincipalNameProperty = "userPrincipalName";
        private const string LastActivityDateProperty = "lastActivityDate";
        private const string DetailsByPeriodProperty = "copilotActivityUserDetailsByPeriod";
        private const string ReportPeriodProperty = "reportPeriod";

        // Version 1 per-app properties (confirmed from the published beta response example).
        private const string ChatLastActivityProperty = "copilotChatLastActivityDate";
        private const string TeamsLastActivityProperty = "microsoftTeamsCopilotLastActivityDate";
        private const string WordLastActivityProperty = "wordCopilotLastActivityDate";
        private const string ExcelLastActivityProperty = "excelCopilotLastActivityDate";
        private const string PowerPointLastActivityProperty = "powerPointCopilotLastActivityDate";
        private const string OutlookLastActivityProperty = "outlookCopilotLastActivityDate";
        private const string OneNoteLastActivityProperty = "oneNoteCopilotLastActivityDate";
        private const string LoopLastActivityProperty = "loopCopilotLastActivityDate";

        // Version 2 additions. Names are best-effort - see the class remarks.
        private static readonly string[] ChatWorkLastActivityProperties = { "copilotChatWorkLastActivityDate", "microsoft365CopilotChatWorkLastActivityDate" };
        private static readonly string[] ChatWebLastActivityProperties = { "copilotChatWebLastActivityDate", "microsoft365CopilotChatWebLastActivityDate" };
        private static readonly string[] Microsoft365CopilotLastActivityProperties = { "microsoft365CopilotLastActivityDate" };
        private static readonly string[] EdgeLastActivityProperties = { "edgeCopilotLastActivityDate", "edgeLastActivityDate" };
        private static readonly string[] AgentLastActivityProperties = { "copilotAgentLastActivityDate", "agentLastActivityDate" };

        private static readonly string[] PromptsAllAppsProperties = { "promptsSubmitted", "promptsSubmittedForAllApps", "totalPromptsSubmitted" };
        private static readonly string[] PromptsChatWorkProperties = { "promptsSubmittedForCopilotChatWork", "copilotChatWorkPromptsSubmitted" };
        private static readonly string[] PromptsChatWebProperties = { "promptsSubmittedForCopilotChatWeb", "copilotChatWebPromptsSubmitted" };
        private static readonly string[] ActiveUsageDaysProperties = { "activeUsageDays", "activeUsageDaysForAllApps" };

        /// <summary>True when any row in the response carried report-version 2 data.</summary>
        public static bool HasVersion2Data(IEnumerable<CopilotUsageUserDetailRow> rows)
        {
            return rows != null && rows.Any(r => r.HasVersion2Data);
        }

        public static List<CopilotUsageUserDetailRow> Parse(IEnumerable<JObject> reports)
        {
            var results = new List<CopilotUsageUserDetailRow>();
            if (reports == null) return results;

            foreach (var report in reports)
            {
                ParseUserInto(report, results);
            }

            return results;
        }

        /// <summary>
        /// Appends one row per report period for a single user. Nothing is added when the object has no date
        /// or no identity, since there would be nothing to key a row on.
        /// </summary>
        public static void ParseUserInto(JObject user, List<CopilotUsageUserDetailRow> into)
        {
            if (user == null) return;

            var refreshDate = CopilotUserCountReportParser.GetDate(user, ReportRefreshDateProperty);
            var upn = user[UserPrincipalNameProperty]?.Value<string>()?.Trim();

            if (!refreshDate.HasValue || string.IsNullOrWhiteSpace(upn)) return;

            var periods = user[DetailsByPeriodProperty] as JArray;

            // A response with no per-period breakdown still describes the user; keep it with an unknown
            // period, which the loader fills from the requested window.
            if (periods == null || periods.Count == 0)
            {
                into.Add(BuildRow(user, refreshDate.Value, upn, null));
                return;
            }

            foreach (var period in periods.OfType<JObject>())
            {
                into.Add(BuildRow(user, refreshDate.Value, upn, period));
            }
        }

        private static CopilotUsageUserDetailRow BuildRow(JObject user, DateTime refreshDate, string upn, JObject period)
        {
            return new CopilotUsageUserDetailRow
            {
                ReportRefreshDate = refreshDate,
                UserPrincipalName = upn,
                ReportPeriodDays = period == null ? null : CopilotUserCountReportParser.GetInt(period, ReportPeriodProperty),
                LastActivityDate = CopilotUserCountReportParser.GetDate(user, LastActivityDateProperty),

                // The counters live on the period entry; the dates live on the user.
                PromptsAllApps = GetIntAny(period, PromptsAllAppsProperties),
                PromptsChatWork = GetIntAny(period, PromptsChatWorkProperties),
                PromptsChatWeb = GetIntAny(period, PromptsChatWebProperties),
                ActiveUsageDays = GetIntAny(period, ActiveUsageDaysProperties),

                ChatLastActivityDate = CopilotUserCountReportParser.GetDate(user, ChatLastActivityProperty),
                TeamsLastActivityDate = CopilotUserCountReportParser.GetDate(user, TeamsLastActivityProperty),
                WordLastActivityDate = CopilotUserCountReportParser.GetDate(user, WordLastActivityProperty),
                ExcelLastActivityDate = CopilotUserCountReportParser.GetDate(user, ExcelLastActivityProperty),
                PowerPointLastActivityDate = CopilotUserCountReportParser.GetDate(user, PowerPointLastActivityProperty),
                OutlookLastActivityDate = CopilotUserCountReportParser.GetDate(user, OutlookLastActivityProperty),
                OneNoteLastActivityDate = CopilotUserCountReportParser.GetDate(user, OneNoteLastActivityProperty),
                LoopLastActivityDate = CopilotUserCountReportParser.GetDate(user, LoopLastActivityProperty),
                ChatWorkLastActivityDate = GetDateAny(user, ChatWorkLastActivityProperties),
                ChatWebLastActivityDate = GetDateAny(user, ChatWebLastActivityProperties),
                Microsoft365CopilotLastActivityDate = GetDateAny(user, Microsoft365CopilotLastActivityProperties),
                EdgeLastActivityDate = GetDateAny(user, EdgeLastActivityProperties),
                AgentLastActivityDate = GetDateAny(user, AgentLastActivityProperties),
            };
        }

        private static int? GetIntAny(JObject source, string[] properties)
        {
            if (source == null) return null;
            foreach (var property in properties)
            {
                var value = CopilotUserCountReportParser.GetInt(source, property);
                if (value.HasValue) return value;
            }
            return null;
        }

        private static DateTime? GetDateAny(JObject source, string[] properties)
        {
            if (source == null) return null;
            foreach (var property in properties)
            {
                var value = CopilotUserCountReportParser.GetDate(source, property);
                if (value.HasValue) return value;
            }
            return null;
        }
    }
}
