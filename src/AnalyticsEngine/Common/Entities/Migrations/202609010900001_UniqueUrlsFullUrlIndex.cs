namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Makes <c>dbo.urls.full_url</c> UNIQUE, so duplicate URL lookups can never be created in the first
    /// place, after repointing every reference to a single canonical row and removing the duplicates.
    ///
    /// WHY
    ///   <c>IX_urls_full_url</c> has been a NON-unique index since <see cref="ShrinkUrlsFullUrlColumn"/>
    ///   created it, so duplicate URLs accumulate in customer databases. Those duplicates are what fan a
    ///   single page-view out into several hit rows sharing one <c>page_request_id</c>, which used to break
    ///   the unique <c>IX_PageRequestID</c> and abort a whole day's import (#165). #165 made the hits merge
    ///   tolerate duplicates; this stops them being created. See issue #167.
    ///
    /// NOT PERFORMANCE-MOTIVATED, so the schema-change policy's before/after benchmark requirement does not
    /// apply. This is a correctness constraint: the index already exists and is already used for lookups,
    /// and this only adds uniqueness to it. It is not additive either - it removes rows - hence the care
    /// below.
    ///
    /// IGNORE_DUP_KEY = ON is deliberate and is the importer-hardening half of the change. Without it, a
    /// concurrent check-then-insert race between two importer threads would abort the whole INSERT
    /// statement; with it, SQL Server skips only the duplicate row (as a warning) and every other new URL in
    /// the same statement still inserts.
    ///
    /// COLLATION
    ///   The database collation is case-insensitive (Latin1_General_CI_AS), so this treats
    ///   <c>.../Foo</c> and <c>.../foo</c> as the same URL. That is deliberate: it matches the existing join
    ///   semantics used by the staging merges (<c>urls.full_url = imports.url</c>), so the constraint agrees
    ///   with how the importer already looks URLs up. The de-duplication below groups the same way, so the
    ///   set of survivors is exactly the set the index will permit.
    ///
    /// THE PART THAT IS EASY TO GET WRONG
    ///   Two child tables carry a UNIQUE index that INCLUDES <c>url_id</c>:
    ///     * <c>file_metadata_property_values.IX_url_id_field_id</c>  (url_id, field_id)
    ///     * <c>hits_clicked_elements.IX_hit_id_url_id_timestamp</c>  (hit_id, url_id, timestamp)
    ///   Repointing two duplicate url_ids onto one canonical id can therefore collide inside those tables,
    ///   and the UPDATE would fail with a unique-key violation - breaking the upgrade for exactly the
    ///   customers who have duplicates, which is everyone this migration is for. So collisions are pruned
    ///   FIRST, keeping the row that already points at the canonical URL. The pruning is driven from
    ///   sys.indexes rather than hard-coded, so a database version with a different set of unique indexes is
    ///   handled too.
    ///
    ///   References are likewise discovered from sys.foreign_keys rather than listed, plus the legacy
    ///   non-FK <c>event_meta_sharepoint.url_id</c> which is repointed when that table/column exists.
    ///
    /// RUNTIME
    ///   The de-duplication only touches rows that are actually duplicated, so on a clean database it is a
    ///   single GROUP BY over dbo.urls and nothing else. Where duplicates exist, the repoint and delete run
    ///   in batches with live progress. The index rebuild is the expensive part on a large urls table;
    ///   ONLINE is attempted on Enterprise / Azure SQL DB / Azure SQL MI and falls back to an offline build
    ///   elsewhere, which briefly locks the table - so run large upgrades in a maintenance window with the
    ///   importer stopped.
    ///
    /// The EF entity model is unchanged - uniqueness of an existing index is physical only, and
    /// IX_urls_full_url is created by raw SQL in ShrinkUrlsFullUrlColumn rather than by the model - so its
    /// .resx snapshot is a byte-identical copy of its predecessor's
    /// (202608310800001_ColumnstoreUsageReportMetrics), per the repo's migration rules. The manual
    /// upgrade script therefore stamps __MigrationHistory by copying the predecessor's row.
    /// </summary>
    public partial class UniqueUrlsFullUrlIndex : DbMigration
    {
        /// <summary>Width of the repoint / delete batches. Small enough to keep the log and lock footprint bounded.</summary>
        public const int BatchSize = 20000;

        /// <summary>
        /// De-duplicates <c>dbo.urls</c> and recreates <c>IX_urls_full_url</c> as UNIQUE. Exposed as a
        /// constant so the manual upgrade script and the unit tests use the exact same SQL. Idempotent,
        /// guarded, resumable and edition-aware (ONLINE attempt via sp_executesql inside TRY/CATCH - which
        /// is what makes the "ONLINE is Enterprise only" error catchable - with an offline fallback).
        /// </summary>
        public const string Up_Sql = @"
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

IF OBJECT_ID(N'dbo.urls', N'U') IS NULL
BEGIN
    RAISERROR('UniqueUrlsFullUrlIndex: dbo.urls does not exist; nothing to do.', 0, 1) WITH NOWAIT;
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'full_url')
BEGIN
    RAISERROR('UniqueUrlsFullUrlIndex: dbo.urls.full_url does not exist; nothing to do.', 0, 1) WITH NOWAIT;
END
ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = @ix AND is_unique = 1)
BEGIN
    -- Already applied. Falls through rather than RETURNing so the manual script still reaches its
    -- __MigrationHistory stamp on a database that is already up to date.
    RAISERROR('UniqueUrlsFullUrlIndex: IX_urls_full_url is already UNIQUE; nothing to do.', 0, 1) WITH NOWAIT;
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
        DECLARE @tbl sysname, @col sysname, @ixName sysname, @pk sysname, @otherSel nvarchar(max), @otherPart nvarchar(max);

        DECLARE collide CURSOR LOCAL FAST_FORWARD FOR
            SELECT DISTINCT OBJECT_NAME(fk.parent_object_id), c.name, i.name
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns AS c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            INNER JOIN sys.indexes AS i ON i.object_id = fkc.parent_object_id AND i.is_unique = 1 AND i.is_primary_key = 0
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                              AND ic.column_id = c.column_id AND ic.is_included_column = 0
            WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.urls');

        OPEN collide;
        FETCH NEXT FROM collide INTO @tbl, @col, @ixName;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            -- Needs a single-column primary key to delete through. Every table in this schema has one;
            -- anything else is skipped loudly rather than guessed at. HAVING COUNT(*) = 1 over the whole
            -- (ungrouped) result yields NULL when the PK is composite or absent.
            SET @pk = (SELECT MAX(c2.name)
                       FROM sys.indexes AS pki
                       INNER JOIN sys.index_columns AS pkic ON pkic.object_id = pki.object_id AND pkic.index_id = pki.index_id
                                                           AND pkic.is_included_column = 0
                       INNER JOIN sys.columns AS c2 ON c2.object_id = pkic.object_id AND c2.column_id = pkic.column_id
                       WHERE pki.object_id = OBJECT_ID(N'dbo.' + @tbl) AND pki.is_primary_key = 1
                       HAVING COUNT(*) = 1);

            IF @pk IS NULL
            BEGIN
                SET @msg = @migration + N': WARNING - ' + @tbl + N' has unique index [' + @ixName
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
                WHERE i3.object_id = OBJECT_ID(N'dbo.' + @tbl) AND i3.name = @ixName AND c3.name <> @col
                ORDER BY ic3.key_ordinal;

                SET @sql = N'
;WITH m AS (
    SELECT t.[' + @pk + N'] AS __pk, t.[' + @col + N'] AS __old, ISNULL(r.keep_id, t.[' + @col + N']) AS __new'
        + CASE WHEN @otherSel IS NULL THEN N'' ELSE N', ' + @otherSel END + N'
    FROM dbo.[' + @tbl + N'] AS t
    LEFT JOIN #url_remap AS r ON r.old_id = t.[' + @col + N']
), ranked AS (
    SELECT __pk, ROW_NUMBER() OVER (
        PARTITION BY __new' + CASE WHEN @otherPart IS NULL THEN N'' ELSE N', ' + @otherPart END + N'
        ORDER BY CASE WHEN __new = __old THEN 0 ELSE 1 END, __pk) AS rn
    FROM m
)
DELETE d FROM dbo.[' + @tbl + N'] AS d INNER JOIN ranked AS k ON k.__pk = d.[' + @pk + N'] WHERE k.rn > 1;';

                EXEC sp_executesql @sql;
                SET @rows = @@ROWCOUNT;

                IF @rows > 0
                BEGIN
                    SET @msg = @migration + N': pruned ' + CAST(@rows AS nvarchar(20)) + N' row(s) from ' + @tbl
                        + N' that would have collided on unique index [' + @ixName + N'] once repointed.';
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                END
            END

            FETCH NEXT FROM collide INTO @tbl, @col, @ixName;
        END
        CLOSE collide;
        DEALLOCATE collide;

        /* -----------------------------------------------------------------------------------------
           3. Repoint every reference onto the canonical URL, in batches.
              FK references are discovered; the legacy non-FK event_meta_sharepoint.url_id is added
              explicitly because older databases have it without a constraint.
           ----------------------------------------------------------------------------------------- */
        DECLARE @refs TABLE (seq int IDENTITY(1,1) PRIMARY KEY, tbl sysname NOT NULL, col sysname NOT NULL);

        INSERT INTO @refs (tbl, col)
        SELECT DISTINCT OBJECT_NAME(fk.parent_object_id), c.name
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        INNER JOIN sys.columns AS c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
        WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.urls');

        IF OBJECT_ID(N'dbo.event_meta_sharepoint', N'U') IS NOT NULL
           AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.event_meta_sharepoint') AND name = N'url_id')
           AND NOT EXISTS (SELECT 1 FROM @refs WHERE tbl = N'event_meta_sharepoint' AND col = N'url_id')
        BEGIN
            INSERT INTO @refs (tbl, col) VALUES (N'event_meta_sharepoint', N'url_id');
        END

        DECLARE @i int = 1, @n int = (SELECT MAX(seq) FROM @refs);
        WHILE @i <= ISNULL(@n, 0)
        BEGIN
            SELECT @tbl = tbl, @col = col FROM @refs WHERE seq = @i;

            SET @stepStart = SYSUTCDATETIME();
            SET @total = 0;
            SET @sql = N'UPDATE TOP (' + CAST(@batch AS nvarchar(20)) + N') t SET t.[' + @col + N'] = r.keep_id
                         FROM dbo.[' + @tbl + N'] AS t INNER JOIN #url_remap AS r ON r.old_id = t.[' + @col + N'];';

            SET @rows = 1;
            WHILE @rows > 0
            BEGIN
                EXEC sp_executesql @sql;
                SET @rows = @@ROWCOUNT;
                SET @total = @total + @rows;
            END

            IF @total > 0
            BEGIN
                SET @msg = @migration + N': repointed ' + CAST(@total AS nvarchar(20)) + N' row(s) in ' + @tbl + N'.[' + @col + N'] in '
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
            -- Issued through sp_executesql on purpose: the ""Online index operations can only be performed
            -- in Enterprise edition"" error aborts the batch and is NOT catchable for a plain statement, but
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
";

        /// <summary>
        /// SQL executed by <see cref="Down"/>: restores the non-unique index. The removed duplicate rows are
        /// NOT restored - they cannot be - which is why Down only relaxes the constraint.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.urls', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'IX_urls_full_url' AND is_unique = 1)
    BEGIN
        DROP INDEX [IX_urls_full_url] ON [dbo].[urls];
        CREATE NONCLUSTERED INDEX [IX_urls_full_url] ON [dbo].[urls] ([full_url]);
        RAISERROR('UniqueUrlsFullUrlIndex (Down): IX_urls_full_url restored as non-unique. The de-duplicated rows are not restored.', 0, 1) WITH NOWAIT;
    END
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'UniqueUrlsFullUrlIndex'. De-duplicates dbo.urls (repointing every reference to a canonical row) and recreates IX_urls_full_url as UNIQUE with IGNORE_DUP_KEY = ON, so duplicate URL lookups can no longer be created. Runs outside the migration transaction so it is resumable on a large database - ONLINE where the edition supports it, offline otherwise. Check the SQL session for live progress (RAISERROR ... WITH NOWAIT). Guarded and idempotent: an interrupted run converges on re-run.");

            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'UniqueUrlsFullUrlIndex'. Restores the non-unique index; removed duplicate rows are not restored.");

            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
