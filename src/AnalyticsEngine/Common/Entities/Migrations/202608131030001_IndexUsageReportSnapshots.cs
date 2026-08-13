namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Widens the existing <c>IX_date</c> index on the five per-user Microsoft 365 usage-report
    /// tables from <c>([date])</c> to <c>([date], [last_activity_date]) INCLUDE ([user_id])</c>.
    ///
    /// Why: the website's usage chart reads one report snapshot per displayed week and counts the
    /// distinct users whose <c>last_activity_date</c> falls in that week. With only <c>[date]</c>
    /// indexed, SQL Server seeks the day but must then fetch every one of that day's rows from the
    /// table to test <c>last_activity_date</c> and read <c>user_id</c> - one lookup per user per
    /// week. Carrying both columns in the index makes the whole query an index-only range seek.
    ///
    /// Why re-use the <c>IX_date</c> name rather than add a second index: <c>IX_date</c> is already
    /// created on these tables by the installer's profiling schema script
    /// (<c>Profiling-03-CreateSchema.sql</c>), which creates it only when absent. A separate index
    /// with the same leading column would leave two overlapping indexes to maintain on tables that
    /// take a large daily insert/update load from the usage importer. Widening the existing index
    /// keeps exactly one index per table, and every existing <c>[date]</c> predicate still seeks it.
    ///
    /// This migration changes only the SQL schema, not the EF entity model, so its snapshot is
    /// byte-identical to the previous migration's and EF never raises
    /// <c>AutomaticDataLossException</c>.
    ///
    /// Safety: idempotent and guarded. Each table, column and the current index definition are
    /// checked, so a table already carrying the wider index is skipped and a missing table is
    /// ignored. The build attempts <c>ONLINE</c> on capable editions (Enterprise 3 / Azure SQL DB 5
    /// / MI 8) via <c>sp_executesql</c> inside <c>TRY/CATCH</c> and falls back to an offline build,
    /// which briefly locks the table - on a large tenant these tables hold millions of rows, so run
    /// the upgrade in a maintenance window with the importer stopped. Runs outside the EF
    /// transaction (<c>suppressTransaction: true</c>) so an interrupted upgrade converges on re-run.
    /// </summary>
    public partial class IndexUsageReportSnapshots : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests and the manual
        /// upgrade script run exactly the same statements.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

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
";

        /// <summary>
        /// SQL executed by <see cref="Down"/>. Returns <c>IX_date</c> to its original key-only shape
        /// (the profiling schema script's definition). Idempotent and guarded.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;

DECLARE @targets table (sequence int NOT NULL PRIMARY KEY, table_name sysname NOT NULL);
INSERT INTO @targets (sequence, table_name) VALUES
    (1, N'teams_user_activity_log'),
    (2, N'outlook_user_activity_log'),
    (3, N'onedrive_user_activity_log'),
    (4, N'sharepoint_user_activity_log'),
    (5, N'yammer_user_activity_log');

DECLARE @table sysname;
DECLARE @sql nvarchar(max);
DECLARE @i int = 1;

WHILE @i <= 5
BEGIN
    SELECT @table = table_name FROM @targets WHERE sequence = @i;
    SET @i += 1;

    IF OBJECT_ID(N'dbo.' + @table, N'U') IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = N'IX_date')
    BEGIN
        SET @sql = N'CREATE NONCLUSTERED INDEX [IX_date] ON [dbo].[' + @table + N'] ([date]) WITH (DROP_EXISTING = ON);';
        EXEC sp_executesql @sql;
    END
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'IndexUsageReportSnapshots'. Widens IX_date on the five per-user usage-report tables to ([date], [last_activity_date]) INCLUDE ([user_id]) so the website's weekly usage chart runs as an index-only seek. These tables hold millions of rows on a large tenant; where ONLINE index builds are unavailable each build briefly locks its table, so run this with the importer stopped. Check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'IndexUsageReportSnapshots'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
