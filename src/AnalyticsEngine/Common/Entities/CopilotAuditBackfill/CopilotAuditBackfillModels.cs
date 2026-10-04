using System;
using System.Collections.Generic;

namespace Common.Entities.CopilotAuditBackfill
{
    public static class CopilotAuditBackfillStates
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Completed = "completed";
        public const string CompletedWithGaps = "completedWithGaps";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public static bool IsActive(string state) => state == Queued || state == Running;
    }

    public static class CopilotAuditBackfillErrorCodes
    {
        public const string MissingPermission = "missingPermission";
        public const string CopilotImportOff = "copilotImportOff";
        public const string QueryFailed = "queryFailed";
        public const string QueryRejected = "queryRejected";
        public const string QueryThrottled = "queryThrottled";
        public const string QueryTruncated = "queryTruncated";
        public const string UnrecognisedAuditData = "unrecognisedAuditData";
        public const string StateNotDurable = "stateNotDurable";
        public const string GraphAccessDenied = "graphAccessDenied";
        public const string StateUnavailable = "stateUnavailable";
        public const string Unexpected = "unexpected";
    }

    public static class CopilotAuditBackfillPermissionStates
    {
        public const string Unknown = "unknown";
        public const string Granted = "granted";
        public const string Missing = "missing";
        public const string NoIdentity = "noIdentity";
    }

    public sealed class CopilotAuditBackfillSlice
    {
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public string QueryId { get; set; }
        public DateTime? SubmittedUtc { get; set; }
        public string RecordsNextLink { get; set; }
        public bool ImportingRecords { get; set; }
        public int SplitLevel { get; set; }
        public int AttemptCount { get; set; }
    }

    public sealed class CopilotAuditBackfillGap
    {
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public string Day { get; set; }
        public string ErrorCode { get; set; }
        public string Detail { get; set; }
        public int Attempts { get; set; }
        public bool Incomplete { get; set; }
    }

    public sealed class CopilotAuditBackfillJob
    {
        public int Id { get; set; }
        public string State { get; set; } = CopilotAuditBackfillStates.Queued;
        public string RequestedBy { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public List<CopilotAuditBackfillSlice> PendingSlices { get; set; } = new List<CopilotAuditBackfillSlice>();
        public List<CopilotAuditBackfillSlice> InFlightSlices { get; set; } = new List<CopilotAuditBackfillSlice>();
        public CopilotAuditBackfillSlice CurrentSlice { get; set; }
        public List<string> CompletedDays { get; set; } = new List<string>();
        public List<CopilotAuditBackfillGap> Gaps { get; set; } = new List<CopilotAuditBackfillGap>();
        public List<DateTime> SubmissionTimestampsUtc { get; set; } = new List<DateTime>();
        public DateTime? SubmissionsPausedUntilUtc { get; set; }
        public Dictionary<string, long> MappingFailureCounts { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);
        public long RecordsSeen { get; set; }
        public long RecordsImported { get; set; }
        public long RecordsAlreadyPresent { get; set; }
        public int SlicesSubmitted { get; set; }
        public int SlicesCompleted { get; set; }
        public int SlicesSplit { get; set; }
        public string PermissionStatus { get; set; } = CopilotAuditBackfillPermissionStates.Unknown;
        public bool CopilotImportEnabled { get; set; }
        public string LastErrorCode { get; set; }
        public string LastErrorDetail { get; set; }
        public bool CancelRequested { get; set; }
        public bool IsActive => CopilotAuditBackfillStates.IsActive(State);
    }
}
