/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202608310800001_ColumnstoreUsageReportMetrics
   Predecessor: 202608310747353_DenormaliseCopilotChatUserAndTime
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database schema BY HAND instead of running the installer
   (which applies EF migrations automatically). It performs the same schema changes as the migration and
   then stamps __MigrationHistory so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web
   app Health page recognise it as applied.

   WHAT IT DOES
     Adds a nonclustered COLUMNSTORE index over the columns the Copilot licence-opportunity report
     aggregates, on the four per-user Microsoft 365 daily usage-report tables:
       * teams_user_activity_log
       * outlook_user_activity_log
       * sharepoint_user_activity_log
       * onedrive_user_activity_log

     The existing IX_date - ([date], [last_activity_date]) INCLUDE ([user_id]) - does not cover the metric
     columns those CTEs sum, so the date range could not be served index-only and SQL Server fell back to
     a FULL CLUSTERED SCAN of each table no matter how narrow the selected window was.

   MEASURED IMPACT (synthetic 13.2M-row teams_user_activity_log; medians of 3 warm runs)
     IX_date only (before)      2,619 ms   CPU 13,032 ms   179,062 logical reads
     + covering B-tree index    2,174 ms                    27,541 logical reads
     + COLUMNSTORE              1,388 ms   CPU  2,328 ms     3,519 logical reads    86 MB (16x compression)

     Note the covering B-tree cut reads by 85% but elapsed by only 17%: this step is CPU-bound on
     aggregation, not I/O-bound. That is why columnstore is the primary shape and the B-tree only a
     fallback.

   AVAILABILITY - THIS SCRIPT ADAPTS ITSELF, YOU DO NOT NEED TO CHECK ANYTHING
     Nonclustered columnstore is available in ALL editions only from SQL Server 2016 SP1. SQL Server 2016
     RTM Standard / Express reject it, as do Azure SQL Database Basic / S0-S2 and elastic pools under
     100 eDTU. Rather than testing versions and service tiers, each index is simply ATTEMPTED and the
     script falls back:
         columnstore  ->  covering B-tree index  ->  leave IX_date alone and log
     On SQL Server Express (EngineEdition 4) the B-tree fallback is deliberately SKIPPED, because a
     multi-GB index across four tables could push the database over the 10 GB Express limit - a worse
     outcome than a slow report.

     If neither index can be created the script still SUCCEEDS and still stamps __MigrationHistory. The
     licence-opportunity report simply stays as slow as it is today. Look for the message
     "covering index ALSO failed" in the output if you want to know whether that happened.

   HOW LONG IT TAKES / DOWNTIME
     Index builds only - no data is read, written or moved, and no existing index is altered. Build time
     scales with the row count of each usage table. Columnstore builds are offline on all editions, so
     RUN THIS IN A MAINTENANCE WINDOW WITH THE IMPORTER STOPPED.

   AFTERWARDS
     The importer compacts the columnstore delta rowgroups after each usage-report cycle. No manual index
     maintenance is required.

   RUN ORDER
     The manual scripts form a strict prerequisite chain. Run them in migration-id order. This script
     requires 202608310747353_DenormaliseCopilotChatUserAndTime to be stamped first and will refuse to
     stamp itself (with an error) if it is not.
   ===================================================================================================== */

SET XACT_ABORT ON;
GO
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'ColumnstoreUsageReportMetrics';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @sql nvarchar(max);

DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
-- EngineEdition: 3 = Enterprise, 4 = Express (incl. LocalDB), 5 = Azure SQL DB, 8 = Azure SQL MI.
DECLARE @isExpress bit = CASE WHEN @edition = 4 THEN 1 ELSE 0 END;

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121)
         + N' UTC; EngineEdition=' + CAST(@edition AS nvarchar(10))
         + CASE WHEN @isExpress = 1
                THEN N' (Express - the B-tree fallback is disabled to protect the 10 GB database cap).'
                ELSE N'.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @targets TABLE (
    seq       int IDENTITY(1,1),
    tbl       sysname,
    cs_index  sysname,
    bt_index  sysname,
    cs_cols   nvarchar(1000),   -- columnstore column list
    bt_cols   nvarchar(1000)    -- B-tree fallback INCLUDE list (key is always [date])
);

INSERT @targets (tbl, cs_index, bt_index, cs_cols, bt_cols) VALUES
    (N'teams_user_activity_log',
     N'NCCI_teams_user_activity_log_metrics', N'IX_teams_user_activity_log_metrics',
     N'[user_id], [date], [last_activity_date], [private_chat_count], [team_chat_count], [post_messages], [reply_messages], [meetings_attended_count], [meetings_organized_count]',
     N'[user_id], [last_activity_date], [private_chat_count], [team_chat_count], [post_messages], [reply_messages], [meetings_attended_count], [meetings_organized_count]'),
    (N'outlook_user_activity_log',
     N'NCCI_outlook_user_activity_log_metrics', N'IX_outlook_user_activity_log_metrics',
     N'[user_id], [date], [last_activity_date], [email_send_count], [email_read_count]',
     N'[user_id], [last_activity_date], [email_send_count], [email_read_count]'),
    (N'sharepoint_user_activity_log',
     N'NCCI_sharepoint_user_activity_log_metrics', N'IX_sharepoint_user_activity_log_metrics',
     N'[user_id], [date], [last_activity_date], [viewed_or_edited]',
     N'[user_id], [last_activity_date], [viewed_or_edited]'),
    (N'onedrive_user_activity_log',
     N'NCCI_onedrive_user_activity_log_metrics', N'IX_onedrive_user_activity_log_metrics',
     N'[user_id], [date], [last_activity_date], [viewed_or_edited]',
     N'[user_id], [last_activity_date], [viewed_or_edited]');

DECLARE @seq int = 1;
DECLARE @maxSeq int = (SELECT MAX(seq) FROM @targets);
DECLARE @tbl sysname, @csIndex sysname, @btIndex sysname, @csCols nvarchar(1000), @btCols nvarchar(1000);
DECLARE @rowCount bigint;

WHILE @seq <= @maxSeq
BEGIN
    SELECT @tbl = tbl, @csIndex = cs_index, @btIndex = bt_index, @csCols = cs_cols, @btCols = bt_cols
    FROM @targets WHERE seq = @seq;

    SET @seq += 1;

    IF OBJECT_ID(N'dbo.' + @tbl) IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @tbl + N' does not exist; skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    -- Mutually exclusive: if EITHER shape is already present, this table is done. Prevents a re-run
    -- after an RTM -> SP1 patch from leaving both a multi-GB B-tree and the columnstore index behind.
    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name IN (@csIndex, @btIndex))
    BEGIN
        SET @msg = @migration + N': dbo.' + @tbl + N' already has a usage-metrics index; skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p
                     WHERE p.object_id = OBJECT_ID(N'dbo.' + @tbl) AND p.index_id IN (0, 1));
    SET @stepStart = SYSUTCDATETIME();
    SET @msg = @migration + N': dbo.' + @tbl + N' (' + CAST(@rowCount AS nvarchar(20))
             + N' estimated rows) - attempting columnstore...';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    -- 1. Columnstore. Run through sp_executesql inside TRY/CATCH: an edition/tier rejection aborts the
    --    batch and is only catchable when the statement is executed indirectly.
    BEGIN TRY
        SET @sql = N'CREATE NONCLUSTERED COLUMNSTORE INDEX [' + @csIndex + N'] ON [dbo].[' + @tbl + N'] ('
                 + @csCols + N');';
        EXEC sp_executesql @sql;

        SET @msg = @migration + N': dbo.' + @tbl + N' - columnstore created in '
                 + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END TRY
    BEGIN CATCH
        SET @msg = @migration + N': dbo.' + @tbl + N' - columnstore unavailable (' + ERROR_MESSAGE() + N').';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END CATCH

    -- 2. Covering B-tree fallback, unless this is Express.
    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @csIndex)
       AND @isExpress = 0
    BEGIN
        SET @stepStart = SYSUTCDATETIME();
        BEGIN TRY
            SET @msg = @migration + N': dbo.' + @tbl + N' - falling back to a covering B-tree index...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;

            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @btIndex + N'] ON [dbo].[' + @tbl + N'] ([date]) INCLUDE ('
                     + @btCols + N');';
            EXEC sp_executesql @sql;

            SET @msg = @migration + N': dbo.' + @tbl + N' - covering index created in '
                     + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END TRY
        BEGIN CATCH
            -- 3. Neither shape could be created. IX_date is untouched, so the report still works - just
            --    at its current speed. Log loudly and carry on rather than failing the whole upgrade.
            SET @msg = @migration + N': dbo.' + @tbl + N' - covering index ALSO failed ('
                     + ERROR_MESSAGE() + N'). Leaving IX_date in place; the licence-opportunity report will '
                     + N'remain slow on this server.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END
END

SET @msg = @migration + N': finished in '
         + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

GO

/* =====================================================================================================
   Record the migration so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health
   page treat it as applied.

   This migration does NOT change the EF entity model - it only adds indexes over existing columns - so
   its snapshot is byte-identical to its predecessor's and the stamp simply copies that row rather than
   embedding the model blob again.

   Guarded so a re-run is a no-op, and conditional on the predecessor being present so the scripts cannot
   be applied out of order.
   ===================================================================================================== */
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608310800001_ColumnstoreUsageReportMetrics')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608310747353_DenormaliseCopilotChatUserAndTime')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202608310800001_ColumnstoreUsageReportMetrics', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202608310747353_DenormaliseCopilotChatUserAndTime';
        RAISERROR('ColumnstoreUsageReportMetrics: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('ColumnstoreUsageReportMetrics: the schema change was applied, but prerequisite migration 202608310747353_DenormaliseCopilotChatUserAndTime is missing from __MigrationHistory, so it was NOT stamped. Run the manual scripts in migration-id order.', 16, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('ColumnstoreUsageReportMetrics: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
GO