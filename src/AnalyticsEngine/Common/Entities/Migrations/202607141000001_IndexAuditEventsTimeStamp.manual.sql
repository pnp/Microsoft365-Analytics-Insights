/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202607141000001_IndexAuditEventsTimeStamp
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database schema BY HAND instead of running the installer
   (which applies EF migrations automatically). This is the exact index build the migration performs,
   followed by the __MigrationHistory stamp so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and
   the web app Health page recognise it as applied.

   WHAT IT DOES
     Adds two non-clustered indexes used by the O365 audit-log importer's per-batch "already processed"
     window query (ActivityImportCache.GetAndBuildNewCache):
       * IX_audit_events_time_stamp                         ON audit_events(time_stamp)
       * IX_ignored_audit_events_processed_timestamp        ON ignored_audit_events(processed_timestamp)
     Without them, every commit batch full-scanned the entire, ever-growing audit_events table; the indexes
     turn each scan into a range seek.

   SAFETY
     * Idempotent / re-runnable: each (table, column, index) is guarded; an already-present index is a no-op.
     * The build attempts ONLINE (non-blocking) on Enterprise / Azure SQL DB / MI and falls back to a normal
       OFFLINE build on Express / Standard / LocalDB. An OFFLINE build briefly locks the table - on a large
       audit_events run it in a MAINTENANCE WINDOW WITH THE IMPORTER STOPPED.
     * No wrapping transaction (matches the migration's suppressTransaction: true); an interrupted run
       converges on re-run.

   PREREQUISITE
     The database must already be on migration 202607130900001_DedupCopilotAccessedResourceSiteUrls (the
     previous release). The __MigrationHistory stamp copies that row's model snapshot (identical to this one,
     because this migration changes no EF entity model).

   Run against the Analytics database.
   ===================================================================================================== */
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexAuditEventsTimeStamp';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @targets TABLE (seq int IDENTITY(1,1), tbl sysname, col sysname, ix sysname);
INSERT @targets (tbl, col, ix) VALUES
 (N'audit_events',         N'time_stamp',          N'IX_audit_events_time_stamp'),
 (N'ignored_audit_events', N'processed_timestamp', N'IX_ignored_audit_events_processed_timestamp');

DECLARE @seq int = 1;
DECLARE @maxSeq int = (SELECT MAX(seq) FROM @targets);
DECLARE @tbl sysname, @col sysname, @ix sysname;
DECLARE @rowCount bigint, @edition int;
DECLARE @sql nvarchar(max);
DECLARE @canOnline bit, @onlineDone bit, @indexExists bit;

SET @edition = CAST(SERVERPROPERTY('EngineEdition') AS int);
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index build '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).' ELSE N'not supported on this edition - using offline (briefly locks the table; run large upgrades in a maintenance window).' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

WHILE @seq <= @maxSeq
BEGIN
    SELECT @tbl = tbl, @col = col, @ix = ix FROM @targets WHERE seq = @seq;
    SET @seq = @seq + 1;

    IF OBJECT_ID(N'dbo.' + @tbl, N'U') IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @tbl + N' does not exist, skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @col)
    BEGIN
        SET @msg = @migration + N': dbo.' + @tbl + N'.' + @col + N' does not exist, skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    SET @indexExists = CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix) THEN 1 ELSE 0 END;
    IF @indexExists = 1
    BEGIN
        SET @msg = @migration + N': [' + @ix + N'] already exists on ' + @tbl + N', nothing to do.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(N'dbo.' + @tbl) AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N'.' + @col + N' row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N'.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SET @stepStart = SYSUTCDATETIME();
    SET @onlineDone = 0;

    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            SET @msg = @migration + N': creating [' + @ix + N'] on ' + @tbl + N'(' + @col + N') WITH (ONLINE = ON)...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']) WITH (ONLINE = ON);';
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
        SET @msg = @migration + N': creating [' + @ix + N'] on ' + @tbl + N'(' + @col + N') (offline)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']);';
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
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202607141000001_IndexAuditEventsTimeStamp')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202607130900001_DedupCopilotAccessedResourceSiteUrls')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202607141000001_IndexAuditEventsTimeStamp', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202607130900001_DedupCopilotAccessedResourceSiteUrls';
        RAISERROR('IndexAuditEventsTimeStamp: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('IndexAuditEventsTimeStamp: the indexes were built, but prerequisite migration 202607130900001_DedupCopilotAccessedResourceSiteUrls is missing from __MigrationHistory, so it was NOT stamped. Upgrade to the previous release first, or run the installer to reconcile.', 16, 1);
END
ELSE
    RAISERROR('IndexAuditEventsTimeStamp: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
