-- Self-healing repair of the denormalised dbo.copilot_chats.user_id / time_stamp columns.
--
-- WHY THIS EXISTS
-- Migration DenormaliseCopilotChatUserAndTime backfills every existing row and is then stamped in
-- __MigrationHistory, so it never runs again. But the columns are NULLable, and an upgrade does not
-- necessarily stop the web job before the schema changes: a schema-only upgrade leaves App Service running,
-- and a normal upgrade restarts the OLD package before the new one is deployed. Any Copilot interaction
-- inserted by the OLD importer in that window - or inserted concurrently into a clustered-key range the
-- backfill had already walked past - keeps NULL in both columns for ever.
--
-- Every Copilot report now filters "c.time_stamp >= @from", and NULL fails that comparison, so those
-- interactions would be SILENTLY INVISIBLE: the page would report confident numbers that are quietly too
-- low. That is exactly the defect class issue #360 was raised for, so the importer repairs them.
--
-- WHY IT IS NOT PART OF THE MERGE, AND NOT PART OF THE SAVE PATH
-- It deliberately does NOT live in common_upsert_copilot_agents.sql, and is deliberately NOT called from
-- CopilotAuditEventManager.CommitAllChanges or from ActivityImporter.LoadReportsAndSave. Each of those was
-- tried and each leaves a hole:
--   * the merge only executes when the Copilot staging queue is non-empty, so a tenant with no further
--     Copilot activity would never repair;
--   * the save path is skipped entirely when a cycle downloads nothing;
--   * LoadReportsAndSave is skipped when the activity import throws (e.g. a persistent Activity API
--     authentication failure, which Program.cs catches and continues past) or when DownloadActivityData
--     returns early because no organisation URLs are configured.
-- It is therefore invoked from the WEB JOB TOP LEVEL, in Program.cs, immediately after the
-- DownloadActivityData try/catch and outside it - so a successful, empty, aborted or skipped activity cycle
-- all still heal the database. The repair only needs SQL, so it must not be gated on the import succeeding.
-- Do not "consolidate" this back into the merge or the save path.
--
-- COST WHEN THERE IS NOTHING TO DO (the normal case)
-- "time_stamp IS NULL" is a seek to the head of IX_copilot_chats_time_stamp_user_id, because NULLs sort
-- first. Measured at 4 logical reads on a 12,000,000-row table.
--
-- WHY THE PREDICATE IS "time_stamp IS NULL" AND NOT "... OR user_id IS NULL"
-- audit_events.time_stamp is NOT NULL, so a repaired row always has a time_stamp. audit_events.user_id IS
-- nullable, so a correctly-repaired row may legitimately keep a NULL user_id. Including the OR would
-- re-write those rows on every cycle for ever and would also make the predicate non-SARGable.
--
-- The INNER JOIN leaves genuine orphans (a chat whose audit event no longer exists) alone. They can never
-- be repaired, and they were invisible to the previous INNER JOIN reports too.

SET NOCOUNT ON;

-- Self-guard: safe to call unconditionally, including on a database that has not been upgraded yet.
-- The whole body is deferred through sp_executesql because T-SQL resolves column names for an entire
-- batch at compile time - referencing copilot_chats.time_stamp directly would fail to compile (and abort
-- the batch) on a pre-migration database, even inside an IF that would never execute.
IF COL_LENGTH('dbo.copilot_chats', 'time_stamp') IS NULL
BEGIN
    SELECT CAST(0 AS bigint) AS RepairedRows;
    RETURN;
END

DECLARE @body nvarchar(max) = N'
DECLARE @batch int = 50000;
DECLARE @maxBatches int = 20;     -- bound the work per import cycle; the rest drains on the next one
DECLARE @batchNo int = 0;
DECLARE @rows int = 1;
DECLARE @total bigint = 0;

WHILE @rows > 0 AND @batchNo < @maxBatches
BEGIN
    UPDATE TOP (@batch) c
    SET c.user_id    = ae.user_id,
        c.time_stamp = ae.time_stamp
    FROM dbo.copilot_chats AS c
    INNER JOIN dbo.audit_events AS ae ON ae.id = c.event_id
    WHERE c.time_stamp IS NULL;

    SET @rows = @@ROWCOUNT;
    SET @total += @rows;
    SET @batchNo += 1;
END

SELECT @total AS RepairedRows;';

EXEC sp_executesql @body;
