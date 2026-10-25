namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Makes <c>dbo.event_file_ext.extension_name</c> Unicode: <c>varchar(250)</c> becomes <c>nvarchar(250)</c>, with
    /// the same nullability and collation, and its unique index <c>IX_event_file_ext</c> is re-created around the change.
    ///
    /// <para>
    /// <b>Why.</b> <c>Create DB.sql</c> made the column <c>varchar</c>, which holds only the database's single code
    /// page. A file extension with any other character was stored converted: a Greek one largely as <c>?</c>s
    /// (<c>λόγος</c> and <c>χώρος</c> both as <c>?????</c>), and an accented one sometimes as a different letter
    /// (<c>přípona</c> as <c>prípona</c>). Two different extensions could then become the same string and collide on
    /// the unique index (error 2601), which failed the whole import batch on every retry.
    /// The merges were patched to insert, de-duplicate and join on the converted value, which stopped the failures but
    /// still stored the wrong text, and made different extensions share one row. With the column Unicode, the merges
    /// ("Insert Activity from Staging Table.sql" and <c>insert_sp_copilot_events_from_staging_table.sql</c>) store the
    /// extension exactly as logged, still trimmed to the column's 250 characters.
    /// </para>
    ///
    /// <para>
    /// <b>Rows that become equal.</b> Under a SQL collation (for example <c>SQL_Latin1_General_CP1_CI_AS</c>, the Azure
    /// SQL default), <c>varchar</c> and <c>nvarchar</c> values are compared by different rules, so two extensions that
    /// were different as <c>varchar</c> can be equal as <c>nvarchar</c> (<c>ss</c> and <c>ß</c>, <c>2</c> and <c>²</c>).
    /// The unique index could not be re-created over both, so before re-creating it each such group is merged into its
    /// oldest row: every column that references <c>event_file_ext.id</c> - each foreign key, and any <c>dbo</c> column named
    /// <c>file_extension_id</c>, because <c>event_meta_sharepoint</c>'s has no foreign key - is re-pointed at that row,
    /// and the others are deleted. This is the comparison the importer will make from now on anyway. On a normal
    /// database there is no such group and this step reads only <c>event_file_ext</c>.
    /// Repoint, verification and deletion are one XACT_ABORT transaction. A table lock plus a retained schema
    /// lock excludes even snapshot readers of the old ids until commit; a failed repoint rolls back the whole merge.
    /// </para>
    ///
    /// <para>
    /// <b>Classification: Unicode-correctness type change, not performance-motivated</b>, so the benchmark gate does not
    /// apply. The index is re-created exactly as it was (unique, the one key column), so no query plan changes shape. It
    /// does make an <c>nvarchar</c> lookup sargable: under a SQL collation, EF's <c>SharePointLookupManager</c> query,
    /// whose parameter is <c>nvarchar</c>, had to convert every row of the <c>varchar</c> column before it could compare.
    /// </para>
    ///
    /// <para>
    /// <b>Upgrade time.</b> Seconds at most, on any database size: <c>event_file_ext</c> holds one row per distinct file
    /// extension (typically hundreds to a few thousand rows, whether <c>audit_events</c> has 1M, 10M or 100M rows), so
    /// the <c>ALTER COLUMN</c> rewrite and the index build are both tiny. Each takes a brief schema-modification lock on
    /// <c>event_file_ext</c>, which blocks the importer's merge for that moment; ONLINE is attempted where the edition
    /// supports it. The one exception is a database that has rows which become equal (above): re-pointing them reads
    /// <c>event_meta_sharepoint</c> once, because its <c>file_extension_id</c> has no index - an estimated few minutes
    /// per 100M rows (not measured; the path is expected to be rare), plus a verification scan before deletion.
    /// The lookup stays schema-locked throughout that merge: use a maintenance window. Nothing else references the column's values, so no
    /// other table is touched.
    /// </para>
    ///
    /// <para>
    /// <b>EF model.</b> Unchanged, and this migration reuses its predecessor's snapshot byte for byte. EF already maps
    /// <c>SPEventFileExtension.extension_name</c> as Unicode (<c>nvarchar(max)</c>, NULLable, by convention), and EF does
    /// write it (<c>SharePointLookupManager.GetOrCreateFileExtension</c>), but always as an <c>nvarchar</c> parameter:
    /// against the old <c>varchar</c> column that write was the lossy one, and against <c>nvarchar(250)</c> it is not. The
    /// model's width and nullability have never matched this column, which <c>Create DB.sql</c> creates; as with
    /// <see cref="ShrinkUrlsFullUrlColumn"/>, the physical type is set here and the model is left as it is.
    /// </para>
    ///
    /// <para>
    /// <b>Safety.</b> Idempotent and guarded: a no-op once the column is <c>nvarchar</c> with its unique index, and each
    /// step converges on re-run. It runs with <c>suppressTransaction: true</c>, attempts ONLINE operations through
    /// <c>sp_executesql</c> inside <c>TRY/CATCH</c> with an offline fallback, and reports progress with
    /// <c>RAISERROR ... WITH NOWAIT</c>. Before any change it refuses, listing them, if an index (other than
    /// <c>IX_event_file_ext</c>), a constraint, a foreign key onto the column or a schema-bound object depends on it;
    /// user-created statistics on it are dropped, as SQL Server already does with automatically created ones.
    /// Non-schema-bound views over the table are refreshed afterwards so their metadata reports the new type.
    /// <see cref="Down"/> deliberately leaves the column <c>nvarchar</c> rather than corrupt the extensions stored since.
    /// </para>
    /// </summary>
    public partial class EventFileExtensionUnicode : DbMigration
    {
        /// <summary>
        /// The whole migration as one guarded, idempotent script. A <c>public const</c> so the shipped
        /// <c>202610221500002_EventFileExtensionUnicode.manual.sql</c> can embed it verbatim.
        /// </summary>
        public const string Up_Sql = @"SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'EventFileExtensionUnicode';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);
DECLARE @sql nvarchar(max);
DECLARE @onlineSql nvarchar(max);
DECLARE @rows int;
DECLARE @indexName sysname = N'IX_event_file_ext';
DECLARE @tableId int = OBJECT_ID(N'dbo.event_file_ext', N'U');

-- ONLINE operations exist only on Enterprise (3), Azure SQL DB (5) and Azure SQL MI (8). Each attempt runs through
-- sp_executesql inside TRY/CATCH, so a rejection is catchable, and falls back to the offline form.
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
DECLARE @onlineDone bit;
DECLARE @mergeStep nvarchar(700) = N'lock';
DECLARE @mergeStart datetime2(3);
DECLARE @xactAbortWasOff bit = CASE WHEN (@@OPTIONS & 16384) = 0 THEN 1 ELSE 0 END;

SET @msg = @migration + N': starting; EngineEdition=' + CAST(@edition AS nvarchar(10))
    + CASE WHEN @canOnline = 1 THEN N'; online operations will be attempted.'
           ELSE N'; online operations are unavailable, so dbo.event_file_ext is briefly locked.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF @tableId IS NULL OR COL_LENGTH(N'dbo.event_file_ext', N'extension_name') IS NULL
BEGIN
    SET @msg = @migration + N': dbo.event_file_ext.extension_name does not exist; skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

DECLARE @columnId int = COLUMNPROPERTY(@tableId, N'extension_name', 'ColumnId');
DECLARE @typeName sysname, @maxLength smallint, @isNullable bit, @collation sysname;
SELECT @typeName = t.name, @maxLength = c.max_length, @isNullable = c.is_nullable, @collation = c.collation_name
FROM sys.columns AS c
INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
WHERE c.object_id = @tableId AND c.column_id = @columnId;

-- The width in characters: varchar(n) reports n bytes, nvarchar(n) 2n, (max) -1.
DECLARE @chars int = CASE WHEN @maxLength = -1 THEN -1 WHEN @typeName = N'nvarchar' THEN @maxLength / 2 ELSE @maxLength END;

IF @typeName NOT IN (N'varchar', N'nvarchar') OR @chars NOT BETWEEN 1 AND 850
BEGIN
    SET @msg = @migration + N': ABORTING - dbo.event_file_ext.extension_name is ' + @typeName
        + CASE WHEN @chars = -1 THEN N'(max)' ELSE N'(' + CAST(@chars AS nvarchar(10)) + N')' END
        + N', not the varchar(n) or nvarchar(n) (n up to 850) this migration expects. Nothing was changed.';
    RAISERROR(@msg, 16, 1);
    RETURN;
END

-- Is IX_event_file_ext already the unique index on extension_name alone?
DECLARE @indexId int = (SELECT index_id FROM sys.indexes WHERE object_id = @tableId AND name = @indexName);
DECLARE @indexIsRight bit = CASE WHEN @indexId IS NOT NULL
    AND EXISTS (SELECT 1 FROM sys.indexes
                WHERE object_id = @tableId AND index_id = @indexId AND is_unique = 1 AND type = 2 AND has_filter = 0 AND is_disabled = 0)
    AND (SELECT COUNT(*) FROM sys.index_columns WHERE object_id = @tableId AND index_id = @indexId) = 1
    AND EXISTS (SELECT 1 FROM sys.index_columns
                WHERE object_id = @tableId AND index_id = @indexId AND key_ordinal = 1 AND column_id = @columnId)
    THEN 1 ELSE 0 END;

IF @typeName = N'nvarchar' AND @indexIsRight = 1
BEGIN
    SET @msg = @migration + N': extension_name is already nvarchar(' + CAST(@chars AS nvarchar(10))
        + N') with its unique index; nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-------------------------------------------------------------------------------------------------
-- 1. varchar -> nvarchar. Widening is lossless: every varchar character has a Unicode one.
-------------------------------------------------------------------------------------------------
IF @typeName = N'varchar'
BEGIN
    -- Pre-flight, before anything changes: ALTER COLUMN fails while another index, a constraint or a
    -- schema-bound object depends on the column. None exists on a stock schema, and dropping a DBA's
    -- object is not this migration's call.
    DECLARE @blockers nvarchar(max);
    SELECT @blockers = COALESCE(@blockers + N', ', N'') + b.what
    FROM (
        SELECT N'index ' + QUOTENAME(i.name) AS what
        FROM sys.indexes AS i
        WHERE i.object_id = @tableId AND i.name <> @indexName
          AND EXISTS (SELECT 1 FROM sys.index_columns AS ic
                      WHERE ic.object_id = @tableId AND ic.index_id = i.index_id AND ic.column_id = @columnId)
        UNION ALL
        SELECT N'constraint ' + QUOTENAME(dc.name)
        FROM sys.default_constraints AS dc
        WHERE dc.parent_object_id = @tableId AND dc.parent_column_id = @columnId
        UNION ALL
        SELECT N'constraint ' + QUOTENAME(cc.name)
        FROM sys.check_constraints AS cc
        WHERE cc.parent_object_id = @tableId
          AND (cc.parent_column_id = @columnId OR (cc.parent_column_id = 0 AND cc.definition LIKE N'%extension[_]name%'))
        UNION ALL
        SELECT N'schema-bound object ' + QUOTENAME(OBJECT_SCHEMA_NAME(d.referencing_id)) + N'.' + QUOTENAME(OBJECT_NAME(d.referencing_id))
        FROM sys.sql_expression_dependencies AS d
        WHERE d.referenced_id = @tableId AND d.is_schema_bound_reference = 1
        UNION ALL
        SELECT N'foreign key ' + QUOTENAME(OBJECT_NAME(fkc.constraint_object_id))
        FROM sys.foreign_key_columns AS fkc
        WHERE fkc.referenced_object_id = @tableId AND fkc.referenced_column_id = @columnId
    ) AS b;

    IF @blockers IS NOT NULL
    BEGIN
        SET @msg = @migration + N': ABORTING - these depend on dbo.event_file_ext.extension_name, so its type cannot change: '
            + LEFT(@blockers, 1500) + N'. Drop them, re-run the upgrade, then re-create them. Nothing was changed.';
        RAISERROR(@msg, 16, 1);
        RETURN;
    END

    SET @msg = @migration + N': converting extension_name from varchar(' + CAST(@chars AS nvarchar(10)) + N') to nvarchar('
        + CAST(@chars AS nvarchar(10)) + N') ' + CASE WHEN @isNullable = 1 THEN N'NULL' ELSE N'NOT NULL' END
        + N', collation ' + ISNULL(@collation, N'(none)') + N'.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    IF @indexId IS NOT NULL
    BEGIN
        DROP INDEX [IX_event_file_ext] ON [dbo].[event_file_ext];
        SET @indexId = NULL;
        SET @indexIsRight = 0;
        SET @msg = @migration + N': dropped [' + @indexName + N'] so the column can change type.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    -- ALTER COLUMN drops automatically created statistics itself, but fails on user-created ones.
    DECLARE @statsName sysname;
    DECLARE user_stats CURSOR LOCAL FAST_FORWARD FOR
        SELECT s.name
        FROM sys.stats AS s
        WHERE s.object_id = @tableId AND s.user_created = 1
          AND EXISTS (SELECT 1 FROM sys.stats_columns AS sc
                      WHERE sc.object_id = @tableId AND sc.stats_id = s.stats_id AND sc.column_id = @columnId);
    OPEN user_stats;
    FETCH NEXT FROM user_stats INTO @statsName;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'DROP STATISTICS [dbo].[event_file_ext].' + QUOTENAME(@statsName) + N';';
        EXEC sp_executesql @sql;
        SET @msg = @migration + N': dropped user-created statistics ' + QUOTENAME(@statsName) + N' on extension_name.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        FETCH NEXT FROM user_stats INTO @statsName;
    END
    CLOSE user_stats;
    DEALLOCATE user_stats;

    -- The collation is stated, so the column keeps its own rather than taking the database default.
    SET @sql = N'ALTER TABLE [dbo].[event_file_ext] ALTER COLUMN [extension_name] nvarchar(' + CAST(@chars AS nvarchar(10)) + N')'
        + CASE WHEN @collation IS NULL THEN N'' ELSE N' COLLATE ' + @collation END
        + CASE WHEN @isNullable = 1 THEN N' NULL' ELSE N' NOT NULL' END;
    SET @onlineDone = 0;
    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            SET @onlineSql = @sql + N' WITH (ONLINE = ON);';
            EXEC sp_executesql @onlineSql;
            SET @onlineDone = 1;
        END TRY
        BEGIN CATCH
            SET @msg = @migration + N': online ALTER COLUMN failed (' + ERROR_MESSAGE() + N'); retrying offline.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END
    IF @onlineDone = 0
    BEGIN
        SET @sql = @sql + N';';
        EXEC sp_executesql @sql;
    END

    SET @msg = @migration + N': extension_name is now nvarchar(' + CAST(@chars AS nvarchar(10)) + N').';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

-------------------------------------------------------------------------------------------------
-- 2. Rows that are equal as nvarchar but were not as varchar (e.g. 'ss' and the German sharp s under
--    a SQL collation). Merged into the oldest row of each group, so the unique index can be built.
--    Normally there are none, and this reads only event_file_ext.
-------------------------------------------------------------------------------------------------
IF @indexIsRight = 0 AND @indexId IS NOT NULL
BEGIN
    DROP INDEX [IX_event_file_ext] ON [dbo].[event_file_ext];
    SET @indexId = NULL;
END
IF OBJECT_ID(N'tempdb..#ext_merge') IS NOT NULL DROP TABLE #ext_merge;
CREATE TABLE #ext_merge (id int NOT NULL PRIMARY KEY, keep_id int NOT NULL);

SET XACT_ABORT ON;
SET @mergeStart = SYSUTCDATETIME();
BEGIN TRY
BEGIN TRANSACTION;
SELECT @rows = COUNT(*) FROM dbo.event_file_ext WITH (TABLOCKX, HOLDLOCK);

INSERT INTO #ext_merge (id, keep_id)
SELECT x.id, x.keep_id
FROM (SELECT e.id, MIN(e.id) OVER (PARTITION BY e.extension_name) AS keep_id
      FROM dbo.event_file_ext AS e) AS x
WHERE x.id <> x.keep_id;
SET @rows = @@ROWCOUNT;

IF @rows > 0
BEGIN
    -- Retain Sch-M until commit as well as the data lock: RCSI readers otherwise see the old ids despite TABLOCKX.
    -- Restating the existing type is metadata-only on this small lookup table, and waits out in-flight writers.
    SET @mergeStep = N'schema-lock';
    SET @sql = N'ALTER TABLE [dbo].[event_file_ext] ALTER COLUMN [extension_name] nvarchar(' + CAST(@chars AS nvarchar(10)) + N')'
        + CASE WHEN @collation IS NULL THEN N'' ELSE N' COLLATE ' + @collation END
        + CASE WHEN @isNullable = 1 THEN N' NULL' ELSE N' NOT NULL' END + N';';
    EXEC sp_executesql @sql;

    SET @msg = @migration + N': ' + CAST(@rows AS nvarchar(10))
        + N' extension row(s) equal another one under Unicode comparison; re-pointing their references at the oldest row of each group.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    -- Every foreign key to event_file_ext.id, and any dbo column named file_extension_id: the one on
    -- event_meta_sharepoint has no foreign key.
    DECLARE @refSchema sysname, @refTable sysname, @refColumn sysname;
    DECLARE refs CURSOR LOCAL FAST_FORWARD FOR
        SELECT OBJECT_SCHEMA_NAME(fkc.parent_object_id), OBJECT_NAME(fkc.parent_object_id), pc.name
        FROM sys.foreign_key_columns AS fkc
        INNER JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        WHERE fkc.referenced_object_id = @tableId
        UNION
        SELECT OBJECT_SCHEMA_NAME(c.object_id), OBJECT_NAME(c.object_id), c.name
        FROM sys.columns AS c
        INNER JOIN sys.tables AS t ON t.object_id = c.object_id
        WHERE c.name = N'file_extension_id' AND t.schema_id = SCHEMA_ID(N'dbo') AND t.is_ms_shipped = 0;
    OPEN refs;
    FETCH NEXT FROM refs INTO @refSchema, @refTable, @refColumn;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @mergeStep = N'repoint ' + QUOTENAME(@refSchema) + N'.' + QUOTENAME(@refTable) + N'.' + QUOTENAME(@refColumn);
        SET @sql = N'UPDATE t SET t.' + QUOTENAME(@refColumn) + N' = m.keep_id FROM ' + QUOTENAME(@refSchema) + N'.' + QUOTENAME(@refTable)
            + N' AS t INNER JOIN #ext_merge AS m ON m.id = t.' + QUOTENAME(@refColumn) + N'; SET @n = @@ROWCOUNT;';
        EXEC sp_executesql @sql, N'@n int OUTPUT', @n = @rows OUTPUT;
        SET @msg = @migration + N': re-pointed ' + CAST(@rows AS nvarchar(20)) + N' row(s) of '
            + QUOTENAME(@refSchema) + N'.' + QUOTENAME(@refTable) + N'.' + QUOTENAME(@refColumn) + N'.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        FETCH NEXT FROM refs INTO @refSchema, @refTable, @refColumn;
    END
    CLOSE refs;

    SET @mergeStep = N'verify-references';
    OPEN refs;
    FETCH NEXT FROM refs INTO @refSchema, @refTable, @refColumn;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'IF EXISTS (SELECT 1 FROM ' + QUOTENAME(@refSchema) + N'.' + QUOTENAME(@refTable)
            + N' AS t INNER JOIN #ext_merge AS m ON m.id = t.' + QUOTENAME(@refColumn) + N')
            THROW 50002, ''EventFileExtensionUnicode: merged-away extension ids still have references; no extension rows were deleted.'', 1;';
        EXEC sp_executesql @sql;
        FETCH NEXT FROM refs INTO @refSchema, @refTable, @refColumn;
    END
    CLOSE refs;
    DEALLOCATE refs;

    SET @mergeStep = N'delete';
    DELETE e
    FROM dbo.event_file_ext AS e
    INNER JOIN #ext_merge AS m ON m.id = e.id;
    SET @msg = @migration + N': removed ' + CAST(@@ROWCOUNT AS nvarchar(10)) + N' merged extension row(s).';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

COMMIT TRANSACTION;
SET @msg = @migration + N': MergeOutcome: stage=merge outcome=Succeeded elapsedMs='
    + CAST(DATEDIFF_BIG(MILLISECOND, @mergeStart, SYSUTCDATETIME()) AS nvarchar(20))
    + N' mergedRows=' + CAST((SELECT COUNT(*) FROM #ext_merge) AS nvarchar(20)) + N'.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    SET @msg = @migration + N': MergeOutcome: stage=' + @mergeStep + N' outcome=Failed sqlError='
        + CAST(ERROR_NUMBER() AS nvarchar(10)) + N' elapsedMs='
        + CAST(DATEDIFF_BIG(MILLISECOND, @mergeStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'; merge rolled back.';
    RAISERROR(N'%s', 10, 1, @msg) WITH NOWAIT;
    IF @xactAbortWasOff = 1 SET XACT_ABORT OFF;
    THROW;
END CATCH
IF @xactAbortWasOff = 1 SET XACT_ABORT OFF;
DROP TABLE #ext_merge;

-------------------------------------------------------------------------------------------------
-- 3. The unique index, exactly as Create DB.sql defines it.
-------------------------------------------------------------------------------------------------
IF @indexIsRight = 0
BEGIN
    SET @sql = N'CREATE UNIQUE NONCLUSTERED INDEX [IX_event_file_ext] ON [dbo].[event_file_ext] ([extension_name])';
    SET @onlineDone = 0;
    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            SET @onlineSql = @sql + N' WITH (ONLINE = ON);';
            EXEC sp_executesql @onlineSql;
            SET @onlineDone = 1;
        END TRY
        BEGIN CATCH
            SET @msg = @migration + N': online build of [' + @indexName + N'] failed (' + ERROR_MESSAGE() + N'); retrying offline.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END
    IF @onlineDone = 0
    BEGIN
        SET @sql = @sql + N';';
        EXEC sp_executesql @sql;
    END

    SET @msg = @migration + N': [' + @indexName + N'] is ready.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

-------------------------------------------------------------------------------------------------
-- 4. Views over the table keep the column type they were created with in their metadata until
--    refreshed. A view that cannot be refreshed still works, so that is only a warning.
-------------------------------------------------------------------------------------------------
DECLARE @view nvarchar(600);
DECLARE dependent_views CURSOR LOCAL FAST_FORWARD FOR
    SELECT DISTINCT QUOTENAME(OBJECT_SCHEMA_NAME(d.referencing_id)) + N'.' + QUOTENAME(OBJECT_NAME(d.referencing_id))
    FROM sys.sql_expression_dependencies AS d
    INNER JOIN sys.views AS v ON v.object_id = d.referencing_id
    WHERE d.referenced_id = @tableId;
OPEN dependent_views;
FETCH NEXT FROM dependent_views INTO @view;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        EXEC sys.sp_refreshview @view;
    END TRY
    BEGIN CATCH
        SET @msg = @migration + N': warning - could not refresh view ' + @view + N' (' + ERROR_MESSAGE()
            + N'). It still works; only its metadata reports the old column type until it is refreshed.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END CATCH
    FETCH NEXT FROM dependent_views INTO @view;
END
CLOSE dependent_views;
DEALLOCATE dependent_views;

SET @msg = @migration + N': finished in ' + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'EventFileExtensionUnicode'. Converts dbo.event_file_ext.extension_name from varchar to nvarchar, so a file extension in any script (e.g. Greek) is stored exactly, and re-creates its unique index IX_event_file_ext. The table holds one row per distinct extension, so this takes seconds; check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        /// <summary>
        /// Deliberately leaves the column <c>nvarchar</c>. Converting it back would turn every extension outside the code
        /// page into <c>?</c>s again, and could make two rows collide on the unique index. The builds before this
        /// migration work against an <c>nvarchar</c> column: they insert, compare and join on a <c>varchar</c>-converted
        /// value, which SQL Server widens to the column's type.
        /// </summary>
        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'EventFileExtensionUnicode': dbo.event_file_ext.extension_name is deliberately left nvarchar, so no stored extension is corrupted.");
        }
    }
}
