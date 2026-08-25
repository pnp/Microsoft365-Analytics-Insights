/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202608250900001_IndexCopilotAccessedResourceFkColumns
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database schema BY HAND instead of running the installer
   (which applies EF migrations automatically). It performs the same schema change as the migration and
   then stamps __MigrationHistory so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web
   app Health page recognise it as applied.

   RUN ORDER
     This is the LAST migration in the release. Its prerequisite is
     202608210700003_IndexCopilotInteractionsDedupWindow, which must already be stamped - the script
     refuses to stamp itself otherwise. Run every manual script in migration-id order.

   WHAT IT DOES
     Builds the two foreign-key indexes on dbo.copilot_event_accessed_resources for the action_id and
     list_item_unique_id_id columns added by 202608190622001_CopilotDroppedAuditFields:
       * IX_copilot_event_accessed_resources_action_id
       * IX_copilot_event_accessed_resources_list_item_unique_id_id

     These were previously the last step of CopilotDroppedAuditFields itself. They were split out because
     an index build cannot run inside the migration transaction on a large table, and EF commits
     everything before a transaction-suppressed statement - so a failed build left that migration's tables
     and columns committed but unstamped, and the retry then failed on objects that already existed. As
     its own migration the build is independently resumable: an interrupted run simply builds whatever is
     still missing on the next attempt.

   RUNTIME
     Measured at synthetic scale (offline build, buffer pool dropped before each build, medians of 3 runs)
     on a 3,000,000-row junction table: about 2.5 s / 40.7 MB for the action_id index and 3.1 s / 40.7 MB
     for list_item_unique_id_id - roughly 5.6 s and 81 MB for the pair. Extrapolating as O(n log n) that is
     about 2 s / 27 MB at 1M rows and a few minutes at 100M.

     ONLINE builds are attempted on Enterprise (EngineEdition 3), Azure SQL DB (5) and Azure SQL MI (8).
     On Standard / Express / Web / LocalDB each build briefly locks the table, so run a large upgrade in a
     maintenance window with the importer stopped.

   SAFETY
     Idempotent and guarded throughout - safe to re-run, and safe on a database where the indexes already
     exist or where the columns are missing (it skips rather than failing). Run it with sqlcmd -b so
     execution stops at the first error.
   ===================================================================================================== */

SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexCopilotAccessedResourceFkColumns';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @tbl sysname = N'copilot_event_accessed_resources';
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8) THEN 1 ELSE 0 END;
DECLARE @onlineDone bit;
DECLARE @rowCount bigint;
DECLARE @sql nvarchar(max);
DECLARE @ix sysname;
DECLARE @col sysname;
DECLARE @i int = 1;

DECLARE @targets table (seq int NOT NULL PRIMARY KEY, ix sysname NOT NULL, col sysname NOT NULL);
INSERT INTO @targets (seq, ix, col) VALUES
    (1, N'IX_copilot_event_accessed_resources_action_id', N'action_id'),
    (2, N'IX_copilot_event_accessed_resources_list_item_unique_id_id', N'list_item_unique_id_id');

SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index builds '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).'
           ELSE N'are not supported on this edition - each build briefly locks the table, so run large upgrades in a maintenance window with the importer stopped.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.' + @tbl, N'U') IS NULL
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' does not exist; skipping the junction indexes.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p
                     WHERE p.object_id = OBJECT_ID(N'dbo.' + @tbl) AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N' row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N'.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    WHILE @i <= 2
    BEGIN
        SELECT @ix = ix, @col = col FROM @targets WHERE seq = @i;

        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @col)
        BEGIN
            SET @msg = @migration + N': ' + @tbl + N'.' + @col + N' does not exist; skipping [' + @ix + N'].';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix)
        BEGIN
            SET @msg = @migration + N': [' + @ix + N'] already exists; nothing to do.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE
        BEGIN
            SET @stepStart = SYSUTCDATETIME();
            SET @onlineDone = 0;

            IF @canOnline = 1
            BEGIN
                BEGIN TRY
                    -- Through sp_executesql on purpose: the "ONLINE index operations can only be performed
                    -- in Enterprise edition" error aborts the batch and is NOT catchable for a plain
                    -- statement, but IS catchable when executed this way.
                    SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']) WITH (ONLINE = ON);';
                    EXEC sp_executesql @sql;
                    SET @onlineDone = 1;
                    SET @msg = @migration + N': built [' + @ix + N'] ONLINE in '
                        + CAST(DATEDIFF(SECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N's.';
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                END TRY
                BEGIN CATCH
                    SET @msg = @migration + N': ONLINE build of [' + @ix + N'] failed ('
                        + ERROR_MESSAGE() + N') - falling back to an offline build.';
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                END CATCH
            END

            IF @onlineDone = 0
            BEGIN
                SET @stepStart = SYSUTCDATETIME();
                SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']);';
                EXEC sp_executesql @sql;
                SET @msg = @migration + N': built [' + @ix + N'] offline in '
                    + CAST(DATEDIFF(SECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N's.';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END
        END

        SET @i += 1;
    END
END

SET @msg = @migration + N': schema step complete in '
    + CAST(DATEDIFF(SECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N's.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

/* =====================================================================================================
   Record the migration so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health
   page treat it as applied.

   This migration does NOT change the EF entity model - indexes on existing columns are physical only - so
   its snapshot is byte-identical to its predecessor's and the stamp simply copies that row rather than
   embedding the model blob again.

   Guarded so a re-run is a no-op, and conditional on the predecessor being present so the scripts cannot
   be applied out of order.
   ===================================================================================================== */
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608250900001_IndexCopilotAccessedResourceFkColumns')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608210700003_IndexCopilotInteractionsDedupWindow')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202608250900001_IndexCopilotAccessedResourceFkColumns', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202608210700003_IndexCopilotInteractionsDedupWindow';
        RAISERROR('IndexCopilotAccessedResourceFkColumns: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('IndexCopilotAccessedResourceFkColumns: the schema change was applied, but prerequisite migration 202608210700003_IndexCopilotInteractionsDedupWindow is missing from __MigrationHistory, so it was NOT stamped. Run the manual scripts in migration-id order.', 16, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('IndexCopilotAccessedResourceFkColumns: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
