/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202607130900001_DedupCopilotAccessedResourceSiteUrls
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database schema BY HAND instead of running the installer
   (which applies EF migrations automatically). This is the exact clean-up the migration performs, followed
   by the __MigrationHistory stamp so EF and the web app Health page recognise it as applied.

   WHAT IT DOES
     De-duplicates copilot_event_accessed_resource_site_urls: the Copilot audit SiteUrl carries a volatile
     per-access token (e.g. ?xsdata=...), so older imports stored a near-unique row per access (millions of
     near-duplicate rows). This collapses each site to one canonical row (its path, token stripped),
     re-points the junction (copilot_event_accessed_resources.resource_site_url_id) to the survivors, and
     deletes the duplicates. The merge itself now normalises SiteUrl before de-dup, so new imports stay clean.

   SAFETY
     * Idempotent / re-runnable: a no-op once the table is clean.
     * Every UPDATE/DELETE runs in small batches (WHILE loop) so no single statement takes a large lock or a
       long transaction. No wrapping transaction is used (each batch commits).
     * On a large table this is a one-time pass over millions of rows - run it in a MAINTENANCE WINDOW WITH
       THE IMPORTER STOPPED. NULL site_url rows are left untouched.

   PREREQUISITE
     The database must already be on migration 202607101200001_IndexCopilotAccessedResourceLookups (the
     previous release). The __MigrationHistory stamp copies that row's model snapshot (identical to this one).

   Run against the Analytics database.
   ===================================================================================================== */
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'DedupCopilotAccessedResourceSiteUrls';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @rows int;
DECLARE @batch int = 4000;
DECLARE @done bigint;
DECLARE @dupCount bigint;
DECLARE @normCount bigint;

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_event_accessed_resource_site_urls', N'U') IS NULL
   OR OBJECT_ID(N'dbo.copilot_event_accessed_resources', N'U') IS NULL
BEGIN
    SET @msg = @migration + N': lookup/junction table missing, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- Build the id -> normalised-path -> canonical-id map (only non-null site_url rows). The path is
-- everything before the first '?' or '#'; NULLIF turns CHARINDEX's 0 (not found) into NULL so MIN
-- ignores it, and when neither is present the whole value is kept. Mirrors the merge normalisation.
IF OBJECT_ID('tempdb..#norm') IS NOT NULL DROP TABLE #norm;
SELECT s.id,
       LEFT(s.site_url,
            ISNULL((SELECT MIN(pos) FROM (VALUES
                       (NULLIF(CHARINDEX('?', s.site_url), 0)),
                       (NULLIF(CHARINDEX('#', s.site_url), 0))) q(pos)) - 1,
                   LEN(s.site_url))) AS norm_path
INTO #norm
FROM dbo.copilot_event_accessed_resource_site_urls s
WHERE s.site_url IS NOT NULL;

IF OBJECT_ID('tempdb..#map') IS NOT NULL DROP TABLE #map;
SELECT n.id,
       n.norm_path,
       MIN(n.id) OVER (PARTITION BY n.norm_path) AS canonical_id
INTO #map
FROM #norm n;
CREATE UNIQUE CLUSTERED INDEX IX_map_id ON #map(id);

SELECT @dupCount = COUNT_BIG(*) FROM #map WHERE id <> canonical_id;
SELECT @normCount = COUNT_BIG(*) FROM #map m
    INNER JOIN dbo.copilot_event_accessed_resource_site_urls s ON s.id = m.id
    WHERE m.id = m.canonical_id AND s.site_url <> m.norm_path;

SET @msg = @migration + N': ' + CAST(@dupCount AS nvarchar(20)) + N' duplicate site_url row(s) to collapse, '
    + CAST(@normCount AS nvarchar(20)) + N' canonical row(s) to normalise.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF @dupCount = 0 AND @normCount = 0
BEGIN
    SET @msg = @migration + N': already clean, nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    DROP TABLE #norm; DROP TABLE #map;
    RETURN;
END

-- 1) Re-point junction rows from duplicate site_url ids to their canonical id (batched, FK-safe).
SET @stepStart = SYSUTCDATETIME();
SET @done = 0;
SET @rows = 1;
WHILE @rows > 0
BEGIN
    UPDATE TOP (@batch) j
        SET j.resource_site_url_id = m.canonical_id
    FROM dbo.copilot_event_accessed_resources j
    INNER JOIN #map m ON j.resource_site_url_id = m.id AND m.id <> m.canonical_id;
    SET @rows = @@ROWCOUNT;
    SET @done = @done + @rows;
    IF @done % 200000 = 0 OR @rows = 0
    BEGIN
        SET @msg = @migration + N': re-pointed ' + CAST(@done AS nvarchar(20)) + N' junction row(s)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
END
SET @msg = @migration + N': junction re-point done (' + CAST(@done AS nvarchar(20)) + N' rows) in '
    + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- 2) Normalise the surviving canonical rows' value to the path (batched).
SET @stepStart = SYSUTCDATETIME();
SET @done = 0;
SET @rows = 1;
WHILE @rows > 0
BEGIN
    UPDATE TOP (@batch) s
        SET s.site_url = m.norm_path
    FROM dbo.copilot_event_accessed_resource_site_urls s
    INNER JOIN #map m ON s.id = m.id AND m.id = m.canonical_id AND s.site_url <> m.norm_path;
    SET @rows = @@ROWCOUNT;
    SET @done = @done + @rows;
END
SET @msg = @migration + N': normalised ' + CAST(@done AS nvarchar(20)) + N' canonical row(s) in '
    + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- 3) Delete the now-unreferenced duplicate rows (batched). Safe: every junction row was re-pointed above.
SET @stepStart = SYSUTCDATETIME();
SET @done = 0;
SET @rows = 1;
WHILE @rows > 0
BEGIN
    DELETE TOP (@batch) s
    FROM dbo.copilot_event_accessed_resource_site_urls s
    INNER JOIN #map m ON s.id = m.id AND m.id <> m.canonical_id;
    SET @rows = @@ROWCOUNT;
    SET @done = @done + @rows;
    IF @done % 200000 = 0 OR @rows = 0
    BEGIN
        SET @msg = @migration + N': deleted ' + CAST(@done AS nvarchar(20)) + N' duplicate site_url row(s)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
END
SET @msg = @migration + N': deleted ' + CAST(@done AS nvarchar(20)) + N' duplicate row(s) in '
    + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DROP TABLE #norm;
DROP TABLE #map;

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- =====================================================================================================
-- Record the migration so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health
-- page treat it as applied. No model change here, so the EF snapshot is byte-identical to the previous
-- migration's - copy that row's Model / ContextKey / ProductVersion. Guarded so re-running is safe.
-- =====================================================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202607130900001_DedupCopilotAccessedResourceSiteUrls')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202607101200001_IndexCopilotAccessedResourceLookups')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202607130900001_DedupCopilotAccessedResourceSiteUrls', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202607101200001_IndexCopilotAccessedResourceLookups';
        RAISERROR('DedupCopilotAccessedResourceSiteUrls: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('DedupCopilotAccessedResourceSiteUrls: the clean-up ran, but prerequisite migration 202607101200001_IndexCopilotAccessedResourceLookups is missing from __MigrationHistory, so it was NOT stamped. Upgrade to the previous release first, or run the installer to reconcile.', 16, 1);
END
ELSE
    RAISERROR('DedupCopilotAccessedResourceSiteUrls: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
