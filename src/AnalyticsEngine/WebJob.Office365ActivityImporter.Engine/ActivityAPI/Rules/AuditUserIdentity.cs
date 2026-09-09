using System;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules
{
    /// <summary>What kind of identifier an audit record's <c>UserId</c> turned out to be.</summary>
    public enum AuditUserIdKind
    {
        /// <summary>Nothing usable - null, empty or whitespace.</summary>
        Missing = 0,

        /// <summary>A user principal name, which is what <c>dbo.users.user_name</c> stores.</summary>
        Upn = 1,

        /// <summary>An Entra ID object id (GUID). Needs resolving to a UPN before it can be stored.</summary>
        EntraObjectId = 2,

        /// <summary>
        /// A service or system principal rather than a person - <c>app@sharepoint</c>, <c>DlpAgent</c>,
        /// a Windows SID, and similar.
        /// </summary>
        SystemAccount = 3,

        /// <summary>Something else. Stored verbatim, exactly as before.</summary>
        Other = 4,
    }

    /// <summary>
    /// Classifies the Office 365 Management Activity API's common-schema <c>UserId</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The importer stages <c>UserId</c> straight into <c>dbo.users.user_name</c>, and the merge creates
    /// a user row for any value it has not seen. That is correct for the overwhelmingly common case,
    /// where <c>UserId</c> is a UPN - but the common schema does not guarantee one. Microsoft documents
    /// the same field carrying Entra object ids (GUIDs), Windows SIDs and service principals such as
    /// <c>app@sharepoint</c>, and DLP records additionally carry the literal <c>DlpAgent</c> in
    /// <c>UserKey</c>. Each distinct non-UPN value silently becomes its own "user", so one real person
    /// can be split across several rows and every per-user report under-counts them.
    /// https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#common-schema
    /// </para>
    /// <para>
    /// Pure and allocation-light on the hot path: the UPN case - which is nearly every event - is
    /// settled by a single character scan and never allocates or calls anything.
    /// </para>
    /// </remarks>
    public static class AuditUserIdentity
    {
        /// <summary>
        /// Service principal that SharePoint attributes app-only activity to. Not a person, and must
        /// never be resolved against Entra or counted as a user.
        /// </summary>
        public const string SharePointAppAccount = "app@sharepoint";

        /// <summary>The literal DLP records carry instead of a principal.</summary>
        public const string DlpAgentAccount = "DlpAgent";

        /// <summary>
        /// Classifies <paramref name="userId"/>. Never throws - an unrecognised value is
        /// <see cref="AuditUserIdKind.Other"/> and keeps today's behaviour.
        /// </summary>
        public static AuditUserIdKind Classify(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return AuditUserIdKind.Missing;
            }

            var trimmed = userId.Trim();

            if (IsSystemAccount(trimmed))
            {
                return AuditUserIdKind.SystemAccount;
            }

            // A UPN is the expected shape and by far the most common, so settle it before the more
            // expensive checks. Deliberately a shape test, not validation: Entra restricts the
            // characters a UPN may contain, but this code's job is routing, not policing.
            if (trimmed.IndexOf('@') > 0)
            {
                return AuditUserIdKind.Upn;
            }

            Guid ignored;
            if (Guid.TryParse(trimmed, out ignored))
            {
                return AuditUserIdKind.EntraObjectId;
            }

            return AuditUserIdKind.Other;
        }

        /// <summary>
        /// True when the value names a service/system principal rather than a person.
        /// </summary>
        /// <remarks>
        /// A Windows SID is matched by prefix rather than by a full parse: the point is only to keep it
        /// out of Entra resolution and out of the user population, and the exact sub-authority layout
        /// does not change that answer.
        /// </remarks>
        public static bool IsSystemAccount(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            var trimmed = userId.Trim();

            return trimmed.Equals(SharePointAppAccount, StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals(DlpAgentAccount, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the value must be resolved before it can be stored as a user. Only Entra object
        /// ids qualify: everything else is either already a UPN, not a person, or unrecognised - and
        /// resolving an unrecognised value would be guesswork.
        /// </summary>
        public static bool NeedsResolving(string userId)
        {
            return Classify(userId) == AuditUserIdKind.EntraObjectId;
        }
    }
}
