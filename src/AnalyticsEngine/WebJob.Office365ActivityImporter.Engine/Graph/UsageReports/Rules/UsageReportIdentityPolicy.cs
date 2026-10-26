using System;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Rules
{
    /// <summary>
    /// Recognises the pseudonyms Graph puts in the daily usage reports when a tenant has "Display concealed user,
    /// group, and site names in all reports" turned on - Microsoft's default for new tenants. Graph still answers
    /// 200 OK with one row per user (or group), but the identity is a hash, so it can never be linked to a real
    /// record. Feeding it through the get-or-create path invented one placeholder user per licensed account (up to
    /// 200,000) with activity attached to it.
    /// </summary>
    /// <remarks>
    /// The user rule is the one the Copilot per-user report already applies
    /// (<see cref="CopilotUsageUserDetailRow.LooksLikeRealUpn"/>, used by <see cref="CopilotUsageReportPolicy"/>), so
    /// both report families agree on what a concealed identity is. A blank value is NOT concealed: that is the
    /// separate "no identifier" case, which the save loop skips and counts on its own.
    /// </remarks>
    public static class UsageReportIdentityPolicy
    {
        /// <summary>
        /// True when a user-keyed report row carries a pseudonym instead of a user principal name - a bare hash
        /// (no <c>@</c> at all), a <c>hash@hash</c> shape, or a 32-hex local part.
        /// </summary>
        public static bool IsConcealedUserIdentity(string value)
            => !string.IsNullOrWhiteSpace(value) && !CopilotUsageUserDetailRow.LooksLikeRealUpn(value);

        /// <summary>
        /// True when a group-keyed report row (Yammer / Viva Engage groups) carries a hash instead of a group name.
        /// Group names are free text, so an e-mail rule cannot apply; only the hash shape is rejected - one unbroken
        /// run of 32 (MD5-length) or 64 (SHA-256-length) hexadecimal characters, which a group someone actually
        /// named is vanishingly unlikely to be.
        /// </summary>
        public static bool IsConcealedGroupName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var trimmed = value.Trim();
            return (trimmed.Length == 32 || trimmed.Length == 64) && IsAllHex(trimmed);
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
    }

    /// <summary>
    /// Thrown when every identifiable row a daily usage report returned has a concealed identity. Nothing is saved:
    /// importing it would create one placeholder record per row.
    /// </summary>
    /// <remarks>
    /// Distinct from a Graph or SQL failure, because retrying cannot help until an administrator changes the
    /// tenant's report-privacy setting. The usage-report phase therefore reports it as a failed report, but does not
    /// let it disarm the once-a-day throttle - see <c>GraphImporter.GetAndSaveActivityReportsMultiThreaded</c>.
    /// </remarks>
    public class ConcealedUsageReportException : Exception
    {
        public ConcealedUsageReportException(string reportName, int concealedRowCount)
            : base($"IMPORTANT: usage reports have associated user email concealed - every one of the {concealedRowCount:N0} row(s) " +
                  $"{reportName} returned from Graph had a concealed (hashed) identity, because the tenant has 'Display concealed user, group, " +
                  "and site names in all reports' turned on. Nothing was saved for this report: the rows cannot be linked to real users, and " +
                  "importing them would create one placeholder record per row. To import this data, turn that setting off in the Microsoft 365 " +
                  "admin centre (Settings > Org settings > Services > Reports). See the Office 365 Advanced Analytics Engine prerequisites.")
        {
            ReportName = reportName;
            ConcealedRowCount = concealedRowCount;
        }

        public string ReportName { get; }

        public int ConcealedRowCount { get; }
    }
}
