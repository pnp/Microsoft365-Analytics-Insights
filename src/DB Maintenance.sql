/* =====================================================================================================
   INDEX AND STATISTICS MAINTENANCE - Microsoft 365 Analytics Insights database
   =====================================================================================================
   An optional script for DBAs. The product never runs it. Run it in the Analytics database.

   DO YOU NEED IT?
     If the profiling extension is installed, its Database_Maintenance Azure Automation runbook already
     maintains every index and statistic each week with dbo.IndexOptimize (Ola Hallengren's maintenance
     solution), using the same 5% / 30% thresholds as this script. You do not need both. Use this script
     where that runbook is not deployed, for example on a SQL Server you manage yourself.

   WHEN TO RUN IT
     In a quiet period: weekly at night at most, or when the dry run shows heavily fragmented large
     indexes. Running it more often than the data changes gains nothing.

   STOP THE IMPORTER FIRST
     Before running it with @DryRun = 0, stop the web jobs (App Service application setting
     WEBJOBS_STOPPED = 1), and restore the setting afterwards.
       * REORGANIZE is always online. Stopping it part-way keeps the work already done.
       * REBUILD is ONLINE only on Enterprise or Developer edition (SERVERPROPERTY('EngineEdition') = 3),
         Azure SQL Database (5) and Azure SQL Managed Instance (8), and even then it takes a short
         schema-modification lock at the start and at the end, which waits for running queries.
       * On every other edition (Standard, Web, Express, LocalDB) a REBUILD is OFFLINE: it locks the
         table for as long as it runs. On large tables such as audit_events and hits that can be a long
         time, and the importer and the reports wait for it.
     Where the edition supports it, the script tries each rebuild ONLINE first, and rebuilds OFFLINE if
     that rebuild is refused.

   WHAT IT DOES
     For each rowstore index (clustered or non-clustered) of at least @MinPages pages:
       fragmentation below 5%          nothing
       5% up to 30%                    ALTER INDEX ... REORGANIZE
       30% and above                   ALTER INDEX ... REBUILD (ONLINE where supported, see above)
     Smallest indexes first, so a time limit (@MaxMinutes) stops before the longest operations rather than
     after one of them. Then sp_updatestats refreshes the statistics that have changed since their last
     update. Each index keeps its own fill factor. Heaps, columnstore, XML and spatial indexes are skipped.

     It never shrinks the database. The script this one replaces rebuilt every index, offline and with
     FILLFACTOR = 80 however fragmented it was, then ran DBCC SHRINKDATABASE. A shrink moves pages to the
     front of the file and fragments the indexes again, undoing the rebuild, and the file grows back at
     the next import anyway. Indexes that script rebuilt still have fill factor 80, so they are about a
     quarter larger than they need to be (sys.indexes.fill_factor). This script leaves that setting alone;
     no index in the product sets a fill factor of its own, so to return one to the default, rebuild it
     once with FILLFACTOR = 100.

   DRY RUN FIRST
     With @DryRun = 1, the default, nothing is changed: the script lists each index it would maintain, with
     its size and fragmentation and the action it would take. Set @DryRun = 0 below to run the maintenance.

   HOW LONG IT TAKES
     In proportion to the size of the indexes it rebuilds or reorganizes, so use the page counts from the
     dry run (one page is 8 KB). Measured on LocalDB (Express, so OFFLINE, warm cache) with a hits-like
     table of 1M synthetic rows: rebuilding its two fragmented non-clustered indexes (8,400 pages, 66 MB)
     took 0.9 and 1.4 seconds in two runs, at least 6,000 pages a second. At that rate the same two
     indexes would take about 15 seconds at 10M rows and about 2.5 minutes at 100M rows (extrapolated, not
     measured). REORGANIZE is much slower per page - 3,400 pages fragmented 23% took 7 seconds, about 500
     pages a second - but it is online and can be stopped at any time. Wider rows (audit_events), a
     clustered index, a busy disk or an ONLINE rebuild all take longer; an ONLINE rebuild does not block
     while it runs. The dry run reads only the level above the leaf of each index (LIMITED mode), so it
     takes seconds.
     @MaxMinutes stops the script starting another index after that many minutes; the one in progress
     finishes. Run the script again to carry on: indexes already done are no longer fragmented, so they
     are skipped.

   SAFE TO RE-RUN
     Yes. It only acts on indexes that are fragmented when it runs.
   ===================================================================================================== */

SET NOCOUNT ON;

DECLARE @DryRun           bit   = 1;     -- 1 = only list what would be done. Set to 0 to run the maintenance.
DECLARE @MinPages         int   = 1000;  -- smaller indexes are skipped (1,000 pages = 8 MB)
DECLARE @ReorganizeAbove  float = 5;     -- % fragmentation at which an index is reorganized...
DECLARE @RebuildAbove     float = 30;    -- ...and at which it is rebuilt instead
DECLARE @MaxMinutes       int   = NULL;  -- start no new index operation after this many minutes (NULL = no limit)
DECLARE @UpdateStatistics bit   = 1;     -- run sp_updatestats at the end

DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @onlineCapable bit = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);

IF DB_ID() <= 4 OR OBJECT_ID(N'dbo.__MigrationHistory', N'U') IS NULL
BEGIN
    RAISERROR('Index maintenance: this does not look like the Analytics database (no dbo.__MigrationHistory). Switch to it, then run the script again. Nothing has been changed.', 16, 1) WITH NOWAIT;
    RETURN;
END

SET @msg = N'Index maintenance on ' + QUOTENAME(DB_NAME()) + N', EngineEdition ' + CAST(@edition AS nvarchar(10))
    + CASE WHEN @onlineCapable = 1 THEN N': rebuilds are attempted ONLINE.' ELSE N': every rebuild is OFFLINE on this edition and locks its table.' END
    + CASE WHEN @DryRun = 1 THEN N' DRY RUN: nothing will be changed.' ELSE N'' END;
RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;

IF OBJECT_ID(N'tempdb..#maintenance') IS NOT NULL DROP TABLE #maintenance;
CREATE TABLE #maintenance (
    id            int IDENTITY(1, 1) PRIMARY KEY,
    schema_name   sysname     NOT NULL,
    table_name    sysname     NOT NULL,
    index_name    sysname     NOT NULL,
    page_count    bigint      NOT NULL,
    fragmentation float       NOT NULL,
    action        varchar(10) NOT NULL);

INSERT #maintenance (schema_name, table_name, index_name, page_count, fragmentation, action)
SELECT s.name, t.name, i.name, ps.page_count, ps.fragmentation,
       CASE WHEN ps.fragmentation >= @RebuildAbove THEN 'REBUILD' ELSE 'REORGANIZE' END
FROM (
    SELECT object_id, index_id, SUM(page_count) AS page_count, MAX(avg_fragmentation_in_percent) AS fragmentation
    FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED')
    WHERE alloc_unit_type_desc = N'IN_ROW_DATA'
    GROUP BY object_id, index_id
) AS ps
JOIN sys.indexes AS i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
JOIN sys.tables  AS t ON t.object_id = i.object_id
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE i.type IN (1, 2)                       -- rowstore clustered and non-clustered only
  AND i.is_disabled = 0 AND i.is_hypothetical = 0
  AND t.is_ms_shipped = 0
  AND ps.page_count >= @MinPages
  AND ps.fragmentation >= @ReorganizeAbove
ORDER BY ps.page_count, s.name, t.name, i.name;

SET @msg = CAST(@@ROWCOUNT AS nvarchar(10)) + N' index(es) of at least ' + CAST(@MinPages AS nvarchar(10))
    + N' pages are fragmented by ' + CAST(@ReorganizeAbove AS nvarchar(10)) + N'% or more.';
RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;

DECLARE @id int = 0, @schema sysname, @table sysname, @index sysname, @pages bigint, @fragmentation float,
        @action varchar(10), @target nvarchar(800), @sql nvarchar(max), @opStart datetime2(3), @online bit,
        @done int = 0, @failed int = 0;

WHILE 1 = 1
BEGIN
    SELECT TOP (1) @id = id, @schema = schema_name, @table = table_name, @index = index_name,
           @pages = page_count, @fragmentation = fragmentation, @action = action
    FROM #maintenance WHERE id > @id ORDER BY id;
    IF @@ROWCOUNT = 0 BREAK;

    SET @target = QUOTENAME(@index) + N' ON ' + QUOTENAME(@schema) + N'.' + QUOTENAME(@table);
    SET @msg = CAST(@action AS nvarchar(10)) + N' ' + @target + N': ' + CAST(@pages AS nvarchar(20)) + N' pages, '
        + CONVERT(nvarchar(20), CAST(@fragmentation AS decimal(5, 1))) + N'% fragmented';

    IF @DryRun = 1
    BEGIN
        -- Not "[dry run] ...": sqlcmd strips a leading [bracketed] word from every message it prints.
        SET @msg = N'Would ' + @msg + CASE WHEN @action = 'REBUILD' AND @onlineCapable = 0 THEN N' (OFFLINE: locks the table)' ELSE N'' END;
        RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;
        CONTINUE;
    END

    IF @MaxMinutes IS NOT NULL AND DATEDIFF(SECOND, @start, SYSUTCDATETIME()) >= @MaxMinutes * 60
    BEGIN
        RAISERROR('Time limit reached: no further index operation started. Run the script again to carry on.', 0, 1) WITH NOWAIT;
        BREAK;
    END

    RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;
    SET @opStart = SYSUTCDATETIME();
    SET @online = 0;
    BEGIN TRY
        IF @action = 'REORGANIZE'
        BEGIN
            SET @sql = N'ALTER INDEX ' + @target + N' REORGANIZE;';
            EXEC sp_executesql @sql;
        END
        ELSE
        BEGIN
            IF @onlineCapable = 1
            BEGIN
                -- Through sp_executesql so that a refused ONLINE rebuild is catchable here; as a plain
                -- statement some of those errors abort the batch instead.
                BEGIN TRY
                    SET @sql = N'ALTER INDEX ' + @target + N' REBUILD WITH (ONLINE = ON);';
                    EXEC sp_executesql @sql;
                    SET @online = 1;
                END TRY
                BEGIN CATCH
                    SET @msg = N'  ONLINE rebuild refused (' + ERROR_MESSAGE() + N'); rebuilding OFFLINE instead, which locks the table.';
                    RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;
                END CATCH
            END
            IF @online = 0
            BEGIN
                SET @sql = N'ALTER INDEX ' + @target + N' REBUILD;';
                EXEC sp_executesql @sql;
            END
        END
        SET @done += 1;
        SET @msg = N'  done in ' + CAST(DATEDIFF(MILLISECOND, @opStart, SYSUTCDATETIME()) AS nvarchar(20)) + N' ms'
            + CASE WHEN @action = 'REORGANIZE' THEN N'.' WHEN @online = 1 THEN N', ONLINE.' ELSE N', OFFLINE.' END;
        RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;
    END TRY
    BEGIN CATCH
        SET @failed += 1;
        SET @msg = N'  FAILED (error ' + CAST(ERROR_NUMBER() AS nvarchar(10)) + N'): ' + ERROR_MESSAGE() + N' Carrying on with the next index.';
        RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;
    END CATCH
END

IF @UpdateStatistics = 1
BEGIN
    IF @DryRun = 1
        RAISERROR('Dry run: would then run sp_updatestats (statistics with modifications since their last update).', 0, 1) WITH NOWAIT;
    ELSE IF @MaxMinutes IS NOT NULL AND DATEDIFF(SECOND, @start, SYSUTCDATETIME()) >= @MaxMinutes * 60
        RAISERROR('Time limit reached: statistics not updated. Run the script again to carry on.', 0, 1) WITH NOWAIT;
    ELSE
    BEGIN
        RAISERROR('Updating statistics that have changed (sp_updatestats)...', 0, 1) WITH NOWAIT;
        BEGIN TRY
            EXEC sp_updatestats;
        END TRY
        BEGIN CATCH
            SET @failed += 1;
            SET @msg = N'  sp_updatestats FAILED (error ' + CAST(ERROR_NUMBER() AS nvarchar(10)) + N'): ' + ERROR_MESSAGE();
            RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;
        END CATCH
    END
END

SET @msg = N'Index maintenance finished in ' + CAST(DATEDIFF(SECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N' s: '
    + CASE WHEN @DryRun = 1 THEN N'dry run, nothing changed.'
           ELSE CAST(@done AS nvarchar(10)) + N' index operation(s) done, ' + CAST(@failed AS nvarchar(10)) + N' failed.' END;
IF @failed > 0
    RAISERROR(N'%s Read the FAILED lines above.', 16, 1, @msg) WITH NOWAIT;
ELSE
    RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;
