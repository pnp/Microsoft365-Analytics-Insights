using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.LeadershipCohort
{
    /// <summary>
    /// The leadership cohort an administrator configured for the Copilot Adoption leadership comparison (#654).
    /// </summary>
    /// <remarks>
    /// <para>Deliberately one explicit Entra ID group, named by object id. Not the manager hierarchy, not a title match
    /// and not a recursive expansion: "who counts as a leader" is an organisational decision, so an administrator makes
    /// it once, in a group they already govern, and the product never guesses.</para>
    /// <para>Stored in the <c>AnalyticsState</c> table (<see cref="State.StatePartitions.LeadershipCohort"/>), not in the
    /// installer configuration, so it is set from the portal and needs no redeployment. Absent means the feature is off,
    /// which is the default.</para>
    /// </remarks>
    public sealed class LeadershipCohortSettings
    {
        /// <summary>The Entra ID group's object id, as a lower-case "D" format GUID.</summary>
        [JsonProperty("groupId")]
        public string GroupId { get; set; }

        /// <summary>
        /// Changes every time the settings are saved, even to the same group, so a saved snapshot can be tied to the
        /// settings it was refreshed for. A snapshot from an older revision is never shown.
        /// </summary>
        [JsonProperty("revision")]
        public string Revision { get; set; }

        [JsonProperty("updatedUtc")]
        public DateTime UpdatedUtc { get; set; }
    }

    /// <summary>The outcome of the last membership refresh, as recorded in <see cref="LeadershipCohortSnapshot.Status"/>.</summary>
    public static class LeadershipCohortRefreshStatuses
    {
        /// <summary>The members were read and stored. The only status whose members are ever used.</summary>
        public const string Ready = "ready";

        /// <summary>Microsoft Graph has no group with the configured id (deleted, or a typo).</summary>
        public const string GroupNotFound = "groupNotFound";

        /// <summary>Graph refused the read: the runtime app needs GroupMember.Read.All or Group.Read.All.</summary>
        public const string PermissionMissing = "permissionMissing";

        /// <summary>
        /// The group has more direct user members than <see cref="LeadershipCohortStore.MaxMembers"/>. Refused rather
        /// than truncated: a partial cohort would be compared as though it were the whole one.
        /// </summary>
        public const string TooLarge = "tooLarge";

        /// <summary>Anything else - see <see cref="LeadershipCohortSnapshot.FailureKind"/>.</summary>
        public const string Failed = "failed";
    }

    /// <summary>Why a refresh with <see cref="LeadershipCohortRefreshStatuses.Failed"/> failed.</summary>
    public static class LeadershipCohortFailureKinds
    {
        /// <summary>Graph answered with an error that is not a permission or not-found answer (throttling, outage).</summary>
        public const string GraphError = "graphError";

        /// <summary>The Graph client could not be created or authenticate (credentials, Key Vault, network).</summary>
        public const string GraphClient = "graphClient";

        /// <summary>The members could not be matched to the analytics database's users.</summary>
        public const string SqlError = "sqlError";
    }

    /// <summary>
    /// The header of the stored leadership membership: what was refreshed, when, and with what outcome. Counts only -
    /// the members themselves are in pages of SQL user ids that only the comparison reads.
    /// </summary>
    public sealed class LeadershipCohortSnapshot
    {
        /// <summary>
        /// Unique per refresh. Every member page carries the version it was written for, and a reader that finds a page
        /// from another version treats the membership as unavailable rather than mixing two refreshes.
        /// </summary>
        [JsonProperty("version")]
        public string Version { get; set; }

        /// <summary>Which of the two page slots holds this version's members ("a" or "b").</summary>
        [JsonProperty("slot")]
        public string Slot { get; set; }

        [JsonProperty("groupId")]
        public string GroupId { get; set; }

        /// <summary>The group's display name as Graph returned it. Tenant data: shown as stored, never translated.</summary>
        [JsonProperty("groupDisplayName")]
        public string GroupDisplayName { get; set; }

        /// <summary>The <see cref="LeadershipCohortSettings.Revision"/> this refresh was made for.</summary>
        [JsonProperty("settingsRevision")]
        public string SettingsRevision { get; set; }

        /// <summary>One of <see cref="LeadershipCohortRefreshStatuses"/>.</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        /// <summary>One of <see cref="LeadershipCohortFailureKinds"/> when <see cref="Status"/> is failed.</summary>
        [JsonProperty("failureKind")]
        public string FailureKind { get; set; }

        /// <summary>The HTTP status Graph answered with, when a Graph read failed.</summary>
        [JsonProperty("httpStatus")]
        public int? HttpStatus { get; set; }

        [JsonProperty("attemptedUtc")]
        public DateTime AttemptedUtc { get; set; }

        /// <summary>When the members were last read successfully. Null unless <see cref="Status"/> is ready.</summary>
        [JsonProperty("refreshedUtc")]
        public DateTime? RefreshedUtc { get; set; }

        /// <summary>Direct user members of the group.</summary>
        [JsonProperty("directMembers")]
        public int DirectMembers { get; set; }

        /// <summary>Of those, how many are users the analytics database knows (matched on the Entra object id).</summary>
        [JsonProperty("matchedUsers")]
        public int MatchedUsers { get; set; }

        /// <summary>Member pages stored for this version.</summary>
        [JsonProperty("pageCount")]
        public int PageCount { get; set; }

        /// <summary>Graph pages read to list the members.</summary>
        [JsonProperty("graphPages")]
        public int GraphPages { get; set; }

        [JsonIgnore]
        public bool IsReady => string.Equals(Status, LeadershipCohortRefreshStatuses.Ready, StringComparison.Ordinal);
    }

    /// <summary>One stored page of member ids.</summary>
    internal sealed class LeadershipCohortMemberPage
    {
        [JsonProperty("v")]
        public string Version { get; set; }

        [JsonProperty("ids")]
        public List<int> UserIds { get; set; } = new List<int>();
    }

    /// <summary>The leadership state could not be read or written. Never means "not configured".</summary>
    public sealed class LeadershipCohortStateUnavailableException : Exception
    {
        public LeadershipCohortStateUnavailableException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
