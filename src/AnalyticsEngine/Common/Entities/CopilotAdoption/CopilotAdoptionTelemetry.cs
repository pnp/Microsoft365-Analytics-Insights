using System;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Non-blocking observer for one Copilot adoption analysis.
    ///
    /// Implementations must never perform network I/O on the calling thread and must never throw. The
    /// analysis calls this at the boundaries needed to distinguish a database/EF wait from projection,
    /// scoring and cache publication without exposing SQL, parameters or tenant-derived values.
    /// </summary>
    public interface ICopilotAdoptionRunTelemetry
    {
        long StepStarted(string step);

        void StepCompleted(
            long operationId,
            string step,
            long durationMs,
            bool failed,
            string exceptionType = null);

        long QueryStarted(string step, string query);

        void QueryCompleted(
            long operationId,
            string step,
            string query,
            long durationMs,
            bool failed,
            string exceptionType = null);

        void Checkpoint(string stage, long durationMs = 0);
    }

    /// <summary>No-op telemetry used outside the web application and by existing callers.</summary>
    public sealed class NullCopilotAdoptionRunTelemetry : ICopilotAdoptionRunTelemetry
    {
        public static readonly NullCopilotAdoptionRunTelemetry Instance =
            new NullCopilotAdoptionRunTelemetry();

        private NullCopilotAdoptionRunTelemetry()
        {
        }

        public long StepStarted(string step) => 0;

        public void StepCompleted(
            long operationId,
            string step,
            long durationMs,
            bool failed,
            string exceptionType = null)
        {
        }

        public long QueryStarted(string step, string query) => 0;

        public void QueryCompleted(
            long operationId,
            string step,
            string query,
            long durationMs,
            bool failed,
            string exceptionType = null)
        {
        }

        public void Checkpoint(string stage, long durationMs = 0)
        {
        }
    }

    /// <summary>Stable lifecycle stage names used by Application Insights queries.</summary>
    public static class CopilotAdoptionTelemetryStages
    {
        public const string Started = "Started";
        public const string QueryStarted = "QueryStarted";
        public const string QueryCompleted = "QueryCompleted";
        public const string QueryFailed = "QueryFailed";
        public const string StepStarted = "StepStarted";
        public const string StepCompleted = "StepCompleted";
        public const string StepFailed = "StepFailed";
        public const string ScoringStarted = "ScoringStarted";
        public const string ScoringCompleted = "ScoringCompleted";
        public const string ServiceReturned = "ServiceReturned";
        public const string CachePublished = "CachePublished";
        public const string CompletionTelemetryReturned = "CompletionTelemetryReturned";
        public const string Failed = "Failed";
        public const string Heartbeat = "Heartbeat";
        public const string HostStopping = "HostStopping";
    }

    /// <summary>
    /// Stable, compile-time query names. Values describe query purpose only; they never include SQL,
    /// parameters, database identifiers or tenant data.
    /// </summary>
    public static class CopilotAdoptionQueries
    {
        public const string LicenceTypes = "LicenceTypes";
        public const string AuditDataProbe = "AuditDataProbe";
        public const string PendingBackfillProbe = "PendingBackfillProbe";
        public const string CopilotReportDate = "CopilotReportDate";
        public const string CopilotReportPeriod = "CopilotReportPeriod";
        public const string M365ReportDate = "M365ReportDate";
        public const string CopilotReportAnonymisation = "CopilotReportAnonymisation";
        public const string SeatAssignments = "SeatAssignments";
        public const string CoworkAgentLookup = "CoworkAgentLookup";
        public const string LicensedUserDetail = "LicensedUserDetail";
        public const string LicensedUsageByApp = "LicensedUsageByApp";
        public const string WeeklyTrend = "WeeklyTrend";
        public const string UnlicensedActiveUsers = "UnlicensedActiveUsers";
        public const string LicenceOpportunities = "LicenceOpportunities";
        public const string AgentUsage = "AgentUsage";
        public const string AgentUsageByDepartment = "AgentUsageByDepartment";
        public const string UnlicensedUsage = "UnlicensedUsage";
        public const string UnlicensedUsageByApp = "UnlicensedUsageByApp";
        public const string ResourceTypes = "ResourceTypes";
    }
}
