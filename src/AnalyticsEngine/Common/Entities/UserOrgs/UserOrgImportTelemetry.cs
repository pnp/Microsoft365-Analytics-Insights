namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// Receives the CSV import's lifecycle events, so an engineer can follow one import from upload to
    /// outcome without asking the admin what they saw.
    /// </summary>
    /// <remarks>
    /// Implementations must never throw and must never block: an event is a diagnostic, and losing one
    /// is always better than failing or slowing the import it describes.
    /// </remarks>
    public interface IUserOrgImportTelemetry
    {
        void Record(UserOrgImportTelemetryEvent item);
    }

    /// <summary>
    /// One lifecycle event.
    /// </summary>
    /// <remarks>
    /// Deliberately carries ids, codes, counts and timings only - never a user principal name, an
    /// organisation name, a file name or an exception message. Those are tenant data, and Application
    /// Insights is not where tenant data belongs.
    /// </remarks>
    public sealed class UserOrgImportTelemetryEvent
    {
        /// <summary>One of <see cref="UserOrgImportStages"/>.</summary>
        public string Stage { get; set; }

        public int? JobId { get; set; }

        public int? OrgTypeId { get; set; }

        public UserOrgImportMode? Mode { get; set; }

        /// <summary>A blocking, refusal or error code, where the stage has one.</summary>
        public string Code { get; set; }

        /// <summary>The exception's type name - never its message.</summary>
        public string ExceptionType { get; set; }

        public long? DurationMs { get; set; }

        public long? Bytes { get; set; }

        public int? Rows { get; set; }

        public int? RowsApplied { get; set; }

        public int? RowsCleared { get; set; }

        public int? RowsUnknownUpn { get; set; }

        public int? RowsInvalid { get; set; }

        public int? Attempts { get; set; }

        /// <summary>A stage-specific count: jobs resumed, jobs abandoned, users a confirmation covered.</summary>
        public int? Count { get; set; }
    }

    /// <summary>The stages a CSV import reports, in roughly the order they happen.</summary>
    public static class UserOrgImportStages
    {
        /// <summary>An upload was refused before it was parsed: no file, too large, or unreadable.</summary>
        public const string UploadRejected = "UploadRejected";

        /// <summary>A file was parsed and staged as a draft.</summary>
        public const string Previewed = "Previewed";

        /// <summary>A file was parsed and refused outright; <c>Code</c> says why.</summary>
        public const string PreviewBlocked = "PreviewBlocked";

        /// <summary>An admin asked to import a draft and a check refused it; <c>Code</c> says which.</summary>
        public const string CommitRefused = "CommitRefused";

        /// <summary>A draft was admitted as a queued import.</summary>
        public const string Committed = "Committed";

        /// <summary>A queued import was handed to a background worker.</summary>
        public const string Dispatched = "Dispatched";

        /// <summary>A worker took the import.</summary>
        public const string Claimed = "Claimed";

        /// <summary>A worker found the import already taken or finished, and left it alone.</summary>
        public const string NotClaimed = "NotClaimed";

        /// <summary>The import was applied.</summary>
        public const string Succeeded = "Succeeded";

        /// <summary>The import was overtaken by a later one and not applied.</summary>
        public const string Superseded = "Superseded";

        /// <summary>The apply's own checks refused the import; <c>Code</c> says which. Nothing changed.</summary>
        public const string Refused = "Refused";

        /// <summary>The import failed. Nothing changed.</summary>
        public const string Failed = "Failed";

        /// <summary>A progress heartbeat could not be written. The import carries on.</summary>
        public const string HeartbeatFailed = "HeartbeatFailed";

        /// <summary>
        /// The staged rows of a finished import could not be removed. Harmless - the next resume sweep
        /// removes them - but worth seeing if it keeps happening.
        /// </summary>
        public const string CleanupFailed = "CleanupFailed";

        /// <summary>A sweep for interrupted imports ran; <c>Count</c> is how many it sent back to a worker.</summary>
        public const string ResumeSwept = "ResumeSwept";

        /// <summary>A sweep for interrupted imports failed.</summary>
        public const string ResumeFailed = "ResumeFailed";

        /// <summary>The web app began shutting down while imports were in flight on it.</summary>
        public const string HostStopping = "HostStopping";
    }

    /// <summary>Discards every event. The default, so telemetry is always optional.</summary>
    public sealed class NullUserOrgImportTelemetry : IUserOrgImportTelemetry
    {
        public static readonly NullUserOrgImportTelemetry Instance = new NullUserOrgImportTelemetry();

        private NullUserOrgImportTelemetry()
        {
        }

        public void Record(UserOrgImportTelemetryEvent item)
        {
        }
    }
}
