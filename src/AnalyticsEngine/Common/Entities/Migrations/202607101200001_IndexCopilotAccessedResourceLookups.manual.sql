/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202607101200001_IndexCopilotAccessedResourceLookups
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database schema BY HAND instead of running the installer
   (AnalyticsInstaller.exe --initdb / DatabaseUpgrader.CheckDbUpgraded, which applies EF migrations
   automatically). This is the exact schema change that the above migration performs, followed by the
   __MigrationHistory stamp so Entity Framework and the web app Health page recognise it as applied.

   WHAT IT DOES
     Narrows copilot_event_accessed_resource_ids.resource_id, copilot_event_accessed_resource_names.[name]
     and copilot_event_accessed_resource_site_urls.site_url from nvarchar(max) to nvarchar(850) and adds a
     non-clustered index on each, so the Copilot import merge SEEKS instead of full-scanning these
     ever-growing lookup tables. Unicode-safe (nvarchar(850) = the 1700-byte index-key limit; see issue #122).

   SAFETY
     * Idempotent / re-runnable: a no-op if already applied.
     * Attempts ONLINE (non-blocking) on Enterprise / Azure SQL DB / MI; falls back to OFFLINE elsewhere
       (Express / Standard / LocalDB). The OFFLINE ALTER COLUMN holds a schema-modification lock and
       rewrites the table, so on large tables RUN IN A MAINTENANCE WINDOW WITH THE IMPORTER STOPPED.
     * Over-width (> 850 char) values are trimmed before narrowing.
     * No wrapping transaction is needed - every step is guarded.

   PREREQUISITE
     The database must already be on migration 202606141000001_UrlFullUrlNvarchar (the previous release).
     The __MigrationHistory stamp copies that row's model snapshot (identical to this migration's), so it
     must be present. If it is not, upgrade to the previous release first, or run the installer to reconcile.

   Run against the Analytics database.
   ===================================================================================================== */

SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexCopilotAccessedResourceLookups';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @maxLen int = 850;

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @targets TABLE (seq int IDENTITY(1,1), tbl sysname, col sysname, ix sysname);
INSERT @targets (tbl, col, ix) VALUES
 (N'copilot_event_accessed_resource_ids',       N'resource_id', N'IX_copilot_event_accessed_resource_ids_resource_id'),
 (N'copilot_event_accessed_resource_names',     N'name',        N'IX_copilot_event_accessed_resource_names_name'),
 (N'copilot_event_accessed_resource_site_urls', N'site_url',    N'IX_copilot_event_accessed_resource_site_urls_site_url');

DECLARE @seq int = 1;
DECLARE @maxSeq int = (SELECT MAX(seq) FROM @targets);
DECLARE @tbl sysname, @col sysname, @ix sysname;
DECLARE @typeName sysname, @maxLength smallint;
DECLARE @alreadyNarrow bit, @indexExists bit;
DECLARE @rowCount bigint, @trunc int, @edition int;
DECLARE @sql nvarchar(max);
DECLARE @canOnline bit, @onlineDone bit;

-- Decide once whether ONLINE index/column operations are even attemptable. They require Enterprise (3),
-- Azure SQL DB (5) or Azure SQL MI (8); Express/Standard/LocalDB (e.g. dev) do NOT support them. We still
-- wrap each ONLINE attempt in TRY/CATCH below (via sp_executesql, which makes the edition error catchable)
-- so that even on a capable edition where a specific ONLINE operation is unsupported (notably shrinking an
-- nvarchar(max) LOB column on some versions) the migration falls back to an offline build rather than failing.
SET @edition = CAST(SERVERPROPERTY('EngineEdition') AS int);
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE operations '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).' ELSE N'not supported on this edition - using offline (a schema lock is held during ALTER COLUMN; run large upgrades in a maintenance window).' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

WHILE @seq <= @maxSeq
BEGIN
    SELECT @tbl = tbl, @col = col, @ix = ix FROM @targets WHERE seq = @seq;
    SET @seq = @seq + 1;

    IF OBJECT_ID(N'dbo.' + @tbl, N'U') IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @tbl + N' does not exist, skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @col)
    BEGIN
        SET @msg = @migration + N': dbo.' + @tbl + N'.' + @col + N' does not exist, skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    SELECT @typeName = t.name, @maxLength = c.max_length
    FROM sys.columns c INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.' + @tbl) AND c.name = @col;

    -- nvarchar(850) stores 850 chars in 1700 bytes, so sys.columns.max_length = 1700 when already narrowed.
    SET @alreadyNarrow = CASE WHEN @typeName = N'nvarchar' AND @maxLength = @maxLen * 2 THEN 1 ELSE 0 END;
    SET @indexExists = CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix) THEN 1 ELSE 0 END;

    IF @alreadyNarrow = 1 AND @indexExists = 1
    BEGIN
        SET @msg = @migration + N': ' + @tbl + N'.' + @col + N' is already nvarchar(' + CAST(@maxLen AS nvarchar(10)) + N') and [' + @ix + N'] exists, nothing to do.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(N'dbo.' + @tbl) AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N'.' + @col + N' row estimate = ' + CAST(@rowCount AS nvarchar(20))
        + N' (current type ' + @typeName + CASE WHEN @maxLength = -1 THEN N'(max)' ELSE N'(' + CAST(@maxLength AS nvarchar(10)) + N')' END + N').';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    -- Trim any over-width value BEFORE the shrink (rare; SharePoint resource ids / file names / site URLs are
    -- realistically < 850). The merge applies the same LEFT(...,850) going forward, so a trimmed value still
    -- de-duplicates to the same key. We trim rather than abort so a pathological value can't block the upgrade.
    IF @alreadyNarrow = 0
    BEGIN
        SET @sql = N'UPDATE dbo.[' + @tbl + N'] SET [' + @col + N'] = LEFT([' + @col + N'], ' + CAST(@maxLen AS nvarchar(10))
            + N') WHERE [' + @col + N'] IS NOT NULL AND LEN([' + @col + N']) > ' + CAST(@maxLen AS nvarchar(10)) + N';';
        EXEC sp_executesql @sql;
        SET @trunc = @@ROWCOUNT;
        IF @trunc > 0
        BEGIN
            SET @msg = @migration + N': trimmed ' + CAST(@trunc AS nvarchar(20)) + N' over-width value(s) in ' + @tbl + N'.' + @col + N' to ' + CAST(@maxLen AS nvarchar(10)) + N' chars.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
    END

    -- SQL Server blocks ALTER COLUMN while an index references the column, so drop ours first if present.
    IF @indexExists = 1
    BEGIN
        EXEC(N'DROP INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'];');
        SET @indexExists = 0;
    END

    IF @alreadyNarrow = 0
    BEGIN
        SET @stepStart = SYSUTCDATETIME();
        SET @onlineDone = 0;

        -- Attempt a non-blocking ONLINE ALTER first on capable editions. If it isn't supported (edition, or the
        -- nvarchar(max) -> nvarchar(850) LOB shrink itself), the CATCH logs it and we retry offline below - which
        -- also re-surfaces any genuine (non-ONLINE) failure instead of masking it.
        IF @canOnline = 1
        BEGIN
            BEGIN TRY
                SET @msg = @migration + N': altering ' + @tbl + N'.' + @col + N' to nvarchar(' + CAST(@maxLen AS nvarchar(10)) + N') NULL WITH (ONLINE = ON)...';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
                SET @sql = N'ALTER TABLE [dbo].[' + @tbl + N'] ALTER COLUMN [' + @col + N'] nvarchar(850) NULL WITH (ONLINE = ON);';
                EXEC sp_executesql @sql;
                SET @onlineDone = 1;
            END TRY
            BEGIN CATCH
                SET @msg = @migration + N': ONLINE ALTER of ' + @tbl + N'.' + @col + N' unavailable (' + ERROR_MESSAGE() + N'); retrying offline.';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END CATCH
        END

        IF @onlineDone = 0
        BEGIN
            SET @msg = @migration + N': altering ' + @tbl + N'.' + @col + N' to nvarchar(' + CAST(@maxLen AS nvarchar(10)) + N') NULL (offline - holds a schema-modification lock; on large tables run in a maintenance window with the importer stopped)...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            SET @sql = N'ALTER TABLE [dbo].[' + @tbl + N'] ALTER COLUMN [' + @col + N'] nvarchar(850) NULL;';
            EXEC sp_executesql @sql;
        END

        SET @msg = @migration + N': ALTER COLUMN completed in ' + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20))
            + N'ms (' + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    -- Create the supporting index, ONLINE (non-blocking) where possible, else offline. Same TRY/CATCH fallback
    -- so the upgrade completes on every edition (including Express / LocalDB used for dev).
    SET @stepStart = SYSUTCDATETIME();
    SET @onlineDone = 0;
    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            SET @msg = @migration + N': creating [' + @ix + N'] WITH (ONLINE = ON)...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']) WITH (ONLINE = ON);';
            EXEC sp_executesql @sql;
            SET @onlineDone = 1;
        END TRY
        BEGIN CATCH
            SET @msg = @migration + N': ONLINE index build of [' + @ix + N'] unavailable (' + ERROR_MESSAGE() + N'); retrying offline.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END
    IF @onlineDone = 0
    BEGIN
        SET @msg = @migration + N': creating [' + @ix + N'] (offline)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']);';
        EXEC sp_executesql @sql;
    END
    SET @msg = @migration + N': [' + @ix + N'] created in ' + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20))
        + N'ms (' + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SET @msg = @migration + N': finished in ' + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- =====================================================================================================
-- Record the migration so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health
-- page treat it as applied. This migration's EF model snapshot is byte-identical to the previous
-- migration's, so we COPY that row's Model / ContextKey / ProductVersion rather than embedding the blob -
-- which is exactly what EF itself writes. Guarded so re-running is safe.
-- =====================================================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202607101200001_IndexCopilotAccessedResourceLookups')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202606141000001_UrlFullUrlNvarchar')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202607101200001_IndexCopilotAccessedResourceLookups', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202606141000001_UrlFullUrlNvarchar';
        RAISERROR('IndexCopilotAccessedResourceLookups: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        RAISERROR('IndexCopilotAccessedResourceLookups: the schema change was applied, but prerequisite migration 202606141000001_UrlFullUrlNvarchar is missing from __MigrationHistory, so this migration was NOT stamped. Upgrade to the previous release first, or run the installer to reconcile __MigrationHistory.', 16, 1);
    END
END
ELSE
    RAISERROR('IndexCopilotAccessedResourceLookups: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
