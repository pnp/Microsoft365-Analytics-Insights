/* =====================================================================================================
   MANUAL DATABASE UPGRADE - 202610221500002_EventFileExtensionUnicode

   For DBAs who upgrade the Analytics database by hand instead of running the installer.
   This is the exact change the migration makes, followed by the __MigrationHistory stamp so EF
   (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health page treat it as applied.

   RUN ORDER
     Run the release's manual scripts in migration-id order. This one requires
     202610221500001_CopilotAgentNameAndFlagRecency to be stamped in __MigrationHistory first, and
     refuses to stamp itself otherwise.

   WHAT IT DOES
     dbo.event_file_ext.extension_name becomes nvarchar instead of varchar (same width, nullability and
     collation), and its unique index IX_event_file_ext is dropped and re-created around the change.
     As varchar, a file extension in any script outside the database's code page (Greek, for example)
     was stored as question marks, so different extensions could share one row. The new importer build
     stores every extension exactly as logged, and needs this change: run this script before starting it.

     Under a SQL collation (such as SQL_Latin1_General_CP1_CI_AS, the Azure SQL default) a few values
     that were different as varchar are equal as nvarchar - 'ss' and the German sharp s, for example. If
     the database holds such a pair, the script keeps the older row, re-points every reference to the
     other one (each foreign key to event_file_ext.id, and event_meta_sharepoint.file_extension_id),
     and deletes it, so the unique index can be re-created. Normally there is no such pair.

   CLASSIFICATION: UNICODE-CORRECTNESS TYPE CHANGE
     Not performance-motivated, so the benchmark gate does not apply. The index is re-created exactly
     as it was.

   UPGRADE TIME
     Seconds, whatever the size of the database: event_file_ext holds one row per distinct file
     extension (typically hundreds to a few thousand rows, independent of how many audit events you
     have). The column change and the index build each take a brief schema-modification lock on
     dbo.event_file_ext, which pauses the importer's merge for that moment; ONLINE is attempted where
     the edition supports it (Enterprise, Azure SQL Database, Azure SQL Managed Instance). Only if the
     script reports rows to re-point does it read dbo.event_meta_sharepoint once (no index on its
     file_extension_id): allow a few minutes per 100 million rows in that case.

   BEFORE ANY CHANGE IT STOPS, CHANGING NOTHING, IF
     another index, a constraint, a foreign key onto the column or a schema-bound object depends on
     extension_name. The message lists them: drop them, re-run the script, and re-create them.

   SAFE TO RE-RUN
     Every step is guarded. Re-running the script against an upgraded database changes nothing.
   ===================================================================================================== */

SET NOCOUNT ON;

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
                WHERE object_id = @tableId AND index_id = @indexId AND is_unique = 1 AND type = 2 AND has_filter = 0)
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
IF OBJECT_ID(N'tempdb..#ext_merge') IS NOT NULL DROP TABLE #ext_merge;
CREATE TABLE #ext_merge (id int NOT NULL PRIMARY KEY, keep_id int NOT NULL);

INSERT INTO #ext_merge (id, keep_id)
SELECT x.id, x.keep_id
FROM (SELECT e.id, MIN(e.id) OVER (PARTITION BY e.extension_name) AS keep_id
      FROM dbo.event_file_ext AS e) AS x
WHERE x.id <> x.keep_id;
SET @rows = @@ROWCOUNT;

IF @rows > 0
BEGIN
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
        SET @sql = N'UPDATE t SET t.' + QUOTENAME(@refColumn) + N' = m.keep_id FROM ' + QUOTENAME(@refSchema) + N'.' + QUOTENAME(@refTable)
            + N' AS t INNER JOIN #ext_merge AS m ON m.id = t.' + QUOTENAME(@refColumn) + N'; SET @n = @@ROWCOUNT;';
        EXEC sp_executesql @sql, N'@n int OUTPUT', @n = @rows OUTPUT;
        SET @msg = @migration + N': re-pointed ' + CAST(@rows AS nvarchar(20)) + N' row(s) of '
            + QUOTENAME(@refSchema) + N'.' + QUOTENAME(@refTable) + N'.' + QUOTENAME(@refColumn) + N'.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        FETCH NEXT FROM refs INTO @refSchema, @refTable, @refColumn;
    END
    CLOSE refs;
    DEALLOCATE refs;

    DELETE e
    FROM dbo.event_file_ext AS e
    INNER JOIN #ext_merge AS m ON m.id = e.id;
    SET @msg = @migration + N': removed ' + CAST(@@ROWCOUNT AS nvarchar(10)) + N' merged extension row(s).';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

DROP TABLE #ext_merge;

-------------------------------------------------------------------------------------------------
-- 3. The unique index, exactly as Create DB.sql defines it.
-------------------------------------------------------------------------------------------------
IF @indexIsRight = 0
BEGIN
    IF @indexId IS NOT NULL
    BEGIN
        -- Present but not the unique single-column index (only possible if the column was already nvarchar).
        DROP INDEX [IX_event_file_ext] ON [dbo].[event_file_ext];
        SET @msg = @migration + N': dropped [' + @indexName + N'], which did not have the expected shape.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

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
RAISERROR(@msg, 0, 1) WITH NOWAIT;
GO

-------------------------------------------------------------------------------------------------
-- Record the migration in __MigrationHistory.
--
-- The guard checks SCHEMA only: extension_name must be nvarchar with its unique index, and the
-- predecessor must be stamped. It never checks data. Reached on every path, including a re-run
-- against a database that is already up to date. The model blob is copied from the predecessor,
-- because this migration does not change the EF model.
-------------------------------------------------------------------------------------------------
RAISERROR('EventFileExtensionUnicode: stamping __MigrationHistory.', 0, 1) WITH NOWAIT;

IF NOT EXISTS (SELECT 1
               FROM sys.columns AS c
               INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
               WHERE c.object_id = OBJECT_ID(N'dbo.event_file_ext') AND c.name = N'extension_name' AND t.name = N'nvarchar')
   OR NOT EXISTS (SELECT 1
                  FROM sys.indexes AS i
                  INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                  INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE i.object_id = OBJECT_ID(N'dbo.event_file_ext') AND i.name = N'IX_event_file_ext'
                    AND i.is_unique = 1 AND ic.key_ordinal = 1 AND c.name = N'extension_name')
BEGIN
    RAISERROR('EventFileExtensionUnicode: NOT stamped - the schema work did not complete: dbo.event_file_ext.extension_name is not nvarchar, or its unique index IX_event_file_ext is missing. Re-run this script and read the messages above.', 16, 1);
END
ELSE IF NOT EXISTS (SELECT 1 FROM [dbo].[__MigrationHistory] WHERE [MigrationId] = N'202610221500001_CopilotAgentNameAndFlagRecency')
BEGIN
    RAISERROR('EventFileExtensionUnicode: NOT stamped - prerequisite migration 202610221500001_CopilotAgentNameAndFlagRecency is not stamped in __MigrationHistory. Run its manual script first.', 16, 1);
END
ELSE IF NOT EXISTS (SELECT 1 FROM [dbo].[__MigrationHistory] WHERE [MigrationId] = N'202610221500002_EventFileExtensionUnicode')
BEGIN
    INSERT INTO [dbo].[__MigrationHistory] ([MigrationId], [ContextKey], [Model], [ProductVersion])
    SELECT N'202610221500002_EventFileExtensionUnicode', [ContextKey], [Model], [ProductVersion]
    FROM [dbo].[__MigrationHistory]
    WHERE [MigrationId] = N'202610221500001_CopilotAgentNameAndFlagRecency';
    RAISERROR('EventFileExtensionUnicode: stamped __MigrationHistory.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('EventFileExtensionUnicode: __MigrationHistory already stamped.', 0, 1) WITH NOWAIT;
END
