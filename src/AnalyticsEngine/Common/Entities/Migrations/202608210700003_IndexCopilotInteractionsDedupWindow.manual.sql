/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202608210700003_IndexCopilotInteractionsDedupWindow
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database schema BY HAND instead of running the installer
   (which applies EF migrations automatically). This is the exact SQL the migration performs, followed by
   the __MigrationHistory stamp so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app
   Health page recognise it as applied.

   WHAT IT DOES
     Adds the de-duplication window index to the Copilot interactions table:
       * IX_copilot_interactions_dedup_window
             ON copilot_interactions (session_id, created_utc) INCLUDE (graph_interaction_id)

     WHY (issue #294). The interaction-history import de-duplicates a batch by loading the existing
     (session_id, graph_interaction_id) keys for every session the batch touches. That read had no time
     bound, so it pulled a thread's ENTIRE history every cycle - and a persistent BizChat thread never
     ends, so the cost grew for the life of the thread. The importer now bounds the read to the batch's own
     time range less a safety margin, and this index is what makes the bounded form seekable.

     MEASURED (synthetic scale: 2,250,000 interactions over 50,000 sessions incl. 50 threads of 5,000
     turns; one 1,000-session chunk; medians of 6 runs):
       before unbounded        288,000 rows loaded /  5,484 reads /  45 ms  (Index Seek)
       after  bounded + index   12,467 rows loaded /  3,050 reads /   6 ms  (Index Seek)
     IMPORTANT: bounding WITHOUT this index is worse than not bounding at all (15,555 reads / 337 ms),
     because created_utc is absent from the existing unique index - so the code change and this index must
     be applied together.
   SAFETY
     * Idempotent / re-runnable: every step is guarded and an already-applied state is a no-op.
     * Attempts ONLINE (non-blocking) on Enterprise / Azure SQL DB / Managed Instance, and falls back to an
       OFFLINE build everywhere else (Standard, Express, LocalDB).
     * No wrapping transaction (matches suppressTransaction: true); an interrupted run converges on re-run.

   UPGRADE TIME
     * copilot_interactions is CREATED BY THIS SAME RELEASE, so on an upgrade from the previous stable
       build the table is EMPTY and this is instant.
     * On a database already running the import from a dev build: synthetic 2,250,000 rows built in 5.8 s
       occupying 97 MB (~2.6 s and ~43 MB per million interactions).
   PREREQUISITE
     The database must already be on migration 202608210700002_IndexCopilotInteractionKeywordsByKeyword. The __MigrationHistory stamp
     copies that row's model snapshot, which is identical to this one because this migration changes no EF
     entity model.

   Run against the Analytics database.
   ===================================================================================================== */
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexCopilotInteractionsDedupWindow';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @tbl sysname = N'copilot_interactions';
DECLARE @ix  sysname = N'IX_copilot_interactions_dedup_window';
DECLARE @rowCount bigint, @edition int;
DECLARE @sql nvarchar(max);
DECLARE @canOnline bit, @onlineDone bit;
DECLARE @objId int;

SET @edition = CAST(SERVERPROPERTY('EngineEdition') AS int);
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index build '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).' ELSE N'not supported on this edition - using offline.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SET @objId = OBJECT_ID(N'dbo.' + @tbl, N'U');

IF @objId IS NULL
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'session_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'created_utc')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'graph_interaction_id')
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' is missing an expected column, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- IF/ELSE that falls through rather than an early RETURN, so a by-hand run against an already-indexed
-- database still reaches the __MigrationHistory stamp in the manual script.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @objId AND name = @ix)
BEGIN
    SET @msg = @migration + N': [' + @ix + N'] already exists on ' + @tbl + N', nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p WHERE p.object_id = @objId AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N' row estimate = ' + CAST(@rowCount AS nvarchar(20))
        + N' (this table is new in this release, so it is normally empty here and the build is instant).';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SET @stepStart = SYSUTCDATETIME();
    SET @onlineDone = 0;

    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            SET @msg = @migration + N': creating [' + @ix + N'] on ' + @tbl + N'(session_id, created_utc) INCLUDE (graph_interaction_id) WITH (ONLINE = ON)...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([session_id], [created_utc]) INCLUDE ([graph_interaction_id]) WITH (ONLINE = ON);';
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
        SET @msg = @migration + N': creating [' + @ix + N'] on ' + @tbl + N'(session_id, created_utc) INCLUDE (graph_interaction_id) (offline)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([session_id], [created_utc]) INCLUDE ([graph_interaction_id]);';
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
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608210700003_IndexCopilotInteractionsDedupWindow')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202608210700002_IndexCopilotInteractionKeywordsByKeyword')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202608210700003_IndexCopilotInteractionsDedupWindow', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202608210700002_IndexCopilotInteractionKeywordsByKeyword';
        RAISERROR('IndexCopilotInteractionsDedupWindow: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('IndexCopilotInteractionsDedupWindow: the schema change was applied, but prerequisite migration 202608210700002_IndexCopilotInteractionKeywordsByKeyword is missing from __MigrationHistory, so it was NOT stamped. Apply the earlier migrations first, or run the installer to reconcile.', 16, 1);
END
ELSE
    RAISERROR('IndexCopilotInteractionsDedupWindow: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
