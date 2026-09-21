/* =====================================================================================================
   MANUAL DATABASE UPGRADE - 202609190900001_IndexPlatformUserActivityLogDate

   For DBAs who upgrade the Analytics database by hand instead of running the installer.
   This is the exact index change the migration performs, followed by the __MigrationHistory stamp so
   EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health page treat it as
   applied.

   WHAT IT DOES
     Creates (or widens, if the installer's profiling extension already made the narrow one) the
     IX_date index on dbo.platform_user_activity_log:

         ([date]) INCLUDE ([user_id], <all 34 platform / app / app-on-platform bit columns>)

     This table is the sixth per-user Microsoft 365 usage-report table. The other five were indexed
     by 202608131030001_IndexUsageReportSnapshots; this one was left out because no shipped report
     read it. The portal's new "Office apps" report area now does - eleven windowed aggregates on
     one page load - and without an index on [date] every one of them read the whole table.

     [date] is the only column that takes part in MATCHING (the predicate is a plain [date] >= @from
     range); user_id and the bit columns are only returned and aggregated. That is exactly the case
     INCLUDE is for, so the key stays narrow and the INCLUDE list carries the rest.

   MEASURED IMPACT (synthetic scale: 18,000,000 rows = 100k users x 180 days; medians of 3 warm runs
   with the plan cache cleared; the real report queries, taken from the built assembly)

     Without the index every query is a Clustered Index Scan, so the reporting window is NOT a cost
     lever - a 30-day window reads as many pages as a 180-day one, and the cost grows with retained
     history forever. With it, a 30-day window reads ~8x fewer pages (~95,500 -> ~11,700) and every
     plan becomes an Index Seek.

     Doubling the history to 365 days and re-running the SAME 180-day window shows what that is
     worth: reads WITHOUT the index doubled with the table (~94,500 -> ~191,500), while reads WITH
     it were identical at both sizes (~69,500). See the pull request for the full table.

   SAFETY
     * Idempotent / re-runnable: a database already carrying the covering index is skipped, and a
       missing table or column is skipped rather than erroring.
     * The __MigrationHistory stamp at the end verifies the index SHAPE (key column plus INCLUDE list),
       not just that something named IX_date exists, so a failed widening that leaves the profiling
       extension's narrow index behind is not recorded as a successful apply.
     * DROP_EXISTING is used when the narrow index is present, so the table is never left without an
       index on [date].
     * Attempts ONLINE (non-blocking) on Enterprise / Azure SQL DB / MI and falls back to OFFLINE.
       This table holds ONE ROW PER USER PER DAY and reaches tens of millions of rows on a large
       tenant: where ONLINE is unavailable the build briefly locks the table - run this in a
       MAINTENANCE WINDOW WITH THE IMPORTER STOPPED.
     * No wrapping transaction (matches suppressTransaction: true); an interrupted run converges on
       re-run.
     * Touches no rows, so there is no backfill and nothing to reconcile afterwards.

   PREREQUISITE
     The database must already be on migration 202609171125117_DropUnreportedCopilotStudioCreditColumns.
     The __MigrationHistory stamp copies that row's model snapshot (identical to this one, because
     this migration changes no EF entity model).

   Run against the Analytics database.
   ===================================================================================================== */
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexPlatformUserActivityLogDate';
DECLARE @table sysname = N'platform_user_activity_log';
DECLARE @index sysname = N'IX_date';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit;
DECLARE @sql nvarchar(max);
DECLARE @onlineDone bit = 0;
DECLARE @rowCount bigint;
DECLARE @indexId int = NULL;
DECLARE @isCurrent bit = 0;
DECLARE @include nvarchar(max) = N'[user_id], [windows], [mac], [mobile], [web], [outlook], [word], [excel], [powerpoint], [onenote], [teams], [outlook_windows], [outlook_mac], [outlook_mobile], [outlook_web], [word_windows], [word_mac], [word_mobile], [word_web], [excel_windows], [excel_mac], [excel_mobile], [excel_web], [powerpoint_windows], [powerpoint_mac], [powerpoint_mobile], [powerpoint_web], [onenote_windows], [onenote_mac], [onenote_mobile], [onenote_web], [teams_windows], [teams_mac], [teams_mobile], [teams_web]';

-- ONLINE index operations exist only on Enterprise (3), Azure SQL DB (5) and Azure SQL MI (8).
-- Express / Standard / LocalDB reject them; the attempt runs through sp_executesql inside
-- TRY/CATCH so that rejection is catchable and we can fall back to an offline build.
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC; EngineEdition='
    + CAST(@edition AS nvarchar(10))
    + CASE WHEN @canOnline = 1
        THEN N'; ONLINE index build will be attempted (with offline fallback).'
        ELSE N'; ONLINE index builds are not supported on this edition, so the build briefly locks the table - run this with the importer stopped, in a maintenance window.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.' + @table, N'U') IS NULL
BEGIN
    SET @msg = @migration + N': dbo.' + @table + N' does not exist, nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = N'date')
     OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = N'user_id')
     OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = N'onenote_web')
BEGIN
    SET @msg = @migration + N': dbo.' + @table + N' is missing an expected column, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SELECT @indexId = index_id
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = @index;

    -- Already covering? [date] leading, and the last of the 34 bits present as an INCLUDE. Checking
    -- one representative included column is enough: the index is only ever created by this migration
    -- (or by the profiling script, which creates the key-only shape), so a partial INCLUDE list
    -- cannot arise.
    IF @indexId IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c
                     ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = OBJECT_ID(N'dbo.' + @table) AND ic.index_id = @indexId
                     AND ic.key_ordinal = 1 AND c.name = N'date')
       AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c
                     ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = OBJECT_ID(N'dbo.' + @table) AND ic.index_id = @indexId
                     AND ic.is_included_column = 1 AND c.name = N'user_id')
       AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c
                     ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = OBJECT_ID(N'dbo.' + @table) AND ic.index_id = @indexId
                     AND ic.is_included_column = 1 AND c.name = N'onenote_web')
        SET @isCurrent = 1;

    IF @isCurrent = 1
    BEGIN
        SET @msg = @migration + N': [' + @index + N'] on ' + @table + N' already covers the Office apps report, skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions AS p
                         WHERE p.object_id = OBJECT_ID(N'dbo.' + @table) AND p.index_id IN (0, 1));
        SET @msg = @migration
            + CASE WHEN @indexId IS NULL THEN N': creating [' ELSE N': widening [' END
            + @index + N'] on ' + @table + N' (row estimate ' + CAST(@rowCount AS nvarchar(20)) + N').';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;

        -- DROP_EXISTING keeps the change to a single atomic statement when the narrow profiling-script
        -- index is already there, so the table is never left without an index on [date].
        IF @canOnline = 1
        BEGIN
            BEGIN TRY
                SET @sql = N'CREATE NONCLUSTERED INDEX [' + @index + N'] ON [dbo].[' + @table
                    + N'] ([date]) INCLUDE (' + @include + N') WITH ('
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
                + N'] ([date]) INCLUDE (' + @include + N')'
                + CASE WHEN @indexId IS NULL THEN N';' ELSE N' WITH (DROP_EXISTING = ON);' END;
            EXEC sp_executesql @sql;
        END

        SET @msg = @migration + N': [' + @index + N'] on ' + @table + N' ready in '
            + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20))
            + N'ms (' + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
END

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

/* ---------------------------------------------------------------------------------------------------
   Record the migration as applied.

   The guard checks SCHEMA only - that the index this script creates actually exists. It deliberately
   does NOT check any data state: this migration writes no rows, and a data-state guard is the shape
   that has previously refused to stamp a successfully-completed migration and stranded the rest of
   the chain behind it.

   The check matters because a severity-16 RAISERROR does not abort a batch - sqlcmd and SSMS carry on
   to the next one - so an unguarded stamp would record a failed apply as complete, after which EF
   never retries it.
   --------------------------------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory
               WHERE MigrationId = N'202609190900001_IndexPlatformUserActivityLogDate')
BEGIN
    -- Checks the SHAPE, not just the name. CREATE INDEX ... WITH (DROP_EXISTING = ON) is atomic, so a
    -- widening that fails leaves the installer profiling extension's key-only IX_date in place. A guard
    -- that only asked whether something called IX_date exists would accept that survivor and stamp the
    -- migration as applied, after which EF never retries and the Office apps report keeps paying for the
    -- key lookups this index exists to remove. These are the same three probes the body uses to decide
    -- the index is already current, so a correctly built index passes them by construction.
    IF NOT EXISTS (SELECT 1 FROM sys.indexes AS i
                   JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                   JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE i.object_id = OBJECT_ID(N'dbo.platform_user_activity_log') AND i.name = N'IX_date'
                     AND ic.key_ordinal = 1 AND c.name = N'date')
       OR NOT EXISTS (SELECT 1 FROM sys.indexes AS i
                      JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                      JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                      WHERE i.object_id = OBJECT_ID(N'dbo.platform_user_activity_log') AND i.name = N'IX_date'
                        AND ic.is_included_column = 1 AND c.name = N'user_id')
       OR NOT EXISTS (SELECT 1 FROM sys.indexes AS i
                      JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                      JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                      WHERE i.object_id = OBJECT_ID(N'dbo.platform_user_activity_log') AND i.name = N'IX_date'
                        AND ic.is_included_column = 1 AND c.name = N'onenote_web')
        RAISERROR('IndexPlatformUserActivityLogDate: NOT stamped - IX_date on dbo.platform_user_activity_log is missing, or is still the narrow key-only shape the profiling extension creates, so the index build did not complete. Re-run this script, or run the installer to reconcile.', 16, 1);
    ELSE IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory
                        WHERE MigrationId = N'202609171125117_DropUnreportedCopilotStudioCreditColumns')
        RAISERROR('IndexPlatformUserActivityLogDate: the index was created, but prerequisite migration 202609171125117_DropUnreportedCopilotStudioCreditColumns is missing from __MigrationHistory, so it was NOT stamped. Upgrade to the previous release first, or run the installer to reconcile.', 16, 1);
    ELSE
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609190900001_IndexPlatformUserActivityLogDate', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609171125117_DropUnreportedCopilotStudioCreditColumns';
        RAISERROR('IndexPlatformUserActivityLogDate: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
END
ELSE
    RAISERROR('IndexPlatformUserActivityLogDate: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;