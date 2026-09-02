using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.Health
{
    /// <summary>
    /// Read port for everything the Health sections need out of SQL. The EF adapter is
    /// <see cref="SqlHealthDataSource"/>; tests substitute an in-memory fake so the section-building
    /// logic runs with zero SQL Server dependency. See issues #379 / #381.
    /// </summary>
    public interface IHealthDataSource
    {
        /// <summary>
        /// Cheap "can we reach the DB?" probe (<c>SELECT 1</c>) for the overall roll-up - no table scans.
        /// Never throws: a failure comes back as <see cref="DatabaseProbeResult.Error"/>.
        /// </summary>
        Task<DatabaseProbeResult> ProbeDatabaseAsync();

        /// <summary>
        /// Approximate row counts + database size (DMVs), the tracked-Teams count and the latest Copilot
        /// usage-report import per report. Never throws: partial failures come back as
        /// <see cref="DatabaseCountsResult.CountsError"/> / <see cref="DatabaseCountsResult.DataError"/>.
        /// </summary>
        Task<DatabaseCountsResult> GetDatabaseCountsAsync();

        /// <summary>
        /// One-pass 24h + 7d count and newest-timestamp for a fact table. Never throws: a failure (or a
        /// timeout on a very large tenant) comes back as <see cref="RecentVolumeResult.Error"/>.
        /// </summary>
        /// <param name="table">Fact table name - a compile-time constant, never user input.</param>
        /// <param name="timestampColumn">Its timestamp column - a compile-time constant, never user input.</param>
        Task<RecentVolumeResult> GetRecentVolumeAsync(string table, string timestampColumn);

        /// <summary>
        /// The migrations this build has that the database has not applied. Read-only - it does NOT
        /// apply anything. Throws when the schema can't be read; the caller reports that as a
        /// schema error.
        /// </summary>
        Task<IReadOnlyList<string>> GetPendingMigrationsAsync();

        /// <summary>
        /// Teams call-records webhook subscription state (from the applied installer config plus a
        /// cached Graph lookup). Throws when it can't be read; the caller reports that as a config error.
        /// </summary>
        Task<CallWebhookStatusResult> GetCallWebhookStatusAsync();
    }

    /// <summary>Outcome of the cheap database-reachability probe.</summary>
    public class DatabaseProbeResult
    {
        /// <summary>The failure message, or null when the database was reachable.</summary>
        public string Error { get; set; }

        public bool Reachable => string.IsNullOrEmpty(Error);
    }

    /// <summary>One Graph Copilot usage-report import (the most recent one for that report).</summary>
    public class CopilotUsageReportImportRow
    {
        public string ReportName { get; set; }
        public DateTime ImportedUtc { get; set; }
        public bool IsUpnObfuscated { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// The SQL "data overview" figures. Each block fails independently, mirroring the two nested
    /// try/catch scopes this replaces: a DMV permission failure only sets <see cref="CountsError"/> and
    /// still returns the tracked-Teams / Copilot-import figures, while a hard connection failure sets
    /// <see cref="DataError"/> and leaves whatever had already been read in place.
    /// </summary>
    public class DatabaseCountsResult
    {
        public long ActivityCount { get; set; }
        public long HitCount { get; set; }
        public long TeamsCount { get; set; }
        public long SentEmailCount { get; set; }
        public long CallRecordCount { get; set; }
        public long CopilotChatCount { get; set; }
        public long UserCount { get; set; }
        public long DatabaseSizeMb { get; set; }

        /// <summary>Set when the cheap DMV counts / DB size couldn't be read (e.g. no VIEW DATABASE STATE).</summary>
        public string CountsError { get; set; }

        public int TeamsBeingTrackedCount { get; set; }

        /// <summary>The latest import per Copilot usage report, or null when that query didn't run.</summary>
        public IReadOnlyList<CopilotUsageReportImportRow> CopilotUsageReportImports { get; set; }

        /// <summary>Set only on a hard failure (e.g. the database is unreachable).</summary>
        public string DataError { get; set; }
    }

    /// <summary>24h / 7d volume and freshness for one fact table.</summary>
    public class RecentVolumeResult
    {
        public long Last24h { get; set; }
        public long Last7d { get; set; }
        public DateTime? Newest { get; set; }

        /// <summary>Set when the scan failed or timed out; the counts are then not meaningful.</summary>
        public string Error { get; set; }
    }

    /// <summary>Teams call-records webhook subscription state.</summary>
    public class CallWebhookStatusResult
    {
        public bool CallsImportEnabled { get; set; }
        public string WebhookState { get; set; }
        public DateTimeOffset? WebhookExpiryUtc { get; set; }
        public string WebhookDetail { get; set; }
    }
}
