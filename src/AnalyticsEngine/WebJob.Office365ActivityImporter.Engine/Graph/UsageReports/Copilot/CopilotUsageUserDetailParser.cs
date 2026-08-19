using Common.Entities.Entities.UsageReports;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// One parsed row of getMicrosoft365CopilotUsageUserDetail, before it is matched to a user in our
    /// database. Kept separate from <see cref="CopilotUsageUserActivityLog"/> so the tenant's identity
    /// concealment can be detected from the raw report - before anything is written, and before any user
    /// records would be created.
    /// </summary>
    public class CopilotUsageUserDetailRow
    {
        public DateTime ReportRefreshDate { get; set; }
        public string UserPrincipalName { get; set; }
        public string DisplayName { get; set; }
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

        public override string ToString() => $"{UserPrincipalName} @ {ReportRefreshDate:yyyy-MM-dd}";
    }

    /// <summary>
    /// Parses the getMicrosoft365CopilotUsageUserDetail CSV.
    ///
    /// Every version 2 column is read optionally: <c>version</c> defaults to v1 on the Graph side, and a v1
    /// response is a valid response that simply lacks the prompt-count, active-usage-day and newer-surface
    /// columns. Those land as NULL rather than 0 so "Graph didn't tell us" stays distinguishable from
    /// "the user submitted no prompts" - the two mean very different things in an adoption report.
    /// </summary>
    public static class CopilotUsageUserDetailParser
    {
        private const string ReportRefreshDateHeader = "Report Refresh Date";
        private const string UserPrincipalNameHeader = "User Principal Name";
        private const string DisplayNameHeader = "Display Name";
        private const string LastActivityDateHeader = "Last Activity Date";
        private const string ReportPeriodHeader = "Report Period";

        // Version 1 per-app columns.
        private const string ChatLastActivityHeader = "Copilot Chat Last Activity Date";
        private const string TeamsLastActivityHeader = "Microsoft Teams Copilot Last Activity Date";
        private const string WordLastActivityHeader = "Word Copilot Last Activity Date";
        private const string ExcelLastActivityHeader = "Excel Copilot Last Activity Date";
        private const string PowerPointLastActivityHeader = "PowerPoint Copilot Last Activity Date";
        private const string OutlookLastActivityHeader = "Outlook Copilot Last Activity Date";
        private const string OneNoteLastActivityHeader = "OneNote Copilot Last Activity Date";
        private const string LoopLastActivityHeader = "Loop Copilot Last Activity Date";

        // Version 2 additions.
        private const string PromptsAllAppsHeader = "Prompts submitted for all apps";
        private const string PromptsChatWorkHeader = "Prompts submitted for Copilot Chat (work)";
        private const string PromptsChatWebHeader = "Prompts submitted for Copilot Chat (web)";
        private const string ActiveUsageDaysHeader = "Active Usage Days for all apps";
        private const string ChatWorkLastActivityHeader = "Copilot Chat (work) Last Activity Date";
        private const string ChatWebLastActivityHeader = "Copilot Chat (web) Last Activity Date";
        private const string Microsoft365CopilotLastActivityHeader = "Microsoft 365 Copilot Last Activity Date";
        private const string EdgeLastActivityHeader = "Edge Last Activity Date";
        private const string AgentLastActivityHeader = "Copilot Agent Last Activity Date";

        /// <summary>
        /// Columns the loader cannot work without. A renamed column here must fail the import loudly rather
        /// than silently yield zero rows, which looks exactly like a tenant with no Copilot licences.
        /// </summary>
        public static readonly string[] RequiredHeaders =
        {
            ReportRefreshDateHeader, UserPrincipalNameHeader,
        };

        /// <summary>
        /// True when the report contains the version 2 columns. Used to log loudly if we asked for v2 and
        /// Graph answered with v1 anyway - otherwise the prompt columns would just silently be NULL.
        /// </summary>
        public static bool IsVersion2(CsvReportTable table) => IsVersion2(table?.Headers);

        public static bool IsVersion2(IReadOnlyList<string> headers)
        {
            if (headers == null) return false;
            return headers.Any(h => string.Equals(h, PromptsAllAppsHeader, StringComparison.OrdinalIgnoreCase))
                || headers.Any(h => string.Equals(h, ActiveUsageDaysHeader, StringComparison.OrdinalIgnoreCase));
        }

        public static List<CopilotUsageUserDetailRow> Parse(CsvReportTable table)
        {
            return table == null ? new List<CopilotUsageUserDetailRow>() : Parse(table.Rows);
        }

        public static List<CopilotUsageUserDetailRow> Parse(IEnumerable<IReadOnlyDictionary<string, string>> rows)
        {
            var results = new List<CopilotUsageUserDetailRow>();
            if (rows == null) return results;

            foreach (var row in rows)
            {
                var parsed = ParseRow(row);
                if (parsed != null) results.Add(parsed);
            }

            return results;
        }

        /// <summary>Returns null when the row has no date or no identity, so nothing can be keyed on it.</summary>
        public static CopilotUsageUserDetailRow ParseRow(IReadOnlyDictionary<string, string> row)
        {
            var refreshDate = row.GetDate(ReportRefreshDateHeader);
            var upn = row.GetString(UserPrincipalNameHeader);

            if (!refreshDate.HasValue || string.IsNullOrWhiteSpace(upn)) return null;

            return new CopilotUsageUserDetailRow
            {
                ReportRefreshDate = refreshDate.Value,
                UserPrincipalName = upn,
                DisplayName = row.GetString(DisplayNameHeader),
                ReportPeriodDays = row.GetInt(ReportPeriodHeader),
                LastActivityDate = row.GetDate(LastActivityDateHeader),

                PromptsAllApps = row.GetInt(PromptsAllAppsHeader),
                PromptsChatWork = row.GetInt(PromptsChatWorkHeader),
                PromptsChatWeb = row.GetInt(PromptsChatWebHeader),
                ActiveUsageDays = row.GetInt(ActiveUsageDaysHeader),

                ChatLastActivityDate = row.GetDate(ChatLastActivityHeader),
                TeamsLastActivityDate = row.GetDate(TeamsLastActivityHeader),
                WordLastActivityDate = row.GetDate(WordLastActivityHeader),
                ExcelLastActivityDate = row.GetDate(ExcelLastActivityHeader),
                PowerPointLastActivityDate = row.GetDate(PowerPointLastActivityHeader),
                OutlookLastActivityDate = row.GetDate(OutlookLastActivityHeader),
                OneNoteLastActivityDate = row.GetDate(OneNoteLastActivityHeader),
                LoopLastActivityDate = row.GetDate(LoopLastActivityHeader),
                ChatWorkLastActivityDate = row.GetDate(ChatWorkLastActivityHeader),
                ChatWebLastActivityDate = row.GetDate(ChatWebLastActivityHeader),
                Microsoft365CopilotLastActivityDate = row.GetDate(Microsoft365CopilotLastActivityHeader),
                EdgeLastActivityDate = row.GetDate(EdgeLastActivityHeader),
                AgentLastActivityDate = row.GetDate(AgentLastActivityHeader),
            };
        }
    }
}
