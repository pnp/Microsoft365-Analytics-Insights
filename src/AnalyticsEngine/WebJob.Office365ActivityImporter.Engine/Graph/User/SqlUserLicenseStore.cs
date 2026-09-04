using Common.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Data.SqlClient;
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
        // time and SQL Server compiles the plan once then reuses it for every subsequent batch. That
        // plan reuse is most of the 2.1x gap between this and inlining the values as literals.
        private string _fullBatchInsertSql;
        private string _fullBatchDeleteSql;

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
                await conn.OpenAsync();
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

        public async Task<int> AddAssignments(IReadOnlyList<UserLicenseAssignment> assignments)
        {
            if (assignments == null || assignments.Count == 0)
            {
                return 0;
            }

            var written = 0;
            for (var i = 0; i < assignments.Count; i += MAX_ROWS_PER_STATEMENT)
            {
                var take = Math.Min(MAX_ROWS_PER_STATEMENT, assignments.Count - i);

                // NOT EXISTS keeps the insert idempotent: dbo.user_license_type_lookups has a UNIQUE
                // index on (license_type_id, user_id), so a row inserted by anything else since the
                // current state was read would otherwise fail the whole batch.
                var sql = BuildBatchSql(take, ref _fullBatchInsertSql, valuesList =>
                    "INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id)\r\n" +
                    "SELECT v.user_id, v.license_type_id\r\n" +
                    $"FROM (VALUES {valuesList}) AS v(user_id, license_type_id)\r\n" +
                    "WHERE NOT EXISTS (\r\n" +
                    "    SELECT 1 FROM dbo.user_license_type_lookups AS t\r\n" +
                    "    WHERE t.license_type_id = v.license_type_id AND t.user_id = v.user_id);");

                written += await ExecuteInsertBatchWithDuplicateRetry(sql, assignments, i, take);
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
            string sql, IReadOnlyList<UserLicenseAssignment> assignments, int offset, int count)
        {
            const int MAX_ATTEMPTS = 3;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await _db.Database.ExecuteSqlCommandAsync(sql, BuildParameters(assignments, offset, count));
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

        public async Task<int> RemoveAssignments(IReadOnlyList<UserLicenseAssignment> assignments)
        {
            if (assignments == null || assignments.Count == 0)
            {
                return 0;
            }

            var deleted = 0;
            for (var i = 0; i < assignments.Count; i += MAX_ROWS_PER_STATEMENT)
            {
                var take = Math.Min(MAX_ROWS_PER_STATEMENT, assignments.Count - i);

                var sql = BuildBatchSql(take, ref _fullBatchDeleteSql, valuesList =>
                    "DELETE t\r\n" +
                    "FROM dbo.user_license_type_lookups AS t\r\n" +
                    $"INNER JOIN (VALUES {valuesList}) AS v(user_id, license_type_id)\r\n" +
                    "    ON t.license_type_id = v.license_type_id AND t.user_id = v.user_id;");

                deleted += await _db.Database.ExecuteSqlCommandAsync(sql, BuildParameters(assignments, i, take));
            }

            return deleted;
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

        private static object[] BuildParameters(IReadOnlyList<UserLicenseAssignment> assignments, int offset, int count)
        {
            var parameters = new object[count * 2];
            for (var i = 0; i < count; i++)
            {
                var assignment = assignments[offset + i];
                parameters[i * 2] = new SqlParameter("@u" + i, SqlDbType.Int) { Value = assignment.UserId };
                parameters[(i * 2) + 1] = new SqlParameter("@l" + i, SqlDbType.Int) { Value = assignment.LicenseTypeId };
            }
            return parameters;
        }
    }
}
