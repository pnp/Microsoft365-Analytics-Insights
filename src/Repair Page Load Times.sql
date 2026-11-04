/*
====================================================================================================
 Repair Page Load Times.sql
 Microsoft 365 Analytics Insights: one-off repair of dbo.hits.page_load_time
====================================================================================================

 WHAT IT REPAIRS
 ---------------
 The App Insights importer turned App Insights' page-view duration, which is in milliseconds, into
 seconds by dividing it by 10,000 instead of 1,000. So every page load time it stored in
 dbo.hits.page_load_time is a tenth of the real time: a page that took 2.5 seconds to load was
 stored as 0.25. The Web Activity page, the SQL views and the Power BI reports all read that column,
 so they show pages loading ten times faster than they did.

 The release this script ships with stores the right value. This script multiplies by 10 the times
 that older builds stored. It changes nothing else:
   - 0 means "not measured" and NULL means "no value". Both are left as they are.
   - Hits timestamped at or after @CutoffUtc are left as they are.
   - The old value was rounded to two decimal places of the tenth, so a repaired time is accurate
     to 0.1 second. A load under 0.05 second was stored as 0 and stays "not measured".

 Nothing in the product runs this script. Run it by hand, once, after upgrading. It is not a
 database migration, it has no __MigrationHistory entry and it doesn't change the schema.

 BEFORE YOU START
 ----------------
 1. Upgrade first: deploy the release this script came with, or a later one, so that the App
    Insights importer stores new page load times at the right scale.
 2. Choose @CutoffUtc: the UTC time from which the fixed importer stored the hits. Every measured
    hit timestamped before it is multiplied by 10. Start with the beginning of the hour in which
    you started the upgrade: for an upgrade that started at 14:37 UTC on 20 November 2026, use
    '2026-11-20T14:00:00'. The dry run (step 4) shows whether that is right.
    If you're unsure, choose an earlier time rather than a later one. A hit the repair leaves out
    keeps its current, too-small time; a hit the fixed importer stored that the repair includes
    becomes ten times too large.
 3. Optional, and only before you upgrade: record exactly where the old rows end. Stop the App
    Insights importer WebJob, run SELECT MAX(id) FROM dbo.hits; and then upgrade. Set
    @LastLegacyHitId to that number, and @CutoffUtc to the time you stopped the importer. The
    script then also leaves alone every hit stored after that id, including page views from before
    the upgrade that the fixed importer imported late.

 HOW TO RUN IT
 -------------
 Connect to the solution's database in SQL Server Management Studio, Azure Data Studio or sqlcmd,
 as its administrator (db_owner). The script records its progress in extended properties on the
 database, which needs that permission.

 4. Dry run. Set @CutoffUtc, leave @DryRun = 1 and run the script. It changes nothing, and returns:
      - how many hits it would repair, their average load time now and after the repair, and how
        many hits it leaves alone, and why;
      - the median load time per hour for the 24 hours either side of @CutoffUtc.
    Old times are about ten times smaller than new ones, so the median jumps at the hour the fixed
    importer took over, and it should jump at @CutoffUtc.
      - If it jumps earlier, the importer had a backlog when you upgraded (it was stopped, failing
        or catching up), and the fixed build imported page views from before the upgrade. Move
        @CutoffUtc back to the start of the hour where the jump is.
      - If it jumps later, the old build was still importing after @CutoffUtc. Move it forward.
    The dry run reads the whole table, which takes a few minutes on a large one.
 5. Repair. Set @DryRun = 0 and run the script again. It works through dbo.hits in batches of
    @BatchSize ids, each in its own short transaction, and reports progress every 10 seconds. The
    importers and the web app can keep running: each batch locks at most @BatchSize rows, below the
    5,000 at which SQL Server would lock the whole table, for a fraction of a second.
 6. Refresh the Power BI datasets that read this database. The Web Activity page reads dbo.hits
    directly, so it shows the repaired times straight away. If you installed the optional
    dbo.hits_PBI table (Resources/Under Development), it holds copies of the old times; copy the
    repaired ones into it:
        UPDATE p SET p.page_load_time = h.page_load_time
        FROM dbo.hits_PBI AS p INNER JOIN dbo.hits AS h ON h.id = p.hits_id
        WHERE p.page_load_time <> h.page_load_time;

 IT RUNS ONCE
 ------------
 Multiplying by 10 twice would make every repaired time ten times too large, so the script records
 what it has done in extended properties on the database:
     SELECT name, value FROM sys.extended_properties
     WHERE class = 0 AND name LIKE N'PageLoadTimeRepair.%';
 Once PageLoadTimeRepair.CompletedUtc is there, the script refuses to run again. A backup restored
 later carries the properties it was taken with, so it is still right about its own rows.
 - Extended properties need no schema change: no table, column or procedure is added.
 - Each batch updates its hits and the progress properties in the same transaction, so the two
   can't disagree. The progress property is written first, so if a cancelled batch's transaction
   were ever committed by hand, the worst case is a batch left unrepaired, never one repaired twice.
 - They are properties of the database rather than of dbo.hits. Changing a property of dbo.hits
   takes a schema-modification lock on the table, which would stop the importer and the web app
   reading it for every batch.
 - A run that finds nothing to repair records nothing, so you can run it again with another
   @CutoffUtc.

 IF IT STOPS PART-WAY
 --------------------
 A lost connection, a cancelled query or an error undoes only the batch in progress. Run the script
 again with the same @CutoffUtc and @DryRun = 0, and it carries on after the last batch it
 completed. After cancelling it in SSMS, run ROLLBACK if the window still has a transaction open:
 the script won't start while one is.

 HOW LONG IT TAKES
 -----------------
 Measured on a developer machine (SQL Server 2019 LocalDB, a synthetic dbo.hits with the product's
 columns and indexes, 40% of hits with a measured load time, batches of 4,000):
     hits in dbo.hits     dry run      repair
            1,000,000        3 s          9 s
           10,000,000        8 s         42 s
          100,000,000    ~1.5 min      ~7 min    (extrapolated)
 The repair writes about 130 bytes of transaction log per repaired hit: about 0.5 GB per 10 million
 hits when 40% of them have a measured time. On Azure SQL Database the service tier's log rate
 limit usually decides how long it takes: at 5 MB/s of log, 100 million hits (40 million repaired)
 take about 17 minutes; at 1 MB/s, about an hour and a half. It needs no maintenance window, and it
 can be stopped and resumed at any time.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET DEADLOCK_PRIORITY LOW;   -- If a batch deadlocks with the importer, the batch gives way and is retried.

-- ===== SETTINGS ====================================================================================
DECLARE @CutoffUtc       datetime = NULL;   -- REQUIRED, in UTC, as 'yyyy-mm-ddThh:mi:ss'. See step 2.
DECLARE @DryRun          bit      = 1;      -- 1 = report only (the default). 0 = repair.
DECLARE @LastLegacyHitId int      = NULL;   -- Optional. See step 3.
DECLARE @BatchSize       int      = 4000;   -- hits.id values per transaction.
-- ===================================================================================================

DECLARE @PropCutoff    sysname = N'PageLoadTimeRepair.CutoffUtc',
        @PropLastHit   sysname = N'PageLoadTimeRepair.LastHitId',
        @PropDoneUpTo  sysname = N'PageLoadTimeRepair.RepairedUpToHitId',
        @PropRows      sysname = N'PageLoadTimeRepair.RowsRepaired',
        @PropStarted   sysname = N'PageLoadTimeRepair.StartedUtc',
        @PropCompleted sysname = N'PageLoadTimeRepair.CompletedUtc';

DECLARE @msg nvarchar(2000), @openTransactions int = @@TRANCOUNT, @nowUtc datetime2(0) = SYSUTCDATETIME();

IF @openTransactions > 0
BEGIN
    RAISERROR(N'This connection has an open transaction (@@TRANCOUNT = %d), probably left by a cancelled run. Run ROLLBACK, then run the script again.', 16, 1, @openTransactions);
    RETURN;
END;

IF OBJECT_ID(N'dbo.hits', N'U') IS NULL OR COL_LENGTH(N'dbo.hits', N'page_load_time') IS NULL
BEGIN
    RAISERROR(N'dbo.hits.page_load_time was not found. Connect to the Microsoft 365 Analytics Insights database and run the script again.', 16, 1);
    RETURN;
END;

-- What an earlier run recorded, if anything.
DECLARE @prevCutoff datetime, @lastHitId int, @doneUpTo int, @rows bigint, @startedUtc datetime2(0), @completedUtc datetime2(0), @props int;
SELECT @props = COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name LIKE N'PageLoadTimeRepair.%';
SELECT @prevCutoff   = CAST(value AS datetime)     FROM sys.extended_properties WHERE class = 0 AND name = @PropCutoff;
SELECT @lastHitId    = CAST(value AS int)          FROM sys.extended_properties WHERE class = 0 AND name = @PropLastHit;
SELECT @doneUpTo     = CAST(value AS int)          FROM sys.extended_properties WHERE class = 0 AND name = @PropDoneUpTo;
SELECT @rows         = CAST(value AS bigint)       FROM sys.extended_properties WHERE class = 0 AND name = @PropRows;
SELECT @startedUtc   = CAST(value AS datetime2(0)) FROM sys.extended_properties WHERE class = 0 AND name = @PropStarted;
SELECT @completedUtc = CAST(value AS datetime2(0)) FROM sys.extended_properties WHERE class = 0 AND name = @PropCompleted;

IF @completedUtc IS NOT NULL
BEGIN
    SET @msg = CONCAT(N'The page load time repair has already run on this database. It completed at ', CONVERT(varchar(19), @completedUtc, 126),
        N' UTC with @CutoffUtc ''', CONVERT(varchar(19), @prevCutoff, 126), N''' and repaired ', FORMAT(@rows, 'N0', 'en-US'),
        N' hits. Running it again would multiply their load times by 10 a second time, so it stops here without changing anything.');
    RAISERROR(N'%s', 16, 1, @msg);
    RETURN;
END;

IF @props > 0 AND (@prevCutoff IS NULL OR @lastHitId IS NULL OR @doneUpTo IS NULL OR @rows IS NULL)
BEGIN
    RAISERROR(N'Some PageLoadTimeRepair.* extended properties are missing from this database, so the script can''t tell how far an earlier run got. Nothing was changed. Check them with: SELECT name, value FROM sys.extended_properties WHERE class = 0 AND name LIKE N''PageLoadTimeRepair.%%'';', 16, 1);
    RETURN;
END;

IF @prevCutoff IS NOT NULL AND (@CutoffUtc IS NULL OR @CutoffUtc <> @prevCutoff)
BEGIN
    SET @msg = CONCAT(N'A repair with @CutoffUtc ''', CONVERT(varchar(19), @prevCutoff, 126), N''' started at ', CONVERT(varchar(19), @startedUtc, 126),
        N' UTC and stopped after hits.id ', FORMAT(@doneUpTo, 'N0', 'en-US'), N', having repaired ', FORMAT(@rows, 'N0', 'en-US'),
        N' hits. To finish it, set @CutoffUtc = ''', CONVERT(varchar(19), @prevCutoff, 126), N''' and run the script again. Nothing was changed.');
    RAISERROR(N'%s', 16, 1, @msg);
    RETURN;
END;

IF @CutoffUtc IS NULL
BEGIN
    RAISERROR(N'Set @CutoffUtc to the UTC time from which the fixed importer stored hits, for example ''2026-11-20T14:00:00''. See BEFORE YOU START at the top of the script.', 16, 1);
    RETURN;
END;

IF @CutoffUtc > @nowUtc
BEGIN
    SET @msg = CONCAT(N'@CutoffUtc ''', CONVERT(varchar(19), @CutoffUtc, 126), N''' is in the future (it is now ', CONVERT(varchar(19), @nowUtc, 126),
        N' UTC). Use the time the fixed importer started storing hits.');
    RAISERROR(N'%s', 16, 1, @msg);
    RETURN;
END;

IF @BatchSize IS NULL OR @BatchSize < 1 OR @LastLegacyHitId < 1
BEGIN
    RAISERROR(N'@BatchSize must be at least 1, and @LastLegacyHitId must be NULL or at least 1.', 16, 1);
    RETURN;
END;

IF @prevCutoff IS NOT NULL AND @LastLegacyHitId < @lastHitId
BEGIN
    SET @msg = CONCAT(N'The repair in progress checks hits up to id ', FORMAT(@lastHitId, 'N0', 'en-US'),
        N', and that can''t be lowered once it has started. Set @LastLegacyHitId to NULL to finish it. Nothing was changed.');
    RAISERROR(N'%s', 16, 1, @msg);
    RETURN;
END;

DECLARE @firstId int, @maxId int;
SELECT @firstId = MIN(id), @maxId = MAX(id) FROM dbo.hits;

IF @prevCutoff IS NULL AND @maxId IS NULL
BEGIN
    RAISERROR(N'dbo.hits is empty: there is nothing to repair. Nothing was changed or recorded.', 0, 1) WITH NOWAIT;
    RETURN;
END;

IF @prevCutoff IS NULL
BEGIN
    -- A new run: every hit stored from here on has a higher id, so fixing the last id now keeps the
    -- repair off anything the fixed importer stores while it runs.
    SET @doneUpTo  = ISNULL(@firstId - 1, 0);
    SET @lastHitId = CASE WHEN @LastLegacyHitId < ISNULL(@maxId, 0) THEN @LastLegacyHitId ELSE ISNULL(@maxId, 0) END;
    SET @rows      = 0;
END;

-- ===== DRY RUN =====================================================================================
IF @DryRun IS NULL OR @DryRun <> 0
BEGIN
    SET @msg = CONCAT(N'DRY RUN: nothing will be changed. Set @DryRun = 0 to repair.', CHAR(10),
        N'@CutoffUtc: ', CONVERT(varchar(19), @CutoffUtc, 126), N' UTC. Hits timestamped before it, with a load time above 0 and an id from ',
        FORMAT(@doneUpTo + 1, 'N0', 'en-US'), N' to ', FORMAT(@lastHitId, 'N0', 'en-US'), N', are multiplied by 10.', CHAR(10),
        N'Highest hits.id now: ', FORMAT(ISNULL(@maxId, 0), 'N0', 'en-US'), N' (see step 3 for @LastLegacyHitId).');
    IF @prevCutoff IS NOT NULL
        SET @msg = CONCAT(@msg, CHAR(10), N'A repair with this @CutoffUtc started at ', CONVERT(varchar(19), @startedUtc, 126), N' UTC and stopped after hits.id ',
            FORMAT(@doneUpTo, 'N0', 'en-US'), N', having repaired ', FORMAT(@rows, 'N0', 'en-US'), N' hits. A run with @DryRun = 0 carries on from there.');
    RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;

    SELECT
        [Hits to repair]                       = COUNT_BIG(CASE WHEN h.id > @doneUpTo AND h.id <= @lastHitId AND h.hit_timestamp < @CutoffUtc AND h.page_load_time > 0 THEN 1 END),
        [Their average seconds now]            = CAST(AVG(CASE WHEN h.id > @doneUpTo AND h.id <= @lastHitId AND h.hit_timestamp < @CutoffUtc AND h.page_load_time > 0 THEN h.page_load_time END) AS decimal(12, 3)),
        [Their average seconds after]          = CAST(AVG(CASE WHEN h.id > @doneUpTo AND h.id <= @lastHitId AND h.hit_timestamp < @CutoffUtc AND h.page_load_time > 0 THEN ROUND(h.page_load_time * 10, 2) END) AS decimal(12, 3)),
        [Already repaired by this run]         = COUNT_BIG(CASE WHEN h.id <= @doneUpTo AND h.hit_timestamp < @CutoffUtc AND h.page_load_time > 0 THEN 1 END),
        [Left alone: 0 or NULL, not measured]  = COUNT_BIG(CASE WHEN h.hit_timestamp < @CutoffUtc AND (h.page_load_time IS NULL OR h.page_load_time <= 0) THEN 1 END),
        [Left alone: stored after last id]     = COUNT_BIG(CASE WHEN h.id > @lastHitId AND h.hit_timestamp < @CutoffUtc AND h.page_load_time > 0 THEN 1 END),
        [Left alone: at or after cutoff]       = COUNT_BIG(CASE WHEN h.hit_timestamp >= @CutoffUtc THEN 1 END)
    FROM dbo.hits AS h;

    -- Old times are about a tenth of new ones, so the median jumps at the hour the fixed importer took over.
    WITH measured AS
    (
        SELECT DATEADD(HOUR, DATEDIFF(HOUR, 0, h.hit_timestamp), 0) AS hour_utc, h.page_load_time
        FROM dbo.hits AS h
        WHERE h.hit_timestamp >= DATEADD(HOUR, -24, @CutoffUtc)
          AND h.hit_timestamp <  DATEADD(HOUR, 24, @CutoffUtc)
          AND h.page_load_time > 0
    )
    SELECT DISTINCT
        [Hour (UTC)]       = hour_utc,
        [Hour is]          = CASE WHEN DATEADD(HOUR, 1, hour_utc) <= @CutoffUtc THEN 'before the cutoff'
                                  WHEN hour_utc >= @CutoffUtc THEN 'after the cutoff'
                                  ELSE 'split by the cutoff' END,
        [Measured views]   = COUNT(*) OVER (PARTITION BY hour_utc),
        [Median seconds]   = CAST(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY page_load_time) OVER (PARTITION BY hour_utc) AS decimal(12, 3)),
        [Average seconds]  = CAST(AVG(page_load_time) OVER (PARTITION BY hour_utc) AS decimal(12, 3))
    FROM measured
    ORDER BY [Hour (UTC)];

    RETURN;
END;

-- ===== REPAIR ======================================================================================
DECLARE @runStart datetime2(3) = SYSUTCDATETIME(), @lastReport datetime2(3) = SYSUTCDATETIME(), @startDoneUpTo int = @doneUpTo,
        @hi int, @batchRows int, @newRows bigint, @attempt int, @elapsed int, @fraction float;

IF @prevCutoff IS NULL
BEGIN
    BEGIN TRANSACTION;
        EXEC sys.sp_addextendedproperty @name = @PropCutoff,   @value = @CutoffUtc;
        EXEC sys.sp_addextendedproperty @name = @PropLastHit,  @value = @lastHitId;
        EXEC sys.sp_addextendedproperty @name = @PropDoneUpTo, @value = @doneUpTo;
        EXEC sys.sp_addextendedproperty @name = @PropRows,     @value = @rows;
        EXEC sys.sp_addextendedproperty @name = @PropStarted,  @value = @nowUtc;
    COMMIT TRANSACTION;
    SET @startedUtc = @nowUtc;
END;

SET @msg = CONCAT(N'Repairing hits timestamped before ', CONVERT(varchar(19), @CutoffUtc, 126), N' UTC with an id from ',
    FORMAT(@doneUpTo + 1, 'N0', 'en-US'), N' to ', FORMAT(@lastHitId, 'N0', 'en-US'), N', in batches of ', FORMAT(@BatchSize, 'N0', 'en-US'), N' ids.');
RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;

WHILE @doneUpTo < @lastHitId
BEGIN
    SET @hi = CASE WHEN CAST(@lastHitId AS bigint) - @doneUpTo > @BatchSize THEN @doneUpTo + @BatchSize ELSE @lastHitId END;
    SET @attempt = 0;

    WHILE 1 = 1
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;
                EXEC sys.sp_updateextendedproperty @name = @PropDoneUpTo, @value = @hi;

                UPDATE dbo.hits
                SET page_load_time = ROUND(page_load_time * 10, 2)
                WHERE id > @doneUpTo AND id <= @hi
                  AND hit_timestamp < @CutoffUtc
                  AND page_load_time > 0;
                SET @batchRows = @@ROWCOUNT;

                SET @newRows = @rows + @batchRows;
                EXEC sys.sp_updateextendedproperty @name = @PropRows, @value = @newRows;
            COMMIT TRANSACTION;
            BREAK;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0
                ROLLBACK TRANSACTION;
            SET @attempt += 1;
            IF ERROR_NUMBER() <> 1205 OR @attempt >= 5
            BEGIN
                SET @msg = CONCAT(N'Stopped after hits.id ', FORMAT(@doneUpTo, 'N0', 'en-US'), N' (', FORMAT(@rows, 'N0', 'en-US'),
                    N' hits repaired). Run the script again with the same settings to carry on.');
                RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;
                THROW;
            END;
            WAITFOR DELAY '00:00:01';
        END CATCH;
    END;

    SET @rows = @newRows;
    SET @doneUpTo = @hi;

    IF DATEDIFF(SECOND, @lastReport, SYSUTCDATETIME()) >= 10 OR @doneUpTo >= @lastHitId
    BEGIN
        SET @lastReport = SYSUTCDATETIME();
        SET @elapsed = DATEDIFF(SECOND, @runStart, @lastReport);
        SET @fraction = CAST(@doneUpTo - @startDoneUpTo AS float) / NULLIF(CAST(@lastHitId AS bigint) - @startDoneUpTo, 0);
        SET @msg = CONCAT(CONVERT(varchar(8), @lastReport, 108), N' UTC  up to hits.id ', FORMAT(@doneUpTo, 'N0', 'en-US'), N' of ',
            FORMAT(@lastHitId, 'N0', 'en-US'), N', ', FORMAT(@rows, 'N0', 'en-US'), N' hits repaired',
            CASE WHEN @fraction > 0 AND @fraction < 1
                 THEN CONCAT(N', about ', FORMAT(CEILING(@elapsed * (1 - @fraction) / @fraction / 60.0), 'N0', 'en-US'), N' min left')
                 ELSE N'' END);
        RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;
    END;
END;

IF @rows = 0
BEGIN
    -- Nothing was repaired, so nothing is recorded: the script can run again with another cutoff.
    BEGIN TRANSACTION;
        EXEC sys.sp_dropextendedproperty @name = @PropCutoff;
        EXEC sys.sp_dropextendedproperty @name = @PropLastHit;
        EXEC sys.sp_dropextendedproperty @name = @PropDoneUpTo;
        EXEC sys.sp_dropextendedproperty @name = @PropRows;
        EXEC sys.sp_dropextendedproperty @name = @PropStarted;
    COMMIT TRANSACTION;
    RAISERROR(N'Nothing to repair: no hit with a load time above 0 is timestamped before @CutoffUtc and stored up to the last id. Nothing was changed or recorded.', 0, 1) WITH NOWAIT;
    RETURN;
END;

SET @nowUtc = SYSUTCDATETIME();
EXEC sys.sp_addextendedproperty @name = @PropCompleted, @value = @nowUtc;

SET @msg = CONCAT(N'Done: ', FORMAT(@rows, 'N0', 'en-US'), N' hits repaired. Completion is recorded in PageLoadTimeRepair.CompletedUtc, ',
    N'so the script won''t run again on this database. Now refresh the Power BI datasets (step 6).');
RAISERROR(N'%s', 0, 1, @msg) WITH NOWAIT;
