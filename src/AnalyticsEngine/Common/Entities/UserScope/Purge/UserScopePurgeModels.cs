using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.UserScope.Purge
{
    /// <summary>
    /// The states a purge passes through. Stored in the purge's record and sent to the portal exactly as written here,
    /// so they are part of the API: never rename one.
    /// </summary>
    public static class UserScopePurgeStates
    {
        /// <summary>Accepted; working out who is outside the scope has not finished yet.</summary>
        public const string Queued = "queued";

        /// <summary>Deleting and anonymising.</summary>
        public const string Running = "running";

        public const string Completed = "completed";
        public const string Failed = "failed";

        /// <summary>Stopped at an administrator's request. What was removed before it stopped stays removed.</summary>
        public const string Cancelled = "cancelled";

        public static bool IsActive(string state) => state == Queued || state == Running;
    }

    /// <summary>
    /// What a purge is working on, as a stable key the portal turns into its own words. Part of the API.
    /// </summary>
    public static class UserScopePurgePhases
    {
        public const string Snapshot = "snapshot";
        public const string AuditEvents = "auditEvents";
        public const string WebActivity = "webActivity";
        public const string Calls = "calls";
        public const string PageComments = "pageComments";
        public const string SentEmails = "sentEmails";
        public const string Teams = "teams";
        public const string UsageReports = "usageReports";
        public const string CopilotInteractions = "copilotInteractions";
        public const string LicencesAndCredits = "licencesAndCredits";
        public const string SharedWith = "sharedWith";
        public const string Managers = "managers";
        public const string Users = "users";
        public const string Done = "done";
    }

    /// <summary>Why a purge failed, as a stable code the portal turns into a sentence. Part of the API.</summary>
    public static class UserScopePurgeErrorCodes
    {
        /// <summary>The groups could not be read completely when the purge needed them, so nobody was purged.</summary>
        public const string ScopeUnavailable = "scopeUnavailable";

        /// <summary>The filter resolved to nobody, so every user would have been purged. Refused.</summary>
        public const string ScopeEmpty = "scopeEmpty";

        /// <summary>A database statement kept failing.</summary>
        public const string DatabaseError = "databaseError";

        /// <summary>
        /// <c>UserGroupsFilter</c> was changed between the purge being confirmed and it working out who to remove, so
        /// it removed nobody rather than act on a filter nobody confirmed.
        /// </summary>
        public const string FilterChanged = "filterChanged";

        /// <summary>
        /// <c>UserGroupsFilter</c> was changed while the purge was running (changing an App Service setting restarts the
        /// web app, and a purge carries on when it starts again). It stopped rather than carry on removing people chosen
        /// under the old filter, some of whom may be inside the new one. What it had removed before stays removed.
        /// </summary>
        public const string FilterChangedWhileRunning = "filterChangedWhileRunning";

        /// <summary>
        /// The purge couldn't save its progress to Azure Table storage, where it keeps it. What it removed before stays
        /// removed, and starting it again finishes it.
        /// </summary>
        public const string StateUnavailable = "stateUnavailable";

        public const string Unexpected = "unexpected";
    }

    /// <summary>One purge's record, kept in <see cref="UserScopePurgeStateStore"/> - never in the analytics database.</summary>
    public sealed class UserScopePurgeJob
    {
        public int Id { get; set; }
        public string State { get; set; }
        public string Phase { get; set; }

        /// <summary>
        /// The step in progress (or next to run): 0 is working out who to purge, step <c>n</c> is
        /// <c>UserScopePurgePlan.Steps[n - 1]</c>, and <see cref="StepCount"/> means finished.
        /// </summary>
        public int StepIndex { get; set; }

        /// <summary>
        /// How far the step in progress has got through its table: the last key of the last window it finished, in
        /// invariant string form. Null at the start of a step.
        /// </summary>
        public string StepAfter { get; set; }

        public int StepCount { get; set; }
        public string RequestedBy { get; set; }
        public string FilterFingerprint { get; set; }
        public int? ScopeMemberCount { get; set; }

        /// <summary>
        /// People outside the scope: the users the purge removes. A purge that starts again after a restart adds the
        /// ones it had already removed to the ones it still has to.
        /// </summary>
        public int CandidateCount { get; set; }

        public int UsersDeleted { get; set; }

        /// <summary>People kept because new data about them arrived while the purge ran; a second run removes them.</summary>
        public int UsersSkipped { get; set; }

        /// <summary>Rows deleted or anonymised, by table (<c>table.column</c> for rows anonymised in place).</summary>
        public Dictionary<string, long> RowsAffected { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Whether a stop has been asked for. Kept under a key of its own, not in the record.</summary>
        [JsonIgnore]
        public bool CancelRequested { get; set; }

        public DateTime CreatedUtc { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public string ErrorCode { get; set; }

        /// <summary>Technical detail for the logs - an exception type and SQL error number, never a person's identity.</summary>
        public string ErrorDetail { get; set; }

        [JsonIgnore]
        public bool IsActive => UserScopePurgeStates.IsActive(State);
    }
}
