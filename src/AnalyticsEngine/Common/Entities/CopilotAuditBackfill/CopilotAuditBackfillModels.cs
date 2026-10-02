using System;
using System.Collections.Generic;

namespace Common.Entities.CopilotAuditBackfill
{
    public static class CopilotAuditBackfillStates
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public static bool IsActive(string state) => state == Queued || state == Running;
    }

    public static class CopilotAuditBackfillErrorCodes
    {
        public const string MissingPermission = "missingPermission";
        public const string CopilotImportOff = "copilotImportOff";
        public const string QueryFailed = "queryFailed";
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
        public int SplitLevel { get; set; }
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
        public CopilotAuditBackfillSlice CurrentSlice { get; set; }
        public List<string> CompletedDays { get; set; } = new List<string>();
        public long RecordsSeen { get; set; }
        public long RecordsImported { get; set; }
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
