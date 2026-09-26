using System;
using System.Collections.Generic;

namespace Common.Entities.UserOrgs
{
    /// <summary>Where an org type's values come from. Persisted as <c>user_org_types.source_kind</c>.</summary>
    public enum UserOrgSourceKind
    {
        /// <summary>Read from a custom Entra attribute during the normal user-metadata import.</summary>
        EntraAttribute = 1,

        /// <summary>Uploaded as a CSV of UPN and org name in the portal.</summary>
        CsvUpload = 2,
    }

    /// <summary>How a CSV upload treats users it does not mention.</summary>
    public enum UserOrgImportMode
    {
        /// <summary>
        /// The file is the complete statement of this org type's membership: a user not in the file has
        /// their value for this org type cleared.
        /// </summary>
        Replace = 1,

        /// <summary>Only the rows in the file change; every other user keeps whatever they had.</summary>
        Merge = 2,
    }

    /// <summary>Lifecycle of a CSV import job. Persisted as <c>user_org_import_jobs.status</c>.</summary>
    public enum UserOrgImportStatus
    {
        Pending = 1,
        Running = 2,
        Succeeded = 3,
        Failed = 4,
        Cancelled = 5,
    }

    /// <summary>One admin-defined org dimension.</summary>
    public sealed class UserOrgType
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public UserOrgSourceKind SourceKind { get; set; }

        /// <summary>
        /// The canonical Entra attribute name, or <c>null</c> for a CSV-sourced type. Always a value
        /// that <see cref="EntraOrgAttributeSpec.TryParse"/> accepts - it is validated (and probed
        /// against live Graph) before it is ever stored.
        /// </summary>
        public string EntraAttributeName { get; set; }

        /// <summary>
        /// Whether this org type takes part in imports. A disabled Entra type is also excluded from the
        /// Graph <c>$select</c>, which is why disabling one changes the delta-token key.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// How many times this type's values have been discarded because its source changed.
        /// </summary>
        /// <remarks>
        /// Part of the Microsoft Graph delta-token cache key, so a token minted under a previous
        /// configuration can never be reused once the values it produced have been thrown away. The
        /// attribute names alone are not enough: repointing a type from one attribute to another and
        /// back again, or deleting and recreating it, returns to a key that already has a stored
        /// token - and Graph would answer that token with only the users changed since, leaving
        /// everybody else permanently unassigned in a type that had just been emptied.
        /// </remarks>
        public int SourceGeneration { get; set; } = 1;

        public DateTime CreatedUtc { get; set; }

        public DateTime? ModifiedUtc { get; set; }

        /// <summary>
        /// When this type's values were last brought up to date from its source, or <c>null</c> if they
        /// never have been.
        /// </summary>
        /// <remarks>
        /// For an Entra type, the start of the last user import that read the attribute and applied its
        /// values - including one in which nobody's value changed, because the delta query confirms that
        /// too. The start, not the end, so it never claims more freshness than the data has. For a CSV
        /// type, the moment the last import was applied. Reset whenever the values are discarded because
        /// the source changed, so it cannot vouch for values from a source the type no longer uses.
        /// </remarks>
        public DateTime? LastRefreshedUtc { get; set; }
    }

    /// <summary>An org type plus the counts the admin page shows next to it.</summary>
    public sealed class UserOrgTypeSummary
    {
        public UserOrgType Type { get; set; }

        /// <summary>How many users currently have a value for this org type.</summary>
        public int AssignedUserCount { get; set; }

        /// <summary>How many distinct values exist for this org type.</summary>
        public int DistinctValueCount { get; set; }

        /// <summary>The most recent CSV import for this org type, or <c>null</c> if there has never been one.</summary>
        public UserOrgImportJob LastImport { get; set; }
    }

    /// <summary>
    /// One user's value for one org type, as handed to a bulk merge.
    /// </summary>
    public sealed class UserOrgAssignmentUpdate
    {
        public UserOrgAssignmentUpdate()
        {
        }

        public UserOrgAssignmentUpdate(int userId, int orgTypeId, string orgValue)
        {
            UserId = userId;
            OrgTypeId = orgTypeId;
            OrgValue = orgValue;
        }

        public int UserId { get; set; }

        public int OrgTypeId { get; set; }

        /// <summary>
        /// The value to store, or <c>null</c> to clear this user's assignment for this org type.
        /// Expected to have been through <see cref="UserOrgRules.NormaliseOrgValue"/> already.
        /// </summary>
        public string OrgValue { get; set; }
    }

    /// <summary>What a bulk merge actually did.</summary>
    public sealed class UserOrgMergeResult
    {
        /// <summary>Assignments inserted or changed.</summary>
        public int Applied { get; set; }

        /// <summary>Assignments removed because the incoming value was null.</summary>
        public int Cleared { get; set; }

        /// <summary>New rows added to <c>user_org_values</c>.</summary>
        public int ValuesCreated { get; set; }

        /// <summary>
        /// Updates dropped because their org type was reconfigured while this batch was being built.
        /// </summary>
        /// <remarks>
        /// Any of: the type switched source, was disabled, or had its values discarded. The caller
        /// must treat a non-zero count as a reason to withhold the Graph delta token - the users
        /// whose values were dropped will not appear in a delta again unless they change, so
        /// committing the token would strand them until they do.
        /// </remarks>
        public int FencedOut { get; set; }

        /// <summary>
        /// Updates discarded because a later update in the same batch targeted the same user and org
        /// type. Reported rather than hidden: for a CSV this means the file listed a user twice.
        /// </summary>
        public int DuplicatesCollapsed { get; set; }
    }

    /// <summary>One resolved org value for a user, for display.</summary>
    public sealed class UserOrgValueForUser
    {
        public int OrgTypeId { get; set; }

        public string OrgTypeName { get; set; }

        public string Value { get; set; }

        public DateTime LastUpdatedUtc { get; set; }
    }

    /// <summary>One organisation and how many users are in it, for browsing an org type.</summary>
    public sealed class UserOrgValueCount
    {
        public int Id { get; set; }

        public string Name { get; set; }

        /// <summary>
        /// Users in this organisation now. Zero is possible: an organisation stays listed after its last
        /// member moves out, until the type's values are discarded - which is also why the admin page's
        /// "Distinct values" figure counts it.
        /// </summary>
        public int MemberCount { get; set; }
    }

    /// <summary>One page of an org type's organisations.</summary>
    public sealed class UserOrgValuePage
    {
        public IReadOnlyList<UserOrgValueCount> Values { get; set; } = new UserOrgValueCount[0];

        /// <summary>How many organisations match, across every page.</summary>
        public int TotalCount { get; set; }
    }

    /// <summary>One user in an organisation, with the directory details that identify them.</summary>
    public sealed class UserOrgMember
    {
        public int UserId { get; set; }

        public string UserPrincipalName { get; set; }

        public string Department { get; set; }

        public string JobTitle { get; set; }

        /// <summary><c>false</c> for a disabled account, <c>null</c> when the import has not recorded it.</summary>
        public bool? AccountEnabled { get; set; }
    }

    /// <summary>One page of the users in an organisation.</summary>
    public sealed class UserOrgMemberPage
    {
        public int OrgValueId { get; set; }

        public string OrgValueName { get; set; }

        public IReadOnlyList<UserOrgMember> Members { get; set; } = new UserOrgMember[0];

        /// <summary>How many users match, across every page.</summary>
        public int TotalCount { get; set; }
    }

    /// <summary>One CSV upload.</summary>
    public sealed class UserOrgImportJob
    {
        public int Id { get; set; }

        public int OrgTypeId { get; set; }

        public UserOrgImportMode Mode { get; set; }

        public UserOrgImportStatus Status { get; set; }

        public string FileName { get; set; }

        public string StartedBy { get; set; }

        public DateTime QueuedUtc { get; set; }

        public DateTime? StartedUtc { get; set; }

        public DateTime? FinishedUtc { get; set; }

        /// <summary>
        /// Last time the worker reported progress. A <see cref="UserOrgImportStatus.Running"/> job whose
        /// heartbeat has gone stale was interrupted - almost always by an App Service recycle - and is
        /// reported as such rather than appearing to run forever.
        /// </summary>
        public DateTime? HeartbeatUtc { get; set; }

        public int RowsTotal { get; set; }

        /// <summary>
        /// Assignments this import actually created or changed.
        /// </summary>
        /// <remarks>
        /// A count of mutations, not of rows processed: re-importing an identical file reports zero,
        /// because nothing needed changing. The portal labels it "changed" for that reason - "set"
        /// would read as a row count and make a correct no-op look like a failed import.
        /// </remarks>
        public int RowsApplied { get; set; }

        public int RowsCleared { get; set; }

        public int RowsUnknownUpn { get; set; }

        public int RowsInvalid { get; set; }

        /// <summary>
        /// Whether the administrator acknowledged how many users a Replace would clear.
        /// </summary>
        /// <remarks>
        /// Carried on the job because the destructive delete happens later, in the background
        /// worker's transaction, and that is the only place the question can be answered without the
        /// answer going stale. Another administrator's import can queue, run and finish between the
        /// web request's check and this one.
        /// </remarks>
        public bool ConfirmClear { get; set; }

        /// <summary>
        /// The org type's source generation when this file was staged, or <c>null</c> for a job
        /// created before generations existed.
        /// </summary>
        /// <remarks>
        /// The apply refuses unless it still matches. "Still CSV-sourced" is a weaker question and
        /// not the one that matters: a type switched to Entra and back is CSV-sourced again, with its
        /// values deliberately discarded in between, so a file queued before the switch would
        /// silently restore exactly what the admin threw away.
        /// </remarks>
        public int? ExpectedGeneration { get; set; }

        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Thrown when a job is no longer the one that should run - it was overtaken by a later import
    /// for the same org type after it stopped reporting progress.
    /// </summary>
    /// <remarks>
    /// Distinct from a failure on purpose. Nothing went wrong and nothing was changed, so the admin
    /// must not be shown an error about an import that was correctly declined.
    /// </remarks>
    public sealed class UserOrgJobSupersededException : Exception
    {
        public UserOrgJobSupersededException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// Thrown when an admin-supplied org configuration is rejected. Carries a message written for an IT
    /// admin, because the API surfaces it straight into the portal.
    /// </summary>
    public sealed class UserOrgValidationException : Exception
    {
        public UserOrgValidationException(string message) : base(message)
        {
        }

        public UserOrgValidationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>One parsed CSV row, ready to be staged.</summary>
    public sealed class UserOrgStagedRow
    {
        public UserOrgStagedRow()
        {
        }

        public UserOrgStagedRow(int lineNumber, string upn, string orgValue)
        {
            LineNumber = lineNumber;
            Upn = upn;
            OrgValue = orgValue;
        }

        /// <summary>The 1-based line in the uploaded file, so a problem can be pointed at.</summary>
        public int LineNumber { get; set; }

        public string Upn { get; set; }

        /// <summary>The org value, or <c>null</c>/empty meaning "clear this user's value".</summary>
        public string OrgValue { get; set; }
    }

    /// <summary>
    /// A comparer that treats two updates as targeting the same slot when they share a user and an org
    /// type - which is exactly the uniqueness the <c>user_org_assignments</c> primary key enforces.
    /// </summary>
    public sealed class UserOrgAssignmentSlotComparer : IEqualityComparer<UserOrgAssignmentUpdate>
    {
        public static readonly UserOrgAssignmentSlotComparer Instance = new UserOrgAssignmentSlotComparer();

        public bool Equals(UserOrgAssignmentUpdate x, UserOrgAssignmentUpdate y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            return x.UserId == y.UserId && x.OrgTypeId == y.OrgTypeId;
        }

        public int GetHashCode(UserOrgAssignmentUpdate obj)
        {
            if (obj == null) return 0;
            unchecked
            {
                return (obj.UserId * 397) ^ obj.OrgTypeId;
            }
        }
    }
}
