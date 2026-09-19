namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds the covering <c>IX_date</c> index to <c>dbo.platform_user_activity_log</c>, the sixth
    /// per-user Microsoft 365 usage-report table - the one
    /// <see cref="IndexUsageReportSnapshots"/> left out because nothing reported on it yet.
    ///
    /// Shape: <c>([date]) INCLUDE ([user_id], &lt;all 34 platform/app bit columns&gt;)</c>.
    ///
    /// Why now: the portal's new "Office apps" report area queries this table for the first time,
    /// with twelve windowed aggregates that run in parallel on one page load. The table had no index
    /// on <c>[date]</c> at all in the shipped product (the installer's profiling schema script
    /// creates one, but only when the optional profiling extension is installed), so every one of
    /// those charts was a full clustered-index scan.
    ///
    /// Why INCLUDE rather than a wider key: <c>[date]</c> is the only column that takes part in
    /// MATCHING - the predicate is a plain <c>[date] &gt;= @from</c> range. <c>user_id</c> and the bit
    /// columns are only ever RETURNED and aggregated, never matched, which is precisely the case the
    /// repo's index guidance says <c>INCLUDE</c> is for. A composite key over them would be larger and
    /// would buy no extra seekability.
    ///
    /// Why re-use the name <c>IX_date</c>: the installer's profiling schema script
    /// (<c>Profiling-03-CreateSchema.sql</c>) already creates <c>IX_date</c> on this exact table when
    /// the profiling extension is installed. A separately-named index on the same leading column
    /// would leave two overlapping indexes to maintain on a table that takes one row per user per day
    /// from the importer. <c>DROP_EXISTING</c> upgrades the narrow one in place, so the table is never
    /// left without an index on <c>[date]</c>.
    ///
    /// MEASURED IMPACT - synthetic scale, 18,000,000 rows (100k users x 180 days), medians of 3 warm
    /// runs with the database plan cache cleared between runs; every query carries
    /// <c>OPTION (RECOMPILE)</c>. Queries are the real ones, read out of the built assembly.
    ///
    /// <code>
    ///                           30-day window                    180-day window
    /// chart query          reads before -> after   ms          reads before -> after   ms
    /// -------------------  ---------------------   ---------   ---------------------   ---------
    /// AppPopularity            95,339 ->  11,626    8.6s->3.9s     95,179 ->  69,866   29.4->25.1s
    /// AppWeekly                95,307 ->  11,650   20.2s->9.7s     95,491 ->  69,866   59.0->52.9s
    /// PlatformWeekly           95,311 ->  11,650   12.7s->4.5s     95,407 ->  69,866   40.4->26.8s
    /// AppBreadth               95,399 ->  11,650    2.7s->0.7s     94,595 ->  69,442   13.2-> 6.9s
    /// AppPlatformMatrix       133,411 ->  46,602   45.2s->15.0s   334,787 -> 303,466  147.6->91.3s
    /// AppByDepartment          96,322 ->  12,617   11.8s->3.3s     96,318 ->  70,569   29.0->20.2s
    /// DepartmentAdoptionRate   96,037 ->  12,196    0.9s->0.4s     95,069 ->  70,016    5.1-> 3.4s
    /// AppByDomain              95,917 ->  12,204    9.8s->4.3s     96,069 ->  70,220   33.9->20.9s
    /// WebOnly                  95,351 ->  11,638    7.0s->2.0s     94,883 ->  69,574   22.0->18.4s
    /// </code>
    ///
    /// Plan operator: <c>Clustered Index Scan</c> -> <c>Index Seek</c> on every one of the eighteen
    /// measurements. Reads and elapsed time both improve in every case; nothing regresses at either
    /// selectivity.
    ///
    /// The before column is a full scan in every case, which is why a 30-day window read exactly as
    /// many pages as a 180-day one: without an index on <c>[date]</c> the reporting window was not a
    /// cost lever at all. Afterwards it is - 30 days reads 8x fewer pages than before, and 6x fewer
    /// than the 180-day window does. Several charts exceeded the report API's 25-second per-chart
    /// timeout outright beforehand, so the area could not have shipped without this.
    ///
    /// The 180-day column improves least, and that is expected rather than disappointing: the
    /// benchmark holds exactly 180 days, so a 180-day window IS the whole table and the seek
    /// degenerates to a range scan. It still reads ~27% fewer pages because this index is narrower
    /// than the table it replaces - it carries neither <c>id</c> nor <c>last_activity_date</c>. On a
    /// tenant with years of history a 180-day window is a small fraction of the table and behaves
    /// like the 30-day column.
    ///
    /// Cost: the index is 540 MB against a 735 MB base table (73%) at 18m rows, and built in 49
    /// seconds. Scaling roughly O(n log n), budget about 30 MB and 3 seconds per million rows, so a
    /// 100m-row table means roughly 3 GB and 5-6 minutes.
    ///
    /// This migration changes only the SQL schema, not the EF entity model, so its snapshot is
    /// byte-identical to the previous migration's and EF never raises
    /// <c>AutomaticDataLossException</c>.
    ///
    /// Safety: idempotent and guarded. The table, its columns and the current index definition are
    /// all checked, so a database already carrying the covering index is skipped and a missing table
    /// or column is skipped rather than erroring. The build attempts <c>ONLINE</c> on capable
    /// editions (Enterprise 3 / Azure SQL DB 5 / MI 8) via <c>sp_executesql</c> inside
    /// <c>TRY/CATCH</c> - that error is only catchable when issued that way - and falls back to an
    /// offline build, which briefly locks the table. On a large tenant this table holds tens of
    /// millions of rows, so run the upgrade in a maintenance window with the importer stopped. Runs
    /// outside the EF transaction (<c>suppressTransaction: true</c>) so an interrupted upgrade
    /// converges on re-run.
    /// </summary>
    public partial class IndexPlatformUserActivityLogDate : DbMigration
    {
        /// <summary>
        /// The index's INCLUDE list: <c>user_id</c> plus every platform, app and app-on-platform bit.
        /// </summary>
        /// <remarks>
        /// All 34 bits are carried because the report area genuinely reads all of them - the platform
        /// and app charts use the ten headline bits, and the app-on-platform matrix and the
        /// browser-only chart use the 24 cross bits. Leaving the cross bits out would make those two
        /// charts a key lookup per matching row, which at tens of millions of rows is worse than the
        /// scan this index replaces. SQL Server packs bit columns, so all 34 cost about five bytes a
        /// row; the whole INCLUDE list adds roughly nine bytes on top of the key.
        /// </remarks>
        public const string IncludeColumns =
            "[user_id], " +
            "[windows], [mac], [mobile], [web], " +
            "[outlook], [word], [excel], [powerpoint], [onenote], [teams], " +
            "[outlook_windows], [outlook_mac], [outlook_mobile], [outlook_web], " +
            "[word_windows], [word_mac], [word_mobile], [word_web], " +
            "[excel_windows], [excel_mac], [excel_mobile], [excel_web], " +
            "[powerpoint_windows], [powerpoint_mac], [powerpoint_mobile], [powerpoint_web], " +
            "[onenote_windows], [onenote_mac], [onenote_mobile], [onenote_web], " +
            "[teams_windows], [teams_mac], [teams_mobile], [teams_web]";

        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so the unit tests and the manual
        /// upgrade script run exactly the same statements.
        /// </summary>
        public static readonly string Up_Sql = @"
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
DECLARE @include nvarchar(max) = N'" + IncludeColumns + @"';

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
";

        /// <summary>
        /// SQL executed by <see cref="Down"/>. Returns <c>IX_date</c> to the key-only shape the
        /// profiling schema script defines, rather than dropping it - dropping it would silently
        /// remove an index the profiling extension expects to find. Idempotent and guarded.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.platform_user_activity_log', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.platform_user_activity_log') AND name = N'IX_date')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_date] ON [dbo].[platform_user_activity_log] ([date])
        WITH (DROP_EXISTING = ON);
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'IndexPlatformUserActivityLogDate'. Creates the covering IX_date index on dbo.platform_user_activity_log ([date] INCLUDE user_id + the 34 app/platform bits) so the portal's new Office apps report runs as an index-only range seek instead of scanning the whole table. This table holds one row per user per day and reaches tens of millions of rows on a large tenant; where ONLINE index builds are unavailable the build briefly locks the table, so run this with the importer stopped. Check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'IndexPlatformUserActivityLogDate'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
