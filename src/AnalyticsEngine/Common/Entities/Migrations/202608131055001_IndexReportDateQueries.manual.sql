/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202608131055001_IndexReportDateQueries

   Adds or upgrades the covering date indexes used by the in-app Reports queries:
     * audit_events(time_stamp) INCLUDE (operation_id, user_id)
     * hits(hit_timestamp) INCLUDE (session_id)
     * call_records(start) INCLUDE (end)
     * sent_emails(sent_date)

   The script attempts ONLINE builds on Enterprise, Azure SQL Database and Azure SQL Managed
   Instance, then falls back to offline builds. On editions without online index builds, stop the
   relevant importers and run this in a maintenance window. It is idempotent and can be rerun.

   Prerequisite: 202608131030001_IndexUsageReportSnapshots
   Run against the Analytics database.
   ===================================================================================================== */
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexReportDateQueries';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit;
DECLARE @table sysname;
DECLARE @index sysname;
DECLARE @keyColumn sysname;
DECLARE @includeSql nvarchar(300);
DECLARE @include1 sysname;
DECLARE @include2 sysname;
DECLARE @sql nvarchar(max);
DECLARE @onlineDone bit;
DECLARE @rowCount bigint;
DECLARE @indexId int;
DECLARE @definitionIsCurrent bit;
DECLARE @i int = 1;

SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;

DECLARE @targets table
(
    sequence int NOT NULL PRIMARY KEY,
    table_name sysname NOT NULL,
    index_name sysname NOT NULL,
    key_column sysname NOT NULL,
    include_sql nvarchar(300) NOT NULL,
    include_1 sysname NULL,
    include_2 sysname NULL
);

INSERT INTO @targets
    (sequence, table_name, index_name, key_column, include_sql, include_1, include_2)
VALUES
    (1, N'audit_events', N'IX_audit_events_time_stamp', N'time_stamp',
        N' INCLUDE ([operation_id], [user_id])', N'operation_id', N'user_id'),
    (2, N'hits', N'IX_hits_hit_timestamp', N'hit_timestamp',
        N' INCLUDE ([session_id])', N'session_id', NULL),
    (3, N'call_records', N'IX_call_records_start', N'start',
        N' INCLUDE ([end])', N'end', NULL),
    (4, N'sent_emails', N'IX_sent_emails_sent_date', N'sent_date',
        N'', NULL, NULL);

SET @msg = @migration + N': starting; EngineEdition=' + CAST(@edition AS nvarchar(10))
    + CASE WHEN @canOnline = 1
        THEN N'; online index builds will be attempted.'
        ELSE N'; online index builds are unavailable, so large tables will be locked during each build. Stop the relevant importer and use a maintenance window.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

WHILE @i <= 4
BEGIN
    SELECT
        @table = table_name,
        @index = index_name,
        @keyColumn = key_column,
        @includeSql = include_sql,
        @include1 = include_1,
        @include2 = include_2
    FROM @targets
    WHERE sequence = @i;

    IF OBJECT_ID(N'dbo.' + @table, N'U') IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @table + N' does not exist; skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = @keyColumn)
      OR (@include1 IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = @include1))
      OR (@include2 IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = @include2))
    BEGIN
        SET @msg = @migration + N': dbo.' + @table + N' is missing an expected column; skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        SELECT @indexId = index_id
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = @index;

        SET @definitionIsCurrent = 0;
        IF @indexId IS NOT NULL
           AND EXISTS (
                SELECT 1
                FROM sys.index_columns AS ic
                JOIN sys.columns AS c
                  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = OBJECT_ID(N'dbo.' + @table)
                  AND ic.index_id = @indexId
                  AND ic.key_ordinal = 1
                  AND c.name = @keyColumn)
           AND (@include1 IS NULL OR EXISTS (
                SELECT 1
                FROM sys.index_columns AS ic
                JOIN sys.columns AS c
                  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = OBJECT_ID(N'dbo.' + @table)
                  AND ic.index_id = @indexId
                  AND ic.is_included_column = 1
                  AND c.name = @include1))
           AND (@include2 IS NULL OR EXISTS (
                SELECT 1
                FROM sys.index_columns AS ic
                JOIN sys.columns AS c
                  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = OBJECT_ID(N'dbo.' + @table)
                  AND ic.index_id = @indexId
                  AND ic.is_included_column = 1
                  AND c.name = @include2))
            SET @definitionIsCurrent = 1;

        IF @definitionIsCurrent = 1
        BEGIN
            SET @msg = @migration + N': [' + @index + N'] already has the required definition; skipping.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE
        BEGIN
            SELECT @rowCount = ISNULL(SUM(rows), 0)
            FROM sys.partitions
            WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND index_id IN (0, 1);

            SET @msg = @migration
                + CASE WHEN @indexId IS NULL THEN N': creating [' ELSE N': rebuilding [' END
                + @index + N'] on dbo.' + @table + N' ('
                + CAST(@rowCount AS nvarchar(20)) + N' estimated rows).';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;

            SET @onlineDone = 0;
            IF @canOnline = 1
            BEGIN
                BEGIN TRY
                    SET @sql = N'CREATE NONCLUSTERED INDEX [' + @index + N'] ON [dbo].['
                        + @table + N'] ([' + @keyColumn + N'])' + @includeSql
                        + CASE WHEN @indexId IS NULL
                            THEN N' WITH (ONLINE = ON);'
                            ELSE N' WITH (DROP_EXISTING = ON, ONLINE = ON);' END;
                    EXEC sp_executesql @sql;
                    SET @onlineDone = 1;
                END TRY
                BEGIN CATCH
                    SET @msg = @migration + N': online build of [' + @index + N'] failed ('
                        + ERROR_MESSAGE() + N'); retrying offline.';
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                END CATCH
            END

            IF @onlineDone = 0
            BEGIN
                SET @sql = N'CREATE NONCLUSTERED INDEX [' + @index + N'] ON [dbo].['
                    + @table + N'] ([' + @keyColumn + N'])' + @includeSql
                    + CASE WHEN @indexId IS NULL
                        THEN N';'
                        ELSE N' WITH (DROP_EXISTING = ON);' END;
                EXEC sp_executesql @sql;
            END

            SET @msg = @migration + N': [' + @index + N'] is ready.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
    END

    SET @indexId = NULL;
    SET @i += 1;
END

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608131055001_IndexReportDateQueries')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608131030001_IndexUsageReportSnapshots')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202608131055001_IndexReportDateQueries', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202608131030001_IndexUsageReportSnapshots';
        RAISERROR('IndexReportDateQueries: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('IndexReportDateQueries: indexes were built, but prerequisite migration 202608131030001_IndexUsageReportSnapshots is missing from __MigrationHistory, so this migration was not stamped.', 16, 1);
END
ELSE
    RAISERROR('IndexReportDateQueries: already recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
