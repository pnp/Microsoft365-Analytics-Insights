namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Removes the duplicate <c>dbo.online_meetings</c> rows older builds stored, repoints every reference to one
    /// surviving row per meeting, narrows <c>meeting_id</c> from <c>nvarchar(max)</c> to <c>nvarchar(850)</c> and
    /// makes it UNIQUE (<c>UX_online_meetings_meeting_id</c>, IGNORE_DUP_KEY = ON).
    ///
    /// WHY
    ///   The Teams-meeting merge (insert_teams_copilot_events_from_staging_table.sql) compared <c>created</c>
    ///   with itself and never compared <c>meeting_id</c>, and compared datetime with datetime2, so every batch
    ///   (and every retry of it) stored each meeting again. Its link then matched every copy and violated
    ///   copilot_event_meetings' primary key (copilot_chat_id), so the fan-out never persisted - but the copies
    ///   did, and links point at whichever copy existed when their batch ran. Copilot adoption counts meeting
    ///   credits as distinct copilot_event_meetings.meeting_id values, so one meeting split across copies is
    ///   counted once per copy. The merge now matches on meeting_id; this cleans up the rows it already wrote.
    ///   And meeting_id was nvarchar(max), which cannot be an index key, so every existence check in that merge
    ///   was a scan of the whole table.
    ///
    /// CLASSIFICATION: performance-motivated + data repair (it removes rows), so it carries a benchmark.
    ///
    /// WHAT IS ONE MEETING
    ///   meeting_id alone - {user object id}_{meeting thread id}, built by StringUtils.GetOnlineMeetingId. The
    ///   creation time and subject are attributes of a meeting, not its identity: a renamed meeting is the same
    ///   meeting. The merge now matches on meeting_id alone too, so the de-duplication, the merge and the index
    ///   all agree on what a duplicate is.
    ///
    /// SURVIVOR
    ///   Per meeting_id, the lowest id (the oldest copy - the one the merge already links new interactions to on a
    ///   database that still has copies) unless it has no subject (NULL or blank) and another copy has one: a copy
    ///   with a subject wins.
    ///   <c>created</c> is NOT NULL, so every copy has one and it does not decide. A renamed meeting keeps its
    ///   first-seen subject, which is also what the merge keeps from now on (it never updates a stored meeting).
    ///
    /// REFERENCES
    ///   Preflight requires the stock copilot_event_meetings.meeting_id FK to be trusted and enabled before any
    ///   repair; restore it WITH CHECK if schema drift removed or disabled it. References are discovered from
    ///   sys.foreign_keys (today only copilot_event_meetings.meeting_id, ON DELETE CASCADE), and each
    ///   one is moved onto the survivor. copilot_event_meetings' primary key is copilot_chat_id, so one interaction
    ///   cannot be linked to two copies and its repoint cannot collide. Where a PRIMARY KEY or UNIQUE index does
    ///   include the reference, two copies' rows can meet on the survivor. Only in copilot_event_meetings - while it
    ///   has no columns but copilot_chat_id and meeting_id, no table references it and no enabled DELETE trigger is on
    ///   it - and only for a unique index whose key holds both columns, so the colliding rows are the same
    ///   interaction-to-meeting link twice, is the extra row deleted (keeping the one already on the survivor, else
    ///   the one on the oldest copy): that loses nothing. Anywhere else - a table this migration does not own, or
    ///   anything added to the link table - such a collision stops the migration with error 50001, naming the table
    ///   and index but no row. The transaction moving that batch of copies is rolled back, batches committed before it
    ///   stay done, nothing in the table is deleted, and the manual script does not stamp. A DBA merges or removes the
    ///   conflicting rows and runs it again; it carries on with the copies that are left. References that do not
    ///   collide, or have no unique key, just move.
    ///
    /// CONCURRENT WRITER SAFETY
    ///   Stop the old importer for the upgrade and leave it stopped until the matching new binaries are deployed.
    ///   Its old link matches subject and creation time too, so IGNORE_DUP_KEY can skip a renamed meeting's copy
    ///   while that old link finds nothing. Concurrent-write resilience below is not old-binary compatibility.
    ///   Customers do not all stop the importer, and an importer that is still the previous build keeps storing
    ///   copies. So instead of refusing to run (UniqueUrlsFullUrlIndex), each batch of copies is removed in its own
    ///   short transaction that first UPDATEs those rows, then repoints their references, then deletes them. The
    ///   update matters: it X-locks the rows AND dirties their pages, so a concurrent link cannot pass its
    ///   foreign-key check against a copy between the repoint and the delete - without it, ON DELETE CASCADE
    ///   would silently delete that link with the copy. A writer that loses that race gets a foreign-key error and
    ///   its batch is retried, which links to the survivor. Batches run at DEADLOCK_PRIORITY LOW and retry on
    ///   1205 / 1222, so a deadlock with the importer costs the migration a retry, not the importer its batch.
    ///   XACT_ABORT is ON while it runs, so a batch that is cancelled - an SSMS Stop, a client timeout - is rolled
    ///   back rather than left open with its copies NULL and locked; the session's setting is restored at the end.
    ///   A UNIQUE index cannot be built while a duplicate exists, so the mop-up pass runs BEFORE each build
    ///   attempt (the first catches copies stored while the column was being narrowed), and is repeated when a
    ///   concurrent writer slips a copy in before the build (error 1505). The fourth pass is the last resort: it
    ///   and an offline build run in one transaction holding an exclusive table lock, so nothing can be stored in
    ///   between; writers wait for it, as they do for any offline build. Once the index exists there is nothing
    ///   left to mop up: no copy can be stored again.
    ///
    /// INDEX SHAPE
    ///   UNIQUE with IGNORE_DUP_KEY = ON, as IX_urls_full_url: a check-then-insert race - or a previous-build
    ///   importer, whose merge still tries to store every meeting again - skips that one row with a warning
    ///   instead of failing the merge, and the link then finds the single row. Not filtered: SQL Server rejects
    ///   IGNORE_DUP_KEY on a filtered index (error 10618), and a unique index treats NULLs as equal, so where more
    ///   than one row has no meeting_id - which no importer writes; the merge requires one - those rows get a
    ///   placeholder ('unidentified-meeting:' + id) that can never match a real meeting id. Their links and
    ///   subjects are untouched. No INCLUDE: the merge only matches meeting_id and returns id, the clustered key,
    ///   which every non-clustered index already carries.
    ///
    /// WIDTH
    ///   850 characters is the widest indexable Unicode key (1700 bytes). Real ids are about 106 characters (a
    ///   36-character user id, '_', and a 19:meeting_...@thread.v2 thread id). A longer value is trimmed to 850
    ///   first - the merge trims the id it looks up to the same width, so a trimmed row is still found - rather
    ///   than blocking the upgrade over an id no build has produced.
    ///
    /// MEASURED (synthetic data only). LocalDB 2019 is Express (EngineEdition 4): every column change, build and rebuild
    /// here is OFFLINE; its buffer pool is capped and was shared with other agents' databases (it held 0.3-1.9 GB in
    /// all), on a VM whose throttled disk (~500 writes a second) they shared too; and it creates databases with
    /// AUTO_CLOSE ON, so a database is flushed out of memory whenever its last connection closes. CPU, logical reads
    /// and the plans do not depend on any of that; wall-clock time depends on it heavily.
    ///   Replica of a ~200k-user tenant: 2M distinct meeting ids (100k Copilot users x 20 meeting threads), 6M links
    ///   (3 interactions per meeting). Before: 40% of the ids stored 2-4 times = 3.6M rows, nvarchar(max), no index.
    ///   After: upgraded by this script (1.6M copies removed, 1.5M links moved) and NOT rebuilt - the state a customer
    ///   is left in. The merge's two real statements on a staged batch (80% of the interactions in meetings already
    ///   stored, 20% in new ones), rolled back after each run, plan cache cleared; median [min-max] of 9 warm runs
    ///   after a discarded cold one, by the wall clock (compile time and synchronous statistics refreshes included).
    ///
    ///   C - one database measured before, straight after its upgrade, and settled (checkpointed, ghost records
    ///   cleaned up, AUTO_CLOSE off as in production), with nothing else running on the instance:
    ///                                 store new meetings                     link interactions
    ///                                 logical reads / CPU / wall clock       logical reads / CPU / wall clock
    ///      500  before                134,104 / 7.3 s / 7.2 s [7.0-9.3]      142,357 / 7.4 s / 7.1 s [7.0-7.9]
    ///           after, upgraded         3,877 / 0 ms / 5 ms [4-7]              11,116 / 0 ms / 9 ms [9-13]
    ///           after, settled          3,877 / 0 ms / 7 ms [7-8]              11,116 / 15 ms / 15 ms [13-20]
    ///   20,000  before                147,904 / 7.7 s / 10.2 s [7.8-13.8]    444,232 / 8.4 s / 21.2 s [7.9-27.8]
    ///           after, upgraded       118,390 / 1.0 s / 10.9 s [1.6-17.7]    374,584 / 0.9 s / 11.8 s [0.9-41.0]
    ///           after, settled        118,390 / 0.25 s / 1.7 s [0.29-14.6]   374,584 / 1.1 s / 18.8 s [0.70-41.7]
    ///
    ///   A and B - two more databases upgraded the same way, measured at 20,000 while other agents loaded the disk;
    ///   B again once its ghost records were cleaned up, A again after ALTER INDEX ALL ON dbo.online_meetings REBUILD,
    ///   B and rebuilt A back to back, and B interleaved with C before its upgrade (two rounds of 4 warm runs each):
    ///                                 store new meetings                     link interactions
    ///           before (A)            147,744 / 9.0 s / 10.4 s [9.8-15.0]    443,909 / 9.5 s / 20.0 s [8.0-30.6]
    ///           A as upgraded         118,347 / 1.0 s / 5.3 s [0.3-20.5]     374,367 / 1.0 s / 9.0 s [0.7-57.5]
    ///           B as upgraded         118,349 / 1.0 s / 18.4 s [1.8-21.9]    374,424 / 1.1 s / 24.6 s [2.3-65.2]
    ///           B, ghosts cleaned     118,349 / 0.9 s / 20.0 s [1.1-27.2]    374,424 / 1.0 s / 6.1 s [1.8-18.5]
    ///           A rebuilt             118,347 / 0.3 s / 2.8 s [0.7-17.5]     374,367 / 1.1 s / 12.0 s [0.8-21.4]
    ///           A rebuilt, disk quiet 118,347 / 0.3 s / 0.33 s [0.29-5.5]    374,367 / 0.7 s / 1.0 s [0.71-49.7]
    ///           B, paired             118,349 / 0.8 s / 17.7 s [1.1-35.5]    374,424 / 1.1 s / 41.3 s [3.8-60.4]
    ///           A rebuilt, paired     118,347 / 1.0 s / 11.3 s [1.7-62.2]    374,367 / 1.7 s / 40.9 s [1.0-83.7]
    ///           C before, interleaved 147,562 / 9.0 s / 11.3 s [8.4-14.7]    443,746 / 9.0 s / 8.7 s [8.2-15.1]
    ///           B, interleaved        118,349 / 0.3 s / 0.38 s [0.28-5.5]    374,424 / 0.8 s / 0.84 s [0.72-41.8]
    ///   (Their 500-row runs matched C's: 3,877 reads in 5-7 ms and 11,116 in 10-14 ms.)
    ///
    ///   What the index changes, in every series: before, both statements scan the whole clustered index and hash 3.6M
    ///   nvarchar(max) ids - 7-10 s of CPU whatever the batch size. After, 500 rows seek UX_online_meetings_meeting_id:
    ///   7.1-7.5 s become 5-15 ms. At 20,000 the optimizer prefers one ordered scan of that 462 MB index and a merge join
    ///   to 20,000 seeks, so logical reads fall only 16-20%, and the dependable gain is CPU: 0.25-1.7 s instead of
    ///   7.7-9.5 s (on C, 7-31x less). At 20,000 the rest of the wall clock is disk, and the index does not remove it:
    ///   - Storing the meetings reads the whole index. It is fast while the index is in memory (B interleaved 0.38 s,
    ///     rebuilt A on a quiet disk 0.33 s, C settled 1.7 s with part of it re-read; 10.2-11.3 s before) and no faster
    ///     than before when it is not: straight after the upgrade (C 10.9 s), or with the buffer pool contended (A and
    ///     B 2.8-20.0 s, re-reading up to ~60k pages a run, against 10.4 s before with its 1.0 GB table in memory).
    ///   - Linking the interactions writes 20,000 rows into copilot_event_meetings' three indexes (and the harness
    ///     rolls them back after every run): the same write before and after. On C its slow runs - 6 of 9 settled,
    ///     14-42 s - read almost nothing from disk and waited on WRITELOG and PAGEIOLATCH_EX, as 8 of 9 before runs
    ///     did for 13-26 s on top of their 8 s of CPU. So C's link median moved only from 21.2 s to 18.8 s, and its
    ///     slowest run went from 28 s to 42 s; interleaved, the medians were 8.7 s and 0.84 s.
    ///   - Rebuilding online_meetings made no difference: see the paired rows, and B not rebuilt was as fast as A
    ///     rebuilt.
    ///   So the evidence is CPU, logical reads and the plans. The wall clock improves at 500 rows every time; at 20,000
    ///   it improves when the disk is not the bottleneck, and otherwise is no better - in single runs worse - than
    ///   before on this VM.
    ///   The unchanged earlier merge on the new table also seeks, but needs a key lookup per row for created and name
    ///   (5,049 reads at 500).
    ///
    ///   Upgrade, logged on 3.6M rows (1.6M copies, 1.5M links moved) with this script: 287 s on a quiet instance (C:
    ///   finding the copies 27 s, removing them 131 s - ~12,200 a second - the column change 71 s, the index 15 s), and
    ///   345 s and 643 s on a busy one (removal 198 s and 509 s, the column change 59 s and 80 s, the index 29 s and
    ///   16 s). Earlier runs, before the XACT_ABORT lines were added (they change no work): 207 s on a quiet disk
    ///   (removal 90 s, ~18,000 a second; column change 67 s; index 17 s); 1M rows with 445k copies 47 s; 2M rows
    ///   without copies 88 s. With the disk saturated by other work the removal fell to 45-64 copies a second (two
    ///   runs, both stopped; 1.6M copies would take 7-10 hours at that rate).
    ///   Space: the index adds ~240 bytes a row (462 MB per 2M). The column change and the deletes leave
    ///   online_meetings' clustered index at 1.2-1.6 GB for 2M meetings straight after the upgrade (pages 69-84% full,
    ///   0.1-1.5M ghost records) and 1.16 GB once ghost cleanup has run (85% full), until it is rebuilt: 579 MB after
    ///   ALTER INDEX ALL ... REBUILD, which took 61 s offline. Moving the links grows copilot_event_meetings'
    ///   IX_meeting_id by half (151 -> 225 MB, pages 67% full), +17% for the table.
    ///
    /// The EF model is unchanged (meeting_id stays an unbounded string in the model, as urls.full_url does after
    /// ShrinkUrlsFullUrlColumn), so the .resx snapshot is a byte-identical copy of its predecessor's and the
    /// manual script stamps __MigrationHistory by copying the predecessor's row.
    /// </summary>
    public partial class UniqueOnlineMeetingIds : DbMigration
    {
        /// <summary>
        /// The whole upgrade. A constant so the manual upgrade script and the tests run exactly this SQL.
        /// Idempotent, guarded and resumable; ONLINE where the edition supports it (attempted through
        /// sp_executesql, where the "Enterprise only" error is catchable) with an offline fallback.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;
-- A cancelled or timed-out batch (SSMS Stop, a client timeout) ends without running CATCH. XACT_ABORT ON makes SQL
-- Server roll back the batch transaction that was open then, instead of leaving it open - its copies already NULL and
-- locked - for SSMS to offer to commit. The session's own setting is put back at the end.
DECLARE @xactAbortWasOff bit = CASE WHEN (@@OPTIONS & 16384) = 0 THEN 1 ELSE 0 END;
SET XACT_ABORT ON;

DECLARE @migration nvarchar(100) = N'UniqueOnlineMeetingIds';
DECLARE @start datetime2(7) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(7);
DECLARE @msg nvarchar(2000);
DECLARE @batch int = 2000;
DECLARE @maxPasses int = 4;
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8) THEN 1 ELSE 0 END;
DECLARE @onlineDone bit;
DECLARE @sql nvarchar(max);
DECLARE @rows bigint;
DECLARE @total bigint;
DECLARE @step nvarchar(40) = N'preflight', @target nvarchar(600) = N'dbo.online_meetings';
DECLARE @pass int = 0, @b int = -1;
DECLARE @lastProgress datetime2(7), @removeStart datetime2(7), @elapsedMs bigint;
DECLARE @batchRemoved bigint, @batchRepointed bigint, @batchPruned bigint;

BEGIN TRY
IF OBJECT_ID(N'dbo.online_meetings', N'U') IS NULL OR COL_LENGTH(N'dbo.online_meetings', N'meeting_id') IS NULL
BEGIN
    RAISERROR('UniqueOnlineMeetingIds: dbo.online_meetings.meeting_id does not exist; nothing to do.', 0, 1) WITH NOWAIT;
END
ELSE IF EXISTS (
    SELECT 1
    FROM sys.indexes AS i
    INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
    INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID(N'dbo.online_meetings') AND i.name = N'UX_online_meetings_meeting_id'
      AND c.name = N'meeting_id' AND i.is_unique = 1 AND i.ignore_dup_key = 1 AND i.has_filter = 0 AND i.is_disabled = 0)
BEGIN
    -- Already applied. Falls through rather than RETURNing, so the manual script still reaches its stamp.
    RAISERROR('UniqueOnlineMeetingIds: UX_online_meetings_meeting_id is already UNIQUE with IGNORE_DUP_KEY = ON; nothing to do.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; '
        + CASE WHEN @canOnline = 1 THEN N'the column change and index build are attempted ONLINE, with an offline fallback.'
               ELSE N'ONLINE is not supported on this edition, so the column change and index build lock dbo.online_meetings while they run.' END;
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    DECLARE @typeName sysname, @maxLength smallint, @isNullable bit, @collation sysname;
    SELECT @typeName = t.name, @maxLength = c.max_length, @isNullable = c.is_nullable, @collation = c.collation_name
    FROM sys.columns AS c
    INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.online_meetings') AND c.name = N'meeting_id';
    DECLARE @narrowed bit = CASE WHEN @typeName = N'nvarchar' AND @maxLength = 1700 THEN 1 ELSE 0 END;

    -- The stock link must have an enabled FK: the per-batch row locks rely on FK checks to exclude late links.
    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys AS fk
        INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        WHERE fk.parent_object_id = OBJECT_ID(N'dbo.copilot_event_meetings')
          AND fk.referenced_object_id = OBJECT_ID(N'dbo.online_meetings')
          AND fkc.parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.copilot_event_meetings'), N'meeting_id', 'ColumnId')
          AND fkc.referenced_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.online_meetings'), N'id', 'ColumnId')
          AND fk.is_disabled = 0 AND fk.is_not_trusted = 0
          AND (SELECT COUNT(*) FROM sys.foreign_key_columns AS fc WHERE fc.constraint_object_id = fk.object_id) = 1)
        THROW 50002, 'UniqueOnlineMeetingIds: expected trusted, enabled foreign key from dbo.copilot_event_meetings.meeting_id to dbo.online_meetings.id is missing. Restore the relationship WITH CHECK before retrying; no meeting rows were changed.', 1;

    IF @narrowed = 0
    BEGIN
        SET @step = N'statistics';
        SET @total = 0;
        DECLARE @statsName sysname;
        DECLARE user_stats CURSOR LOCAL FAST_FORWARD FOR
            SELECT s.name FROM sys.stats AS s
            WHERE s.object_id = OBJECT_ID(N'dbo.online_meetings') AND s.user_created = 1
              AND EXISTS (SELECT 1 FROM sys.stats_columns AS sc
                          WHERE sc.object_id = s.object_id AND sc.stats_id = s.stats_id
                            AND sc.column_id = COLUMNPROPERTY(s.object_id, N'meeting_id', 'ColumnId'));
        OPEN user_stats;
        FETCH NEXT FROM user_stats INTO @statsName;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @sql = N'DROP STATISTICS [dbo].[online_meetings].' + QUOTENAME(@statsName) + N';';
            EXEC sp_executesql @sql;
            SET @total = @total + 1;
            FETCH NEXT FROM user_stats INTO @statsName;
        END
        CLOSE user_stats;
        DEALLOCATE user_stats;
        SET @msg = N'[' + CONVERT(nvarchar(33), SYSUTCDATETIME(), 126) + N'Z] ' + @migration
            + N': stage=statistics outcome=Succeeded dropped=' + CAST(@total AS nvarchar(20))
            + N' elapsedMs=' + CAST(DATEDIFF_BIG(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20))
            + N'; user-created statistics on meeting_id are removed before repair and can be recreated after the upgrade.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    /* 1. Trim any id longer than the index can hold. Real ids are ~106 characters; the merge trims the id it
          looks up to the same 850, so a trimmed row is still found. */
    IF @narrowed = 0
    BEGIN
        SET @step = N'trim';
        SET @msg = N'[' + CONVERT(nvarchar(33), SYSUTCDATETIME(), 126) + N'Z] ' + @migration + N': starting step=trim.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @total = 0;
        SET @rows = 1;
        WHILE @rows > 0
        BEGIN
            UPDATE TOP (2000) dbo.online_meetings SET meeting_id = LEFT(meeting_id, 850) WHERE DATALENGTH(meeting_id) > 1700;
            SET @rows = @@ROWCOUNT;
            SET @total = @total + @rows;
        END
        IF @total > 0
        BEGIN
            SET @msg = @migration + N': trimmed ' + CAST(@total AS nvarchar(20)) + N' meeting id(s) longer than 850 characters.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
    END

    /* 2. Everything that references a meeting row, and the statements that move a batch of copies' references
          onto their survivor. Discovered, not hard-coded. Dynamic SQL is schema-qualified and quoted.
          kind: 0 moves references, 1 deletes rows that would collide (the link table only), 2 stops on them. */
    SET @step = N'references';
    DECLARE @work TABLE (seq int IDENTITY(1,1) PRIMARY KEY, kind tinyint NOT NULL, target nvarchar(600) NOT NULL, stmt nvarchar(max) NOT NULL);
    DECLARE @fkObject int, @fkColumn int, @sch sysname, @tbl sysname, @col sysname, @qualified nvarchar(600);
    DECLARE @ixId int, @ixName sysname, @others nvarchar(max), @canPrune bit, @ranked nvarchar(max), @tableText nvarchar(max), @indexText nvarchar(max);

    DECLARE refs CURSOR LOCAL FAST_FORWARD FOR
        SELECT DISTINCT fkc.parent_object_id, fkc.parent_column_id,
               OBJECT_SCHEMA_NAME(fkc.parent_object_id), OBJECT_NAME(fkc.parent_object_id), c.name
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        INNER JOIN sys.columns AS c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
        WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.online_meetings');
    OPEN refs;
    FETCH NEXT FROM refs INTO @fkObject, @fkColumn, @sch, @tbl, @col;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @qualified = QUOTENAME(@sch) + N'.' + QUOTENAME(@tbl);
        -- Rows that would collide are deleted only from the importer's own link table, and only while nothing can
        -- depend on them: it holds just its two columns, no table references it and no DELETE trigger would run -
        -- and, below, only for a unique index whose key holds both columns, so the colliding rows are the same
        -- interaction-to-meeting link twice. Anything else may hold data nobody can recreate, so a collision there
        -- stops the batch (error 50001) for a DBA to resolve, and nothing is deleted.
        SET @canPrune = CASE WHEN @fkObject = OBJECT_ID(N'dbo.copilot_event_meetings')
                              AND NOT EXISTS (SELECT 1 FROM sys.columns AS c3
                                              WHERE c3.object_id = @fkObject AND c3.name NOT IN (N'copilot_chat_id', N'meeting_id'))
                              AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys AS fk2 WHERE fk2.referenced_object_id = @fkObject)
                              AND NOT EXISTS (SELECT 1 FROM sys.triggers AS tr
                                              INNER JOIN sys.trigger_events AS te ON te.object_id = tr.object_id
                                              WHERE tr.parent_id = @fkObject AND tr.is_disabled = 0 AND te.type_desc = N'DELETE')
                             THEN 1 ELSE 0 END;
        SET @tableText = REPLACE(@sch + N'.' + @tbl, N'''', N'''''');

        -- Collisions: every PRIMARY KEY / UNIQUE index whose key includes the reference. Rows that would meet on
        -- the survivor rank the one already there first, else the one on the oldest copy. Ranked through the table
        -- itself, so no particular primary key shape is needed.
        DECLARE uix CURSOR LOCAL FAST_FORWARD FOR
            SELECT i.index_id, i.name
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            WHERE i.object_id = @fkObject AND i.is_unique = 1 AND i.has_filter = 0 AND i.is_disabled = 0
              AND ic.column_id = @fkColumn AND ic.is_included_column = 0;
        OPEN uix;
        FETCH NEXT FROM uix INTO @ixId, @ixName;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            -- Plain concatenation, not FOR XML: XML methods need QUOTED_IDENTIFIER ON and sqlcmd defaults it OFF.
            SET @others = N'';
            SELECT @others = @others + N', t.' + QUOTENAME(c2.name)
            FROM sys.index_columns AS ic2
            INNER JOIN sys.columns AS c2 ON c2.object_id = ic2.object_id AND c2.column_id = ic2.column_id
            WHERE ic2.object_id = @fkObject AND ic2.index_id = @ixId AND ic2.is_included_column = 0 AND ic2.column_id <> @fkColumn
            ORDER BY ic2.key_ordinal;

            SET @ranked = N'
;WITH ranked AS (
    SELECT ROW_NUMBER() OVER (
        PARTITION BY ISNULL((SELECT r.keep_id FROM #om_remap AS r WHERE r.old_id = t.' + QUOTENAME(@col) + N'), t.' + QUOTENAME(@col) + N')' + @others + N'
        ORDER BY CASE WHEN EXISTS (SELECT 1 FROM #om_remap AS r2 WHERE r2.old_id = t.' + QUOTENAME(@col) + N') THEN 1 ELSE 0 END, t.' + QUOTENAME(@col) + N') AS rn
    FROM ' + @qualified + N' AS t
    WHERE t.' + QUOTENAME(@col) + N' IN (SELECT r3.old_id FROM #om_remap AS r3 WHERE r3.batch_no = @b
                                         UNION ALL
                                         SELECT r4.keep_id FROM #om_remap AS r4 WHERE r4.batch_no = @b)
)';
            IF @canPrune = 1 AND EXISTS (SELECT 1 FROM sys.index_columns AS ic3
                                         INNER JOIN sys.columns AS c4 ON c4.object_id = ic3.object_id AND c4.column_id = ic3.column_id
                                         WHERE ic3.object_id = @fkObject AND ic3.index_id = @ixId AND ic3.is_included_column = 0
                                           AND ic3.key_ordinal > 0 AND c4.name = N'copilot_chat_id')
                INSERT INTO @work (kind, target, stmt)
                VALUES (1, @qualified, @ranked + N'
DELETE FROM ranked WHERE rn > 1;');
            ELSE
            BEGIN
                -- Names only, never row values: the message lands in installer, sqlcmd and SQL Agent logs.
                SET @indexText = REPLACE(@ixName, N'''', N'''''');
                INSERT INTO @work (kind, target, stmt)
                VALUES (2, @qualified, N'
DECLARE @n bigint;' + @ranked + N'
SELECT @n = COUNT_BIG(*) FROM ranked WHERE rn > 1;
IF @n > 0
BEGIN
    DECLARE @m nvarchar(2048) = N''UniqueOnlineMeetingIds: '' + CAST(@n AS nvarchar(20)) + N'' row(s) in ' + @tableText
        + N' reference copies of the same meeting and would collide on its unique index ' + @indexText
        + N' once moved onto the surviving meeting row. This migration never deletes rows from that table, so it has stopped:'
        + N' the transaction moving this batch of copies was rolled back, and batches committed before it stay done.'
        + N' Merge or remove the conflicting rows in ' + @tableText + N' so that each meeting keeps one row per key,'
        + N' then run the migration again; it carries on with the copies that are left.'';
    THROW 50001, @m, 1;
END');
            END

            FETCH NEXT FROM uix INTO @ixId, @ixName;
        END
        CLOSE uix;
        DEALLOCATE uix;

        INSERT INTO @work (kind, target, stmt)
        VALUES (0, @qualified, N'
UPDATE t SET t.' + QUOTENAME(@col) + N' = r.keep_id
FROM ' + @qualified + N' AS t
INNER JOIN #om_remap AS r ON r.old_id = t.' + QUOTENAME(@col) + N'
WHERE r.batch_no = @b;');

        FETCH NEXT FROM refs INTO @fkObject, @fkColumn, @sch, @tbl, @col;
    END
    CLOSE refs;
    DEALLOCATE refs;

    IF OBJECT_ID(N'tempdb..#om_remap') IS NOT NULL DROP TABLE #om_remap;
    CREATE TABLE #om_remap (old_id int NOT NULL PRIMARY KEY, keep_id int NOT NULL, batch_no int NOT NULL);
    CREATE INDEX IX_om_remap_batch ON #om_remap (batch_no) INCLUDE (keep_id);

    DECLARE @done bit = 0, @built bit, @placeholdersChecked bit = 0, @locked bit, @own bit;
    DECLARE @lastBatch int, @retry int, @batchDone bit, @w int, @lastWork int, @kind tinyint, @stmt nvarchar(max);
    DECLARE @copies bigint, @removed bigint, @repointed bigint, @pruned bigint;
    SELECT @lastWork = ISNULL(MAX(seq), 0) FROM @work;

    WHILE @done = 0
    BEGIN
        SET @pass = @pass + 1;
        SET @b = -1;
        SET @target = N'dbo.online_meetings';
        SET @built = 0;
        -- Last resort, for a writer that keeps storing copies faster than a pass removes them: one more pass and
        -- an offline build inside one transaction holding an exclusive table lock, so nothing can be stored in
        -- between. Writers wait meanwhile, as they do for any offline index build.
        SET @locked = CASE WHEN @narrowed = 1 AND @pass >= @maxPasses THEN 1 ELSE 0 END;
        SET @own = 1 - @locked;

        BEGIN TRY
            IF @locked = 1
            BEGIN
                SET @step = N'lock';
                RAISERROR('UniqueOnlineMeetingIds: meetings keep being stored again; locking dbo.online_meetings for a final pass and an offline build.', 0, 1) WITH NOWAIT;
                SET DEADLOCK_PRIORITY HIGH;
                BEGIN TRANSACTION;
                SELECT @rows = COUNT_BIG(*) FROM dbo.online_meetings WITH (TABLOCKX, HOLDLOCK) WHERE id < 0;
            END

            /* 3. De-duplicate. Pass 1 is the bulk pass; every later pass mops up copies a concurrent writer stored
                  since the previous one. Grouped exactly as the unique index will compare them. A hash aggregate
                  on CHECKSUM (which follows the column's collation) first finds the ids that can be duplicated,
                  so only their rows are sorted - and as nvarchar(850), which every id fits after step 1, because
                  sorting the nvarchar(max) values themselves is far slower. */
            SET @stepStart = SYSUTCDATETIME();
            SET @step = N'find';
            SET @msg = N'[' + CONVERT(nvarchar(33), @stepStart, 126) + N'Z] ' + @migration
                + N': starting step=find pass=' + CAST(@pass AS nvarchar(10)) + N'; scanning dbo.online_meetings for copies.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            TRUNCATE TABLE #om_remap;

            ;WITH suspects AS (
                SELECT CHECKSUM(m.meeting_id) AS h
                FROM dbo.online_meetings AS m
                WHERE m.meeting_id IS NOT NULL
                GROUP BY CHECKSUM(m.meeting_id)
                HAVING COUNT_BIG(*) > 1
            ), ranked AS (
                SELECT m.id,
                       ROW_NUMBER() OVER (PARTITION BY CAST(m.meeting_id AS nvarchar(850))
                                          ORDER BY CASE WHEN NULLIF(LTRIM(RTRIM(m.name)), N'') IS NULL THEN 1 ELSE 0 END, m.id) AS rn,
                       FIRST_VALUE(m.id) OVER (PARTITION BY CAST(m.meeting_id AS nvarchar(850))
                                               ORDER BY CASE WHEN NULLIF(LTRIM(RTRIM(m.name)), N'') IS NULL THEN 1 ELSE 0 END, m.id
                                               ROWS UNBOUNDED PRECEDING) AS keep_id
                FROM dbo.online_meetings AS m
                WHERE m.meeting_id IS NOT NULL
                  AND CHECKSUM(m.meeting_id) IN (SELECT s.h FROM suspects AS s)
            )
            INSERT INTO #om_remap (old_id, keep_id, batch_no)
            SELECT id, keep_id, CAST((ROW_NUMBER() OVER (ORDER BY keep_id, id) - 1) / @batch AS int)
            FROM ranked
            WHERE rn > 1;
            SET @copies = @@ROWCOUNT;

            SET @msg = @migration + N': pass ' + CAST(@pass AS nvarchar(10)) + N' found ' + CAST(@copies AS nvarchar(20))
                + N' duplicate meeting row(s) in ' + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;

            IF @copies > 0
            BEGIN
                SET @removed = 0; SET @repointed = 0; SET @pruned = 0;
                SET @removeStart = SYSUTCDATETIME();
                SET @lastProgress = @removeStart;
                SELECT @lastBatch = MAX(batch_no) FROM #om_remap;
                SET @b = 0;
                IF @own = 1 SET DEADLOCK_PRIORITY LOW;
                WHILE @b <= @lastBatch
                BEGIN
                    SET @retry = 0;
                    SET @batchDone = 0;
                    WHILE @batchDone = 0
                    BEGIN
                        BEGIN TRY
                            SET @batchRemoved = 0; SET @batchRepointed = 0; SET @batchPruned = 0;
                            IF @own = 1 BEGIN TRANSACTION;

                            -- Lock AND dirty the copies first, so no reference to them can be added until this commits.
                            SET @step = N'lock-copies';
                            SET @target = N'dbo.online_meetings';
                            UPDATE m SET m.meeting_id = NULL
                            FROM dbo.online_meetings AS m
                            INNER JOIN #om_remap AS r ON r.old_id = m.id
                            WHERE r.batch_no = @b;

                            SET @w = 1;
                            WHILE @w <= @lastWork
                            BEGIN
                                SELECT @kind = kind, @stmt = stmt, @target = target FROM @work WHERE seq = @w;
                                SET @step = N'repoint';
                                SET @stmt = @stmt + N'; SET @affectedCount = @@ROWCOUNT;';
                                EXEC sp_executesql @stmt, N'@b int, @affectedCount bigint OUTPUT', @b = @b, @affectedCount = @rows OUTPUT;
                                IF @kind = 1 SET @batchPruned = @batchPruned + @rows; ELSE IF @kind = 0 SET @batchRepointed = @batchRepointed + @rows;
                                SET @w = @w + 1;
                            END

                            SET @step = N'delete';
                            SET @target = N'dbo.online_meetings';
                            DELETE m
                            FROM dbo.online_meetings AS m
                            INNER JOIN #om_remap AS r ON r.old_id = m.id
                            WHERE r.batch_no = @b;
                            SET @batchRemoved = @@ROWCOUNT;

                            IF @own = 1 COMMIT TRANSACTION;
                            SET @removed = @removed + @batchRemoved;
                            SET @repointed = @repointed + @batchRepointed;
                            SET @pruned = @pruned + @batchPruned;
                            SET @batchDone = 1;
                        END TRY
                        BEGIN CATCH
                            IF @own = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
                            IF @own = 1 AND ERROR_NUMBER() IN (1205, 1222) AND @retry < 10
                            BEGIN
                                SET @retry = @retry + 1;
                                SET @msg = @migration + N': batch ' + CAST(@b AS nvarchar(10)) + N' gave way to a concurrent writer (error '
                                    + CAST(ERROR_NUMBER() AS nvarchar(10)) + N'); retry ' + CAST(@retry AS nvarchar(10)) + N' of 10.';
                                RAISERROR(@msg, 0, 1) WITH NOWAIT;
                                WAITFOR DELAY '00:00:01';
                            END
                            ELSE
                                THROW;
                        END CATCH
                    END

                    IF DATEDIFF_BIG(MILLISECOND, @lastProgress, SYSUTCDATETIME()) >= 30000 OR @b = @lastBatch
                    BEGIN
                        SET @elapsedMs = DATEDIFF_BIG(MILLISECOND, @removeStart, SYSUTCDATETIME());
                        SET @msg = N'[' + CONVERT(nvarchar(33), SYSUTCDATETIME(), 126) + N'Z] ' + @migration
                            + N': pass ' + CAST(@pass AS nvarchar(10)) + N' removed ' + CAST(@removed AS nvarchar(20))
                            + N' of ' + CAST(@copies AS nvarchar(20)) + N' duplicate row(s), repointed ' + CAST(@repointed AS nvarchar(20))
                            + N' reference(s), pruned ' + CAST(@pruned AS nvarchar(20)) + N' colliding reference(s) in '
                            + CAST(@elapsedMs AS nvarchar(20)) + N'ms; rate='
                            + CAST(CAST(@removed * 1000.0 / CASE WHEN @elapsedMs = 0 THEN 1 ELSE @elapsedMs END AS decimal(20,1)) AS nvarchar(30))
                            + N' copies/s etaSeconds=' + COALESCE(CAST(CAST((@copies - @removed) * 1.0 * @elapsedMs / NULLIF(@removed * 1000.0, 0) AS bigint) AS nvarchar(30)), N'unknown')
                            + N'; outcome=InProgress.';
                        RAISERROR(@msg, 0, 1) WITH NOWAIT;
                        SET @lastProgress = SYSUTCDATETIME();
                    END
                    SET @b = @b + 1;
                END
                IF @own = 1 SET DEADLOCK_PRIORITY NORMAL;
            END

            /* 4. Narrow the column. Rewrites the table; ONLINE where supported, else it locks the table while it
                  runs. The next pass mops up copies stored meanwhile. */
            IF @narrowed = 0
            BEGIN
                SET @stepStart = SYSUTCDATETIME();
                SET @step = N'column';
                SET @target = N'dbo.online_meetings';
                SET @sql = N'ALTER TABLE [dbo].[online_meetings] ALTER COLUMN [meeting_id] nvarchar(850)'
                    + CASE WHEN @collation IS NULL THEN N'' ELSE N' COLLATE ' + @collation END
                    + CASE WHEN @isNullable = 1 THEN N' NULL' ELSE N' NOT NULL' END;
                SET @onlineDone = 0;
                IF @canOnline = 1
                BEGIN
                    BEGIN TRY
                        SET @msg = N'[' + CONVERT(nvarchar(33), SYSUTCDATETIME(), 126) + N'Z] ' + @migration
                            + N': starting step=column mode=ONLINE; short schema locks can still block access to dbo.online_meetings.';
                        RAISERROR(@msg, 0, 1) WITH NOWAIT;
                        DECLARE @online nvarchar(max) = @sql + N' WITH (ONLINE = ON);';
                        EXEC sp_executesql @online;
                        SET @onlineDone = 1;
                    END TRY
                    BEGIN CATCH
                        SET @msg = @migration + N': ONLINE column change unavailable (' + REPLACE(ERROR_MESSAGE(), N'%', N'%%') + N'); retrying offline.';
                        RAISERROR(@msg, 0, 1) WITH NOWAIT;
                    END CATCH
                END
                IF @onlineDone = 0
                BEGIN
                    SET @msg = N'[' + CONVERT(nvarchar(33), SYSUTCDATETIME(), 126) + N'Z] ' + @migration
                        + N': starting step=column mode=OFFLINE; this blocks all access to dbo.online_meetings until the column change finishes.';
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                    SET @sql = @sql + N';';
                    EXEC sp_executesql @sql;
                END
                SET @narrowed = 1;
                SET @msg = @migration + N': meeting_id is now nvarchar(850) ('
                    + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N', '
                    + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms).';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END
            ELSE
            BEGIN
                /* 5. More than one row without a meeting id would break a unique index (NULLs compare equal in it).
                      No importer writes such a row; give any a placeholder no real id can match. */
                IF @placeholdersChecked = 0
                BEGIN
                    SET @step = N'placeholders';
                    IF (SELECT COUNT_BIG(*) FROM dbo.online_meetings WHERE meeting_id IS NULL) > 1
                    BEGIN
                        UPDATE dbo.online_meetings SET meeting_id = N'unidentified-meeting:' + CONVERT(nvarchar(11), id) WHERE meeting_id IS NULL;
                        SET @msg = @migration + N': ' + CAST(@@ROWCOUNT AS nvarchar(20)) + N' row(s) had no meeting id; each now has a placeholder id.';
                        RAISERROR(@msg, 0, 1) WITH NOWAIT;
                    END
                    SET @placeholdersChecked = 1;
                END

                /* 6. The unique index. A copy stored after the pass above fails the build with 1505: mop up and retry. */
                SET @step = N'index';
                SET @target = N'dbo.online_meetings';
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.online_meetings') AND name = N'UX_online_meetings_meeting_id')
                BEGIN
                    RAISERROR('UniqueOnlineMeetingIds: dropping an UX_online_meetings_meeting_id of a different shape.', 0, 1) WITH NOWAIT;
                    DROP INDEX [UX_online_meetings_meeting_id] ON [dbo].[online_meetings];
                END

                SET @stepStart = SYSUTCDATETIME();
                SET @step = N'index';
                SET @target = N'dbo.online_meetings';
                SET @onlineDone = 0;
                IF @canOnline = 1 AND @locked = 0
                BEGIN
                    BEGIN TRY
                        SET @msg = N'[' + CONVERT(nvarchar(33), SYSUTCDATETIME(), 126) + N'Z] ' + @migration
                            + N': starting step=index mode=ONLINE pass=' + CAST(@pass AS nvarchar(10)) + N'.';
                        RAISERROR(@msg, 0, 1) WITH NOWAIT;
                        SET @sql = N'CREATE UNIQUE NONCLUSTERED INDEX [UX_online_meetings_meeting_id] ON [dbo].[online_meetings] ([meeting_id]) WITH (IGNORE_DUP_KEY = ON, ONLINE = ON);';
                        EXEC sp_executesql @sql;
                        SET @onlineDone = 1;
                    END TRY
                    BEGIN CATCH
                        IF ERROR_NUMBER() = 1505
                            THROW;
                        SET @msg = @migration + N': ONLINE index build unavailable (' + REPLACE(ERROR_MESSAGE(), N'%', N'%%') + N'); retrying offline.';
                        RAISERROR(@msg, 0, 1) WITH NOWAIT;
                    END CATCH
                END
                IF @onlineDone = 0
                BEGIN
                    SET @msg = N'[' + CONVERT(nvarchar(33), SYSUTCDATETIME(), 126) + N'Z] ' + @migration
                        + N': starting step=index mode=OFFLINE pass=' + CAST(@pass AS nvarchar(10)) + N'; writes to dbo.online_meetings wait for the build.';
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                    SET @sql = N'CREATE UNIQUE NONCLUSTERED INDEX [UX_online_meetings_meeting_id] ON [dbo].[online_meetings] ([meeting_id]) WITH (IGNORE_DUP_KEY = ON);';
                    EXEC sp_executesql @sql;
                END
                SET @built = 1;
                SET @msg = @migration + N': UNIQUE UX_online_meetings_meeting_id created ('
                    + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N', '
                    + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms).';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END

            IF @locked = 1
            BEGIN
                COMMIT TRANSACTION;
                SET DEADLOCK_PRIORITY NORMAL;
            END
            SET @done = @built;
        END TRY
        BEGIN CATCH
            IF @locked = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            SET DEADLOCK_PRIORITY NORMAL;
            IF ERROR_NUMBER() = 1505 AND @locked = 0
                -- The 1505 text carries the duplicate meeting id (tenant data), so it is not repeated here.
                RAISERROR('UniqueOnlineMeetingIds: a concurrent writer stored a meeting again before the index was built; running another mop-up pass.', 0, 1) WITH NOWAIT;
            ELSE IF ERROR_NUMBER() IN (1205, 1222) AND @locked = 1 AND @pass < @maxPasses + 3
                RAISERROR('UniqueOnlineMeetingIds: the final locked pass gave way to a concurrent writer; retrying it.', 0, 1) WITH NOWAIT;
            ELSE
                THROW;
        END CATCH
    END
    DROP TABLE #om_remap;
END

SET @msg = @migration + N': finished in ' + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
IF @xactAbortWasOff = 1 SET XACT_ABORT OFF;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    SET DEADLOCK_PRIORITY NORMAL;
    SET @msg = N'[' + CONVERT(nvarchar(33), SYSUTCDATETIME(), 126) + N'Z] ' + @migration
        + N': outcome=Failed step=' + @step + N' target=' + @target + N' pass=' + CAST(@pass AS nvarchar(10))
        + N' batch=' + CAST(@b AS nvarchar(10)) + N' sqlError=' + CAST(ERROR_NUMBER() AS nvarchar(10))
        + N' elapsedMs=' + CAST(DATEDIFF_BIG(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20))
        + N'; this transaction rolled back; previously committed batches remain applied. Original SQL error follows.';
    RAISERROR(N'%s', 10, 1, @msg) WITH NOWAIT;
    IF @xactAbortWasOff = 1 SET XACT_ABORT OFF;
    THROW;
END CATCH
";

        /// <summary>
        /// Drops the unique index and widens meeting_id back to nvarchar(max). Removed duplicates are not restored.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.online_meetings') AND name = N'UX_online_meetings_meeting_id')
    DROP INDEX [UX_online_meetings_meeting_id] ON [dbo].[online_meetings];
IF COL_LENGTH(N'dbo.online_meetings', N'meeting_id') = 1700
    ALTER TABLE [dbo].[online_meetings] ALTER COLUMN [meeting_id] nvarchar(max) NULL;
RAISERROR('UniqueOnlineMeetingIds (Down): index dropped and meeting_id widened to nvarchar(max). Removed duplicate rows are not restored.', 0, 1) WITH NOWAIT;
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'UniqueOnlineMeetingIds'. Removes duplicate Teams meeting rows (repointing every reference to one row per meeting), narrows online_meetings.meeting_id to nvarchar(850) and makes it UNIQUE. Stop the old importer until matching new binaries are deployed; concurrent-writer resilience does not make old link SQL compatible. ONLINE where the edition supports it, offline otherwise. The installer forwards live SQL progress. Guarded and resumable: an interrupted run converges on re-run.");

            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'UniqueOnlineMeetingIds'. Drops the unique index and widens meeting_id; removed duplicate rows are not restored.");

            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
