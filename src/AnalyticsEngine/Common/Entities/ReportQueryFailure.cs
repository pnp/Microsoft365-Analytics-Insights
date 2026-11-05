using Common.Entities.CopilotAdoption;
using Microsoft.Extensions.Logging;
using System;

namespace Common.Entities
{
    /// <summary>
    /// What a report tells its reader when one of its queries fails: a stable reason (a <see cref="CopilotAdoptionFailureKinds"/>
    /// value, which the portal words in the reader's language) and an error reference - never the exception's text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SqlClient and Azure SQL messages name servers, databases, logins and objects ("Login failed for user '...'",
    /// "Database '...' on server '...' is not currently available", "Invalid object name '...'"), and some quote data
    /// values. Report pages, their CSVs and workbooks reach every signed-in reader and get forwarded, so they carry the
    /// reason and the reference only.
    /// </para>
    /// <para>
    /// The reference is logged server-side with the failure's classification - the reason, the SQL error number and the
    /// exception types - so an administrator who is given it can find what happened (<see cref="Log"/>, or the Copilot
    /// adoption lifecycle event's <c>ErrorId</c>). The message itself is not logged either: SQL Server can quote a data
    /// value in it, which is tenant content (#804).
    /// </para>
    /// </remarks>
    public sealed class ReportQueryFailure
    {
        private ReportQueryFailure(CopilotAdoptionFailure failure)
        {
            Reason = failure.FailureKind;
            Reference = failure.ErrorId;
            SqlErrorNumber = failure.SqlErrorNumber;
            ExceptionChain = failure.ExceptionChain;
        }

        /// <summary>A <see cref="CopilotAdoptionFailureKinds"/> value: Timeout, Unavailable, SchemaMismatch...</summary>
        public string Reason { get; }

        /// <summary>The error reference the reader is shown and the server log carries.</summary>
        public string Reference { get; }

        public int? SqlErrorNumber { get; }

        /// <summary>Exception type names, outermost to innermost. Never a message.</summary>
        public string ExceptionChain { get; }

        /// <summary>The English the reader sees where the portal can't word <see cref="Reason"/> itself (CSVs, workbooks).</summary>
        public string EnglishText => English(Reason, Reference);

        public static ReportQueryFailure From(Exception exception, bool cancellationRequested = false) =>
            From(CopilotAdoptionFailure.From(exception, cancellationRequested));

        public static ReportQueryFailure From(CopilotAdoptionFailure failure) => failure == null ? null : new ReportQueryFailure(failure);

        /// <summary>"the query timed out (error reference ...)". The portal's <c>errors.reportQuery.*</c> keys mirror this.</summary>
        public static string English(string reason, string reference) => $"{EnglishReason(reason)} (error reference {reference})";

        public static string EnglishReason(string reason)
        {
            switch (reason)
            {
                case CopilotAdoptionFailureKinds.Timeout: return "the query timed out";
                case CopilotAdoptionFailureKinds.Deadlock: return "the query was stopped by a database deadlock";
                case CopilotAdoptionFailureKinds.Throttled: return "the database was at its resource limit";
                case CopilotAdoptionFailureKinds.Unavailable: return "the database was not available";
                case CopilotAdoptionFailureKinds.Connection: return "the connection to the database failed";
                case CopilotAdoptionFailureKinds.Permission: return "the database refused access";
                case CopilotAdoptionFailureKinds.SchemaMismatch: return "the database has not been upgraded to this version";
                case CopilotAdoptionFailureKinds.SqlError: return "the database returned an error";
                case CopilotAdoptionFailureKinds.Cancelled: return "the query was cancelled";
                case CopilotAdoptionFailureKinds.OutOfMemory: return "the web server ran out of memory";
                default: return "the query failed";
            }
        }

        /// <summary>One warning line an administrator can find by the reference. Classification only, never text.</summary>
        public void Log(ILogger logger, string area, string query, long elapsedMs = 0)
        {
            if (logger == null) return;
            try
            {
                logger.Log(LogLevel.Warning, 0,
                    $"{area} query failed: key={query} reason={Reason} sqlError={SqlErrorNumber?.ToString() ?? "none"} "
                    + $"exceptionChain={ExceptionChain} elapsedMs={elapsedMs} errorReference={Reference}",
                    null, (state, _) => state);
            }
            catch (Exception)
            {
                // Logging must not turn a degraded section into a failed page.
            }
        }
    }
}
