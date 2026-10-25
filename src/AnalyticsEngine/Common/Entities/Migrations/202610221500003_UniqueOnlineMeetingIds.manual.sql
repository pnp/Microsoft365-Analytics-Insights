/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202610221500003_UniqueOnlineMeetingIds
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database BY HAND instead of running the installer. It
   performs the same change as the migration, then stamps __MigrationHistory so EF and the web app Health
   page treat it as applied.

   RUN ORDER
     Run every manual script in migration-id order. The prerequisite is 202610221500002_EventFileExtensionUnicode,
     which must already be stamped; without it this script changes nothing and stops.

   WHAT IT DOES (dbo.online_meetings - the Teams meetings Copilot was used in)
     1. Trims any meeting id longer than 850 characters (none expected; real ids are ~106).
     2. Removes the duplicate rows older builds stored: one row per meeting id survives (the oldest, unless
        only a newer copy has a subject), every reference is moved onto it, then the copies are deleted.
     3. Narrows meeting_id from nvarchar(max) to nvarchar(850).
     4. Creates UX_online_meetings_meeting_id: UNIQUE on meeting_id, IGNORE_DUP_KEY = ON.

   THIS SCRIPT DELETES ROWS - the duplicate meeting rows, which are the defect. Take a backup first.
   Copilot adoption's meeting credits (distinct meetings per user) can go DOWN afterwards: a meeting
   stored three times used to count three times. Ship and run it together with its predecessor,
   202610221500002_EventFileExtensionUnicode.

   STOP THE OLD IMPORTER before upgrading the schema, and deploy the matching new binaries before
   restarting it. Its link matches meeting_id, subject and creation time: a renamed meeting's attempted
   copy is skipped by the new unique index, but the old link then finds no row and silently loses that
   interaction's meeting link. The installer pauses WebJobs across schema work and content deployment.
   By-hand upgrades must keep WEBJOBS_STOPPED=1 until both are complete, then restore its prior value.
   Concurrent-writer resilience is defensive, not permission to run old binaries on the new schema:
   copies are removed in short transactions that lock them before their references move, and mop-up
   passes catch concurrent copies before the index build.

   PREFLIGHT AND DIAGNOSTICS
     Before changing any meeting row, the script requires the trusted, enabled foreign key from
     copilot_event_meetings.meeting_id to online_meetings.id. Restore the relationship WITH CHECK if it
     has drifted. User-created statistics on meeting_id are dropped before repair so ALTER COLUMN is
     not blocked by them; recreate any still needed after the upgrade.
     Timestamped starting lines precede the find scan, column change and index build. Removal progress
     is checked after every batch and emitted after 30 seconds, with rate and ETA, and at pass end.
     A single blocked/slow SQL statement can exceed that interval; its starting line identifies the work.
     Failure diagnostics include step, target, pass, batch, elapsed ms and the original SQL error number;
     THROW preserves that number, including error 50001 for conflicting custom references.

   IF IT STOPS WITH ERROR 50001
     A table that references dbo.online_meetings has a PRIMARY KEY or UNIQUE index including that reference,
     and rows pointing at two copies of one meeting would collide once moved onto the surviving row. The script
     never deletes such rows from a table it does not own, nor from copilot_event_meetings unless they are the
     same interaction-to-meeting link twice and nothing was added to that table (no column, no referencing
     table, no DELETE trigger, no unique index without copilot_chat_id). So it stops and names the table and
     index - no row values. The transaction moving that batch of copies was rolled back, batches committed
     before it stay done, and nothing is stamped. Merge or remove the conflicting rows in the named table so
     each meeting keeps one row per key, then run the script again: it carries on with the copies that are
     left. A database as the product set it up, with nothing added, never stops here.

   RUNTIME (logged on synthetic data, LocalDB, so OFFLINE figures)
     Removing the copies is disk-bound, so allow time in proportion to how many there are:
       quiet disk       ~12,000-18,000 copies a second (3.6M rows: 1.6M copies and their 1.5M links in 90 s
                        in one run, 131 s in another)
       busy disk        ~3,000-8,000 a second (the same 1.6M copies took 198 s in one run, 509 s in another)
       saturated disk   as low as 45 a second (two runs on a disk busy with other work, both stopped:
                        1.6M copies would take about 10 hours at that rate)
     That phase runs as transactions of 2,000 copies each, a fraction of a second apiece on a quiet disk, and
     holds nothing between them, so the importer keeps working. (A batch whose copies have more than ~4,000
     links can escalate to a lock on copilot_event_meetings for that batch.) Stopping part-way is safe:
     finished batches stay done, the interrupted one rolls back - the script sets XACT_ABORT ON, so that holds
     for an SSMS Stop or a client timeout too, not only for ending the session - and a re-run carries on with
     the copies that are left. The clean way to stop is still to end the session (close the connection, or
     Ctrl+C in sqlcmd); after an SSMS Stop, check SELECT @@TRANCOUNT returns 0 before closing the window.
     Only if a previous-build importer keeps storing copies does a last pass lock dbo.online_meetings, for
     itself and an offline index build.
     What DOES block is the column change and the index build. On Standard / Express / Web they run OFFLINE:
     the column change locks dbo.online_meetings against all access and the index build against writes, so the
     importer's Teams meeting step waits for them. Logged with 2M meetings left: the column change 59-80 s, the
     index 15-29 s; with 0.56M, 16 s and 4 s. Plan the maintenance window around them. ONLINE is attempted on
     Enterprise (EngineEdition 3), Azure SQL DB (5) and Azure SQL MI (8).
     Totals logged: 3.6M rows with 1.6M copies 207 s and 287 s on a quiet disk, 345 s and 643 s on a busy one;
     1M rows with 445k copies 47 s; 2M rows, no copies 88 s.

   COUNTING THE COPIES FIRST
     This reads all of dbo.online_meetings once - less work than the migration's first pass, which took 27-29 s
     for 3.6M rows on a quiet or busy disk and about 3 minutes on a saturated one. Two different ids that share
     a checksum count as a copy, so it can over-count slightly.
       SELECT ISNULL(SUM(d.n - 1), 0) AS copies
       FROM (SELECT COUNT_BIG(*) AS n FROM dbo.online_meetings WHERE meeting_id IS NOT NULL
             GROUP BY CHECKSUM(meeting_id) HAVING COUNT_BIG(*) > 1) AS d;

   FREE SPACE
     - The column change and the deletes leave dbo.online_meetings' clustered index at about twice its rebuilt
       size until it is rebuilt (on the replica, for 2M meetings: 1.2-1.6 GB straight after the upgrade, 1.16 GB
       once ghost cleanup has run, against 579 MB rebuilt); an ONLINE column change needs that space while it
       runs too.
     - UX_online_meetings_meeting_id adds about 240 bytes a row (462 MB per 2M meetings).
     - Moving links onto the surviving rows grows copilot_event_meetings' IX_meeting_id by half (151 -> 225 MB
       on the replica; +17% for the table).
     Optional, afterwards, to give that space back (offline on Standard/Express - 61 s for 2M meetings on the
     replica; add WITH (ONLINE = ON) on Enterprise/Azure). On the replica it left the merge's reads unchanged
     and, under the same disk load, did not make it faster:
       ALTER INDEX ALL ON dbo.online_meetings REBUILD;
       ALTER INDEX ALL ON dbo.copilot_event_meetings REBUILD;

   AFTERWARDS
     The importer's Teams meeting step needs far less CPU: on the replica 0.25-1.7 s instead of 7.7-9.5 s for a
     20,000-interaction batch, and milliseconds instead of ~7 s for 500. A large batch's elapsed time is then
     mostly disk - it reads the whole new index (462 MB per 2M meetings) and writes every link - so it gains
     less. Measured on one replica before and after its upgrade (medians): storing the meetings 10.2 s -> 1.7 s,
     linking the interactions 21.2 s -> 18.8 s, with single link runs up to 42 s after against 28 s before.

   SAFETY: idempotent, guarded and resumable; a no-op where the index already exists. Run with sqlcmd -b.
   ===================================================================================================== */
IF OBJECT_ID(N'dbo.__MigrationHistory', N'U') IS NULL
   OR NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202610221500002_EventFileExtensionUnicode')
BEGIN
    RAISERROR('UniqueOnlineMeetingIds: prerequisite migration 202610221500002_EventFileExtensionUnicode is not stamped in __MigrationHistory. Run the manual scripts in migration-id order. Nothing has been changed; NOT stamped.', 16, 1) WITH NOWAIT;
    RETURN;
END;
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

/* Post-flight: SCHEMA only. Never a data-state check - a concurrent importer can always beat one (see
   DenormaliseCopilotChatUserAndTime). Concurrency can't fake the existence and shape of this index. */
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes AS i
    INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
    INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID(N'dbo.online_meetings') AND i.name = N'UX_online_meetings_meeting_id'
      AND c.name = N'meeting_id' AND i.is_unique = 1 AND i.ignore_dup_key = 1)
BEGIN
    RAISERROR('UniqueOnlineMeetingIds: NOT stamped - the schema work did not complete: UX_online_meetings_meeting_id is not a UNIQUE index on dbo.online_meetings.meeting_id with IGNORE_DUP_KEY = ON. Review the errors above, then re-run this script; it resumes where it stopped.', 16, 1) WITH NOWAIT;
    RETURN;
END;

/* Stamp. The EF model is unchanged, so the snapshot is the predecessor's: copy its row. Reached on the
   already-applied path too, and a no-op when already stamped. */
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202610221500003_UniqueOnlineMeetingIds')
BEGIN
    INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
    SELECT N'202610221500003_UniqueOnlineMeetingIds', ContextKey, Model, ProductVersion
    FROM dbo.__MigrationHistory
    WHERE MigrationId = N'202610221500002_EventFileExtensionUnicode';
    RAISERROR('UniqueOnlineMeetingIds: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('UniqueOnlineMeetingIds: already recorded in __MigrationHistory; nothing to do.', 0, 1) WITH NOWAIT;