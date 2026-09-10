using Common.Entities;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Migrations;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.Health
{
    /// <summary>
    /// EF/SQL adapter for <see cref="IHealthDataSource"/> - the only part of the Health feature that
    /// opens an <see cref="AnalyticsEntitiesContext"/>.
    /// </summary>
    /// <remarks>
    /// Performance: every query here runs with a short per-query command timeout.
    /// <see cref="AnalyticsEntitiesContext"/> sets an infinite command timeout (for long
    /// importer/migration work); here a single unindexed scan of audit_events / hits on a big tenant
    /// would otherwise run until Azure App Service kills the HTTP request (~230s) -&gt; 500. Capping each
    /// query makes it degrade to a per-metric error instead.
    /// </remarks>
    public class SqlHealthDataSource : IHealthDataSource
    {
        /// <summary>Per-query SQL timeout for the heavy Data-section scans.</summary>
        public const int SqlQueryTimeoutSecs = 20;

        /// <summary>The overall roll-up only needs "can we reach the DB?", so its probe is capped even shorter.</summary>
        public const int DbProbeTimeoutSecs = 10;

        private readonly IAnalyticsDbContextFactory _contextFactory;

        public SqlHealthDataSource() : this(DefaultAnalyticsDbContextFactory.Instance)
        {
        }

        public SqlHealthDataSource(IAnalyticsDbContextFactory contextFactory)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        public async Task<DatabaseProbeResult> ProbeDatabaseAsync()
        {
            try
            {
                using (var db = _contextFactory.Create())
                {
                    db.Database.CommandTimeout = DbProbeTimeoutSecs;
                    await db.Database.SqlQuery<int>("SELECT 1").ToListAsync();
                }
                return new DatabaseProbeResult();
            }
            catch (Exception ex)
            {
                return new DatabaseProbeResult { Error = HealthDataSectionRules.InnermostMessage(ex) };
            }
        }

        public async Task<DatabaseCountsResult> GetDatabaseCountsAsync()
        {
            var result = new DatabaseCountsResult();

            // Approximate counts + DB size come from DMVs (sys.dm_db_partition_stats / sys.database_files),
            // which need VIEW DATABASE STATE. Isolate them so a locked-down SQL login still gets the more
            // important recent-volume + freshness signals.
            try
            {
                using (var db = _contextFactory.Create())
                {
                    db.Database.CommandTimeout = SqlQueryTimeoutSecs;
                    try
                    {
                        var approx = await LoadApproxRowCounts(db);
                        result.ActivityCount = ApproxFor(approx, "audit_events");
                        result.HitCount = ApproxFor(approx, "hits");
                        result.TeamsCount = ApproxFor(approx, "teams");
                        result.SentEmailCount = ApproxFor(approx, "sent_emails");
                        result.CallRecordCount = ApproxFor(approx, "call_records");
                        result.CopilotChatCount = ApproxFor(approx, "copilot_chats");
                        result.UserCount = ApproxFor(approx, "users");
                        result.DatabaseSizeMb = await LoadDatabaseSizeMb(db);
                    }
                    catch (Exception dmvEx)
                    {
                        // Counts stay 0; the recent-volume / freshness rows are the real "is it flowing" signal.
                        result.CountsError = HealthDataSectionRules.InnermostMessage(dmvEx);
                    }

                    // Teams-being-tracked is a filtered count on a small table (thousands of rows) - cheap.
                    result.TeamsBeingTrackedCount = await db.Teams.Where(t => t.HasRefreshToken).CountAsync();

                    // Latest Copilot usage-report import per report. One row per import on a tiny table, so
                    // this is free. Without it the "tenant conceals user identities" case is invisible: the
                    // import succeeds, stores nothing, and looks exactly like a tenant with no Copilot usage -
                    // and a failed download looks like a recent healthy import.
                    var latestCopilotImports = await db.CopilotUsageReportImportLogs
                        .GroupBy(l => l.ReportName)
                        .Select(g => g.OrderByDescending(l => l.ImportedUtc).FirstOrDefault())
                        .ToListAsync();

                    result.CopilotUsageReportImports = latestCopilotImports
                        .Where(i => i != null)
                        .Select(i => new CopilotUsageReportImportRow
                        {
                            ReportName = i.ReportName,
                            ImportedUtc = i.ImportedUtc,
                            IsUpnObfuscated = i.IsUpnObfuscated,
                            Error = i.Error,
                        })
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                result.DataError = HealthDataSectionRules.InnermostMessage(ex);
            }

            return result;
        }

        public async Task<RecentVolumeResult> GetRecentVolumeAsync(string table, string timestampColumn)
        {
            var volume = new RecentVolumeResult();
            try
            {
                using (var db = _contextFactory.Create())
                {
                    db.Database.CommandTimeout = SqlQueryTimeoutSecs;
                    var p24 = new SqlParameter("@c24", DateTime.UtcNow.AddHours(-24));
                    var p7 = new SqlParameter("@c7", DateTime.UtcNow.AddDays(-7));
                    var sql =
                        $"SELECT SUM(CASE WHEN [{timestampColumn}] > @c24 THEN CAST(1 AS BIGINT) ELSE 0 END) AS Last24h, " +
                        $"SUM(CASE WHEN [{timestampColumn}] > @c7 THEN CAST(1 AS BIGINT) ELSE 0 END) AS Last7d, " +
                        $"MAX([{timestampColumn}]) AS Newest " +
                        $"FROM [dbo].[{table}]";
                    var r = (await db.Database.SqlQuery<RecentVolumeRow>(sql, p24, p7).ToListAsync()).FirstOrDefault();
                    if (r != null)
                    {
                        volume.Last24h = r.Last24h ?? 0;
                        volume.Last7d = r.Last7d ?? 0;
                        volume.Newest = r.Newest.HasValue ? DateTime.SpecifyKind(r.Newest.Value, DateTimeKind.Utc) : (DateTime?)null;
                    }
                }
            }
            catch (Exception ex)
            {
                volume.Error = HealthDataSectionRules.InnermostMessage(ex);
            }
            return volume;
        }

        public Task<IReadOnlyList<string>> GetPendingMigrationsAsync()
        {
            // Read-only: compares this build's migrations against __MigrationHistory. Does NOT apply
            // anything. DbMigrator is synchronous, so there is nothing to await.
            var migrationsConfig = new Common.Entities.Migrations.Configuration();
            var migrator = new DbMigrator(migrationsConfig);
            IReadOnlyList<string> pending = migrator.GetPendingMigrations().ToList();
            return Task.FromResult(pending);
        }

        public async Task<CallWebhookStatusResult> GetCallWebhookStatusAsync()
        {
            using (var db = _contextFactory.Create())
            {
                // Reuse the homepage's tested logic (config from the applied installer config + a
                // cached Graph lookup of the Teams call-records webhook subscription).
                var status = await SystemStatus.LoadFrom(db, null);
                return new CallWebhookStatusResult
                {
                    CallsImportEnabled = status.CallsImportEnabled,
                    WebhookState = status.CallWebhookState.ToString(),
                    WebhookExpiryUtc = status.CallWebhookExpiry,
                    WebhookDetail = status.CallWebhookStatusDetail,
                };
            }
        }

        private static async Task<Dictionary<string, long>> LoadApproxRowCounts(AnalyticsEntitiesContext db)
        {
            const string sql =
                "SELECT o.name AS TableName, SUM(ps.row_count) AS Rows " +
                "FROM sys.dm_db_partition_stats ps " +
                "JOIN sys.objects o ON o.object_id = ps.object_id " +
                "WHERE ps.index_id IN (0,1) AND o.[type] = 'U' " +
                "GROUP BY o.name";
            var rows = await db.Database.SqlQuery<TableRowCount>(sql).ToListAsync();
            var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                if (!string.IsNullOrEmpty(r.TableName)) dict[r.TableName] = r.Rows;
            }
            return dict;
        }

        private static long ApproxFor(Dictionary<string, long> counts, string tableName)
            => counts != null && counts.TryGetValue(tableName, out var n) ? n : 0;

        private static async Task<long> LoadDatabaseSizeMb(AnalyticsEntitiesContext db)
        {
            // type = 0 => data files (exclude the log). size is in 8 KB pages.
            const string sql = "SELECT CAST(ISNULL(SUM(CAST(size AS BIGINT)), 0) * 8 / 1024 AS BIGINT) FROM sys.database_files WHERE [type] = 0";
            var result = await db.Database.SqlQuery<long>(sql).ToListAsync();
            return result.FirstOrDefault();
        }

        private class TableRowCount
        {
            public string TableName { get; set; }
            public long Rows { get; set; }
        }

        private class RecentVolumeRow
        {
            public long? Last24h { get; set; }
            public long? Last7d { get; set; }
            public DateTime? Newest { get; set; }
        }
    }
}
