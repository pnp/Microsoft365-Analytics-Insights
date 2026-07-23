/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202607231700001_CoverCopilotAccessedResourceDedup
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database schema BY HAND instead of running the installer
   (which applies EF migrations automatically). This is the exact index build the migration performs,
   followed by the __MigrationHistory stamp so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and
   the web app Health page recognise it as applied.

   WHAT IT DOES
     Adds a composite key index used by the Copilot import merge's accessed-resource de-duplication:
       * IX_copilot_event_accessed_resources_dedup
             ON copilot_event_accessed_resources
                (copilot_chat_id, resource_id_id, resource_name_id, resource_site_url_id,
                 resource_type_id, sensitivity_label_id)
     The shared Copilot merge de-duplicates the junction with a NOT EXISTS / INTERSECT that compares the full
     resolved tuple for a chat. The table only had single-column indexes (the FK index on copilot_chat_id and
     one per FK column), none of which lets that existence check seek the whole (chat, tuple) - so without a
     seekable composite access path the optimiser re-scans the multi-million-row junction table for every
     resolved row (O(resolved x table)), making insert_junction the dominant Copilot-merge cost at scale (a
     plan/index problem, not fragmentation or data skew). This composite index gives the check an exact
     (chat_id, tuple) seek, dropping the per-batch dedup from minutes to seconds.

   SAFETY
     * Idempotent / re-runnable: the (table, columns, index) is guarded; an already-present index is a no-op.
     * Attempts ONLINE (non-blocking) on Enterprise / Azure SQL DB / MI, falls back to OFFLINE elsewhere.
       This junction table is LARGE on Copilot-heavy tenants: where ONLINE is unavailable the build briefly
       locks the table - run it in a MAINTENANCE WINDOW WITH THE IMPORTER STOPPED.
     * No wrapping transaction (matches suppressTransaction: true); an interrupted run converges on re-run.

   PREREQUISITE
     The database must already be on migration 202607231000001_IndexSitesSiteId (the previous release). The
     __MigrationHistory stamp copies that row's model snapshot (identical to this one, because this migration
     changes no EF entity model).

   Run against the Analytics database.
   ===================================================================================================== */
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'CoverCopilotAccessedResourceDedup';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @tbl sysname = N'copilot_event_accessed_resources';
DECLARE @ix  sysname = N'IX_copilot_event_accessed_resources_dedup';
DECLARE @keyCols nvarchar(400) = N'[copilot_chat_id], [resource_id_id], [resource_name_id], [resource_site_url_id], [resource_type_id], [sensitivity_label_id]';
DECLARE @rowCount bigint, @edition int;
DECLARE @sql nvarchar(max);
DECLARE @canOnline bit, @onlineDone bit;

SET @edition = CAST(SERVERPROPERTY('EngineEdition') AS int);
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index build '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).' ELSE N'not supported on this edition - using offline (briefly locks the table; run in a maintenance window).' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.' + @tbl, N'U') IS NULL
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'copilot_chat_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'resource_id_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'resource_name_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'resource_site_url_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'resource_type_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'sensitivity_label_id')
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' is missing an expected column, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix)
BEGIN
    SET @msg = @migration + N': [' + @ix + N'] already exists on ' + @tbl + N', nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(N'dbo.' + @tbl) AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N' row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N' (this can be a large build).';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SET @stepStart = SYSUTCDATETIME();
    SET @onlineDone = 0;

    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            SET @msg = @migration + N': creating composite index [' + @ix + N'] WITH (ONLINE = ON)...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] (' + @keyCols + N') WITH (ONLINE = ON);';
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
        SET @msg = @migration + N': creating composite index [' + @ix + N'] (offline)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] (' + @keyCols + N');';
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
-- page treat it as applied. No model change here, so the EF snapshot is byte-identical to the previous
-- migration's - copy that row's Model / ContextKey / ProductVersion. Guarded so re-running is safe.
-- =====================================================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202607231700001_CoverCopilotAccessedResourceDedup')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202607231000001_IndexSitesSiteId')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202607231700001_CoverCopilotAccessedResourceDedup', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202607231000001_IndexSitesSiteId';
        RAISERROR('CoverCopilotAccessedResourceDedup: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('CoverCopilotAccessedResourceDedup: the index was built, but prerequisite migration 202607231000001_IndexSitesSiteId is missing from __MigrationHistory, so it was NOT stamped. Upgrade to the previous release first, or run the installer to reconcile.', 16, 1);
END
ELSE
    RAISERROR('CoverCopilotAccessedResourceDedup: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
