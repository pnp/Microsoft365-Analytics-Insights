namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds covering snapshot indexes to the five per-user Microsoft 365 usage-report tables.
    /// The website report reads one completed daily snapshot per displayed week; without these
    /// indexes each workload scans its entire multi-million-row history and can time out.
    ///
    /// This migration changes only the SQL schema, not the EF model. Index builds run outside the
    /// EF transaction, use ONLINE where supported, and fall back to an offline build. Operators on
    /// editions without online index builds should upgrade in a maintenance window with the
    /// importer stopped because each large table is locked while its index is built.
    /// </summary>
    public partial class IndexUsageReportSnapshots : DbMigration
    {
        public const string Up_Sql = @"
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
";

        public const string Down_Sql = @"
SET NOCOUNT ON;

DECLARE @targets table (table_name sysname NOT NULL, index_name sysname NOT NULL);
INSERT INTO @targets (table_name, index_name) VALUES
    (N'teams_user_activity_log',      N'IX_teams_user_activity_log_report_snapshot'),
    (N'outlook_user_activity_log',    N'IX_outlook_user_activity_log_report_snapshot'),
    (N'onedrive_user_activity_log',   N'IX_onedrive_user_activity_log_report_snapshot'),
    (N'sharepoint_user_activity_log', N'IX_sharepoint_user_activity_log_report_snapshot'),
    (N'yammer_user_activity_log',     N'IX_yammer_user_activity_log_report_snapshot');

DECLARE @table sysname;
DECLARE @index sysname;
DECLARE @sql nvarchar(max);

WHILE EXISTS (SELECT 1 FROM @targets)
BEGIN
    SELECT TOP 1 @table = table_name, @index = index_name FROM @targets ORDER BY table_name;
    IF OBJECT_ID(N'dbo.' + @table, N'U') IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = @index)
    BEGIN
        SET @sql = N'DROP INDEX [' + @index + N'] ON [dbo].[' + @table + N'];';
        EXEC sp_executesql @sql;
    END
    DELETE FROM @targets WHERE table_name = @table;
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'IndexUsageReportSnapshots'. Adds covering indexes used by the website's completed-week Microsoft 365 usage chart. Each usage table can contain many millions of daily per-user rows; where online index builds are unavailable, stop the importer and run this upgrade in a maintenance window.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'IndexUsageReportSnapshots'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
