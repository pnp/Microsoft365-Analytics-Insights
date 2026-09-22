using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// SQL Server implementation of <see cref="IUserOrgImportJobStore"/>.
    /// </summary>
    /// <remarks>
    /// The job and its staged rows live in the database rather than in web-process memory, so an
    /// App Service recycle mid-import leaves a visible, resumable record instead of a request that
    /// silently stopped existing. It also gives the admin page an audit trail of who imported what.
    /// </remarks>
    internal sealed class SqlUserOrgImportJobStore : SqlUserOrgStoreBase, IUserOrgImportJobStore
    {
        private const string JobColumns =
            "id, org_type_id, mode, status, file_name, started_by, queued_utc, started_utc, finished_utc, "
            + "heartbeat_utc, rows_total, rows_applied, rows_cleared, rows_unknown_upn, rows_invalid, error_message";

        public SqlUserOrgImportJobStore(string connectionString) : base(connectionString)
        {
        }

        public async Task<int> CreateJobWithRowsAsync(
            UserOrgImportJob job,
            IReadOnlyList<UserOrgStagedRow> rows,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            const string insertJobSql = @"
SET NOCOUNT ON;

-- The active-job check happens HERE, inside the transaction and holding a range lock, rather than in
-- the caller. Checking first and inserting afterwards left a window wide enough to drive a lorry
-- through - the whole CSV parse sat between them - so two admins uploading at once could both see no
-- active job and queue competing imports for the same organisation type, producing a nondeterministic
-- winner or a deadlock.
IF EXISTS (SELECT 1 FROM dbo.user_org_import_jobs WITH (UPDLOCK, HOLDLOCK)
           WHERE org_type_id = @orgTypeId
             AND (
                  -- Pending, but only while it could still plausibly be picked up. A job whose
                  -- dispatch was lost to an App Service recycle after the rows were staged would
                  -- otherwise block every later upload for this organisation type forever.
                  (status = 1 AND queued_utc > DATEADD(SECOND, -@pendingStaleSecs, SYSUTCDATETIME()))
                  -- Running, and still reporting progress.
               OR (status = 2 AND ISNULL(heartbeat_utc, started_utc) > DATEADD(SECOND, -@runningStaleSecs, SYSUTCDATETIME()))
             ))
BEGIN
    RAISERROR('USERORG_ACTIVE_JOB', 16, 1);
    RETURN;
END

INSERT INTO dbo.user_org_import_jobs
    (org_type_id, mode, status, file_name, started_by, queued_utc, rows_total, rows_invalid)
OUTPUT INSERTED.id
VALUES (@orgTypeId, @mode, @status, @fileName, @startedBy, SYSUTCDATETIME(), @rowsTotal, @rowsInvalid);";

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var tx = connection.BeginTransaction())
            {
                int jobId;
                using (var cmd = Command(connection, insertJobSql, tx))
                {
                    cmd.Parameters.Add("@orgTypeId", SqlDbType.Int).Value = job.OrgTypeId;
                    cmd.Parameters.Add("@mode", SqlDbType.TinyInt).Value = (byte)job.Mode;
                    cmd.Parameters.Add("@status", SqlDbType.TinyInt).Value = (byte)UserOrgImportStatus.Pending;
                    cmd.Parameters.Add("@fileName", SqlDbType.NVarChar, 260).Value = DbValue(job.FileName);
                    cmd.Parameters.Add("@startedBy", SqlDbType.NVarChar, 256).Value = job.StartedBy ?? string.Empty;
                    cmd.Parameters.Add("@rowsTotal", SqlDbType.Int).Value = rows == null ? 0 : rows.Count;
                    cmd.Parameters.Add("@rowsInvalid", SqlDbType.Int).Value = job.RowsInvalid;
                    cmd.Parameters.Add("@pendingStaleSecs", SqlDbType.Int).Value =
                        (int)UserOrgImportRunner.StalePendingThreshold.TotalSeconds;
                    cmd.Parameters.Add("@runningStaleSecs", SqlDbType.Int).Value =
                        (int)UserOrgImportRunner.StaleHeartbeatThreshold.TotalSeconds;

                    object id;
                    try
                    {
                        id = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (SqlException ex) when (ex.Message.IndexOf("USERORG_ACTIVE_JOB", StringComparison.Ordinal) >= 0)
                    {
                        throw new UserOrgValidationException(
                            "An import for this organisation type is already in progress. Wait for it to finish "
                            + "before starting another.", ex);
                    }

                    if (id == null || id == DBNull.Value)
                    {
                        throw new UserOrgValidationException(
                            "The import could not be queued. Try again in a moment.");
                    }

                    jobId = Convert.ToInt32(id);
                }

                if (rows != null && rows.Count > 0)
                {
                    var table = BuildStagingTable(jobId, rows);

                    using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, tx))
                    {
                        bulkCopy.DestinationTableName = "dbo.user_org_import_staging";
                        bulkCopy.BatchSize = 10000;
                        bulkCopy.BulkCopyTimeout = CommandTimeoutSeconds;
                        bulkCopy.ColumnMappings.Add("job_id", "job_id");
                        bulkCopy.ColumnMappings.Add("line_number", "line_number");
                        bulkCopy.ColumnMappings.Add("upn", "upn");
                        bulkCopy.ColumnMappings.Add("org_value", "org_value");

                        await bulkCopy.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
                    }
                }

                tx.Commit();
                return jobId;
            }
        }

        public async Task<UserOrgImportJob> GetJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, "SELECT " + JobColumns + " FROM dbo.user_org_import_jobs WHERE id = @id"))
            {
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = jobId;

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
                }
            }
        }

        public async Task<UserOrgImportJob> GetActiveJobForTypeAsync(
            int orgTypeId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = @"
SELECT TOP (1) " + JobColumns + @"
FROM dbo.user_org_import_jobs
WHERE org_type_id = @orgTypeId AND status IN (1, 2)
ORDER BY id DESC;";

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                cmd.Parameters.Add("@orgTypeId", SqlDbType.Int).Value = orgTypeId;

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
                }
            }
        }

        /// <summary>
        /// Claims a pending job.
        /// </summary>
        /// <remarks>
        /// The <c>status = 1</c> predicate inside the UPDATE is what makes this safe: the check and the
        /// transition happen in one atomic statement, so two web instances racing for the same job
        /// cannot both win and import the file twice.
        /// </remarks>
        public async Task<bool> TryClaimJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = @"
UPDATE dbo.user_org_import_jobs
SET status = 2, started_utc = SYSUTCDATETIME(), heartbeat_utc = SYSUTCDATETIME()
WHERE id = @id AND status = 1;";

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = jobId;
                var affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return affected == 1;
            }
        }

        public async Task HeartbeatAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, "UPDATE dbo.user_org_import_jobs SET heartbeat_utc = SYSUTCDATETIME() WHERE id = @id AND status = 2"))
            {
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = jobId;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Applies a claimed job's staged rows.
        /// </summary>
        /// <remarks>
        /// Runs inside one transaction. That matters most for Replace: clearing the users absent from
        /// the file and applying the ones present must be all-or-nothing, or a failure half way leaves
        /// the org type emptied but not repopulated. On a large tenant this does hold locks on
        /// <c>user_org_assignments</c> for the duration, which is the correct trade - a partially
        /// applied Replace would be far worse than a slow one.
        /// </remarks>
        public async Task<UserOrgImportJob> ApplyAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var tx = connection.BeginTransaction())
            {
                using (var cmd = Command(connection, ApplySql, tx))
                {
                    cmd.Parameters.Add("@jobId", SqlDbType.Int).Value = jobId;
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                UserOrgImportJob job;
                using (var cmd = Command(connection, "SELECT " + JobColumns + " FROM dbo.user_org_import_jobs WHERE id = @id", tx))
                {
                    cmd.Parameters.Add("@id", SqlDbType.Int).Value = jobId;
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        job = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
                    }
                }

                tx.Commit();
                return job;
            }
        }

        public async Task CompleteJobAsync(
            int jobId,
            UserOrgImportStatus status,
            string errorMessage,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            // The staged rows are deleted on completion: they are a work queue, not a record. The job
            // row keeps the counts, so the audit trail survives without carrying a copy of every UPN in
            // the customer's file around forever.
            const string sql = @"
UPDATE dbo.user_org_import_jobs
SET status = @status,
    finished_utc = SYSUTCDATETIME(),
    error_message = @error
WHERE id = @id;

DELETE FROM dbo.user_org_import_staging WHERE job_id = @id;";

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = jobId;
                cmd.Parameters.Add("@status", SqlDbType.TinyInt).Value = (byte)status;
                cmd.Parameters.Add("@error", SqlDbType.NVarChar, 2000).Value =
                    DbValue(errorMessage == null ? null : Truncate(errorMessage, 2000));

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Resolves the staged UPNs to users and applies them, honouring the job's Replace/Merge mode.
        /// </summary>
        internal const string ApplySql = @"
SET NOCOUNT ON;

DECLARE @orgTypeId INT, @mode TINYINT;
SELECT @orgTypeId = org_type_id, @mode = mode FROM dbo.user_org_import_jobs WHERE id = @jobId;

IF @orgTypeId IS NULL
BEGIN
    RAISERROR('The import job no longer exists.', 16, 1);
    RETURN;
END

DECLARE @rowsTotal INT = (SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = @jobId);
DECLARE @distinctUpns INT = (SELECT COUNT(DISTINCT upn) FROM dbo.user_org_import_staging WHERE job_id = @jobId);

-- Resolve each UPN to a user, keeping the LAST line for a UPN the file lists more than once: a
-- repeated person is treated as a correction, which is what an admin editing a spreadsheet expects.
-- Partitioning uses the database collation, so two spellings differing only in case are one person.
CREATE TABLE #user_org_matched (user_id INT NOT NULL PRIMARY KEY, org_value NVARCHAR(200) NULL);

INSERT INTO #user_org_matched (user_id, org_value)
SELECT u.id, latest.org_value
FROM (
    SELECT s.upn,
           s.org_value,
           ROW_NUMBER() OVER (PARTITION BY s.upn ORDER BY s.line_number DESC) AS rn
    FROM dbo.user_org_import_staging s
    WHERE s.job_id = @jobId
) latest
JOIN dbo.users u ON u.user_name = latest.upn
WHERE latest.rn = 1;

DECLARE @matched INT = (SELECT COUNT(*) FROM #user_org_matched);
DECLARE @unknown INT = CASE WHEN @distinctUpns > @matched THEN @distinctUpns - @matched ELSE 0 END;

-- Register any value we have not seen for this org type before.
INSERT INTO dbo.user_org_values (org_type_id, name)
SELECT DISTINCT @orgTypeId, m.org_value
FROM #user_org_matched m
WHERE m.org_value IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.user_org_values v
                  WHERE v.org_type_id = @orgTypeId AND v.name = m.org_value);

DECLARE @cleared INT = 0, @applied INT = 0;

IF @mode = 1
BEGIN
    -- Replace: the file is the complete membership of this org type, so anyone it does not give a
    -- value to loses theirs - including users it lists with a blank value.
    DELETE a
    FROM dbo.user_org_assignments a
    WHERE a.org_type_id = @orgTypeId
      AND NOT EXISTS (SELECT 1 FROM #user_org_matched m
                      WHERE m.user_id = a.user_id AND m.org_value IS NOT NULL);
    SET @cleared = @@ROWCOUNT;
END
ELSE
BEGIN
    -- Merge: only the users the file actually mentions change, and only a blank value clears.
    DELETE a
    FROM dbo.user_org_assignments a
    JOIN #user_org_matched m ON m.user_id = a.user_id
    WHERE a.org_type_id = @orgTypeId AND m.org_value IS NULL;
    SET @cleared = @@ROWCOUNT;
END

UPDATE a
SET a.org_value_id = v.id,
    a.last_updated_utc = SYSUTCDATETIME()
FROM dbo.user_org_assignments a
JOIN #user_org_matched m ON m.user_id = a.user_id
JOIN dbo.user_org_values v ON v.org_type_id = @orgTypeId AND v.name = m.org_value
WHERE a.org_type_id = @orgTypeId
  AND m.org_value IS NOT NULL
  AND a.org_value_id <> v.id;
SET @applied = @@ROWCOUNT;

INSERT INTO dbo.user_org_assignments (user_id, org_type_id, org_value_id)
SELECT m.user_id, @orgTypeId, v.id
FROM #user_org_matched m
JOIN dbo.user_org_values v ON v.org_type_id = @orgTypeId AND v.name = m.org_value
WHERE m.org_value IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.user_org_assignments a
                  WHERE a.user_id = m.user_id AND a.org_type_id = @orgTypeId);
SET @applied = @applied + @@ROWCOUNT;

-- rows_invalid is recorded when the file is parsed, not here, so it is deliberately left alone.
UPDATE dbo.user_org_import_jobs
SET rows_total = @rowsTotal,
    rows_applied = @applied,
    rows_cleared = @cleared,
    rows_unknown_upn = @unknown,
    heartbeat_utc = SYSUTCDATETIME()
WHERE id = @jobId;

DROP TABLE #user_org_matched;";

        internal static DataTable BuildStagingTable(int jobId, IReadOnlyList<UserOrgStagedRow> rows)
        {
            var table = new DataTable();
            table.Columns.Add("job_id", typeof(int));
            table.Columns.Add("line_number", typeof(int));
            table.Columns.Add("upn", typeof(string));
            table.Columns.Add("org_value", typeof(string));

            foreach (var row in rows)
            {
                var value = UserOrgRules.NormaliseOrgValue(row.OrgValue);
                table.Rows.Add(
                    jobId,
                    row.LineNumber,
                    row.Upn,
                    value == null ? (object)DBNull.Value : value);
            }

            return table;
        }

        internal static UserOrgImportJob ReadJob(SqlDataReader reader)
        {
            return new UserOrgImportJob
            {
                Id = reader.GetInt32(0),
                OrgTypeId = reader.GetInt32(1),
                Mode = (UserOrgImportMode)reader.GetByte(2),
                Status = (UserOrgImportStatus)reader.GetByte(3),
                FileName = ReadString(reader, 4),
                StartedBy = ReadString(reader, 5),
                QueuedUtc = reader.GetDateTime(6),
                StartedUtc = ReadNullableDate(reader, 7),
                FinishedUtc = ReadNullableDate(reader, 8),
                HeartbeatUtc = ReadNullableDate(reader, 9),
                RowsTotal = reader.GetInt32(10),
                RowsApplied = reader.GetInt32(11),
                RowsCleared = reader.GetInt32(12),
                RowsUnknownUpn = reader.GetInt32(13),
                RowsInvalid = reader.GetInt32(14),
                ErrorMessage = ReadString(reader, 15),
            };
        }

        private static string Truncate(string value, int maxLength)
        {
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}
