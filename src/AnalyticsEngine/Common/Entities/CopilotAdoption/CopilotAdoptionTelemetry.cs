using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.ComponentModel;

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
            CopilotAdoptionFailure failure = null);

        long QueryStarted(string step, string query);

        void QueryCompleted(
            long operationId,
            string step,
            string query,
            long durationMs,
            bool failed,
            CopilotAdoptionFailure failure = null);

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
            CopilotAdoptionFailure failure = null)
        {
        }

        public long QueryStarted(string step, string query) => 0;

        public void QueryCompleted(
            long operationId,
            string step,
            string query,
            long durationMs,
            bool failed,
            CopilotAdoptionFailure failure = null)
        {
        }

        public void Checkpoint(string stage, long durationMs = 0)
        {
        }
    }

    /// <summary>
    /// Why a query, step or run failed, in terms that are safe to send to Application Insights: exception
    /// type names and numeric error codes only, never a message.
    /// </summary>
    /// <remarks>
    /// <para>The base exception type alone cannot tell a SQL command timeout from a connection failure.
    /// Microsoft.Data.SqlClient raises a command timeout as <c>SqlException</c> (Number -2) wrapping
    /// <c>Win32Exception</c> (258, "The wait operation timed out"), so <c>GetBaseException()</c> - which is all
    /// the lifecycle events used to record - is the Win32Exception. Verified against LocalDB with the shipped
    /// driver. The SQL error number is what separates a timeout from a deadlock, an Azure SQL resource limit,
    /// a refused login or a schema that was never upgraded.</para>
    /// <para>Messages are deliberately never captured: SQL Server error text can quote data values.</para>
    /// </remarks>
    public sealed class CopilotAdoptionFailure
    {
        private const int MaxChainDepth = 6;

        private CopilotAdoptionFailure(
            string exceptionType,
            string exceptionChain,
            string failureKind,
            int? sqlErrorNumber,
            int? win32ErrorCode)
        {
            ExceptionType = exceptionType;
            ExceptionChain = exceptionChain;
            FailureKind = failureKind;
            SqlErrorNumber = sqlErrorNumber;
            Win32ErrorCode = win32ErrorCode;
        }

        /// <summary>
        /// The innermost exception's type name - the value the <c>ExceptionType</c> dimension has always
        /// carried, kept unchanged so saved queries keep working.
        /// </summary>
        public string ExceptionType { get; }

        /// <summary>Outermost to innermost exception type names, e.g. <c>SqlException&gt;Win32Exception</c>.</summary>
        public string ExceptionChain { get; }

        /// <summary>One of <see cref="CopilotAdoptionFailureKinds"/>.</summary>
        public string FailureKind { get; }

        /// <summary><c>SqlException.Number</c> of the first SQL exception in the chain, if any.</summary>
        public int? SqlErrorNumber { get; }

        /// <summary><c>Win32Exception.NativeErrorCode</c> of the first Win32 exception in the chain, if any.</summary>
        public int? Win32ErrorCode { get; }

        /// <summary>
        /// Describes <paramref name="exception"/>, or returns null when there is none.
        /// </summary>
        /// <param name="exception">The failure.</param>
        /// <param name="cancellationRequested">
        /// Whether the caller had asked for cancellation. An <see cref="OperationCanceledException"/> nobody asked
        /// for is how EF6 / SqlClient sometimes surface an async command timeout, so it is classified as a
        /// timeout rather than as a cancellation. When cancellation WAS requested, that exception and SqlClient's
        /// own cancel shape (a <see cref="SqlException"/> numbered 0) are both classified as a cancellation.
        /// </param>
        public static CopilotAdoptionFailure From(Exception exception, bool cancellationRequested = false)
        {
            if (exception == null) return null;

            var chain = new List<string>();
            SqlException sql = null;
            Win32Exception win32 = null;
            var sawTimeout = false;
            var sawCancellation = false;
            var sawOutOfMemory = false;

            for (var current = exception; current != null && chain.Count < MaxChainDepth; current = current.InnerException)
            {
                chain.Add(current.GetType().Name);

                if (sql == null && current is SqlException sqlException) sql = sqlException;
                if (win32 == null && current is Win32Exception win32Exception) win32 = win32Exception;
                if (current is TimeoutException) sawTimeout = true;
                if (current is OperationCanceledException) sawCancellation = true;
                if (current is OutOfMemoryException) sawOutOfMemory = true;
            }

            string kind;
            if (cancellationRequested && (sawCancellation || sql?.Number == 0))
            {
                // Checked before the SQL number: a token-triggered abort surfaces from SqlClient as a
                // SqlException "Operation cancelled by user" with Number 0, which on its own reads as a
                // transport failure - the very misdiagnosis this classification exists to prevent.
                kind = CopilotAdoptionFailureKinds.Cancelled;
            }
            else if (sql != null)
            {
                kind = ClassifySqlError(sql.Number);
            }
            else if (win32 != null)
            {
                kind = win32.NativeErrorCode == WaitTimeout
                    ? CopilotAdoptionFailureKinds.Timeout
                    : CopilotAdoptionFailureKinds.Connection;
            }
            else if (sawTimeout)
            {
                kind = CopilotAdoptionFailureKinds.Timeout;
            }
            else if (sawCancellation)
            {
                kind = cancellationRequested
                    ? CopilotAdoptionFailureKinds.Cancelled
                    : CopilotAdoptionFailureKinds.Timeout;
            }
            else if (sawOutOfMemory)
            {
                kind = CopilotAdoptionFailureKinds.OutOfMemory;
            }
            else
            {
                kind = CopilotAdoptionFailureKinds.Other;
            }

            return new CopilotAdoptionFailure(
                exception.GetBaseException().GetType().Name,
                string.Join(">", chain),
                kind,
                sql?.Number,
                win32?.NativeErrorCode);
        }

        /// <summary>WAIT_TIMEOUT - the native code SqlClient wraps in a command timeout.</summary>
        private const int WaitTimeout = 258;

        /// <summary>
        /// Maps a SQL Server / Azure SQL error number onto a <see cref="CopilotAdoptionFailureKinds"/> value.
        /// </summary>
        public static string ClassifySqlError(int number)
        {
            switch (number)
            {
                case -2:    // Execution Timeout Expired (command or connection timeout)
                case 1222:  // Lock request time out period exceeded
                    return CopilotAdoptionFailureKinds.Timeout;

                case 1205:  // Chosen as deadlock victim
                    return CopilotAdoptionFailureKinds.Deadlock;

                case 1204:  // Lock resources exhausted
                case 10928: // Resource ID limit reached (Azure SQL)
                case 10929: // Resource ID minimum guarantee / limit (Azure SQL)
                case 40501: // Service is currently busy (Azure SQL)
                case 49918: // Cannot process request: not enough resources
                case 49919: // Cannot process create or update request: too many operations
                case 49920: // Cannot process request: too many operations in progress
                    return CopilotAdoptionFailureKinds.Throttled;

                case 4221:  // Login to read-secondary failed during replica reconfiguration
                case 40143: // Service encountered an error processing the request
                case 40197: // Service error processing the request (reconfiguration / failover)
                case 40613: // Database is not currently available
                case 42108: // Cannot connect to the SQL pool (transient)
                case 42109: // SQL pool is warming up
                    return CopilotAdoptionFailureKinds.Unavailable;

                case -1:    // Network-related or instance-specific error establishing a connection
                case 0:     // Transport-level error / connection closed
                case 2:
                case 20:
                case 53:
                case 64:
                case 121:
                case 233:
                case 10053:
                case 10054:
                case 10060:
                case 10061:
                case 11001:
                    return CopilotAdoptionFailureKinds.Connection;

                case 229:   // Permission denied on object
                case 230:   // Permission denied on column
                case 262:   // Permission denied in database
                case 297:   // User does not have permission to perform this action
                case 300:   // Permission not granted
                case 916:   // Server principal cannot access the database
                case 4060:  // Cannot open database requested by the login
                case 18452: // Login is from an untrusted domain
                case 18456: // Login failed
                case 40532: // Cannot open server requested by the login
                    return CopilotAdoptionFailureKinds.Permission;

                case 207:   // Invalid column name
                case 208:   // Invalid object name
                case 2812:  // Could not find stored procedure
                case 4104:  // Multi-part identifier could not be bound
                    return CopilotAdoptionFailureKinds.SchemaMismatch;

                default:
                    return CopilotAdoptionFailureKinds.SqlError;
            }
        }
    }

    /// <summary>
    /// Stable <see cref="CopilotAdoptionFailure.FailureKind"/> values. Application Insights queries and alerts
    /// match on these, so they must not be renamed.
    /// </summary>
    public static class CopilotAdoptionFailureKinds
    {
        public const string Timeout = "Timeout";
        public const string Deadlock = "Deadlock";
        public const string Throttled = "Throttled";
        public const string Unavailable = "Unavailable";
        public const string Connection = "Connection";
        public const string Permission = "Permission";
        public const string SchemaMismatch = "SchemaMismatch";
        public const string SqlError = "SqlError";
        public const string Cancelled = "Cancelled";
        public const string OutOfMemory = "OutOfMemory";
        public const string Other = "Other";
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

        /// <summary>
        /// The run is waiting for an admission slot because this web application instance is already running its
        /// limit of analyses. Only emitted when it actually had to wait.
        /// </summary>
        public const string Queued = "Queued";

        /// <summary>The run holds an admission slot; <c>DurationMs</c> is how long it waited for it.</summary>
        public const string GateAcquired = "GateAcquired";

        /// <summary>
        /// The run waited the maximum queue time (three minutes) without obtaining a slot and is proceeding anyway
        /// on the single overflow slot, so hung analyses can never block every other one. Either the slots are
        /// held by slow or hung runs, or runs queued ahead of this one took each slot that freed.
        /// <c>DurationMs</c> is the wait.
        /// </summary>
        public const string GateBypassed = "GateBypassed";

        /// <summary>
        /// Terminal: as <see cref="GateBypassed"/>, but the overflow slot was taken too, so the run was dropped
        /// without running rather than adding unbounded load. The caller's next poll starts a fresh run.
        /// <c>DurationMs</c> is the wait.
        /// </summary>
        public const string GateTimedOut = "GateTimedOut";

        /// <summary>
        /// Terminal: every slot was busy and the queue was already at its limit, so the run was turned away at
        /// once, without queueing. The caller's next poll tries again. Sustained, it means more distinct periods
        /// and seat overrides are being asked for at once than one instance will queue.
        /// </summary>
        public const string QueueFull = "QueueFull";

        /// <summary>
        /// Terminal: the run was dropped before it started because nobody was still waiting for it - typically
        /// a period the user clicked past while an earlier analysis held the slot. It can be dropped while still
        /// queued, or just after taking a slot or the overflow slot. <c>DurationMs</c> is how long it had queued.
        /// </summary>
        public const string Abandoned = "Abandoned";
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
        public const string CoworkReportDate = "CoworkReportDate";
        public const string CoworkReportPeriod = "CoworkReportPeriod";
        public const string M365ReportDate = "M365ReportDate";
        public const string CopilotReportAnonymisation = "CopilotReportAnonymisation";
        public const string SeatAssignments = "SeatAssignments";
        public const string CoworkAgentLookup = "CoworkAgentLookup";
        public const string LicensedUserDetail = "LicensedUserDetail";
        public const string LicensedUsageByApp = "LicensedUsageByApp";
        public const string WeeklyTrend = "WeeklyTrend";
        public const string WeeklyTrendCoverage = "WeeklyTrendCoverage";
        public const string UnlicensedActiveUsers = "UnlicensedActiveUsers";
        public const string LicenceOpportunities = "LicenceOpportunities";
        public const string CoworkReadiness = "CoworkReadiness";
        public const string CoworkUserCredits = "CoworkUserCredits";
        public const string CoworkCreditCapacity = "CoworkCreditCapacity";
        public const string CoworkCreditProbe = "CoworkCreditProbe";
        public const string AgentUsage = "AgentUsage";
        public const string AgentUsageByDepartment = "AgentUsageByDepartment";
        public const string UnlicensedUsage = "UnlicensedUsage";
        public const string UnlicensedUsageByApp = "UnlicensedUsageByApp";
        public const string ResourceTypes = "ResourceTypes";
    }
}
