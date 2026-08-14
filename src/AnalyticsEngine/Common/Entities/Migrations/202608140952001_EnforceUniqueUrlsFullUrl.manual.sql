/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202608140952001_EnforceUniqueUrlsFullUrl
   =====================================================================================================
   De-duplicates dbo.urls.full_url, re-points all known dependent activity to the canonical URL row,
   and rebuilds IX_urls_full_url as UNIQUE with IGNORE_DUP_KEY enabled.

   Run in a maintenance window with all importers stopped. The migration is guarded, batched,
   idempotent and resumable. On editions without online index builds, the final index rebuild locks
   dbo.urls until it completes.

   Prerequisite: 202608131055001_IndexReportDateQueries.
   Run against the Analytics database.
   ===================================================================================================== */SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'EnforceUniqueUrlsFullUrl';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);
DECLARE @batch int = 4000;
DECLARE @dupCount bigint;
DECLARE @done bigint;
DECLARE @remaining bigint;
DECLARE @targetRemaining bigint;
DECLARE @batchCount int;
DECLARE @deletedCount int;
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit;
DECLARE @onlineDone bit;
DECLARE @indexId int;
DECLARE @indexIsReady bit;
DECLARE @sql nvarchar(max);
DECLARE @deleteGuards nvarchar(max) = N'';
DECLARE @sequence tinyint;
DECLARE @targetTable sysname;
DECLARE @targetPk sysname;
DECLARE @targetPkType char(1);
DECLARE @targetCount bigint;

SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.urls', N'U') IS NULL
BEGIN
    RAISERROR('EnforceUniqueUrlsFullUrl: dbo.urls does not exist; run the prerequisite migrations first.', 16, 1);
    RETURN;
END

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns AS c
    INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.urls')
      AND c.name = N'full_url'
      AND t.name = N'nvarchar'
      AND c.max_length = 1700
      AND c.is_nullable = 0
)
BEGIN
    RAISERROR('EnforceUniqueUrlsFullUrl: dbo.urls.full_url must be nvarchar(850) NOT NULL; run the prerequisite migrations first.', 16, 1);
    RETURN;
END

SELECT @indexId = index_id
FROM sys.indexes
WHERE object_id = OBJECT_ID(N'dbo.urls')
  AND name = N'IX_urls_full_url';

SET @indexIsReady = CASE WHEN EXISTS (
    SELECT 1
    FROM sys.indexes AS i
    WHERE i.object_id = OBJECT_ID(N'dbo.urls')
      AND i.name = N'IX_urls_full_url'
      AND i.type = 2
      AND i.is_unique = 1
      AND i.ignore_dup_key = 1
      AND i.is_disabled = 0
      AND (SELECT COUNT(*)
           FROM sys.index_columns AS ic
           WHERE ic.object_id = i.object_id
             AND ic.index_id = i.index_id
             AND ic.key_ordinal > 0) = 1
      AND EXISTS (
          SELECT 1
          FROM sys.index_columns AS ic
          INNER JOIN sys.columns AS c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
          WHERE ic.object_id = i.object_id
            AND ic.index_id = i.index_id
            AND ic.key_ordinal = 1
            AND c.name = N'full_url')
) THEN 1 ELSE 0 END;

IF @indexIsReady = 1
BEGIN
    RAISERROR('EnforceUniqueUrlsFullUrl: IX_urls_full_url is already unique and race-safe; nothing to do.', 0, 1) WITH NOWAIT;
    GOTO MigrationDone;
END

DECLARE @targets table
(
    sequence tinyint NOT NULL PRIMARY KEY,
    table_name sysname NOT NULL,
    pk_column sysname NOT NULL,
    pk_type char(1) NOT NULL
);

-- pk_type: I = int, G = uniqueidentifier.
INSERT INTO @targets (sequence, table_name, pk_column, pk_type)
VALUES
    (1, N'hits', N'id', 'I'),
    (2, N'copilot_event_files', N'copilot_chat_id', 'G'),
    (3, N'event_copilot_files', N'copilot_chat_id', 'G'),
    (4, N'file_metadata_property_values', N'id', 'I'),
    (5, N'hits_clicked_elements', N'id', 'I'),
    (6, N'page_comments', N'id', 'I'),
    (7, N'page_likes', N'id', 'I'),
    (8, N'event_meta_sharepoint', N'event_id', 'G'),
    (9, N'audit_events', N'id', 'G');

IF OBJECT_ID('tempdb..#url_map') IS NOT NULL DROP TABLE #url_map;
;WITH duplicate_values AS
(
    SELECT full_url
    FROM dbo.urls
    GROUP BY full_url
    HAVING COUNT_BIG(*) > 1
)
SELECT u.id,
       MIN(u.id) OVER (PARTITION BY u.full_url) AS canonical_id
INTO #url_map
FROM dbo.urls AS u
INNER JOIN duplicate_values AS d ON d.full_url = u.full_url;

CREATE UNIQUE CLUSTERED INDEX IX_url_map_id ON #url_map(id);

IF OBJECT_ID('tempdb..#dupids') IS NOT NULL DROP TABLE #dupids;
SELECT id
INTO #dupids
FROM #url_map
WHERE id <> canonical_id;
CREATE UNIQUE CLUSTERED INDEX IX_dupids_id ON #dupids(id);

SELECT @dupCount = COUNT_BIG(*) FROM #dupids;
SET @msg = @migration + N': found ' + CAST(@dupCount AS nvarchar(20)) + N' duplicate URL row(s).';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- Refuse to delete URLs if a newer schema introduced a foreign-key reference this migration
-- does not know how to re-point.
IF @dupCount > 0 AND EXISTS (
    SELECT 1
    FROM sys.foreign_key_columns AS fkc
    INNER JOIN sys.tables AS parent_table ON parent_table.object_id = fkc.parent_object_id
    INNER JOIN sys.schemas AS parent_schema ON parent_schema.schema_id = parent_table.schema_id
    INNER JOIN sys.columns AS parent_column
      ON parent_column.object_id = fkc.parent_object_id
     AND parent_column.column_id = fkc.parent_column_id
    WHERE fkc.referenced_object_id = OBJECT_ID(N'dbo.urls')
      AND NOT EXISTS (
          SELECT 1
          FROM @targets AS target
          WHERE parent_schema.name = N'dbo'
            AND target.table_name = parent_table.name
            AND parent_column.name = N'url_id')
)
BEGIN
    RAISERROR('EnforceUniqueUrlsFullUrl: an unknown foreign key references dbo.urls; migration stopped before changing data.', 16, 1);
    RETURN;
END

-- Two child tables have unique keys containing url_id. Re-pointing first could violate those
-- keys, so retain one business row per canonical key and remove only the redundant collision.
IF OBJECT_ID('tempdb..#delete_int') IS NOT NULL DROP TABLE #delete_int;
CREATE TABLE #delete_int
(
    table_code tinyint NOT NULL,
    row_id int NOT NULL,
    PRIMARY KEY CLUSTERED (table_code, row_id)
);

IF OBJECT_ID(N'dbo.file_metadata_property_values', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.file_metadata_property_values', N'url_id') IS NOT NULL
   AND COL_LENGTH(N'dbo.file_metadata_property_values', N'field_id') IS NOT NULL
   AND COL_LENGTH(N'dbo.file_metadata_property_values', N'updated') IS NOT NULL
BEGIN
    ;WITH ranked AS
    (
        SELECT p.id,
               ROW_NUMBER() OVER (
                   PARTITION BY m.canonical_id, p.field_id
                   ORDER BY CASE WHEN p.updated IS NULL THEN 1 ELSE 0 END,
                            p.updated DESC,
                            p.id) AS row_num
        FROM dbo.file_metadata_property_values AS p
        INNER JOIN #url_map AS m ON m.id = p.url_id
    )
    INSERT INTO #delete_int (table_code, row_id)
    SELECT 4, id
    FROM ranked
    WHERE row_num > 1;
END

IF OBJECT_ID(N'dbo.hits_clicked_elements', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.hits_clicked_elements', N'url_id') IS NOT NULL
   AND COL_LENGTH(N'dbo.hits_clicked_elements', N'hit_id') IS NOT NULL
   AND COL_LENGTH(N'dbo.hits_clicked_elements', N'timestamp') IS NOT NULL
BEGIN
    ;WITH ranked AS
    (
        SELECT click.id,
               ROW_NUMBER() OVER (
                   PARTITION BY m.canonical_id, click.hit_id, click.[timestamp]
                   ORDER BY CASE WHEN click.url_id = m.canonical_id THEN 0 ELSE 1 END,
                            click.id) AS row_num
        FROM dbo.hits_clicked_elements AS click
        INNER JOIN #url_map AS m ON m.id = click.url_id
    )
    INSERT INTO #delete_int (table_code, row_id)
    SELECT 5, id
    FROM ranked
    WHERE row_num > 1;
END

IF OBJECT_ID('tempdb..#delete_batch') IS NOT NULL DROP TABLE #delete_batch;
CREATE TABLE #delete_batch (row_id int NOT NULL PRIMARY KEY);

SET @done = 0;
WHILE EXISTS (SELECT 1 FROM #delete_int WHERE table_code = 4)
BEGIN
    DELETE TOP (@batch) work
    OUTPUT deleted.row_id INTO #delete_batch(row_id)
    FROM #delete_int AS work
    WHERE work.table_code = 4;

    DELETE target
    FROM dbo.file_metadata_property_values AS target
    INNER JOIN #delete_batch AS batch_rows ON batch_rows.row_id = target.id;

    SET @done = @done + @@ROWCOUNT;
    TRUNCATE TABLE #delete_batch;
END
IF @done > 0
BEGIN
    SET @msg = @migration + N': removed ' + CAST(@done AS nvarchar(20))
        + N' redundant file metadata row(s) before URL re-pointing.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SET @done = 0;
WHILE EXISTS (SELECT 1 FROM #delete_int WHERE table_code = 5)
BEGIN
    DELETE TOP (@batch) work
    OUTPUT deleted.row_id INTO #delete_batch(row_id)
    FROM #delete_int AS work
    WHERE work.table_code = 5;

    DELETE target
    FROM dbo.hits_clicked_elements AS target
    INNER JOIN #delete_batch AS batch_rows ON batch_rows.row_id = target.id;

    SET @done = @done + @@ROWCOUNT;
    TRUNCATE TABLE #delete_batch;
END
IF @done > 0
BEGIN
    SET @msg = @migration + N': removed ' + CAST(@done AS nvarchar(20))
        + N' redundant click row(s) before URL re-pointing.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

DROP TABLE #delete_batch;
DROP TABLE #delete_int;

-- Materialise every re-point exactly once. This avoids repeatedly scanning the large fact tables
-- for each batch, which becomes quadratic at customer scale.
IF OBJECT_ID('tempdb..#repoint_int') IS NOT NULL DROP TABLE #repoint_int;
CREATE TABLE #repoint_int
(
    table_code tinyint NOT NULL,
    row_id int NOT NULL,
    canonical_id int NOT NULL,
    PRIMARY KEY CLUSTERED (table_code, row_id)
);

IF OBJECT_ID('tempdb..#repoint_guid') IS NOT NULL DROP TABLE #repoint_guid;
CREATE TABLE #repoint_guid
(
    table_code tinyint NOT NULL,
    row_id uniqueidentifier NOT NULL,
    canonical_id int NOT NULL,
    PRIMARY KEY CLUSTERED (table_code, row_id)
);

SET @sequence = 1;
WHILE @sequence <= 9
BEGIN
    SELECT @targetTable = table_name,
           @targetPk = pk_column,
           @targetPkType = pk_type
    FROM @targets
    WHERE sequence = @sequence;

    IF OBJECT_ID(N'dbo.' + @targetTable, N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.' + @targetTable, N'url_id') IS NOT NULL
       AND COL_LENGTH(N'dbo.' + @targetTable, @targetPk) IS NOT NULL
    BEGIN
        SET @sql = CASE WHEN @targetPkType = 'I'
            THEN N'INSERT INTO #repoint_int (table_code, row_id, canonical_id) '
            ELSE N'INSERT INTO #repoint_guid (table_code, row_id, canonical_id) ' END
            + N'SELECT @code, target.' + QUOTENAME(@targetPk) + N', map.canonical_id '
            + N'FROM [dbo].' + QUOTENAME(@targetTable) + N' AS target '
            + N'INNER JOIN #url_map AS map ON map.id = target.[url_id] '
            + N'WHERE map.id <> map.canonical_id;';
        EXEC sp_executesql @sql, N'@code tinyint', @code = @sequence;
    END

    SET @sequence += 1;
END

IF OBJECT_ID('tempdb..#int_batch') IS NOT NULL DROP TABLE #int_batch;
CREATE TABLE #int_batch
(
    row_id int NOT NULL PRIMARY KEY,
    canonical_id int NOT NULL
);

IF OBJECT_ID('tempdb..#guid_batch') IS NOT NULL DROP TABLE #guid_batch;
CREATE TABLE #guid_batch
(
    row_id uniqueidentifier NOT NULL PRIMARY KEY,
    canonical_id int NOT NULL
);

SET @sequence = 1;
WHILE @sequence <= 9
BEGIN
    SELECT @targetTable = table_name,
           @targetPk = pk_column,
           @targetPkType = pk_type
    FROM @targets
    WHERE sequence = @sequence;

    SET @targetCount = CASE WHEN @targetPkType = 'I'
        THEN (SELECT COUNT_BIG(*) FROM #repoint_int WHERE table_code = @sequence)
        ELSE (SELECT COUNT_BIG(*) FROM #repoint_guid WHERE table_code = @sequence) END;

    IF @targetCount > 0
    BEGIN
        SET @done = 0;
        IF @targetPkType = 'I'
        BEGIN
            WHILE EXISTS (SELECT 1 FROM #repoint_int WHERE table_code = @sequence)
            BEGIN
                DELETE TOP (@batch) work
                OUTPUT deleted.row_id, deleted.canonical_id
                  INTO #int_batch(row_id, canonical_id)
                FROM #repoint_int AS work
                WHERE work.table_code = @sequence;

                SET @sql = N'UPDATE target SET target.[url_id] = batch_rows.canonical_id '
                    + N'FROM [dbo].' + QUOTENAME(@targetTable) + N' AS target '
                    + N'INNER JOIN #int_batch AS batch_rows ON batch_rows.row_id = target.'
                    + QUOTENAME(@targetPk) + N';';
                EXEC sp_executesql @sql;
                SET @done = @done + @@ROWCOUNT;
                TRUNCATE TABLE #int_batch;
            END
        END
        ELSE
        BEGIN
            WHILE EXISTS (SELECT 1 FROM #repoint_guid WHERE table_code = @sequence)
            BEGIN
                DELETE TOP (@batch) work
                OUTPUT deleted.row_id, deleted.canonical_id
                  INTO #guid_batch(row_id, canonical_id)
                FROM #repoint_guid AS work
                WHERE work.table_code = @sequence;

                SET @sql = N'UPDATE target SET target.[url_id] = batch_rows.canonical_id '
                    + N'FROM [dbo].' + QUOTENAME(@targetTable) + N' AS target '
                    + N'INNER JOIN #guid_batch AS batch_rows ON batch_rows.row_id = target.'
                    + QUOTENAME(@targetPk) + N';';
                EXEC sp_executesql @sql;
                SET @done = @done + @@ROWCOUNT;
                TRUNCATE TABLE #guid_batch;
            END
        END

        SET @msg = @migration + N': re-pointed ' + CAST(@done AS nvarchar(20))
            + N' dbo.' + @targetTable + N' row(s).';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    SET @sequence += 1;
END

DROP TABLE #int_batch;
DROP TABLE #guid_batch;
DROP TABLE #repoint_int;
DROP TABLE #repoint_guid;

-- Verify every known reference is gone and build the same guards into the parent DELETE. This is
-- required because several FKs cascade; relying on an FK error would silently delete child data.
SET @remaining = 0;
SET @sequence = 1;
WHILE @sequence <= 9
BEGIN
    SELECT @targetTable = table_name
    FROM @targets
    WHERE sequence = @sequence;

    IF OBJECT_ID(N'dbo.' + @targetTable, N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.' + @targetTable, N'url_id') IS NOT NULL
    BEGIN
        SET @targetRemaining = 0;
        SET @sql = N'SELECT @countOut = COUNT_BIG(*) '
            + N'FROM [dbo].' + QUOTENAME(@targetTable) + N' AS target '
            + N'INNER JOIN #dupids AS duplicate ON duplicate.id = target.[url_id];';
        EXEC sp_executesql @sql, N'@countOut bigint OUTPUT', @countOut = @targetRemaining OUTPUT;
        SET @remaining = @remaining + @targetRemaining;

        SET @deleteGuards = @deleteGuards
            + N' AND NOT EXISTS (SELECT 1 FROM [dbo].' + QUOTENAME(@targetTable)
            + N' AS reference WHERE reference.[url_id] = url_row.[id])';
    END

    SET @sequence += 1;
END

IF @remaining > 0
BEGIN
    SET @msg = @migration + N': ' + CAST(@remaining AS nvarchar(20))
        + N' reference(s) still target duplicate URLs; migration stopped before parent deletion.';
    RAISERROR(@msg, 16, 1);
    RETURN;
END

IF OBJECT_ID('tempdb..#url_delete_batch') IS NOT NULL DROP TABLE #url_delete_batch;
CREATE TABLE #url_delete_batch (id int NOT NULL PRIMARY KEY);

SET @done = 0;
WHILE EXISTS (SELECT 1 FROM #dupids)
BEGIN
    DELETE TOP (@batch) work
    OUTPUT deleted.id INTO #url_delete_batch(id)
    FROM #dupids AS work;

    SELECT @batchCount = COUNT(*) FROM #url_delete_batch;
    SET @deletedCount = 0;
    SET @sql = N'DELETE url_row '
        + N'FROM [dbo].[urls] AS url_row '
        + N'INNER JOIN #url_delete_batch AS batch_rows ON batch_rows.id = url_row.id '
        + N'WHERE 1 = 1' + @deleteGuards + N'; '
        + N'SET @deletedOut = @@ROWCOUNT;';
    EXEC sp_executesql @sql, N'@deletedOut int OUTPUT', @deletedOut = @deletedCount OUTPUT;

    IF @deletedCount <> @batchCount
    BEGIN
        RAISERROR('EnforceUniqueUrlsFullUrl: a new reference appeared during cleanup; migration stopped without cascade-deleting it.', 16, 1);
        RETURN;
    END

    SET @done = @done + @deletedCount;
    TRUNCATE TABLE #url_delete_batch;
END

DROP TABLE #url_delete_batch;
DROP TABLE #dupids;
DROP TABLE #url_map;

IF EXISTS (
    SELECT 1
    FROM dbo.urls
    GROUP BY full_url
    HAVING COUNT_BIG(*) > 1
)
BEGIN
    RAISERROR('EnforceUniqueUrlsFullUrl: duplicate full_url rows appeared during cleanup; stop importers and rerun.', 16, 1);
    RETURN;
END

IF @done > 0
BEGIN
    SET @msg = @migration + N': deleted ' + CAST(@done AS nvarchar(20)) + N' duplicate URL row(s).';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SELECT @indexId = index_id
FROM sys.indexes
WHERE object_id = OBJECT_ID(N'dbo.urls')
  AND name = N'IX_urls_full_url';

IF @indexId IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes
       WHERE object_id = OBJECT_ID(N'dbo.urls')
         AND index_id = @indexId
         AND type = 2)
BEGIN
    RAISERROR('EnforceUniqueUrlsFullUrl: IX_urls_full_url exists but is not non-clustered; migration stopped.', 16, 1);
    RETURN;
END

SET @msg = @migration
    + CASE WHEN @indexId IS NULL THEN N': creating ' ELSE N': rebuilding ' END
    + N'IX_urls_full_url as a unique index'
    + CASE WHEN @canOnline = 1 THEN N' (online attempt).' ELSE N' (offline; maintenance window required).' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SET @onlineDone = 0;
IF @canOnline = 1
BEGIN
    BEGIN TRY
        SET @sql = N'CREATE UNIQUE NONCLUSTERED INDEX [IX_urls_full_url] '
            + N'ON [dbo].[urls] ([full_url]) WITH ('
            + CASE WHEN @indexId IS NULL THEN N'' ELSE N'DROP_EXISTING = ON, ' END
            + N'IGNORE_DUP_KEY = ON, ONLINE = ON);';
        EXEC sp_executesql @sql;
        SET @onlineDone = 1;
    END TRY
    BEGIN CATCH
        SET @msg = @migration + N': online index build failed (' + ERROR_MESSAGE()
            + N'); retrying offline.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END CATCH
END

IF @onlineDone = 0
BEGIN
    SET @sql = N'CREATE UNIQUE NONCLUSTERED INDEX [IX_urls_full_url] '
        + N'ON [dbo].[urls] ([full_url]) WITH ('
        + CASE WHEN @indexId IS NULL THEN N'' ELSE N'DROP_EXISTING = ON, ' END
        + N'IGNORE_DUP_KEY = ON);';
    EXEC sp_executesql @sql;
END

MigrationDone:
SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- Record the migration so EF and the Health page recognise the manual upgrade.
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608140952001_EnforceUniqueUrlsFullUrl')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608131055001_IndexReportDateQueries')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202608140952001_EnforceUniqueUrlsFullUrl', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202608131055001_IndexReportDateQueries';
        RAISERROR('EnforceUniqueUrlsFullUrl: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('EnforceUniqueUrlsFullUrl: work completed, but prerequisite migration 202608131055001_IndexReportDateQueries is missing from __MigrationHistory, so this migration was NOT stamped.', 16, 1);
END
ELSE
    RAISERROR('EnforceUniqueUrlsFullUrl: already recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;