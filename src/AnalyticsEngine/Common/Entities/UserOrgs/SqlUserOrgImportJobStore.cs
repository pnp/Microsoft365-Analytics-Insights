using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
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
    internal sealed class SqlUserOrgImportJobStore : SqlUserOrgStoreBase, IUserOrgImportJobStore, IUserOrgChangeOutbox
    {
        /// <summary>
        /// The job columns, in the order <see cref="ReadJob"/> reads them.
        /// </summary>
        /// <remarks>
        /// Shared rather than written out again wherever a job is selected. A second hand-written
        /// list drifts the moment a column is added, and it fails as a null-read deep inside
        /// <see cref="ReadJob"/> rather than anywhere near the query that is actually wrong.
        /// </remarks>
        internal const string JobColumns =
            "id, org_type_id, mode, status, file_name, started_by, queued_utc, started_utc, finished_utc, "
            + "heartbeat_utc, rows_total, rows_applied, rows_cleared, rows_unknown_upn, rows_invalid, confirmed_clear_count, "
            + "expected_generation, attempts, error_code, error_message, change_log_status";

        /// <summary>The same columns, qualified with a table alias for a query that needs one.</summary>
        internal static string JobColumnsFor(string alias)
        {
            var columns = JobColumns.Split(',');
            for (var i = 0; i < columns.Length; i++)
            {
                columns[i] = alias + "." + columns[i].Trim();
            }
            return string.Join(", ", columns);
        }

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

DECLARE @lockResult INT, @lockName NVARCHAR(255) = N'user_org_type_' + CAST(@orgTypeId AS NVARCHAR(20));

-- The same application lock the apply takes, so queueing and applying for one org type cannot
-- interleave. Taken before anything is read, so the checks below see a settled state. A short
-- timeout on purpose: this runs inside an administrator's HTTP request, and 'an import is already
-- in progress' is the honest answer when a live worker holds the lock - far better than hanging the
-- upload for the ten minutes a large merge may take.
EXEC @lockResult = sp_getapplock
    @Resource = @lockName, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;

IF @lockResult < 0
BEGIN
    RAISERROR('USERORG_ACTIVE_JOB', 16, 1);
    RETURN;
END

-- The org type must still be CSV-sourced and enabled, and must not have been reset since. It is
-- checked in the caller too, but that happens before a file of up to half a million rows is parsed,
-- so an admin reconfiguring the type in the meantime would otherwise have a CSV import land on it
-- afterwards. Re-checked here because this is inside the transaction that admits the job.
IF NOT EXISTS (SELECT 1 FROM dbo.user_org_types
               WHERE id = @orgTypeId AND source_kind = 2 AND is_enabled = 1
                 AND (@expectedGeneration IS NULL OR source_generation = @expectedGeneration))
BEGIN
    RAISERROR('USERORG_NOT_CSV_SOURCED', 16, 1);
    RETURN;
END

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

-- Retire whatever the check above decided was no longer alive, in the SAME transaction that admits
-- the replacement. Leaving it Pending would let a late dispatch claim and apply a file the admin has
-- already superseded; TryClaimJobAsync only accepts status = 1, so moving it off Pending here is what
-- actually fences that. Leaving it Running would also leave the portal reporting two live imports.
DECLARE @superseded TABLE (id INT NOT NULL);

UPDATE dbo.user_org_import_jobs
SET status = 4,
    finished_utc = SYSUTCDATETIME(),
    error_code = N'superseded',
    error_message = @supersededMessage
OUTPUT INSERTED.id INTO @superseded (id)
WHERE org_type_id = @orgTypeId
  AND status IN (1, 2);

DELETE s
FROM dbo.user_org_import_staging s
WHERE EXISTS (SELECT 1 FROM @superseded x WHERE x.id = s.job_id);

INSERT INTO dbo.user_org_import_jobs
    (org_type_id, mode, status, file_name, started_by, queued_utc, rows_total, rows_invalid, confirmed_clear_count, expected_generation)
OUTPUT INSERTED.id
VALUES (@orgTypeId, @mode, @status, @fileName, @startedBy, SYSUTCDATETIME(), @rowsTotal, @rowsInvalid, @confirmedClearCount, @expectedGeneration);";

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
                    cmd.Parameters.Add("@confirmedClearCount", SqlDbType.Int).Value =
                        job.ConfirmedClearCount.HasValue ? (object)job.ConfirmedClearCount.Value : DBNull.Value;
                    cmd.Parameters.Add("@expectedGeneration", SqlDbType.Int).Value =
                        job.ExpectedGeneration.HasValue ? (object)job.ExpectedGeneration.Value : DBNull.Value;
                    cmd.Parameters.Add("@pendingStaleSecs", SqlDbType.Int).Value =
                        (int)UserOrgImportRunner.StalePendingThreshold.TotalSeconds;
                    cmd.Parameters.Add("@runningStaleSecs", SqlDbType.Int).Value =
                        (int)UserOrgImportRunner.StaleHeartbeatThreshold.TotalSeconds;
                    cmd.Parameters.Add("@supersededMessage", SqlDbType.NVarChar, 2000).Value =
                        UserOrgImportRunner.SupersededMessage;

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
                    catch (SqlException ex) when (ex.Message.IndexOf("USERORG_NOT_CSV_SOURCED", StringComparison.Ordinal) >= 0)
                    {
                        throw new UserOrgValidationException(
                            "That organisation type no longer takes its values from a CSV file - it was changed "
                            + "while this file was being read. Reload the page and check the configuration.", ex);
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
                    await BulkStageAsync(connection, tx, jobId, rows, cancellationToken).ConfigureAwait(false);
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
ORDER BY queued_utc DESC, id DESC;";

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
        /// Claims a pending job, or takes over a running one whose worker has gone quiet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The status predicate inside the UPDATE is what makes this safe: the check and the transition
        /// happen in one atomic statement, so two web instances racing for the same job cannot both win.
        /// </para>
        /// <para>
        /// Taking over a stale job is how an interrupted import resumes. It cannot apply the file twice,
        /// even if the first worker was merely slow rather than gone: both take the org type's
        /// application lock before applying, the apply refuses a job that is no longer Running, and the
        /// apply records its success in the same transaction as its changes - so whichever gets there
        /// second finds the job already Succeeded and changes nothing. Nor can the slow one end the
        /// takeover by failing: its failure is fenced on the attempt number this returned it, which the
        /// takeover moved.
        /// </para>
        /// </remarks>
        /// <returns>The claim's attempt number, or <c>null</c> when the job could not be claimed.</returns>
        public async Task<int?> TryClaimJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            // The attempt number comes back from the same statement that moves it, so the claim knows its
            // own identity without a second read that a takeover could slip in front of. OUTPUT ... INTO,
            // because a bare OUTPUT clause is refused on a table that has a trigger.
            const string sql = @"
DECLARE @claimed TABLE (attempts tinyint NOT NULL);

UPDATE dbo.user_org_import_jobs
SET status = 2, started_utc = SYSUTCDATETIME(), heartbeat_utc = SYSUTCDATETIME(),
    attempts = CASE WHEN attempts < 255 THEN attempts + 1 ELSE attempts END
OUTPUT INSERTED.attempts INTO @claimed
WHERE id = @id
  AND (status = 1
       OR (status = 2 AND ISNULL(heartbeat_utc, started_utc) < DATEADD(SECOND, -@staleSecs, SYSUTCDATETIME())));

SELECT attempts FROM @claimed;";

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = jobId;
                cmd.Parameters.Add("@staleSecs", SqlDbType.Int).Value =
                    (int)UserOrgImportRunner.StaleHeartbeatThreshold.TotalSeconds;
                var attempt = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return attempt == null || attempt == DBNull.Value ? (int?)null : Convert.ToInt32(attempt);
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
        /// <para>
        /// Runs inside one transaction. That matters most for Replace: clearing the users absent from
        /// the file and applying the ones present must be all-or-nothing, or a failure half way leaves
        /// the org type emptied but not repopulated.
        /// </para>
        /// <para>
        /// The cost is measured, not assumed. A 200,000-row Replace on LocalDB applies in 6-8 s and
        /// escalates to a table lock on <c>user_org_assignments</c> for that time. With read committed
        /// snapshot - Azure SQL's default - readers are unaffected; writers to the table (the Entra
        /// user import, another org type's CSV import) wait for it and then carry on. Disabling lock
        /// escalation with row-lock hints was measured too: it does let other types' writers through,
        /// but takes 600,000 key locks (1.5 million at the 500,000-row cap), runs about 30% slower, and
        /// is a schema change. A few seconds' wait for a batch writer is the better trade.
        /// </para>
        /// </remarks>
        public async Task<UserOrgImportJob> ApplyAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var tx = connection.BeginTransaction())
            {
                using (var cmd = Command(connection, ApplySql, tx))
                {
                    cmd.Parameters.Add("@jobId", SqlDbType.Int).Value = jobId;

                    try
                    {
                        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (SqlException ex) when (ex.Message.IndexOf("USERORG_JOB_NOT_RUNNABLE", StringComparison.Ordinal) >= 0)
                    {
                        throw new UserOrgJobSupersededException(
                            "This import was overtaken by a later one for the same organisation type, so it was not applied.",
                            ex);
                    }
                    catch (SqlException ex) when (ex.Message.IndexOf("USERORG_NOT_CSV_SOURCED", StringComparison.Ordinal) >= 0)
                    {
                        throw new UserOrgValidationException(
                            "This organisation type was reconfigured after the file was uploaded, so the file was "
                            + "not imported. Check the type's settings and upload it again if it is still the file "
                            + "you want.", UserOrgImportErrorCodes.TypeChanged, null, ex);
                    }
                    catch (SqlException ex) when (ex.Message.IndexOf("USERORG_UNCONFIRMED_CLEAR", StringComparison.Ordinal) >= 0)
                    {
                        throw new UserOrgValidationException(
                            "By the time this import ran it would have cleared more users' values than were "
                            + "confirmed, so nothing was changed. Preview the file again - another import may have "
                            + "run since - and confirm the new numbers, or use Merge.",
                            UserOrgImportErrorCodes.ClearExceedsConfirmed, null, ex);
                    }
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
            CancellationToken cancellationToken = default(CancellationToken),
            string errorCode = null,
            int? claimedAttempt = null)
        {
            // The staged rows are deleted on completion: they are a work queue, not a record. The job
            // row keeps the counts, so the audit trail survives without carrying a copy of every UPN in
            // the customer's file around forever.
            //
            // Only a job that is still live (Pending or Running) can be completed. A job that has
            // already reached a terminal status must not be rewritten - specifically, a worker that was
            // superseded because it went quiet (see CreateJobWithRowsAsync) must not be able to report
            // its own outcome over the top of that and leave the portal claiming two finished imports
            // raced to the same result. For the same reason a worker that was taken over reports nothing
            // over the live claim (claimedAttempt). The staged rows go once nobody can still run the job -
            // after this update, or after an apply that recorded its own success - and never while a live
            // claim other than the one reporting is working from them.
            const string sql = @"
UPDATE dbo.user_org_import_jobs
SET status = @status,
    finished_utc = SYSUTCDATETIME(),
    error_code = @code,
    error_message = @error
WHERE id = @id AND status IN (1, 2)
  AND (@attempt IS NULL OR attempts = @attempt);

DELETE FROM dbo.user_org_import_staging
WHERE job_id = @id
  AND NOT EXISTS (SELECT 1 FROM dbo.user_org_import_jobs WHERE id = @id AND status IN (1, 2));";

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = jobId;
                cmd.Parameters.Add("@status", SqlDbType.TinyInt).Value = (byte)status;
                cmd.Parameters.Add("@code", SqlDbType.NVarChar, 64).Value = DbValue(errorCode);
                cmd.Parameters.Add("@error", SqlDbType.NVarChar, 2000).Value =
                    DbValue(errorMessage == null ? null : Truncate(errorMessage, 2000));
                cmd.Parameters.Add("@attempt", SqlDbType.Int).Value =
                    claimedAttempt.HasValue ? (object)claimedAttempt.Value : DBNull.Value;

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Resolves the staged UPNs to users and applies them, honouring the job's Replace/Merge mode.
        /// </summary>
        internal const string ApplySql = @"
SET NOCOUNT ON;

DECLARE @orgTypeId INT, @mode TINYINT, @confirmedClearCount INT, @expectedGeneration INT, @lockResult INT, @lockName NVARCHAR(255);

-- Read the org type first, unlocked, purely to name the lock. The authoritative read happens below,
-- once the lock is held.
SELECT @orgTypeId = org_type_id FROM dbo.user_org_import_jobs WHERE id = @jobId;

IF @orgTypeId IS NULL
BEGIN
    RAISERROR('USERORG_JOB_NOT_RUNNABLE', 16, 1);
    RETURN;
END

SET @lockName = N'user_org_type_' + CAST(@orgTypeId AS NVARCHAR(20));

-- An application lock, not a lock on the job row. Everything that queues, applies, reconfigures or
-- deletes work for one org type takes this same lock, so those operations cannot interleave - but
-- the job row itself stays writable, which matters because the heartbeat runs on its own connection
-- and must keep reporting progress for the whole merge. Holding a transaction-duration UPDLOCK on
-- the job row instead blocked every heartbeat until the apply committed, so any import taking
-- longer than StaleHeartbeatThreshold was reported to the admin as interrupted while it was in fact
-- running perfectly. It also inverted the lock order against deleting an org type, which took
-- staging before jobs.
--
-- If the lock cannot be taken, a live worker holds it. That is the right answer to refuse on: a
-- worker that has genuinely died releases it when its connection is torn down and its transaction
-- rolls back.
EXEC @lockResult = sp_getapplock
    @Resource = @lockName, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 600000;

IF @lockResult < 0
BEGIN
    RAISERROR('USERORG_JOB_NOT_RUNNABLE', 16, 1);
    RETURN;
END

-- Still Running? That is the fence against a worker that went quiet, was superseded and had its
-- staged rows deleted underneath it - applying anyway means a Replace matches nobody, so it deletes
-- EVERY assignment for the org type.
--
-- Reset first. A SELECT that matches no rows leaves its assignment variables at whatever they
-- already held, so without this @orgTypeId would keep the value the unlocked read above put in it
-- and the fence would pass in exactly the cases it exists to catch.
SET @orgTypeId = NULL;

SELECT @orgTypeId = org_type_id, @mode = mode, @confirmedClearCount = ISNULL(confirmed_clear_count, 0), @expectedGeneration = expected_generation
FROM dbo.user_org_import_jobs
WHERE id = @jobId AND status = 2;

IF @orgTypeId IS NULL
BEGIN
    RAISERROR('USERORG_JOB_NOT_RUNNABLE', 16, 1);
    RETURN;
END

-- And is the type still the one this file was uploaded for? An admin can reconfigure it between the
-- upload and the worker running. Source kind alone is too weak a question: a type switched to Entra
-- and back is CSV-sourced again, with its values deliberately discarded in between, so the
-- generation is what actually says the mapping is unchanged. Reported separately from the fence
-- above because nothing has retired this job - the caller has to record the outcome itself.
--
-- UPDLOCK, HOLDLOCK because this apply writes the type row at the end (its refresh time), and every
-- writer in this feature takes the TYPE row before that type's values and assignments. Writing it
-- last without holding it from here would invert that order against the Entra merge, which holds
-- type rows while it writes - and the merge is the one writer the application lock does not cover.
-- An update lock still lets readers through; only the final write blocks them, briefly.
IF NOT EXISTS (SELECT 1 FROM dbo.user_org_types WITH (UPDLOCK, HOLDLOCK)
               WHERE id = @orgTypeId AND source_kind = 2 AND is_enabled = 1
                 AND (@expectedGeneration IS NULL OR source_generation = @expectedGeneration))
BEGIN
    RAISERROR('USERORG_NOT_CSV_SOURCED', 16, 1);
    RETURN;
END

DECLARE @rowsTotal INT = (SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = @jobId);
" + MatchStagedRowsSql + @"
-- Rows, not people: the same count the preview showed (SummariseDraftAsync), so the finished import's
-- figures reconcile with it - a file listing one unknown person on 900 lines says 900.
DECLARE @unknown INT = (SELECT COUNT(*) FROM dbo.user_org_import_staging s
                        WHERE s.job_id = @jobId
                          AND NOT EXISTS (SELECT 1 FROM dbo.users u WHERE u.user_name = s.upn));
" + ClearCountSql + @"
-- The confirmation is re-tested HERE, inside the transaction that does the deleting, using the
-- DELETEs' own predicates so the two cannot disagree. The web request checked it as well, but
-- another administrator's import can queue, run and finish between that check and this one - and a
-- confirmation given for clearing 12 people must not cover clearing 20,000. Merge is tested too: it
-- clears everyone the file lists with an empty value. Placed before the first write so the refusal
-- leaves the database untouched even without the caller's rollback.
IF @clearCount > @confirmedClearCount
BEGIN
    DROP TABLE #user_org_matched;
    RAISERROR('USERORG_UNCONFIRMED_CLEAR', 16, 1);
    RETURN;
END

-- Register any value we have not seen for this org type before.
INSERT INTO dbo.user_org_values (org_type_id, name)
SELECT DISTINCT @orgTypeId, m.org_value
FROM #user_org_matched m
WHERE m.org_value IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.user_org_values v
                  WHERE v.org_type_id = @orgTypeId AND v.name = m.org_value);

DECLARE @cleared INT = 0, @applied INT = 0;

-- Every change, user by user, for the change log: the value before and after, captured by the
-- statements that make the changes, so the log cannot describe anything but what was done. The four
-- statements below touch disjoint sets of users, so one row per user is all there can be.
CREATE TABLE #user_org_changes (user_id INT NOT NULL PRIMARY KEY, old_value_id INT NULL, new_value_id INT NULL);

IF @mode = 1
BEGIN
    -- Replace: the file is the complete membership of this org type, so anyone it does not give a
    -- value to loses theirs - including users it lists with a blank value.
    DELETE a
    OUTPUT deleted.user_id, deleted.org_value_id, CAST(NULL AS INT)
        INTO #user_org_changes (user_id, old_value_id, new_value_id)
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
    OUTPUT deleted.user_id, deleted.org_value_id, CAST(NULL AS INT)
        INTO #user_org_changes (user_id, old_value_id, new_value_id)
    FROM dbo.user_org_assignments a
    JOIN #user_org_matched m ON m.user_id = a.user_id
    WHERE a.org_type_id = @orgTypeId AND m.org_value IS NULL;
    SET @cleared = @@ROWCOUNT;
END

UPDATE a
SET a.org_value_id = v.id,
    a.last_updated_utc = SYSUTCDATETIME()
OUTPUT inserted.user_id, deleted.org_value_id, inserted.org_value_id
    INTO #user_org_changes (user_id, old_value_id, new_value_id)
FROM dbo.user_org_assignments a
JOIN #user_org_matched m ON m.user_id = a.user_id
JOIN dbo.user_org_values v ON v.org_type_id = @orgTypeId AND v.name = m.org_value
WHERE a.org_type_id = @orgTypeId
  AND m.org_value IS NOT NULL
  AND a.org_value_id <> v.id;
SET @applied = @@ROWCOUNT;

INSERT INTO dbo.user_org_assignments (user_id, org_type_id, org_value_id)
OUTPUT inserted.user_id, CAST(NULL AS INT), inserted.org_value_id
    INTO #user_org_changes (user_id, old_value_id, new_value_id)
SELECT m.user_id, @orgTypeId, v.id
FROM #user_org_matched m
JOIN dbo.user_org_values v ON v.org_type_id = @orgTypeId AND v.name = m.org_value
WHERE m.org_value IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.user_org_assignments a
                  WHERE a.user_id = m.user_id AND a.org_type_id = @orgTypeId);
SET @applied = @applied + @@ROWCOUNT;

-- The change list goes to the outbox in this same transaction, so an import that applied always has
-- one - even if the web app stops before it is written to the change log. The UPN and the value names
-- are copied now, because the log must still say who and what after either is gone.
INSERT INTO dbo.user_org_import_changes (job_id, user_id, upn, old_value, new_value)
SELECT @jobId, c.user_id, u.user_name, ov.name, nv.name
FROM #user_org_changes c
JOIN dbo.users u ON u.id = c.user_id
LEFT JOIN dbo.user_org_values ov ON ov.id = c.old_value_id
LEFT JOIN dbo.user_org_values nv ON nv.id = c.new_value_id;

-- rows_invalid is recorded when the file is parsed, not here, so it is deliberately left alone.
--
-- The job is marked Succeeded HERE, in the same transaction as the changes, rather than by the worker
-- afterwards. So a job that is anything but Succeeded changed nothing, however its worker died: the
-- portal can say so without hedging, and an interrupted job can simply be run again.
UPDATE dbo.user_org_import_jobs
SET rows_total = @rowsTotal,
    rows_applied = @applied,
    rows_cleared = @cleared,
    rows_unknown_upn = @unknown,
    status = 3,
    finished_utc = SYSUTCDATETIME(),
    error_code = NULL,
    error_message = NULL,
    change_log_status = 1,
    heartbeat_utc = SYSUTCDATETIME()
WHERE id = @jobId AND status = 2;

-- Still Running at the end, as at the start. The resume sweep retires a job whose worker has gone quiet
-- without taking the type's lock, and deletes its staged rows - possibly before this read them. A job it
-- retired meanwhile must not be reported as applied: refused, so the caller rolls back every change
-- above, and the job keeps the outcome the sweep gave it.
IF @@ROWCOUNT = 0
BEGIN
    RAISERROR('USERORG_JOB_NOT_RUNNABLE', 16, 1);
    RETURN;
END

-- In the same transaction as the writes, so the refresh time and the values cannot disagree - even
-- when the file changed nobody, which is still a confirmation that the values are current.
UPDATE dbo.user_org_types SET last_refreshed_utc = SYSUTCDATETIME() WHERE id = @orgTypeId;

DROP TABLE #user_org_changes;
DROP TABLE #user_org_matched;";

        /// <summary>
        /// Resolves a job's staged rows to users, into <c>#user_org_matched</c>, for <c>@jobId</c>.
        /// </summary>
        /// <remarks>
        /// Shared verbatim by the preview, the commit and the apply, so what an administrator is shown,
        /// what is checked when they import, and what is applied cannot disagree.
        /// </remarks>
        internal const string MatchStagedRowsSql = @"
-- Resolve each UPN to a user, keeping the LAST line for a UPN the file lists more than once: a
-- repeated person is treated as a correction, which is what an admin editing a spreadsheet expects.
-- Partitioning uses the database collation, so two spellings differing only in case are one person.
-- The value is kept in the database's collation too, not tempdb's: the apply compares it with
-- user_org_values.name, which on a database collated unlike the server fails with Msg 468.
CREATE TABLE #user_org_matched (user_id INT NOT NULL PRIMARY KEY, org_value NVARCHAR(848) COLLATE DATABASE_DEFAULT NULL);

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
";

        /// <summary>
        /// How many users importing <c>#user_org_matched</c> would clear, with the apply's own DELETE
        /// predicates. Declares <c>@replaceClears</c>, <c>@mergeClears</c> and <c>@clearCount</c> (for
        /// <c>@mode</c>).
        /// </summary>
        internal const string ClearCountSql = @"
DECLARE @replaceClears INT = (SELECT COUNT(*)
                              FROM dbo.user_org_assignments a
                              WHERE a.org_type_id = @orgTypeId
                                AND NOT EXISTS (SELECT 1 FROM #user_org_matched m
                                                WHERE m.user_id = a.user_id AND m.org_value IS NOT NULL));
DECLARE @mergeClears INT = (SELECT COUNT(*)
                            FROM dbo.user_org_assignments a
                            JOIN #user_org_matched m ON m.user_id = a.user_id
                            WHERE a.org_type_id = @orgTypeId AND m.org_value IS NULL);
DECLARE @clearCount INT = CASE WHEN @mode = 1 THEN @replaceClears ELSE @mergeClears END;
";

        public async Task<int> CreateDraftAsync(
            UserOrgImportJob draft,
            IReadOnlyList<UserOrgStagedRow> rows,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (draft == null)
            {
                throw new ArgumentNullException(nameof(draft));
            }

            // Mode is not known until the import is committed; Merge is only a placeholder that the
            // status keeps from ever being read.
            const string sql = @"
SET NOCOUNT ON;

-- When the preview was asked for. Only drafts asked for BEFORE it are its to replace: a large file
-- still uploading when its admin cleared it and chose a smaller one must not, finishing last, throw
-- away the draft of the file on screen.
DECLARE @requested DATETIME2 = COALESCE(@requestedUtc, SYSUTCDATETIME());

-- Throw away what nobody will import: drafts past their lifetime, and this admin's earlier previews of
-- files for the same type. Previewing a file five times must not leave five copies of every UPN in it.
DELETE FROM dbo.user_org_import_jobs
WHERE status = 6
  AND (queued_utc < DATEADD(SECOND, -@draftLifetimeSecs, SYSUTCDATETIME())
       OR (org_type_id = @orgTypeId AND started_by = @startedBy AND queued_utc < @requested));

INSERT INTO dbo.user_org_import_jobs
    (org_type_id, mode, status, file_name, started_by, queued_utc, rows_total, rows_invalid, expected_generation)
OUTPUT INSERTED.id
VALUES (@orgTypeId, 2, 6, @fileName, @startedBy, @requested, @rowsTotal, @rowsInvalid, @expectedGeneration);";

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var tx = connection.BeginTransaction())
            {
                int draftId;
                using (var cmd = Command(connection, sql, tx))
                {
                    cmd.Parameters.Add("@orgTypeId", SqlDbType.Int).Value = draft.OrgTypeId;
                    cmd.Parameters.Add("@fileName", SqlDbType.NVarChar, 260).Value = DbValue(draft.FileName);
                    cmd.Parameters.Add("@startedBy", SqlDbType.NVarChar, 256).Value = draft.StartedBy ?? string.Empty;
                    cmd.Parameters.Add("@rowsTotal", SqlDbType.Int).Value = rows == null ? 0 : rows.Count;
                    cmd.Parameters.Add("@rowsInvalid", SqlDbType.Int).Value = draft.RowsInvalid;
                    cmd.Parameters.Add("@expectedGeneration", SqlDbType.Int).Value =
                        draft.ExpectedGeneration.HasValue ? (object)draft.ExpectedGeneration.Value : DBNull.Value;
                    cmd.Parameters.Add("@draftLifetimeSecs", SqlDbType.Int).Value =
                        (int)UserOrgImportJobLimits.DraftLifetime.TotalSeconds;
                    cmd.Parameters.Add("@requestedUtc", SqlDbType.DateTime2).Value =
                        draft.QueuedUtc == default(DateTime) ? (object)DBNull.Value : DateTime.SpecifyKind(draft.QueuedUtc, DateTimeKind.Utc);

                    draftId = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                }

                if (rows != null && rows.Count > 0)
                {
                    await BulkStageAsync(connection, tx, draftId, rows, cancellationToken).ConfigureAwait(false);
                }

                tx.Commit();
                return draftId;
            }
        }

        public async Task<UserOrgDraftSummary> SummariseDraftAsync(
            int draftId,
            int sampleRows,
            int unknownRowLimit,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = @"
SET NOCOUNT ON;

DECLARE @orgTypeId INT = (SELECT org_type_id FROM dbo.user_org_import_jobs WHERE id = @jobId AND status = 6);
IF @orgTypeId IS NULL
BEGIN
    SELECT TOP (0) 0 AS org_type_id;
    RETURN;
END

DECLARE @mode TINYINT = 2;
" + MatchStagedRowsSql + ClearCountSql + @"
SELECT @orgTypeId AS org_type_id,
       (SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = @jobId) AS rows_total,
       (SELECT COUNT(*) FROM dbo.user_org_import_staging s
        WHERE s.job_id = @jobId AND NOT EXISTS (SELECT 1 FROM dbo.users u WHERE u.user_name = s.upn)) AS unknown_rows,
       (SELECT COUNT(*) FROM #user_org_matched) AS matched_users,
       (SELECT COUNT(*) FROM #user_org_matched WHERE org_value IS NOT NULL) AS matched_with_value,
       (SELECT COUNT(*) FROM dbo.user_org_assignments WHERE org_type_id = @orgTypeId) AS currently_assigned,
       @replaceClears AS replace_clears,
       @mergeClears AS merge_clears;

SELECT TOP (@sampleRows) s.line_number, s.upn, s.org_value,
       CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.users u WHERE u.user_name = s.upn) THEN 1 ELSE 0 END AS bit) AS user_exists
FROM dbo.user_org_import_staging s
WHERE s.job_id = @jobId
ORDER BY s.line_number;

SELECT TOP (@unknownRowLimit) s.line_number, s.upn, s.org_value
FROM dbo.user_org_import_staging s
WHERE s.job_id = @jobId AND NOT EXISTS (SELECT 1 FROM dbo.users u WHERE u.user_name = s.upn)
ORDER BY s.line_number;

DROP TABLE #user_org_matched;";

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                cmd.Parameters.Add("@jobId", SqlDbType.Int).Value = draftId;
                cmd.Parameters.Add("@sampleRows", SqlDbType.Int).Value = Math.Max(0, sampleRows);
                cmd.Parameters.Add("@unknownRowLimit", SqlDbType.Int).Value = Math.Max(0, unknownRowLimit);

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    var summary = new UserOrgDraftSummary
                    {
                        DraftId = draftId,
                        OrgTypeId = reader.GetInt32(0),
                        RowsTotal = reader.GetInt32(1),
                        UnknownRows = reader.GetInt32(2),
                        MatchedUsers = reader.GetInt32(3),
                        MatchedUsersWithValue = reader.GetInt32(4),
                        CurrentlyAssigned = reader.GetInt32(5),
                        ReplaceWouldClear = reader.GetInt32(6),
                        MergeWouldClear = reader.GetInt32(7),
                    };

                    var sample = new List<UserOrgDraftRow>();
                    await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        sample.Add(new UserOrgDraftRow
                        {
                            LineNumber = reader.GetInt32(0),
                            Upn = ReadString(reader, 1),
                            OrgValue = ReadString(reader, 2),
                            UserExists = reader.GetBoolean(3),
                        });
                    }

                    var unknown = new List<UserOrgStagedRow>();
                    await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        unknown.Add(new UserOrgStagedRow(reader.GetInt32(0), ReadString(reader, 1), ReadString(reader, 2)));
                    }

                    summary.SampleRows = sample;
                    summary.UnknownRowList = unknown;
                    return summary;
                }
            }
        }

        public async Task CommitDraftAsync(
            int draftId,
            int orgTypeId,
            UserOrgImportMode mode,
            int confirmedClearCount,
            string startedBy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = @"
SET NOCOUNT ON;

DECLARE @lockResult INT, @lockName NVARCHAR(255) = N'user_org_type_' + CAST(@orgTypeId AS NVARCHAR(20));

-- The same application lock the apply takes, so admitting and applying for one org type cannot
-- interleave. A short timeout: this runs inside an administrator's HTTP request, and 'an import is
-- already in progress' is the honest answer when a live worker holds the lock.
EXEC @lockResult = sp_getapplock
    @Resource = @lockName, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;

IF @lockResult < 0
BEGIN
    RAISERROR('USERORG_ACTIVE_JOB', 16, 1);
    RETURN;
END

DECLARE @expectedGeneration INT, @isDraft BIT = 0;

-- Only the administrator who previewed a file can import it. Drafts are numbered like every other
-- job, so without this anyone signed in could guess another admin's draft - a preview they looked at
-- and decided against, perhaps - and import it with a clear count they never saw, and a file they
-- never had.
SELECT @isDraft = 1, @expectedGeneration = expected_generation
FROM dbo.user_org_import_jobs WITH (UPDLOCK, HOLDLOCK)
WHERE id = @jobId AND org_type_id = @orgTypeId AND status = 6
  AND started_by = @startedBy
  AND queued_utc > DATEADD(SECOND, -@draftLifetimeSecs, SYSUTCDATETIME());

IF @isDraft = 0
BEGIN
    RAISERROR('USERORG_DRAFT_MISSING', 16, 1);
    RETURN;
END

-- Still the type the file was previewed for: CSV-sourced, enabled, and not reset in between.
IF NOT EXISTS (SELECT 1 FROM dbo.user_org_types
               WHERE id = @orgTypeId AND source_kind = 2 AND is_enabled = 1
                 AND (@expectedGeneration IS NULL OR source_generation = @expectedGeneration))
BEGIN
    RAISERROR('USERORG_NOT_CSV_SOURCED', 16, 1);
    RETURN;
END

IF EXISTS (SELECT 1 FROM dbo.user_org_import_jobs WITH (UPDLOCK, HOLDLOCK)
           WHERE org_type_id = @orgTypeId
             AND (
                  (status = 1 AND queued_utc > DATEADD(SECOND, -@pendingStaleSecs, SYSUTCDATETIME()))
               OR (status = 2 AND ISNULL(heartbeat_utc, started_utc) > DATEADD(SECOND, -@runningStaleSecs, SYSUTCDATETIME()))
             ))
BEGIN
    RAISERROR('USERORG_ACTIVE_JOB', 16, 1);
    RETURN;
END
" + MatchStagedRowsSql + ClearCountSql + @"
-- A file that names nobody changes nobody - except that a Replace of it would clear everyone. Either
-- way it is not the file the admin meant, so it is refused rather than queued.
IF NOT EXISTS (SELECT 1 FROM #user_org_matched)
BEGIN
    DROP TABLE #user_org_matched;
    RAISERROR('USERORG_NO_MATCHING_USERS', 16, 1);
    RETURN;
END

-- The confirmation covers the number the admin was shown, not 'any number'. Checked against the
-- assignments as they are now, so another import finishing since the preview is caught here, in the
-- request, rather than only later in the worker.
IF @clearCount > @confirmedClearCount
BEGIN
    DROP TABLE #user_org_matched;
    RAISERROR('USERORG_CLEAR_EXCEEDS:%d', 16, 1, @clearCount);
    RETURN;
END

DROP TABLE #user_org_matched;

-- Retire whatever the active check decided was no longer alive, in the SAME transaction that admits
-- this job, so a late dispatch cannot claim a file the admin has already superseded.
DECLARE @superseded TABLE (id INT NOT NULL);

UPDATE dbo.user_org_import_jobs
SET status = 4,
    finished_utc = SYSUTCDATETIME(),
    error_code = N'superseded',
    error_message = @supersededMessage
OUTPUT INSERTED.id INTO @superseded (id)
WHERE org_type_id = @orgTypeId
  AND status IN (1, 2);

DELETE s
FROM dbo.user_org_import_staging s
WHERE EXISTS (SELECT 1 FROM @superseded x WHERE x.id = s.job_id);

UPDATE dbo.user_org_import_jobs
SET status = 1,
    mode = @mode,
    confirmed_clear_count = @confirmedClearCount,
    started_by = @startedBy,
    queued_utc = SYSUTCDATETIME()
WHERE id = @jobId;";

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var tx = connection.BeginTransaction())
            {
                using (var cmd = Command(connection, sql, tx))
                {
                    cmd.Parameters.Add("@jobId", SqlDbType.Int).Value = draftId;
                    cmd.Parameters.Add("@orgTypeId", SqlDbType.Int).Value = orgTypeId;
                    cmd.Parameters.Add("@mode", SqlDbType.TinyInt).Value = (byte)mode;
                    cmd.Parameters.Add("@confirmedClearCount", SqlDbType.Int).Value = Math.Max(0, confirmedClearCount);
                    cmd.Parameters.Add("@startedBy", SqlDbType.NVarChar, 256).Value = startedBy ?? string.Empty;
                    cmd.Parameters.Add("@draftLifetimeSecs", SqlDbType.Int).Value =
                        (int)UserOrgImportJobLimits.DraftLifetime.TotalSeconds;
                    cmd.Parameters.Add("@pendingStaleSecs", SqlDbType.Int).Value =
                        (int)UserOrgImportRunner.StalePendingThreshold.TotalSeconds;
                    cmd.Parameters.Add("@runningStaleSecs", SqlDbType.Int).Value =
                        (int)UserOrgImportRunner.StaleHeartbeatThreshold.TotalSeconds;
                    cmd.Parameters.Add("@supersededMessage", SqlDbType.NVarChar, 2000).Value =
                        UserOrgImportRunner.SupersededMessage;

                    try
                    {
                        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (SqlException ex) when (ex.Message.IndexOf("USERORG_ACTIVE_JOB", StringComparison.Ordinal) >= 0)
                    {
                        throw new UserOrgValidationException(
                            "An import for this organisation type is already in progress. Wait for it to finish "
                            + "before starting another.", UserOrgImportRefusalCodes.ImportInProgress, null, ex);
                    }
                    catch (SqlException ex) when (ex.Message.IndexOf("USERORG_DRAFT_MISSING", StringComparison.Ordinal) >= 0)
                    {
                        throw new UserOrgValidationException(
                            "This preview has expired or was already imported. Choose the file again.",
                            UserOrgImportRefusalCodes.DraftNotFound, null, ex);
                    }
                    catch (SqlException ex) when (ex.Message.IndexOf("USERORG_NOT_CSV_SOURCED", StringComparison.Ordinal) >= 0)
                    {
                        throw new UserOrgValidationException(
                            "That organisation type was changed after the file was previewed. Reload the page, check "
                            + "its settings and choose the file again.", UserOrgImportRefusalCodes.TypeChanged, null, ex);
                    }
                    catch (SqlException ex) when (ex.Message.IndexOf("USERORG_NO_MATCHING_USERS", StringComparison.Ordinal) >= 0)
                    {
                        throw new UserOrgValidationException(
                            "None of the rows in this file match a user in this database, so nothing would be imported. "
                            + "Check that the user column holds the same user principal names this product imports.",
                            UserOrgImportRefusalCodes.NoMatchingUsers, null, ex);
                    }
                    catch (SqlException ex) when (ex.Message.IndexOf("USERORG_CLEAR_EXCEEDS", StringComparison.Ordinal) >= 0)
                    {
                        var count = ParseTrailingNumber(ex.Message);
                        throw new UserOrgValidationException(
                            $"This import would now clear the value of {count:N0} user(s), not the {confirmedClearCount:N0} "
                            + "that were confirmed - the data changed since the preview. Choose the file again to see the "
                            + "new numbers.",
                            UserOrgImportRefusalCodes.ClearExceedsConfirmed,
                            new Dictionary<string, object> { { "count", count }, { "confirmed", confirmedClearCount } },
                            ex);
                    }
                }

                tx.Commit();
            }
        }

        public async Task<IReadOnlyList<UserOrgImportJob>> ListJobsAsync(
            int orgTypeId,
            int take,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = @"
SELECT TOP (@take) " + JobColumns + @"
FROM dbo.user_org_import_jobs
WHERE org_type_id = @orgTypeId AND status <> 6
ORDER BY queued_utc DESC, id DESC;";

            var jobs = new List<UserOrgImportJob>();
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                cmd.Parameters.Add("@orgTypeId", SqlDbType.Int).Value = orgTypeId;
                cmd.Parameters.Add("@take", SqlDbType.Int).Value = Math.Max(1, Math.Min(take, 100));

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        jobs.Add(ReadJob(reader));
                    }
                }
            }

            return jobs;
        }

        public async Task<IReadOnlyList<int>> ResumeStaleJobsAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = @"
SET NOCOUNT ON;

DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @abandoned TABLE (id INT NOT NULL);

-- Stop what cannot sensibly be resumed: claimed too often, or queued too long ago. Nothing was changed
-- - an import records its success in the same transaction as its changes - so this is safe to say.
UPDATE dbo.user_org_import_jobs
SET status = 4,
    finished_utc = @now,
    error_code = N'interruptedRepeatedly',
    error_message = @abandonedMessage
OUTPUT INSERTED.id INTO @abandoned (id)
WHERE ((status = 1 AND queued_utc < DATEADD(SECOND, -@lostDispatchSecs, @now))
    OR (status = 2 AND ISNULL(heartbeat_utc, started_utc) < DATEADD(SECOND, -@staleSecs, @now)))
  AND (attempts >= @maxAttempts OR queued_utc < DATEADD(SECOND, -@resumeWindowSecs, @now));

DELETE s
FROM dbo.user_org_import_staging s
WHERE EXISTS (SELECT 1 FROM @abandoned a WHERE a.id = s.job_id);

-- Everything that should be with a worker and is not: a dispatch lost with the web process that
-- queued it, and a running job whose worker went quiet. The caller dispatches these again, and the
-- claim takes them over atomically - see TryClaimJobAsync for why that cannot apply a file twice.
SELECT id
FROM dbo.user_org_import_jobs
WHERE (status = 1 AND queued_utc < DATEADD(SECOND, -@lostDispatchSecs, @now))
   OR (status = 2 AND ISNULL(heartbeat_utc, started_utc) < DATEADD(SECOND, -@staleSecs, @now));

-- Housekeeping: staged rows a finished job's completion failed to remove, and expired drafts.
DELETE s
FROM dbo.user_org_import_staging s
WHERE EXISTS (SELECT 1 FROM dbo.user_org_import_jobs j WHERE j.id = s.job_id AND j.status IN (3, 4, 5));

DELETE FROM dbo.user_org_import_jobs
WHERE status = 6 AND queued_utc < DATEADD(SECOND, -@draftLifetimeSecs, @now);";

            var ids = new List<int>();
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                cmd.Parameters.Add("@lostDispatchSecs", SqlDbType.Int).Value =
                    (int)UserOrgImportJobLimits.LostDispatchGrace.TotalSeconds;
                cmd.Parameters.Add("@staleSecs", SqlDbType.Int).Value =
                    (int)UserOrgImportRunner.StaleHeartbeatThreshold.TotalSeconds;
                cmd.Parameters.Add("@maxAttempts", SqlDbType.Int).Value = UserOrgImportJobLimits.MaxAttempts;
                cmd.Parameters.Add("@resumeWindowSecs", SqlDbType.Int).Value =
                    (int)UserOrgImportJobLimits.ResumeWindow.TotalSeconds;
                cmd.Parameters.Add("@draftLifetimeSecs", SqlDbType.Int).Value =
                    (int)UserOrgImportJobLimits.DraftLifetime.TotalSeconds;
                cmd.Parameters.Add("@abandonedMessage", SqlDbType.NVarChar, 2000).Value = UserOrgImportRunner.AbandonedMessage;

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        ids.Add(reader.GetInt32(0));
                    }
                }
            }

            return ids;
        }

        #region Change outbox

        public async Task<IUserOrgChangeLease> TryLeaseAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            // A transaction-owned application lock on a connection of its own, held for as long as the
            // lease lives. Transaction-owned rather than session-owned because a pooled connection is not
            // reset when it goes back to the pool - a session lock could outlive its lease - whereas
            // rolling the transaction back always releases it. A worker that dies releases it with its
            // connection. The transaction touches no data until the lease completes, so it blocks nothing
            // else.
            var name = "user_org_changelog_" + jobId.ToString(CultureInfo.InvariantCulture);
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            SqlTransaction tx = null;
            try
            {
                tx = connection.BeginTransaction();
                using (var cmd = Command(connection, @"
DECLARE @result INT;
EXEC @result = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;
SELECT @result;", tx))
                {
                    cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = name;
                    var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                    if (result < 0)
                    {
                        tx.Rollback();
                        tx.Dispose();
                        connection.Dispose();
                        return null;
                    }
                }

                return new TransactionLease(connection, tx, jobId, name);
            }
            catch
            {
                tx?.Dispose();
                connection.Dispose();
                throw;
            }
        }

        public async Task<IReadOnlyList<UserOrgChangeRecord>> ReadAsync(
            int jobId,
            int afterUserId,
            int take,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = @"
SELECT TOP (@take) user_id, upn, old_value, new_value
FROM dbo.user_org_import_changes
WHERE job_id = @jobId AND user_id > @after
ORDER BY user_id;";

            var changes = new List<UserOrgChangeRecord>();
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                cmd.Parameters.Add("@jobId", SqlDbType.Int).Value = jobId;
                cmd.Parameters.Add("@after", SqlDbType.Int).Value = afterUserId;
                cmd.Parameters.Add("@take", SqlDbType.Int).Value = Math.Max(1, take);

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        changes.Add(new UserOrgChangeRecord
                        {
                            UserId = reader.GetInt32(0),
                            Upn = ReadString(reader, 1),
                            OldValue = ReadString(reader, 2),
                            NewValue = ReadString(reader, 3),
                        });
                    }
                }
            }

            return changes;
        }

        public async Task<IReadOnlyList<int>> ListPendingAsync(int take, CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = @"
SELECT TOP (@take) id
FROM dbo.user_org_import_jobs
WHERE status = 3 AND change_log_status = 1
ORDER BY id;";

            var ids = new List<int>();
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                cmd.Parameters.Add("@take", SqlDbType.Int).Value = Math.Max(1, take);
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        ids.Add(reader.GetInt32(0));
                    }
                }
            }

            return ids;
        }

        /// <summary>Holds an application lock for as long as it lives. See <see cref="IUserOrgChangeLease"/>.</summary>
        private sealed class TransactionLease : IUserOrgChangeLease
        {
            // The job never says the log was written while its outbox still holds the list - nothing would
            // ever pick those rows up again - because both are done in one transaction: the lease's own.
            private const string CompleteSql = @"
UPDATE dbo.user_org_import_jobs SET change_log_status = @status WHERE id = @jobId AND change_log_status = 1;
DELETE FROM dbo.user_org_import_changes WHERE job_id = @jobId;";

            private readonly int _jobId;
            private readonly string _name;
            private SqlConnection _connection;
            private SqlTransaction _tx;

            public TransactionLease(SqlConnection connection, SqlTransaction tx, int jobId, string name)
            {
                _connection = connection;
                _tx = tx;
                _jobId = jobId;
                _name = name;
            }

            public async Task<bool> IsHeldAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                var connection = _connection;
                var tx = _tx;
                if (connection == null || tx == null || connection.State != ConnectionState.Open)
                {
                    return false;
                }

                try
                {
                    using (var cmd = Command(connection, "SELECT APPLOCK_MODE('public', @name, 'Transaction');", tx))
                    {
                        cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = _name;
                        var mode = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                        return string.Equals(mode, "Exclusive", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // A connection that has gone - which is exactly what this asks - fails the command.
                    return false;
                }
            }

            public async Task CompleteAsync(UserOrgChangeLogStatus writtenTo, CancellationToken cancellationToken = default(CancellationToken))
            {
                if (writtenTo == UserOrgChangeLogStatus.Pending)
                {
                    throw new ArgumentOutOfRangeException(nameof(writtenTo), "A change log is completed by saying where it was written.");
                }

                var connection = _connection;
                var tx = _tx;
                if (connection == null || tx == null)
                {
                    throw new ObjectDisposedException(nameof(TransactionLease));
                }

                using (var cmd = Command(connection, CompleteSql, tx))
                {
                    cmd.Parameters.Add("@jobId", SqlDbType.Int).Value = _jobId;
                    cmd.Parameters.Add("@status", SqlDbType.TinyInt).Value = (byte)writtenTo;
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Committing gives the lock up with the transaction.
                tx.Commit();
                Interlocked.Exchange(ref _tx, null)?.Dispose();
            }

            public void Dispose()
            {
                var tx = Interlocked.Exchange(ref _tx, null);
                var connection = Interlocked.Exchange(ref _connection, null);
                try
                {
                    tx?.Rollback();
                }
                catch (Exception)
                {
                    // The connection is going away regardless, and the lock with it.
                }
                finally
                {
                    tx?.Dispose();
                    connection?.Dispose();
                }
            }
        }

        #endregion

        private static async Task BulkStageAsync(
            SqlConnection connection,
            SqlTransaction tx,
            int jobId,
            IReadOnlyList<UserOrgStagedRow> rows,
            CancellationToken cancellationToken)
        {
            using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, tx))
            using (var reader = new StagedRowReader(jobId, rows))
            {
                bulkCopy.DestinationTableName = "dbo.user_org_import_staging";
                bulkCopy.BatchSize = 10000;
                bulkCopy.BulkCopyTimeout = CommandTimeoutSeconds;
                bulkCopy.ColumnMappings.Add(0, "job_id");
                bulkCopy.ColumnMappings.Add(1, "line_number");
                bulkCopy.ColumnMappings.Add(2, "upn");
                bulkCopy.ColumnMappings.Add(3, "org_value");

                await bulkCopy.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
            }
        }

        private static int ParseTrailingNumber(string message)
        {
            var colon = message.IndexOf(':');
            var digits = colon < 0 ? string.Empty : new string(message.Substring(colon + 1).TakeWhile(char.IsDigit).ToArray());
            int value;
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value) ? value : 0;
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
                ConfirmedClearCount = reader.IsDBNull(15) ? (int?)null : reader.GetInt32(15),
                ExpectedGeneration = reader.IsDBNull(16) ? (int?)null : reader.GetInt32(16),
                Attempts = reader.GetByte(17),
                ErrorCode = ReadString(reader, 18),
                ErrorMessage = ReadString(reader, 19),
                ChangeLogStatus = reader.IsDBNull(20) ? (UserOrgChangeLogStatus?)null : (UserOrgChangeLogStatus)reader.GetByte(20),
            };
        }

        private static string Truncate(string value, int maxLength)
        {
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        /// <summary>
        /// Streams staged rows into <see cref="SqlBulkCopy"/>.
        /// </summary>
        /// <remarks>
        /// Instead of a <see cref="DataTable"/> copy of the whole file. At the 500,000-row cap that table
        /// held about 80 MB on top of the parsed rows it was built from, for the few seconds the upload
        /// took - on the same web process that serves every report.
        /// </remarks>
        private sealed class StagedRowReader : IDataReader
        {
            private static readonly string[] Names = { "job_id", "line_number", "upn", "org_value" };

            private readonly int _jobId;
            private readonly IReadOnlyList<UserOrgStagedRow> _rows;
            private int _index = -1;

            public StagedRowReader(int jobId, IReadOnlyList<UserOrgStagedRow> rows)
            {
                _jobId = jobId;
                _rows = rows ?? new UserOrgStagedRow[0];
            }

            public int FieldCount => Names.Length;

            public int Depth => 0;

            public bool IsClosed => false;

            public int RecordsAffected => -1;

            public object this[int i] => GetValue(i);

            public object this[string name] => GetValue(GetOrdinal(name));

            public bool Read()
            {
                return ++_index < _rows.Count;
            }

            public object GetValue(int i)
            {
                var row = _rows[_index];
                switch (i)
                {
                    case 0: return _jobId;
                    case 1: return row.LineNumber;
                    case 2: return row.Upn;
                    case 3:
                        // Normalised again here rather than trusted: this is the last point before the
                        // value reaches an nvarchar(848) column.
                        var value = UserOrgRules.NormaliseOrgValue(row.OrgValue);
                        return value == null ? (object)DBNull.Value : value;
                    default: throw new IndexOutOfRangeException();
                }
            }

            public int GetValues(object[] values)
            {
                var count = Math.Min(values.Length, FieldCount);
                for (var i = 0; i < count; i++)
                {
                    values[i] = GetValue(i);
                }

                return count;
            }

            public bool IsDBNull(int i) => GetValue(i) is DBNull;

            public string GetName(int i) => Names[i];

            public int GetOrdinal(string name) => Array.IndexOf(Names, name);

            public Type GetFieldType(int i) => i < 2 ? typeof(int) : typeof(string);

            public string GetDataTypeName(int i) => i < 2 ? "int" : "nvarchar";

            public int GetInt32(int i) => (int)GetValue(i);

            public string GetString(int i) => (string)GetValue(i);

            public bool NextResult() => false;

            public void Close()
            {
            }

            public void Dispose()
            {
            }

            public DataTable GetSchemaTable() => throw new NotSupportedException();

            public bool GetBoolean(int i) => throw new NotSupportedException();

            public byte GetByte(int i) => throw new NotSupportedException();

            public long GetBytes(int i, long fieldOffset, byte[] buffer, int bufferoffset, int length) => throw new NotSupportedException();

            public char GetChar(int i) => throw new NotSupportedException();

            public long GetChars(int i, long fieldoffset, char[] buffer, int bufferoffset, int length) => throw new NotSupportedException();

            public IDataReader GetData(int i) => throw new NotSupportedException();

            public DateTime GetDateTime(int i) => throw new NotSupportedException();

            public decimal GetDecimal(int i) => throw new NotSupportedException();

            public double GetDouble(int i) => throw new NotSupportedException();

            public float GetFloat(int i) => throw new NotSupportedException();

            public Guid GetGuid(int i) => throw new NotSupportedException();

            public short GetInt16(int i) => throw new NotSupportedException();

            public long GetInt64(int i) => throw new NotSupportedException();
        }
    }
}
