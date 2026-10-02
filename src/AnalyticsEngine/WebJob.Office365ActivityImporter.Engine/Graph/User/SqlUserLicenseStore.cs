using Common.Entities;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Linq;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Threading.Tasks;
namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// SQL Server adapter for <see cref="IUserLicenseStore"/>.
    /// </summary>
    /// <remarks>
    /// Raw batched SQL rather than EF: EF6 issues one INSERT round-trip per entity, which was measured
    /// at synthetic scale (600k assignments over 200k users, local SQL, no network latency) at
    /// <b>533 rows/s</b> - about 19 minutes to populate the table, and that refill is exactly what
    /// customers were watching an incomplete licence table during. The batched, parameterised
    /// statements below do the same work at <b>11,285 rows/s</b>. Because
    /// <see cref="UserLicenseProcessor"/> now writes only the difference, a steady-state cycle
    /// writes nothing at all.
    /// </remarks>
    public class SqlUserLicenseStore : IUserLicenseStore
    {
        /// <summary>
        /// Rows per statement. Measured against a 600k-row table (rows/s, higher is better):
        /// 100 -> 10,125; <b>250 -> 11,285</b>; 500 -> 10,524; 1000 -> 8,301. Wide value lists lose
        /// because SQL Server's cost to compile a table value constructor grows faster than the
        /// saving from fewer round-trips. 250 also keeps the parameter count (2 per row) far below
        /// the 2100 per-statement limit and the 1000-row table-value-constructor limit.
        /// </summary>
        private const int MAX_ROWS_PER_STATEMENT = 250;
        private readonly AnalyticsEntitiesContext _db;
        private readonly ILogger _logger;
        // Cached statement text for a full-size batch. Parameterised, so it is byte-identical every
        // time and SQL Server compiles the plan once then reuses it for every subsequent batch.
        private string _fullBatchInsertSql;
        private string _fullBatchInsertWithHistorySql;
        private string _fullBatchDeleteSql;
        private string _fullBatchDeleteWithHistorySql;
        private string _fullBatchSeedSql;
        public SqlUserLicenseStore(AnalyticsEntitiesContext db, ILogger logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        public async Task<HashSet<UserLicenseAssignment>> LoadAssignmentsFor(ICollection<int> userIds)
        {
            var loaded = new HashSet<UserLicenseAssignment>();
            if (userIds == null || userIds.Count == 0)
            {
                return loaded;
            }
            var scope = userIds as HashSet<int> ?? new HashSet<int>(userIds);
            // One pass over the whole table, filtered in memory against the scope. The refresh this
            // serves covers the entire user population, so a single scan of the narrow unique index
            // on (license_type_id, user_id) - which covers both columns read here - is cheaper than
            // chunked IN-lists and avoids the 2100-parameter limit entirely. Measured over 600k rows:
            // 1,045 logical reads, ~80ms.
            var conn = _db.Database.Connection;
            var openedHere = conn.State != ConnectionState.Open;
            if (openedHere)
            {
                // Not conn.OpenAsync(): that bypasses EF's Entra token interceptor, and the caller enumerates
                // every SKU's holders from Graph between its last EF call and this one - long enough on a large
                // tenant for the token EF attached then to have expired (#609).
                await AzureSqlTokenAuth.OpenAsync(conn);
            }
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT user_id, license_type_id FROM dbo.user_license_type_lookups";
                    cmd.CommandTimeout = 0;
                    cmd.Transaction = _db.Database.CurrentTransaction?.UnderlyingTransaction;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var userId = reader.GetInt32(0);
                            if (!scope.Contains(userId))
                            {
                                continue;
                            }
                            loaded.Add(new UserLicenseAssignment(userId, reader.GetInt32(1)));
                        }
                    }
                }
            }
            finally
            {
                if (openedHere)
                {
                    conn.Close();
                }
            }
            _logger.LogDebug($"User import - read {loaded.Count.ToString("N0")} existing licence assignment(s) for {scope.Count.ToString("N0")} in-scope user(s).");
            return loaded;
        }
        public async Task<LicenseRefreshRunInfo> StartRefresh(DateTime completedUtc)
        {
            var observed = TruncateToSecond(completedUtc.Kind == DateTimeKind.Utc ? completedUtc : completedUtc.ToUniversalTime());
            var probe = await _db.Database.SqlQuery<RefreshProbe>(@"
IF OBJECT_ID(N'dbo.license_refresh_runs', N'U') IS NULL
   OR OBJECT_ID(N'dbo.user_license_history', N'U') IS NULL
   OR OBJECT_ID(N'dbo.license_seat_count_history', N'U') IS NULL
BEGIN
    SELECT TablesAvailable = 0, PreviousCompletedUtc = CONVERT(datetime2(0), NULL);
END
ELSE
BEGIN
    SELECT TablesAvailable = 1, PreviousCompletedUtc = MAX(completed_utc) FROM dbo.license_refresh_runs;
END").SingleAsync();
            if (probe.TablesAvailable == 0)
            {
                _logger.LogWarning("User import - licence history tables are not present yet; current licence lookups will be refreshed without history until the database migration has run.");
            }
            return new LicenseRefreshRunInfo(observed, probe.PreviousCompletedUtc, probe.TablesAvailable != 0);
        }
        public async Task<int> CarryAssignmentsAcrossRenamedLicenceTypes(IReadOnlyList<int> currentLicenseTypeIds, LicenseRefreshRunInfo refresh)
        {
            if (currentLicenseTypeIds == null || currentLicenseTypeIds.Count == 0)
            {
                return 0;
            }
            var distinct = currentLicenseTypeIds.Distinct().ToList();
            var values = new StringBuilder(distinct.Count * 8);
            var parameters = new List<object>();
            for (var i = 0; i < distinct.Count; i++)
            {
                if (i > 0) values.Append(',');
                values.Append("(@l").Append(i).Append(')');
                parameters.Add(new SqlParameter("@l" + i, SqlDbType.Int) { Value = distinct[i] });
            }
            var sql = @"SET XACT_ABORT ON;
DECLARE @moved TABLE (user_id int NOT NULL, old_license_type_id int NOT NULL, new_license_type_id int NOT NULL);
DECLARE @current TABLE (license_type_id int NOT NULL PRIMARY KEY);
INSERT INTO @current (license_type_id) VALUES " + values + @";
BEGIN TRANSACTION;
;WITH rename_map AS
(
    SELECT old_lt.id AS old_license_type_id, new_lt.id AS new_license_type_id
    FROM @current AS c
    INNER JOIN dbo.license_types AS new_lt ON new_lt.id = c.license_type_id
    INNER JOIN dbo.license_types AS old_lt
        ON old_lt.sku_id = new_lt.sku_id
       AND old_lt.id <> new_lt.id
    WHERE new_lt.sku_id IS NOT NULL AND new_lt.sku_id <> N''
)
UPDATE lookup
SET license_type_id = rename_map.new_license_type_id
OUTPUT inserted.user_id, deleted.license_type_id, inserted.license_type_id
INTO @moved (user_id, old_license_type_id, new_license_type_id)
FROM dbo.user_license_type_lookups AS lookup
INNER JOIN rename_map ON rename_map.old_license_type_id = lookup.license_type_id
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.user_license_type_lookups AS existing
    WHERE existing.user_id = lookup.user_id
      AND existing.license_type_id = rename_map.new_license_type_id
);
IF OBJECT_ID(N'dbo.user_license_history', N'U') IS NOT NULL
BEGIN
    UPDATE history
    SET license_type_id = moved.new_license_type_id
    FROM dbo.user_license_history AS history
    INNER JOIN @moved AS moved
        ON moved.user_id = history.user_id
       AND moved.old_license_type_id = history.license_type_id
    WHERE history.valid_to_utc IS NULL
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.user_license_history AS already
          WHERE already.user_id = moved.user_id
            AND already.license_type_id = moved.new_license_type_id
            AND already.valid_to_utc IS NULL
      );
END
DECLARE @changed int = (SELECT COUNT(*) FROM @moved);
COMMIT TRANSACTION;
SELECT @changed;";
            var moved = await _db.Database.SqlQuery<int>(sql, parameters.ToArray()).SingleAsync();
            if (moved > 0)
            {
                _logger.LogInformation($"User import - carried {moved.ToString("N0")} current licence assignment(s) across renamed licence-type rows for the same SKU, preserving their open history.");
            }
            return moved;
        }
        public async Task<int> SeedCurrentAssignments(IReadOnlyList<UserLicenseAssignment> assignments, LicenseRefreshRunInfo refresh)
        {
            if (assignments == null || assignments.Count == 0 || refresh == null || !refresh.IsFirstHistoryRefresh)
            {
                return 0;
            }
            var written = 0;
            for (var i = 0; i < assignments.Count; i += MAX_ROWS_PER_STATEMENT)
            {
                var take = Math.Min(MAX_ROWS_PER_STATEMENT, assignments.Count - i);
                var sql = BuildBatchSql(take, ref _fullBatchSeedSql, valuesList =>
                    "INSERT INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source, valid_from_previous_refresh_utc)\r\n" +
                    "SELECT v.user_id, v.license_type_id, @refreshUtc, NULL, CONVERT(tinyint, 0), @previousRefreshUtc\r\n" +
                    $"FROM (VALUES {valuesList}) AS v(user_id, license_type_id)\r\n" +
                    "WHERE NOT EXISTS (\r\n" +
                    "    SELECT 1 FROM dbo.user_license_history AS h\r\n" +
                    "    WHERE h.license_type_id = v.license_type_id AND h.user_id = v.user_id AND h.valid_to_utc IS NULL); ");
                written += await _db.Database.ExecuteSqlCommandAsync(sql, BuildParameters(assignments, i, take, refresh));
            }
            return written;
        }
        public async Task<int> AddAssignments(IReadOnlyList<UserLicenseAssignment> assignments, LicenseRefreshRunInfo refresh)
        {
            if (assignments == null || assignments.Count == 0)
            {
                return 0;
            }
            var written = 0;
            for (var i = 0; i < assignments.Count; i += MAX_ROWS_PER_STATEMENT)
            {
                var take = Math.Min(MAX_ROWS_PER_STATEMENT, assignments.Count - i);
                var hasHistory = refresh?.HistoryTablesAvailable == true;
                // NOT EXISTS keeps the insert idempotent: dbo.user_license_type_lookups has a UNIQUE
                // index on (license_type_id, user_id), so a row inserted by anything else since the
                // current state was read would otherwise fail the whole batch.
                var sql = hasHistory
                    ? BuildBatchSql(take, ref _fullBatchInsertWithHistorySql, valuesList =>
                        "INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id)\r\n" +
                        "OUTPUT inserted.user_id, inserted.license_type_id, @refreshUtc, NULL, CONVERT(tinyint, 1), @previousRefreshUtc\r\n" +
                        "INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source, valid_from_previous_refresh_utc)\r\n" +
                        "SELECT v.user_id, v.license_type_id\r\n" +
                        $"FROM (VALUES {valuesList}) AS v(user_id, license_type_id)\r\n" +
                        "WHERE NOT EXISTS (\r\n" +
                        "    SELECT 1 FROM dbo.user_license_type_lookups AS t\r\n" +
                        "    WHERE t.license_type_id = v.license_type_id AND t.user_id = v.user_id); ")
                    : BuildBatchSql(take, ref _fullBatchInsertSql, valuesList =>
                        "INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id)\r\n" +
                        "SELECT v.user_id, v.license_type_id\r\n" +
                        $"FROM (VALUES {valuesList}) AS v(user_id, license_type_id)\r\n" +
                        "WHERE NOT EXISTS (\r\n" +
                        "    SELECT 1 FROM dbo.user_license_type_lookups AS t\r\n" +
                        "    WHERE t.license_type_id = v.license_type_id AND t.user_id = v.user_id); ");
                written += await ExecuteInsertBatchWithDuplicateRetry(sql, assignments, i, take, refresh);
            }
            return written;
        }
        /// <summary>
        /// NOT EXISTS is a check, not a lock: under READ COMMITTED another writer can insert the same
        /// pair between the check and the insert, and the UNIQUE index then fails the whole batch
        /// (SQL error 2601/2627). Retrying re-evaluates NOT EXISTS, which now sees the row and skips
        /// it, so the batch converges instead of aborting the import over a row that already holds
        /// the value we wanted.
        /// </summary>
        private async Task<int> ExecuteInsertBatchWithDuplicateRetry(
            string sql, IReadOnlyList<UserLicenseAssignment> assignments, int offset, int count, LicenseRefreshRunInfo refresh)
        {
            const int MAX_ATTEMPTS = 3;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await _db.Database.ExecuteSqlCommandAsync(sql, BuildParameters(assignments, offset, count, refresh));
                }
                catch (Exception ex) when (attempt < MAX_ATTEMPTS && IsDuplicateKeyViolation(ex))
                {
                    _logger.LogWarning($"User import - a licence assignment batch hit a duplicate-key race (attempt {attempt} of {MAX_ATTEMPTS}); retrying. This means something else inserted the same assignment concurrently.");
                }
            }
        }
        /// <summary>
        /// SQL Server raises 2601 ("Cannot insert duplicate key row in object ... with unique index")
        /// and 2627 ("Violation of UNIQUE KEY constraint") for the same situation here. EF6 may wrap
        /// the provider exception, so walk the chain.
        /// </summary>
        private static bool IsDuplicateKeyViolation(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is SqlException sqlEx)
                {
                    foreach (SqlError error in sqlEx.Errors)
                    {
                        if (error.Number == 2601 || error.Number == 2627)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        public async Task<int> RemoveAssignments(IReadOnlyList<UserLicenseAssignment> assignments, LicenseRefreshRunInfo refresh)
        {
            if (assignments == null || assignments.Count == 0)
            {
                return 0;
            }
            var deleted = 0;
            for (var i = 0; i < assignments.Count; i += MAX_ROWS_PER_STATEMENT)
            {
                var take = Math.Min(MAX_ROWS_PER_STATEMENT, assignments.Count - i);
                var hasHistory = refresh?.HistoryTablesAvailable == true;
                var sql = hasHistory
                    ? BuildBatchSql(take, ref _fullBatchDeleteWithHistorySql, valuesList =>
                        "SET XACT_ABORT ON;\r\n" +
                        "DECLARE @removed TABLE (user_id int NOT NULL, license_type_id int NOT NULL);\r\n" +
                        "BEGIN TRANSACTION;\r\n" +
                        "DELETE t\r\n" +
                        "OUTPUT deleted.user_id, deleted.license_type_id INTO @removed (user_id, license_type_id)\r\n" +
                        "FROM dbo.user_license_type_lookups AS t\r\n" +
                        $"INNER JOIN (VALUES {valuesList}) AS v(user_id, license_type_id)\r\n" +
                        "    ON t.license_type_id = v.license_type_id AND t.user_id = v.user_id;\r\n" +
                        "UPDATE history\r\n" +
                        "SET valid_to_utc = @refreshUtc, valid_to_previous_refresh_utc = @previousRefreshUtc\r\n" +
                        "FROM dbo.user_license_history AS history\r\n" +
                        "INNER JOIN @removed AS removed\r\n" +
                        "    ON removed.user_id = history.user_id AND removed.license_type_id = history.license_type_id\r\n" +
                        "WHERE history.valid_to_utc IS NULL;\r\n" +
                        "DECLARE @deleted int = (SELECT COUNT(*) FROM @removed);\r\n" +
                        "COMMIT TRANSACTION;\r\n" +
                        "SELECT @deleted;")
                    : BuildBatchSql(take, ref _fullBatchDeleteSql, valuesList =>
                        "DELETE t\r\n" +
                        "FROM dbo.user_license_type_lookups AS t\r\n" +
                        $"INNER JOIN (VALUES {valuesList}) AS v(user_id, license_type_id)\r\n" +
                        "    ON t.license_type_id = v.license_type_id AND t.user_id = v.user_id;");
                if (hasHistory)
                {
                    deleted += await _db.Database.SqlQuery<int>(sql, BuildParameters(assignments, i, take, refresh)).SingleAsync();
                }
                else
                {
                    deleted += await _db.Database.ExecuteSqlCommandAsync(sql, BuildParameters(assignments, i, take));
                }
            }
            return deleted;
        }
        public async Task<int?> CompleteRefresh(LicenseRefreshRunInfo refresh, IReadOnlyList<LicenseSeatCountSnapshot> seatCounts)
        {
            if (refresh == null || !refresh.HistoryTablesAvailable)
            {
                return null;
            }
            var runId = await _db.Database.SqlQuery<int>(
                "INSERT INTO dbo.license_refresh_runs (completed_utc, previous_completed_utc) OUTPUT inserted.id VALUES (@completedUtc, @previousRefreshUtc);",
                BuildRefreshParameters(refresh)).SingleAsync();
            if (seatCounts != null)
            {
                foreach (var seatCount in seatCounts)
                {
                    await _db.Database.ExecuteSqlCommandAsync(
                        @"INSERT INTO dbo.license_seat_count_history
(refresh_run_id, license_type_id, observed_utc, consumed_units, prepaid_enabled_units, prepaid_warning_units, prepaid_suspended_units)
VALUES (@runId, @licenseTypeId, @observedUtc, @consumed, @enabled, @warning, @suspended);",
                        new SqlParameter("@runId", SqlDbType.Int) { Value = runId },
                        new SqlParameter("@licenseTypeId", SqlDbType.Int) { Value = seatCount.LicenseTypeId },
                        new SqlParameter("@observedUtc", SqlDbType.DateTime2) { Value = refresh.CompletedUtc },
                        NullableInt("@consumed", seatCount.ConsumedUnits),
                        NullableInt("@enabled", seatCount.PrepaidEnabledUnits),
                        NullableInt("@warning", seatCount.PrepaidWarningUnits),
                        NullableInt("@suspended", seatCount.PrepaidSuspendedUnits));
                }
            }
            return runId;
        }
        /// <summary>
        /// Renders the statement for a batch of <paramref name="rowCount"/> rows, caching the text of
        /// a full-size batch so every full batch after the first is a plan-cache hit.
        /// </summary>
        private static string BuildBatchSql(int rowCount, ref string fullBatchCache, Func<string, string> build)
        {
            if (rowCount == MAX_ROWS_PER_STATEMENT && fullBatchCache != null)
            {
                return fullBatchCache;
            }
            var values = new StringBuilder(rowCount * 16);
            for (var i = 0; i < rowCount; i++)
            {
                if (i > 0)
                {
                    values.Append(',');
                }
                values.Append("(@u").Append(i).Append(",@l").Append(i).Append(')');
            }
            var sql = build(values.ToString());
            if (rowCount == MAX_ROWS_PER_STATEMENT)
            {
                fullBatchCache = sql;
            }
            return sql;
        }
        private static object[] BuildParameters(IReadOnlyList<UserLicenseAssignment> assignments, int offset, int count, LicenseRefreshRunInfo refresh = null)
        {
            var parameters = new List<object>(count * 2 + 2);
            for (var i = 0; i < count; i++)
            {
                var assignment = assignments[offset + i];
                parameters.Add(new SqlParameter("@u" + i, SqlDbType.Int) { Value = assignment.UserId });
                parameters.Add(new SqlParameter("@l" + i, SqlDbType.Int) { Value = assignment.LicenseTypeId });
            }
            if (refresh != null)
            {
                parameters.AddRange(BuildRefreshParameters(refresh));
            }
            return parameters.ToArray();
        }
        private static object[] BuildRefreshParameters(LicenseRefreshRunInfo refresh)
        {
            return new object[]
            {
                new SqlParameter("@refreshUtc", SqlDbType.DateTime2) { Value = refresh.CompletedUtc },
                new SqlParameter("@completedUtc", SqlDbType.DateTime2) { Value = refresh.CompletedUtc },
                new SqlParameter("@previousRefreshUtc", SqlDbType.DateTime2) { Value = (object)refresh.PreviousCompletedUtc ?? DBNull.Value },
            };
        }
        private static SqlParameter NullableInt(string name, int? value)
            => new SqlParameter(name, SqlDbType.Int) { Value = (object)value ?? DBNull.Value };
        private static DateTime TruncateToSecond(DateTime value)
            => new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        private sealed class RefreshProbe
        {
            public int TablesAvailable { get; set; }
            public DateTime? PreviousCompletedUtc { get; set; }
        }
    }
}
