/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202608131030001_IndexUsageReportSnapshots

   Adds covering indexes used by the website's completed-week Microsoft 365 usage chart:
       ([date], [last_activity_date]) INCLUDE ([user_id])
   on the Teams, Outlook, OneDrive, SharePoint and Viva Engage per-user activity tables.

   These tables can contain many millions of rows. The script attempts ONLINE builds on Enterprise,
   Azure SQL Database and Azure SQL Managed Instance, then falls back to offline builds. On editions
   without online index builds, stop the importer and run this in a maintenance window because each
   table is locked while its index is built. The script is idempotent and can be rerun after interruption.

   Prerequisite: 202607231700001_CoverCopilotAccessedResourceDedup
   Run against the Analytics database.
   ===================================================================================================== */
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexUsageReportSnapshots';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit;
DECLARE @table sysname;
DECLARE @index sysname;
DECLARE @sql nvarchar(max);
DECLARE @onlineDone bit;
DECLARE @rowCount bigint;
DECLARE @i int = 1;

SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;

DECLARE @targets table
(
    sequence int NOT NULL PRIMARY KEY,
    table_name sysname NOT NULL,
    index_name sysname NOT NULL
);

INSERT INTO @targets (sequence, table_name, index_name) VALUES
    (1, N'teams_user_activity_log',      N'IX_teams_user_activity_log_report_snapshot'),
    (2, N'outlook_user_activity_log',    N'IX_outlook_user_activity_log_report_snapshot'),
    (3, N'onedrive_user_activity_log',   N'IX_onedrive_user_activity_log_report_snapshot'),
    (4, N'sharepoint_user_activity_log', N'IX_sharepoint_user_activity_log_report_snapshot'),
    (5, N'yammer_user_activity_log',     N'IX_yammer_user_activity_log_report_snapshot');

SET @msg = @migration + N': starting; EngineEdition=' + CAST(@edition AS nvarchar(10))
    + CASE WHEN @canOnline = 1
        THEN N'; online index builds will be attempted.'
        ELSE N'; online index builds are unavailable, so large tables will be locked during each build. Stop the importer and use a maintenance window.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

WHILE @i <= 5
BEGIN
    SELECT @table = table_name, @index = index_name
    FROM @targets
    WHERE sequence = @i;

    IF OBJECT_ID(N'dbo.' + @table, N'U') IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @table + N' does not exist; skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = N'date')
         OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = N'last_activity_date')
         OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = N'user_id')
    BEGIN
        SET @msg = @migration + N': dbo.' + @table + N' is missing an expected column; skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = @index)
    BEGIN
        SET @msg = @migration + N': [' + @index + N'] already exists; skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        SELECT @rowCount = ISNULL(SUM(rows), 0)
        FROM sys.partitions
        WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND index_id IN (0, 1);

        SET @msg = @migration + N': creating [' + @index + N'] on dbo.' + @table
            + N' (' + CAST(@rowCount AS nvarchar(20)) + N' estimated rows).';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;

        SET @onlineDone = 0;
        IF @canOnline = 1
        BEGIN
            BEGIN TRY
                SET @sql = N'CREATE NONCLUSTERED INDEX [' + @index + N'] ON [dbo].[' + @table
                    + N'] ([date], [last_activity_date]) INCLUDE ([user_id]) WITH (ONLINE = ON);';
                EXEC sp_executesql @sql;
                SET @onlineDone = 1;
            END TRY
            BEGIN CATCH
                SET @msg = @migration + N': online build of [' + @index + N'] failed ('
                    + ERROR_MESSAGE() + N'); retrying offline.';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END CATCH
        END

        IF @onlineDone = 0
        BEGIN
            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @index + N'] ON [dbo].[' + @table
                + N'] ([date], [last_activity_date]) INCLUDE ([user_id]);';
            EXEC sp_executesql @sql;
        END

        SET @msg = @migration + N': created [' + @index + N'].';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    SET @i += 1;
END

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

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
        RAISERROR('IndexUsageReportSnapshots: indexes were built, but prerequisite migration 202607231700001_CoverCopilotAccessedResourceDedup is missing from __MigrationHistory, so this migration was not stamped.', 16, 1);
END
ELSE
    RAISERROR('IndexUsageReportSnapshots: already recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
