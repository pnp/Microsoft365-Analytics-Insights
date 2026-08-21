/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202608210700001_WidenCopilotAccessedResourceDedupIndex
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database schema BY HAND instead of running the installer
   (which applies EF migrations automatically). This is the exact SQL the migration performs, followed by
   the __MigrationHistory stamp so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app
   Health page recognise it as applied.

   WHAT IT DOES
     Rebuilds the Copilot accessed-resource de-duplication index with TWO EXTRA KEY COLUMNS:
       * IX_copilot_event_accessed_resources_dedup
             ON copilot_event_accessed_resources
                (copilot_chat_id, resource_id_id, resource_name_id, resource_site_url_id,
                 resource_type_id, sensitivity_label_id, action_id, list_item_unique_id_id)

     WHY (issue #287). The Copilot merge used to de-duplicate accessed resources on the five resource
     columns only, picking action_id and list_item_unique_id_id with two INDEPENDENT MIN()s. That dropped
     actions (the same document Read AND Written in one interaction collapsed to one row) and could
     fabricate pairings (an action from one source row paired with a list-item id from another). The merge
     now treats the whole tuple as the row identity, so this index must carry the same columns as KEY
     columns for the de-dup existence check to keep seeking instead of scanning the junction table.

     MEASURED (synthetic scale: 2,000,000 junction rows incl. a 500,000-row chat; medians of 6 runs):
       commit batch of 500 rows      before 1,623 reads / 2 ms  ->  after 1,622 reads / 2 ms   (Index Seek)
       commit batch of 20,000 rows   before 63,889 reads / 85 ms -> after 63,883 reads / 94 ms (Index Seek)
     Leaving the index un-widened while the merge compares the full tuple costs 3,123 reads on the small
     batch and loses the seek entirely on the large one (530 ms vs 94 ms).
   SAFETY
     * Idempotent / re-runnable: every step is guarded and an already-applied state is a no-op.
     * Attempts ONLINE (non-blocking) on Enterprise / Azure SQL DB / Managed Instance, and falls back to an
       OFFLINE build everywhere else (Standard, Express, LocalDB).
     * No wrapping transaction (matches suppressTransaction: true); an interrupted run converges on re-run.

   UPGRADE TIME
     * This is a DROP + REBUILD of one non-clustered index on a table that is LARGE on Copilot-heavy
       tenants. Synthetic measurement: 2,000,000 rows built in 5.9 s occupying 83 MB (~3 s and ~42 MB per
       million rows). Estimate ~3 s at 1M rows, ~30 s at 10M, ~5 min at 100M.
     * The de-dup index is ABSENT while the rebuild runs, so a concurrent Copilot merge falls back to the
       slow pre-index behaviour. Run in a MAINTENANCE WINDOW WITH THE IMPORTER STOPPED.
   PREREQUISITE
     The database must already be on migration 202608200600001_AddCopilotInteractionHistory. The __MigrationHistory stamp
     copies that row's model snapshot, which is identical to this one because this migration changes no EF
     entity model.

   Run against the Analytics database.
   ===================================================================================================== */
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'WidenCopilotAccessedResourceDedupIndex';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @tbl sysname = N'copilot_event_accessed_resources';
DECLARE @ix  sysname = N'IX_copilot_event_accessed_resources_dedup';
DECLARE @rowCount bigint, @edition int;
DECLARE @sql nvarchar(max);
DECLARE @canOnline bit, @onlineDone bit, @needsRebuild bit;
DECLARE @objId int;

-- The full de-dup tuple in the order the NOT EXISTS / INTERSECT compares it: chat first (the correlation
-- key), then the five resolved resource lookups, then the two columns that were wrongly treated as
-- payload. All eight are ints, so the whole key is 32 bytes.
DECLARE @keyCols nvarchar(400) = N'[copilot_chat_id], [resource_id_id], [resource_name_id], [resource_site_url_id], [resource_type_id], [sensitivity_label_id], [action_id], [list_item_unique_id_id]';

SET @edition = CAST(SERVERPROPERTY('EngineEdition') AS int);
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index build '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).' ELSE N'not supported on this edition - using offline (locks the table; run large upgrades in a maintenance window with the importer stopped).' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SET @objId = OBJECT_ID(N'dbo.' + @tbl, N'U');

IF @objId IS NULL
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- All eight key columns must be present. action_id / list_item_unique_id_id arrive with migration
-- CopilotDroppedAuditFields; guard so an older/partial schema is skipped rather than erroring.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'copilot_chat_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'resource_id_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'resource_name_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'resource_site_url_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'resource_type_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'sensitivity_label_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'action_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'list_item_unique_id_id')
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' is missing an expected column, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- Already the right shape? Eight KEY columns, including the two being added. Structured as IF/ELSE that
-- falls through rather than an early RETURN, so a by-hand run against an up-to-date database still
-- reaches the __MigrationHistory stamp in the manual script.
SET @needsRebuild = 1;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @objId AND name = @ix)
BEGIN
    IF (SELECT COUNT(*)
        FROM sys.index_columns ic
        INNER JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE i.object_id = @objId AND i.name = @ix AND ic.is_included_column = 0) = 8
       AND EXISTS (SELECT 1
                   FROM sys.index_columns ic
                   INNER JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                   INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE i.object_id = @objId AND i.name = @ix AND ic.is_included_column = 0 AND c.name = N'action_id')
       AND EXISTS (SELECT 1
                   FROM sys.index_columns ic
                   INNER JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                   INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE i.object_id = @objId AND i.name = @ix AND ic.is_included_column = 0 AND c.name = N'list_item_unique_id_id')
        SET @needsRebuild = 0;
END

IF @needsRebuild = 0
BEGIN
    SET @msg = @migration + N': [' + @ix + N'] already carries all 8 key columns, nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p WHERE p.object_id = @objId AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N' row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N' (this can be a large rebuild).';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @objId AND name = @ix)
    BEGIN
        SET @msg = @migration + N': dropping the 6-key [' + @ix + N'] before recreating it with 8 keys...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'DROP INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'];';
        EXEC sp_executesql @sql;
    END

    SET @stepStart = SYSUTCDATETIME();
    SET @onlineDone = 0;

    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            SET @msg = @migration + N': creating composite index [' + @ix + N'] on ' + @tbl + N'(chat + 5 resource FKs + action_id + list_item_unique_id_id) WITH (ONLINE = ON)...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] (' + @keyCols + N') WITH (ONLINE = ON);';
            EXEC sp_executesql @sql;
            SET @onlineDone = 1;
        END TRY
        BEGIN CATCH
            SET @msg = @migration + N': ONLINE index build of [' + @ix + N'] unavailable (' + ERROR_MESSAGE() + N'); retrying offline.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END

    IF @onlineDone = 0
    BEGIN
        SET @msg = @migration + N': creating composite index [' + @ix + N'] on ' + @tbl + N'(chat + 5 resource FKs + action_id + list_item_unique_id_id) (offline)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] (' + @keyCols + N');';
        EXEC sp_executesql @sql;
    END

    SET @msg = @migration + N': [' + @ix + N'] created in ' + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20))
        + N'ms (' + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SET @msg = @migration + N': finished in ' + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- =====================================================================================================
-- Record the migration so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health
-- page treat it as applied. No model change here, so the EF snapshot is byte-identical to the previous
-- migration's - copy that row's Model / ContextKey / ProductVersion. Guarded so re-running is safe.
-- =====================================================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608210700001_WidenCopilotAccessedResourceDedupIndex')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608200600001_AddCopilotInteractionHistory')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202608210700001_WidenCopilotAccessedResourceDedupIndex', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202608200600001_AddCopilotInteractionHistory';
        RAISERROR('WidenCopilotAccessedResourceDedupIndex: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('WidenCopilotAccessedResourceDedupIndex: the schema change was applied, but prerequisite migration 202608200600001_AddCopilotInteractionHistory is missing from __MigrationHistory, so it was NOT stamped. Apply the earlier migrations first, or run the installer to reconcile.', 16, 1);
END
ELSE
    RAISERROR('WidenCopilotAccessedResourceDedupIndex: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
