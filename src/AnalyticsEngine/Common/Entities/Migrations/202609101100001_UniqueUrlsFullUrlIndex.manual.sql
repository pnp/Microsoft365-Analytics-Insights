/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202609101100001_UniqueUrlsFullUrlIndex
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database schema BY HAND instead of running the installer
   (which applies EF migrations automatically). It performs the same schema change as the migration and
   then stamps __MigrationHistory so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web
   app Health page recognise it as applied.

   RUN ORDER
     This is the LAST migration in the release. Its prerequisite is
     202609101000001_RetireUnusedAuditYammerStreamTables, which must already be stamped - the script
     refuses to stamp itself otherwise. Run every manual script in migration-id order.

   WHAT IT DOES
     Makes dbo.urls.full_url UNIQUE so duplicate URL lookups can no longer be created:
       1. Maps every duplicate full_url to a single canonical surviving row (the lowest id).
       2. Prunes rows in child tables that would violate their OWN unique index once repointed - keeping
          the row that already points at the canonical URL. Two tables need this today
          (file_metadata_property_values.IX_url_id_field_id and
          hits_clicked_elements.IX_hit_id_url_id_timestamp) but they are discovered from sys.indexes, not
          hard-coded.
       3. Repoints every reference onto the canonical URL, in batches. References are discovered from
          sys.foreign_keys, plus the legacy non-FK event_meta_sharepoint.url_id where it still exists.
       4. Deletes the duplicate url rows, in batches.
       5. Recreates IX_urls_full_url as UNIQUE with IGNORE_DUP_KEY = ON.

     IGNORE_DUP_KEY = ON is deliberate: a concurrent check-then-insert race in the importer then skips
     only the duplicate URL (as a warning) instead of aborting the whole INSERT statement, so every other
     new URL in the same batch still inserts.

   THIS SCRIPT DELETES ROWS
     Duplicate dbo.urls rows are removed, and so are child rows that would collide on their own unique
     index once repointed. That is the point of the change - the duplicates are the defect - but it is not
     reversible. Take a backup first. The Down path only relaxes the index back to non-unique; it cannot
     restore deleted rows.

   COLLATION
     The database collation is case-insensitive (Latin1_General_CI_AS), so '.../Foo' and '.../foo' are
     treated as the same URL. That matches the existing join semantics used by the staging merges
     (urls.full_url = imports.url), so the constraint agrees with how the importer already looks URLs up.

   RUNTIME
     On a database with no duplicates this is a single GROUP BY over dbo.urls plus the index rebuild.
     Where duplicates exist, the repoint and delete run in batches of 20,000 with live progress
     (RAISERROR ... WITH NOWAIT). The index rebuild is the expensive part on a large urls table.

     ONLINE builds are attempted on Enterprise (EngineEdition 3), Azure SQL DB (5) and Azure SQL MI (8).
     On Standard / Express / Web / LocalDB the rebuild briefly locks dbo.urls, so run a large upgrade in a
     maintenance window with the importer stopped.

   SAFETY
     Idempotent, guarded and resumable throughout - safe to re-run, and a no-op on a database where the
     index is already UNIQUE. Run it with sqlcmd -b so execution stops at the first error.
   ===================================================================================================== */

/* =====================================================================================================
   PRE-FLIGHT - runs BEFORE anything is modified.

   This script DELETES ROWS, and deletion is not reversible. So every prerequisite is checked up front:
   applying it out of order, or against a schema that is not ready for it, must fail having changed
   nothing rather than fail half-way through.
   ===================================================================================================== */
IF OBJECT_ID(N'dbo.__MigrationHistory', N'U') IS NULL
BEGIN
    RAISERROR('UniqueUrlsFullUrlIndex: dbo.__MigrationHistory does not exist - this does not look like an Analytics database. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609101000001_RetireUnusedAuditYammerStreamTables')
   AND NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609101100001_UniqueUrlsFullUrlIndex')
BEGIN
    RAISERROR('UniqueUrlsFullUrlIndex: prerequisite migration 202609101000001_RetireUnusedAuditYammerStreamTables is not stamped in __MigrationHistory. Run the manual scripts in migration-id order. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

IF OBJECT_ID(N'dbo.urls', N'U') IS NULL
BEGIN
    RAISERROR('UniqueUrlsFullUrlIndex: dbo.urls does not exist. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

-- full_url must already be the narrow nvarchar(850) the index keys on. On an older database it is still
-- an (n)varchar(max) LOB, which cannot be indexed at all - so de-duplicating first and only discovering
-- that at the CREATE INDEX would delete rows for nothing.
IF NOT EXISTS (
    SELECT 1
    FROM sys.columns AS c
    INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.urls') AND c.name = N'full_url'
      AND t.name = N'nvarchar' AND c.max_length = 1700 AND c.is_nullable = 0)
BEGIN
    RAISERROR('UniqueUrlsFullUrlIndex: dbo.urls.full_url is not nvarchar(850) NOT NULL, so it cannot be a unique index key. Apply the earlier migrations (ShrinkUrlsFullUrlColumn / UrlFullUrlNvarchar) first. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

RAISERROR('UniqueUrlsFullUrlIndex: pre-flight checks passed.', 0, 1) WITH NOWAIT;
GO

SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'UniqueUrlsFullUrlIndex';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @ix sysname = N'IX_urls_full_url';
DECLARE @batch int = 20000;
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8) THEN 1 ELSE 0 END;
DECLARE @onlineDone bit;
DECLARE @sql nvarchar(max);
DECLARE @rows bigint;
DECLARE @total bigint = 0;

SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index build '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).'
           ELSE N'is not supported on this edition - the rebuild also locks dbo.urls for its duration.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- Unconditional, and deliberately NOT softened on ONLINE-capable editions. ONLINE affects only the index
-- BUILD; the de-duplication that precedes it repoints references and deletes rows across several separately
-- committed statements, so a writer that inserts a reference to a duplicate URL after that table has been
-- repointed but before the URL row is deleted can have its row cascade-deleted (file_metadata_property_values,
-- page_comments, page_likes and copilot_event_files all cascade from urls) or left orphaned (the legacy
-- event_meta_sharepoint reference has no FK at all).
RAISERROR('UniqueUrlsFullUrlIndex: STOP THE IMPORTER before running this. It de-duplicates dbo.urls by repointing references and deleting rows across several committed statements, which is not safe against concurrent writers - an ONLINE index build does not change that.', 0, 1) WITH NOWAIT;

/* -------------------------------------------------------------------------------------------------
   Concurrency check. The warning above used to be the only protection; this turns it into an actual
   gate, because the failure mode is silent DATA LOSS rather than a visible error: a writer that
   inserts a reference to a duplicate URL after that table has been repointed but before the URL row
   is deleted can have its row cascade-deleted (file_metadata_property_values, page_comments,
   page_likes and copilot_event_files all cascade from urls) or left orphaned (event_meta_sharepoint
   has no FK at all).

   Deliberately placed BEFORE any mutation, so aborting here leaves the database completely unchanged.

   Two independent signals, treated differently on purpose:
     1. Write-intent locks held by another session. Real-time, unambiguous evidence of a live writer, so
        this one ABORTS. Read-only sessions (the web app) take S locks and are correctly ignored. Needs
        VIEW DATABASE STATE / VIEW SERVER STATE, so it is wrapped in TRY/CATCH and degrades to a warning
        rather than blocking the upgrade on a permissions error.
     2. Recent rows in the importers' own run-log tables. Only a hint - an importer stopped five minutes
        ago still leaves them - so this one only WARNS. Making it fatal would block legitimate upgrades
        and would repeat the DenormaliseCopilotChatUserAndTime mistake of failing over data state.
        Each table is existence-guarded because which are present depends on how far the database has
        been migrated. Timestamps are compared against both UTC and local now, whichever gives the wider
        window, so a table that stamps local time is still noticed.

   If you need to bypass the check entirely - you have confirmed the importer is stopped, or you are
   running this from an automated harness - either set @skipConcurrencyCheck to 1 below, or set the
   session flag before running the script:
       EXEC sp_set_session_context N'UniqueUrlsFullUrlIndex_SkipConcurrencyCheck', 1;
   ------------------------------------------------------------------------------------------------- */
DECLARE @skipConcurrencyCheck bit =
    ISNULL(TRY_CONVERT(bit, SESSION_CONTEXT(N'UniqueUrlsFullUrlIndex_SkipConcurrencyCheck')), 0);
DECLARE @recentImports bigint = 0;
DECLARE @activeWriters bigint = 0;

IF @skipConcurrencyCheck = 0
BEGIN
    BEGIN TRY
        SELECT @activeWriters = COUNT_BIG(DISTINCT l.request_session_id)
        FROM sys.dm_tran_locks AS l
        WHERE l.resource_database_id = DB_ID()
          AND l.request_session_id <> @@SPID
          AND l.request_mode IN (N'X', N'IX', N'U', N'IU', N'SIX');
    END TRY
    BEGIN CATCH
        SET @msg = @migration + N': WARNING - could not check for concurrent writers ('
            + ERROR_MESSAGE() + N'). This needs VIEW DATABASE STATE. Make sure the importer is stopped yourself.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END CATCH

    -- Run-log probes are issued through sp_executesql on purpose. These tables only exist once the
    -- relevant migration has run, and a table that exists with a DIFFERENT column set would break
    -- compilation of this entire batch if referenced statically - deferred name resolution covers a
    -- missing TABLE, but not a missing COLUMN of a table that does exist.
    DECLARE @logChecks TABLE (tbl sysname NOT NULL, col sysname NOT NULL);
    INSERT INTO @logChecks (tbl, col) VALUES
        (N'import_log',                      N'time_stamp'),
        (N'copilot_interaction_import_log',  N'run_started_utc'),
        (N'copilot_usage_report_import_log', N'imported_utc');

    DECLARE @logTbl sysname, @logCol sysname, @logCount bigint;

    DECLARE runlogs CURSOR LOCAL FAST_FORWARD FOR SELECT tbl, col FROM @logChecks;
    OPEN runlogs;
    FETCH NEXT FROM runlogs INTO @logTbl, @logCol;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF OBJECT_ID(N'dbo.' + @logTbl, N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.' + @logTbl, @logCol) IS NOT NULL
        BEGIN
            SET @logCount = 0;
            SET @sql = N'SELECT @c = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@logTbl)
                     + N' WHERE ' + QUOTENAME(@logCol) + N' > DATEADD(MINUTE, -15, GETUTCDATE())'
                     + N'    OR ' + QUOTENAME(@logCol) + N' > DATEADD(MINUTE, -15, GETDATE());';
            EXEC sp_executesql @sql, N'@c bigint OUTPUT', @c = @logCount OUTPUT;
            SET @recentImports = @recentImports + ISNULL(@logCount, 0);
        END
        FETCH NEXT FROM runlogs INTO @logTbl, @logCol;
    END
    CLOSE runlogs;
    DEALLOCATE runlogs;

    IF @activeWriters > 0
    BEGIN
        SET @msg = @migration + N': ABORTED - ' + CAST(@activeWriters AS nvarchar(20))
            + N' other session(s) are holding write locks in this database right now. '
            + N'This migration deletes rows and repoints references across several separately committed '
            + N'statements, so running it against a live writer can cascade-delete or orphan that writer''s '
            + N'rows. Stop the Office365ActivityImporter / AppInsightsImporter web-jobs, wait for the current '
            + N'cycle to finish, then re-run. Nothing has been changed. '
            + N'(To override once you have confirmed the importer is stopped, set @skipConcurrencyCheck = 1 at the top of this script.)';
        RAISERROR(@msg, 16, 1) WITH NOWAIT;
        RETURN;
    END

    -- Deliberately a WARNING, not an abort. Recent run-log rows are only a hint: an importer stopped
    -- five minutes ago still leaves them, and the unit-test suite writes dbo.import_log directly. Making
    -- this fatal would block legitimate upgrades and would repeat the DenormaliseCopilotChatUserAndTime
    -- mistake of failing a migration over data state rather than over a real, current hazard. The
    -- write-lock check above is the one that can actually prove a live writer, so it is the one that stops
    -- the migration.
    IF @recentImports > 0
    BEGIN
        SET @msg = @migration + N': WARNING - ' + CAST(@recentImports AS nvarchar(20))
            + N' import-log row(s) were written in the last 15 minutes, so the importer may only just have '
            + N'stopped. No session is holding write locks, so this run is proceeding. If the importer is '
            + N'in fact still running, stop it and restore from backup before continuing.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('UniqueUrlsFullUrlIndex: no concurrent writer detected.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.urls', N'U') IS NULL
BEGIN
    RAISERROR('UniqueUrlsFullUrlIndex: dbo.urls does not exist; nothing to do.', 0, 1) WITH NOWAIT;
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'full_url')
BEGIN
    RAISERROR('UniqueUrlsFullUrlIndex: dbo.urls.full_url does not exist; nothing to do.', 0, 1) WITH NOWAIT;
END
ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = @ix AND is_unique = 1 AND ignore_dup_key = 1)
BEGIN
    -- Already applied. Falls through rather than RETURNing so the manual script still reaches its
    -- __MigrationHistory stamp on a database that is already up to date.
    -- IGNORE_DUP_KEY is part of the match on purpose: an index that is merely UNIQUE (e.g. created by
    -- hand by a DBA) is NOT what this migration produces, because without IGNORE_DUP_KEY a concurrent
    -- check-then-insert race in the importer aborts the entire INSERT instead of skipping one row. Such
    -- an index therefore falls through to the ELSE below and is rebuilt with the correct options.
    RAISERROR('UniqueUrlsFullUrlIndex: IX_urls_full_url is already UNIQUE with IGNORE_DUP_KEY = ON; nothing to do.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    /* ---------------------------------------------------------------------------------------------
       1. Map every duplicate URL to the canonical row that will survive it.
          Grouping uses the database collation, which is what the unique index will enforce, so the
          survivors are exactly the set the index permits.
       --------------------------------------------------------------------------------------------- */
    IF OBJECT_ID('tempdb..#url_remap') IS NOT NULL DROP TABLE #url_remap;

    CREATE TABLE #url_remap (old_id int NOT NULL PRIMARY KEY, keep_id int NOT NULL);

    INSERT INTO #url_remap (old_id, keep_id)
    SELECT u.id, k.keep_id
    FROM dbo.urls AS u
    INNER JOIN (
        SELECT full_url, MIN(id) AS keep_id
        FROM dbo.urls
        GROUP BY full_url
        HAVING COUNT(*) > 1
    ) AS k ON k.full_url = u.full_url
    WHERE u.id <> k.keep_id;

    SET @rows = (SELECT COUNT(*) FROM #url_remap);
    SET @msg = @migration + N': ' + CAST(@rows AS nvarchar(20)) + N' duplicate URL row(s) to remove.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    CREATE INDEX IX_url_remap_keep ON #url_remap (keep_id);

    IF @rows > 0
    BEGIN
        /* -----------------------------------------------------------------------------------------
           2. Prune rows that would COLLIDE once repointed.

              Discovered from sys.indexes, not hard-coded: any UNIQUE index on a referencing table
              whose key columns include the url column can be violated by the repoint. The row that
              already points at the canonical URL is kept.
           ----------------------------------------------------------------------------------------- */
        DECLARE @sch sysname, @tbl sysname, @col sysname, @ixName sysname, @pk sysname, @otherSel nvarchar(max), @otherPart nvarchar(max), @qualified nvarchar(600), @tieBreak nvarchar(200);

        DECLARE collide CURSOR LOCAL FAST_FORWARD FOR
            SELECT DISTINCT OBJECT_SCHEMA_NAME(fk.parent_object_id), OBJECT_NAME(fk.parent_object_id), c.name, i.name
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns AS c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            INNER JOIN sys.indexes AS i ON i.object_id = fkc.parent_object_id AND i.is_unique = 1 AND i.is_primary_key = 0
                                       AND i.has_filter = 0 AND i.is_disabled = 0
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                              AND ic.column_id = c.column_id AND ic.is_included_column = 0
            WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.urls');

        OPEN collide;
        FETCH NEXT FROM collide INTO @sch, @tbl, @col, @ixName;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            -- Schema-qualified and quoted throughout: a customer-added FK from a schema other than dbo
            -- must be repointed against its own schema, not silently re-qualified as dbo.
            SET @qualified = QUOTENAME(@sch) + N'.' + QUOTENAME(@tbl);

            -- Needs a single-column primary key to delete through. Every table in this schema has one;
            -- anything else is skipped loudly rather than guessed at. HAVING COUNT(*) = 1 over the whole
            -- (ungrouped) result yields NULL when the PK is composite or absent.
            SET @pk = (SELECT MAX(c2.name)
                       FROM sys.indexes AS pki
                       INNER JOIN sys.index_columns AS pkic ON pkic.object_id = pki.object_id AND pkic.index_id = pki.index_id
                                                           AND pkic.is_included_column = 0
                       INNER JOIN sys.columns AS c2 ON c2.object_id = pkic.object_id AND c2.column_id = pkic.column_id
                       WHERE pki.object_id = OBJECT_ID(@qualified) AND pki.is_primary_key = 1
                       HAVING COUNT(*) = 1);

            IF @pk IS NULL
            BEGIN
                SET @msg = @migration + N': WARNING - ' + @qualified + N' has unique index [' + @ixName
                    + N'] over ' + @col + N' but no single-column primary key, so colliding rows cannot be pruned. '
                    + N'If the repoint below fails with a duplicate-key error, resolve those rows by hand and re-run.';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END
            ELSE
            BEGIN
                -- Build the index's other key columns as SELECT and PARTITION BY fragments. Deliberately
                -- plain string concatenation rather than FOR XML PATH(...).value(...): the XML data-type
                -- method requires QUOTED_IDENTIFIER ON, which sqlcmd leaves OFF by default, so the manual
                -- upgrade script would fail where the migration succeeded.
                SET @otherSel = NULL;
                SET @otherPart = NULL;

                SELECT
                    @otherSel  = ISNULL(@otherSel + N', ', N'') + N't.[' + c3.name + N']',
                    @otherPart = ISNULL(@otherPart + N', ', N'') + N'[' + c3.name + N']'
                FROM sys.indexes AS i3
                INNER JOIN sys.index_columns AS ic3 ON ic3.object_id = i3.object_id AND ic3.index_id = i3.index_id
                                                   AND ic3.is_included_column = 0
                INNER JOIN sys.columns AS c3 ON c3.object_id = ic3.object_id AND c3.column_id = ic3.column_id
                WHERE i3.object_id = OBJECT_ID(@qualified) AND i3.name = @ixName AND c3.name <> @col
                ORDER BY ic3.key_ordinal;

                -- Retention rule when two rows collide. The row that survives is, in order:
                --   1. the most recently "updated" row, when the table carries such a column, so the
                --      freshest value wins (dbo.file_metadata_property_values.field_value is the case
                --      that matters - keeping a stale property value would be a silent data regression),
                --   2. then the row that already pointed at the canonical URL,
                --   3. then the lowest primary key, purely so the result is deterministic.
                -- NULL "updated" sorts last under DESC, so a row with a known timestamp always beats one
                -- without.
                SET @tieBreak = N'';
                IF EXISTS (SELECT 1
                           FROM sys.columns AS c4
                           INNER JOIN sys.types AS t4 ON t4.user_type_id = c4.user_type_id
                           WHERE c4.object_id = OBJECT_ID(@qualified) AND c4.name = N'updated'
                             AND t4.name IN (N'datetime', N'datetime2', N'smalldatetime', N'datetimeoffset', N'date'))
                    SET @tieBreak = N'__upd DESC, ';

                SET @sql = N'
;WITH m AS (
    SELECT t.[' + @pk + N'] AS __pk, t.[' + @col + N'] AS __old, ISNULL(r.keep_id, t.[' + @col + N']) AS __new'
        + CASE WHEN @tieBreak = N'' THEN N'' ELSE N', t.[updated] AS __upd' END
        + CASE WHEN @otherSel IS NULL THEN N'' ELSE N', ' + @otherSel END + N'
    FROM ' + @qualified + N' AS t
    LEFT JOIN #url_remap AS r ON r.old_id = t.[' + @col + N']
), ranked AS (
    SELECT __pk, ROW_NUMBER() OVER (
        PARTITION BY __new' + CASE WHEN @otherPart IS NULL THEN N'' ELSE N', ' + @otherPart END + N'
        ORDER BY ' + @tieBreak + N'CASE WHEN __new = __old THEN 0 ELSE 1 END, __pk) AS rn
    FROM m
)
DELETE d FROM ' + @qualified + N' AS d INNER JOIN ranked AS k ON k.__pk = d.[' + @pk + N'] WHERE k.rn > 1;';

                EXEC sp_executesql @sql;
                SET @rows = @@ROWCOUNT;

                IF @rows > 0
                BEGIN
                    SET @msg = @migration + N': pruned ' + CAST(@rows AS nvarchar(20)) + N' row(s) from ' + @qualified
                        + N' that would have collided on unique index [' + @ixName + N'] once repointed.';
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                END
            END

            FETCH NEXT FROM collide INTO @sch, @tbl, @col, @ixName;
        END
        CLOSE collide;
        DEALLOCATE collide;

        /* -----------------------------------------------------------------------------------------
           3. Repoint every reference onto the canonical URL, in batches.
              FK references are discovered; the legacy non-FK event_meta_sharepoint.url_id is added
              explicitly because older databases have it without a constraint.
           ----------------------------------------------------------------------------------------- */
        DECLARE @refs TABLE (seq int IDENTITY(1,1) PRIMARY KEY, sch sysname NOT NULL, tbl sysname NOT NULL, col sysname NOT NULL);

        INSERT INTO @refs (sch, tbl, col)
        SELECT DISTINCT OBJECT_SCHEMA_NAME(fk.parent_object_id), OBJECT_NAME(fk.parent_object_id), c.name
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        INNER JOIN sys.columns AS c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
        WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.urls');

        IF OBJECT_ID(N'dbo.event_meta_sharepoint', N'U') IS NOT NULL
           AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.event_meta_sharepoint') AND name = N'url_id')
           AND NOT EXISTS (SELECT 1 FROM @refs WHERE sch = N'dbo' AND tbl = N'event_meta_sharepoint' AND col = N'url_id')
        BEGIN
            INSERT INTO @refs (sch, tbl, col) VALUES (N'dbo', N'event_meta_sharepoint', N'url_id');
        END

        DECLARE @i int = 1, @n int = (SELECT MAX(seq) FROM @refs);
        WHILE @i <= ISNULL(@n, 0)
        BEGIN
            SELECT @sch = sch, @tbl = tbl, @col = col FROM @refs WHERE seq = @i;
            SET @qualified = QUOTENAME(@sch) + N'.' + QUOTENAME(@tbl);

            SET @stepStart = SYSUTCDATETIME();
            SET @total = 0;
            SET @sql = N'UPDATE TOP (' + CAST(@batch AS nvarchar(20)) + N') t SET t.[' + @col + N'] = r.keep_id
                         FROM ' + @qualified + N' AS t INNER JOIN #url_remap AS r ON r.old_id = t.[' + @col + N'];';

            SET @rows = 1;
            WHILE @rows > 0
            BEGIN
                EXEC sp_executesql @sql;
                SET @rows = @@ROWCOUNT;
                SET @total = @total + @rows;
            END

            IF @total > 0
            BEGIN
                SET @msg = @migration + N': repointed ' + CAST(@total AS nvarchar(20)) + N' row(s) in ' + @qualified + N'.[' + @col + N'] in '
                    + CAST(DATEDIFF(SECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N's.';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END

            SET @i += 1;
        END

        /* -----------------------------------------------------------------------------------------
           4. Delete the now-unreferenced duplicate URL rows, in batches.
           ----------------------------------------------------------------------------------------- */
        SET @stepStart = SYSUTCDATETIME();
        SET @total = 0;
        SET @rows = 1;
        WHILE @rows > 0
        BEGIN
            DELETE TOP (20000) u
            FROM dbo.urls AS u
            INNER JOIN #url_remap AS r ON r.old_id = u.id;

            SET @rows = @@ROWCOUNT;
            SET @total = @total + @rows;
        END

        SET @msg = @migration + N': deleted ' + CAST(@total AS nvarchar(20)) + N' duplicate URL row(s) in '
            + CAST(DATEDIFF(SECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N's.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    DROP TABLE #url_remap;

    /* ---------------------------------------------------------------------------------------------
       5. Recreate IX_urls_full_url as UNIQUE.
          IGNORE_DUP_KEY = ON so a concurrent check-then-insert race in the importer skips only the
          duplicate URL rather than aborting the whole INSERT statement.
       --------------------------------------------------------------------------------------------- */
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = @ix)
    BEGIN
        SET @msg = @migration + N': dropping the existing non-unique [' + @ix + N']...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        DROP INDEX [IX_urls_full_url] ON [dbo].[urls];
    END

    SET @stepStart = SYSUTCDATETIME();
    SET @onlineDone = 0;

    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            RAISERROR('UniqueUrlsFullUrlIndex: creating UNIQUE IX_urls_full_url WITH (ONLINE = ON)...', 0, 1) WITH NOWAIT;
            -- Issued through sp_executesql on purpose: the "Online index operations can only be performed
            -- in Enterprise edition" error aborts the batch and is NOT catchable for a plain statement, but
            -- IS catchable when executed this way.
            SET @sql = N'CREATE UNIQUE NONCLUSTERED INDEX [IX_urls_full_url] ON [dbo].[urls] ([full_url]) WITH (IGNORE_DUP_KEY = ON, ONLINE = ON);';
            EXEC sp_executesql @sql;
            SET @onlineDone = 1;
        END TRY
        BEGIN CATCH
            SET @msg = @migration + N': ONLINE build unavailable (' + ERROR_MESSAGE() + N'); retrying offline.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END

    IF @onlineDone = 0
    BEGIN
        RAISERROR('UniqueUrlsFullUrlIndex: creating UNIQUE IX_urls_full_url (offline)...', 0, 1) WITH NOWAIT;
        SET @sql = N'CREATE UNIQUE NONCLUSTERED INDEX [IX_urls_full_url] ON [dbo].[urls] ([full_url]) WITH (IGNORE_DUP_KEY = ON);';
        EXEC sp_executesql @sql;
    END

    SET @msg = @migration + N': UNIQUE [' + @ix + N'] created in '
        + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms ('
        + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

/* =====================================================================================================
   POST-FLIGHT: prove the schema work actually landed BEFORE stamping __MigrationHistory.

   Why this is needed: sqlcmd and SSMS do NOT stop at a severity-16 error - they abandon the failing
   batch and carry on with the next one. Without this check, a CREATE UNIQUE INDEX that failed (an
   unexpected duplicate, a lock timeout, out of space) would still fall through to the stamp below, and
   the database would be recorded as migrated while dbo.urls carried no unique constraint at all. EF and
   the web app Health page would then both report the schema as up to date.

   This deliberately checks SCHEMA ONLY - "does the index exist, is it UNIQUE, does it have
   IGNORE_DUP_KEY = ON". It must never be widened into a check on DATA STATE (e.g. "are there zero
   duplicate URLs left"). A data-state guard cannot be satisfied reliably on a live database and has
   already broken a customer upgrade once (see DenormaliseCopilotChatUserAndTime, whose manual script
   refused to stamp because a few hundred concurrently-inserted rows were still NULL). Concurrency
   cannot falsify the existence and shape of an index, so this check is safe.

   On failure the script stops without stamping. It is idempotent and resumable, so the fix is to
   resolve the reported error and simply run it again.
   ===================================================================================================== */
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.urls')
      AND name = N'IX_urls_full_url'
      AND is_unique = 1
      AND ignore_dup_key = 1)
BEGIN
    RAISERROR('UniqueUrlsFullUrlIndex: NOT stamped - the schema work did not complete. dbo.urls.IX_urls_full_url is not present as a UNIQUE index with IGNORE_DUP_KEY = ON. Review the errors above (sqlcmd/SSMS continue past failed batches, so the real error may be further up), then re-run this script - it is idempotent and will resume.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

/* =====================================================================================================
   Record the migration so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health
   page treat it as applied.

   This migration does NOT change the EF entity model - the uniqueness of an existing index is physical
   only, and IX_urls_full_url is created by raw SQL in ShrinkUrlsFullUrlColumn rather than by the model -
   so its snapshot is byte-identical to its predecessor's and the stamp simply copies that row rather than
   embedding the model blob again.

   Guarded so a re-run is a no-op, and conditional on the predecessor being present so the scripts cannot
   be applied out of order. Reached on every path, including the "already applied" one, so a by-hand run
   against an up-to-date database still stamps rather than leaving the migration pending.
   ===================================================================================================== */
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609101100001_UniqueUrlsFullUrlIndex')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609101000001_RetireUnusedAuditYammerStreamTables')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609101100001_UniqueUrlsFullUrlIndex', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609101000001_RetireUnusedAuditYammerStreamTables';
        RAISERROR('UniqueUrlsFullUrlIndex: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('UniqueUrlsFullUrlIndex: the schema change was applied, but prerequisite migration 202609101000001_RetireUnusedAuditYammerStreamTables is missing from __MigrationHistory, so it was NOT stamped. Run the manual scripts in migration-id order.', 16, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('UniqueUrlsFullUrlIndex: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
SET NOEXEC OFF;
