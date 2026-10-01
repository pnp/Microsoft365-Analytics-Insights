using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models.UserScope
{
    /// <summary>
    /// <c>GET api/UserScope</c>: the facts behind the Administration &gt; User scope page. Facts only - the page writes
    /// every sentence, in the reader's language. Group names, filter patterns and table names are tenant data and are
    /// shown as they are.
    /// </summary>
    public sealed class UserScopeStatusModel
    {
        /// <summary>Whether <c>UserGroupsFilter</c> narrows the imports at all (false when unset, blank or <c>*</c>).</summary>
        [JsonProperty("filtered")]
        public bool Filtered { get; set; }

        [JsonProperty("filterPatterns")]
        public List<string> FilterPatterns { get; set; } = new List<string>();

        [JsonProperty("resolution")]
        public UserScopeResolutionModel Resolution { get; set; }

        /// <summary>Users in the database inside and outside the scope; null unless the scope resolved.</summary>
        [JsonProperty("database")]
        public UserScopeDatabaseCountsModel Database { get; set; }

        /// <summary>Why a purge can't be started now (see <see cref="UserScopePurgeUnavailableReasons"/>); null when it can.</summary>
        [JsonProperty("purgeUnavailableReason")]
        public string PurgeUnavailableReason { get; set; }

        /// <summary>The most recent purge, active or finished; null when there has never been one.</summary>
        [JsonProperty("latestJob")]
        public UserScopePurgeJobModel LatestJob { get; set; }

        /// <summary>
        /// Whether purge records survive a web app restart and are shared by every instance. False while they are kept in
        /// the web app's memory: a restart then stops a running purge, and only the instance running it shows its progress.
        /// </summary>
        [JsonProperty("purgeStateDurable")]
        public bool PurgeStateDurable { get; set; }
    }

    public sealed class UserScopeResolutionModel
    {
        /// <summary><c>unfiltered</c>, <c>resolved</c> or <c>unavailable</c>.</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        /// <summary>
        /// When <c>unavailable</c>: <c>directoryRead</c>, <c>budgetExhausted</c>, <c>clientUnavailable</c> or
        /// <c>unexpected</c>. Otherwise null.
        /// </summary>
        [JsonProperty("failureKind")]
        public string FailureKind { get; set; }

        /// <summary>The HTTP status Microsoft Graph answered with, when reading the groups failed (403: missing Group.Read.All).</summary>
        [JsonProperty("httpStatus")]
        public int? HttpStatus { get; set; }

        [JsonProperty("resolvedUtc")]
        public DateTime? ResolvedUtc { get; set; }

        /// <summary>People in the scope. 0 when there is no filter.</summary>
        [JsonProperty("memberCount")]
        public int MemberCount { get; set; }

        /// <summary>Resolved, but no group matched the filter - so nobody is in scope.</summary>
        [JsonProperty("matchedNoGroup")]
        public bool MatchedNoGroup { get; set; }

        [JsonProperty("groups")]
        public List<UserScopeGroupModel> Groups { get; set; } = new List<UserScopeGroupModel>();

        /// <summary>Filter patterns that matched no group.</summary>
        [JsonProperty("unmatchedPatterns")]
        public List<string> UnmatchedPatterns { get; set; } = new List<string>();
    }

    public sealed class UserScopeGroupModel
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("displayName")]
        public string DisplayName { get; set; }

        /// <summary>Direct user members.</summary>
        [JsonProperty("userMemberCount")]
        public int UserMemberCount { get; set; }

        [JsonProperty("matchedPatterns")]
        public List<string> MatchedPatterns { get; set; } = new List<string>();
    }

    public sealed class UserScopeDatabaseCountsModel
    {
        [JsonProperty("totalUsers")]
        public int TotalUsers { get; set; }

        [JsonProperty("inScopeUsers")]
        public int InScopeUsers { get; set; }

        /// <summary>What a purge would remove: users outside the scope, not counting the anonymous Unknown User.</summary>
        [JsonProperty("outOfScopeUsers")]
        public int OutOfScopeUsers { get; set; }
    }

    /// <summary>One purge, as the page shows it.</summary>
    public sealed class UserScopePurgeJobModel
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        /// <summary><c>queued</c>, <c>running</c>, <c>completed</c>, <c>failed</c> or <c>cancelled</c>.</summary>
        [JsonProperty("state")]
        public string State { get; set; }

        /// <summary>A stable phase key (<c>snapshot</c>, <c>auditEvents</c> ... <c>users</c>, <c>done</c>) the page labels.</summary>
        [JsonProperty("phase")]
        public string Phase { get; set; }

        [JsonProperty("stepIndex")]
        public int StepIndex { get; set; }

        [JsonProperty("stepCount")]
        public int StepCount { get; set; }

        [JsonProperty("candidateCount")]
        public int CandidateCount { get; set; }

        [JsonProperty("usersDeleted")]
        public int UsersDeleted { get; set; }

        [JsonProperty("usersSkipped")]
        public int UsersSkipped { get; set; }

        /// <summary>
        /// Rows changed per table, sorted by name: a table name for rows deleted, <c>table.column</c> for rows anonymised
        /// in place. Schema identifiers, shown as they are.
        /// </summary>
        [JsonProperty("rowsAffected")]
        public List<UserScopePurgeTableCountModel> RowsAffected { get; set; } = new List<UserScopePurgeTableCountModel>();

        [JsonProperty("cancelRequested")]
        public bool CancelRequested { get; set; }

        [JsonProperty("requestedBy")]
        public string RequestedBy { get; set; }

        [JsonProperty("createdUtc")]
        public DateTime CreatedUtc { get; set; }

        [JsonProperty("startedUtc")]
        public DateTime? StartedUtc { get; set; }

        [JsonProperty("updatedUtc")]
        public DateTime UpdatedUtc { get; set; }

        [JsonProperty("completedUtc")]
        public DateTime? CompletedUtc { get; set; }

        /// <summary>Why it failed: <c>scopeUnavailable</c>, <c>scopeEmpty</c>, <c>filterChanged</c>, <c>databaseError</c> or <c>unexpected</c>.</summary>
        [JsonProperty("errorCode")]
        public string ErrorCode { get; set; }
    }

    public sealed class UserScopePurgeTableCountModel
    {
        [JsonProperty("table")]
        public string Table { get; set; }

        [JsonProperty("rows")]
        public long Rows { get; set; }
    }

    /// <summary>Body of <c>POST api/UserScope/purge</c>.</summary>
    public sealed class UserScopePurgeRequest
    {
        /// <summary>The administrator ticked "I understand this cannot be undone". Required.</summary>
        [JsonProperty("acknowledged")]
        public bool Acknowledged { get; set; }
    }

    /// <summary>A failed request's stable error code. The page maps it to a sentence; the server sends no text.</summary>
    public sealed class UserScopeError
    {
        [JsonProperty("code")]
        public string Code { get; set; }
    }

    /// <summary>Values of <see cref="UserScopeStatusModel.PurgeUnavailableReason"/>. Part of the API.</summary>
    public static class UserScopePurgeUnavailableReasons
    {
        public const string NotFiltered = "notFiltered";
        public const string ScopeUnavailable = "scopeUnavailable";
        public const string ScopeEmpty = "scopeEmpty";
        public const string NothingToPurge = "nothingToPurge";
        public const string JobActive = "jobActive";

        /// <summary>Where purge records are kept - Azure Table storage - can't be reached, so a purge could not record its progress.</summary>
        public const string StorageUnavailable = "storageUnavailable";
    }

    /// <summary>The error codes <c>api/UserScope</c> answers with. Part of the API.</summary>
    public static class UserScopeErrorCodes
    {
        public const string AcknowledgementRequired = "acknowledgementRequired";
        public const string ScopeNotFiltered = "scopeNotFiltered";
        public const string ScopeUnavailable = "scopeUnavailable";
        public const string ScopeEmpty = "scopeEmpty";
        public const string NothingToPurge = "nothingToPurge";
        public const string PurgeAlreadyRunning = "purgeAlreadyRunning";
        public const string JobNotFound = "jobNotFound";
        public const string JobNotActive = "jobNotActive";
        public const string DatabaseUnavailable = "databaseUnavailable";

        /// <summary>Where purge records are kept - Azure Table storage - can't be reached. The same code as <c>api/UserImportCheckpoint</c>'s.</summary>
        public const string StorageUnavailable = "storageUnavailable";
    }
}
