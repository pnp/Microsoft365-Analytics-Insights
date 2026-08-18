/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202608131030001_IndexUsageReportSnapshots
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database by hand instead of running the installer.
   This is the exact index change the migration performs, followed by the __MigrationHistory stamp so
   EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health page treat it as
   applied.

   WHAT IT DOES
     Widens the existing IX_date index on the five per-user usage-report tables (Teams, Outlook,
     OneDrive, SharePoint and Viva Engage) from ([date]) to:
         ([date], [last_activity_date]) INCLUDE ([user_id])
     The website usage chart reads one report snapshot per week and counts the distinct users whose
     last_activity_date falls in that week. With only [date] indexed, every one of that day's rows
     must be fetched from the table to test last_activity_date and read user_id. Carrying both
     columns in the index turns the chart into an index-only range seek.

     IX_date is widened rather than joined by a second index because the installer's profiling schema
     script already creates IX_date on these tables. Two indexes sharing a leading column would both
     have to be maintained on tables that take a large daily write load from the usage importer.

   SAFETY
     * Idempotent / re-runnable: a table already carrying the wider index is skipped, and a missing
       table or column is skipped rather than erroring.
     * DROP_EXISTING is used when the narrow index is present, so the table is never left without an
       index on [date].
     * Attempts ONLINE (non-blocking) on Enterprise / Azure SQL DB / MI and falls back to OFFLINE.
       These tables hold MILLIONS of rows on a large tenant: where ONLINE is unavailable each build
       briefly locks its table - run this in a MAINTENANCE WINDOW WITH THE IMPORTER STOPPED.
     * No wrapping transaction (matches suppressTransaction: true); an interrupted run converges on
       re-run.

   PREREQUISITE
     The database must already be on migration 202607231700001_CoverCopilotAccessedResourceDedup.
     The __MigrationHistory stamp copies that row's model snapshot (identical to this one, because
     this migration changes no EF entity model).

   Run against the Analytics database.
   ===================================================================================================== */SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexUsageReportSnapshots';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit;
DECLARE @table sysname;
DECLARE @index sysname = N'IX_date';
DECLARE @sql nvarchar(max);
DECLARE @onlineDone bit;
DECLARE @rowCount bigint;
DECLARE @indexId int;
DECLARE @isCurrent bit;
DECLARE @i int = 1;

-- ONLINE index operations exist only on Enterprise (3), Azure SQL DB (5) and Azure SQL MI (8).
-- Express / Standard / LocalDB reject them; the attempt runs through sp_executesql inside
-- TRY/CATCH so that rejection is catchable and we can fall back to an offline build.
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;

DECLARE @targets table (sequence int NOT NULL PRIMARY KEY, table_name sysname NOT NULL);
INSERT INTO @targets (sequence, table_name) VALUES
    (1, N'teams_user_activity_log'),
    (2, N'outlook_user_activity_log'),
    (3, N'onedrive_user_activity_log'),
    (4, N'sharepoint_user_activity_log'),
    (5, N'yammer_user_activity_log');

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC; EngineEdition='
    + CAST(@edition AS nvarchar(10))
    + CASE WHEN @canOnline = 1
        THEN N'; ONLINE index builds will be attempted (with offline fallback).'
        ELSE N'; ONLINE index builds are not supported on this edition, so each build briefly locks its table - run this with the importer stopped, in a maintenance window.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

WHILE @i <= 5
BEGIN
    SELECT @table = table_name FROM @targets WHERE sequence = @i;
    SET @i += 1;
    SET @indexId = NULL;

    IF OBJECT_ID(N'dbo.' + @table, N'U') IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @table + N' does not exist, skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = N'date')
        OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = N'last_activity_date')
        OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = N'user_id')
    BEGIN
        SET @msg = @migration + N': dbo.' + @table + N' is missing an expected column, skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    SELECT @indexId = index_id
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = @index;

    -- Already widened? Then this migration (or a re-run of it) has nothing to do for this table.
    SET @isCurrent = 0;
    IF @indexId IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c
                     ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = OBJECT_ID(N'dbo.' + @table) AND ic.index_id = @indexId
                     AND ic.key_ordinal = 1 AND c.name = N'date')
       AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c
                     ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = OBJECT_ID(N'dbo.' + @table) AND ic.index_id = @indexId
                     AND ic.key_ordinal = 2 AND c.name = N'last_activity_date')
       AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c
                     ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = OBJECT_ID(N'dbo.' + @table) AND ic.index_id = @indexId
                     AND ic.is_included_column = 1 AND c.name = N'user_id')
        SET @isCurrent = 1;

    IF @isCurrent = 1
    BEGIN
        SET @msg = @migration + N': [' + @index + N'] on ' + @table + N' already covers the usage-report query, skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions AS p
                     WHERE p.object_id = OBJECT_ID(N'dbo.' + @table) AND p.index_id IN (0, 1));
    SET @msg = @migration
        + CASE WHEN @indexId IS NULL THEN N': creating [' ELSE N': widening [' END
        + @index + N'] on ' + @table + N' (row estimate ' + CAST(@rowCount AS nvarchar(20)) + N').';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SET @stepStart = SYSUTCDATETIME();
    SET @onlineDone = 0;

    -- DROP_EXISTING keeps the change to a single atomic statement when the narrow index is already
    -- there, so the table is never left without an index on [date].
    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @index + N'] ON [dbo].[' + @table
                + N'] ([date], [last_activity_date]) INCLUDE ([user_id]) WITH ('
                + CASE WHEN @indexId IS NULL THEN N'' ELSE N'DROP_EXISTING = ON, ' END
                + N'ONLINE = ON);';
            EXEC sp_executesql @sql;
            SET @onlineDone = 1;
        END TRY
        BEGIN CATCH
            SET @msg = @migration + N': ONLINE build of [' + @index + N'] on ' + @table
                + N' unavailable (' + ERROR_MESSAGE() + N'); retrying offline.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END

    IF @onlineDone = 0
    BEGIN
        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @index + N'] ON [dbo].[' + @table
            + N'] ([date], [last_activity_date]) INCLUDE ([user_id])'
            + CASE WHEN @indexId IS NULL THEN N';' ELSE N' WITH (DROP_EXISTING = ON);' END;
        EXEC sp_executesql @sql;
    END

    SET @msg = @migration + N': [' + @index + N'] on ' + @table + N' ready in '
        + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20))
        + N'ms (' + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- =====================================================================================================
-- Record the migration so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app
-- Health page treat it as applied. No model change here, so the EF snapshot is byte-identical to the
-- previous migration's - copy that row's Model / ContextKey / ProductVersion. Guarded so re-running
-- is safe.
-- =====================================================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608131030001_IndexUsageReportSnapshots')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202607231700001_CoverCopilotAccessedResourceDedup')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202608131030001_IndexUsageReportSnapshots', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202607231700001_CoverCopilotAccessedResourceDedup';
        RAISERROR('IndexUsageReportSnapshots: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('IndexUsageReportSnapshots: the indexes were widened, but prerequisite migration 202607231700001_CoverCopilotAccessedResourceDedup is missing from __MigrationHistory, so it was NOT stamped. Upgrade to the previous release first, or run the installer to reconcile.', 16, 1);
END
ELSE
    RAISERROR('IndexUsageReportSnapshots: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;